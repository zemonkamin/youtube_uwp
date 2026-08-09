using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace YouTube
{
    // On-the-fly DASH demuxer for Windows 10 Mobile.
    //
    // YouTube's adaptive (video-only / audio-only) streams are fragmented MP4 (fMP4): an
    // init segment (ftyp+moov, byte range = initRange) describing the codec, a sidx box
    // (byte range = indexRange) indexing the media fragments, then a sequence of moof+mdat
    // fragments. Win10 Mobile's AdaptiveMediaSource refuses YouTube's DASH manifests, so we
    // parse the boxes ourselves and feed encoded samples into a MediaStreamSource, which the
    // MediaPlayer decodes and A/V-syncs. Only the small init+sidx is fetched up front; each
    // fragment's bytes are pulled by HTTP range on demand as the decoder asks for samples.
    //
    // Scope: H.264 (avc1) video + AAC-LC (mp4a) audio — the itags this app selects for WP10M.
    internal static class DashDemuxer
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MediaStreamSource, DashMediaSource> Owners =
            new System.Runtime.CompilerServices.ConditionalWeakTable<MediaStreamSource, DashMediaSource>();

        // Lets the player pass the current speed back to the demuxer that produced this source.
        internal static void ApplyPlaybackRate(MediaStreamSource source, double rate)
        {
            if (source == null)
            {
                return;
            }

            DashMediaSource owner;
            if (Owners.TryGetValue(source, out owner) && owner != null)
            {
                owner.ApplyPlaybackRate(rate);
            }
        }

        internal static async Task<MediaStreamSource> CreateAsync(
            HttpClient http,
            PlayerFormatModel video,
            PlayerFormatModel audio)
        {
            try
            {
                if (http == null || video == null || audio == null)
                {
                    return null;
                }

                // Fetch + parse both init/index segments concurrently (each is a separate
                // googlevideo host + TLS handshake, so serial setup was the slow part).
                var videoTask = DashTrack.CreateAsync(http, video, isVideo: true);
                var audioTask = DashTrack.CreateAsync(http, audio, isVideo: false);
                var videoTrack = await videoTask.ConfigureAwait(false);
                var audioTrack = await audioTask.ConfigureAwait(false);
                if (videoTrack == null || audioTrack == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Dash] Track init failed (video=" + (videoTrack != null) + ", audio=" + (audioTrack != null) + ")");
                    return null;
                }

                var videoProps = VideoEncodingProperties.CreateH264();
                if (videoTrack.Init.Width > 0) videoProps.Width = (uint)videoTrack.Init.Width;
                if (videoTrack.Init.Height > 0) videoProps.Height = (uint)videoTrack.Init.Height;
                var videoDescriptor = new VideoStreamDescriptor(videoProps);

                var audioProps = AudioEncodingProperties.CreateAac(
                    audioTrack.Init.SampleRate > 0 ? (uint)audioTrack.Init.SampleRate : 44100u,
                    audioTrack.Init.Channels > 0 ? (uint)audioTrack.Init.Channels : 2u,
                    audio.AverageBitrate > 0 ? (uint)audio.AverageBitrate : (audio.Bitrate > 0 ? (uint)audio.Bitrate : 128000u));
                ApplyAacUserData(audioProps, audioTrack.Init.CodecPrivate);
                var audioDescriptor = new AudioStreamDescriptor(audioProps);

                var owner = new DashMediaSource(videoTrack, audioTrack, videoDescriptor, audioDescriptor);

                // Only the MediaStreamSource travels to the player, but changing playback speed
                // has to reach the demuxer behind it. A weak table keeps that link without
                // extending either object's lifetime.
                Owners.Add(owner.MediaStreamSource, owner);

                return owner.MediaStreamSource;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] CreateAsync failed: " + ex.Message);
                return null;
            }
        }

        // AAC codec-private for the MF AAC decoder: MF_MT_USER_DATA = the 12-byte tail of
        // HEAACWAVEINFO (wPayloadType=0 => raw AAC as stored in MP4) followed by the
        // AudioSpecificConfig from the esds box.
        private static void ApplyAacUserData(AudioEncodingProperties props, byte[] audioSpecificConfig)
        {
            try
            {
                if (audioSpecificConfig == null || audioSpecificConfig.Length == 0)
                {
                    return;
                }

                var userData = new byte[12 + audioSpecificConfig.Length];
                // wPayloadType = 0 (raw), wAudioProfileLevelIndication = 0xFE (unspecified),
                // wStructType = 0, reserved = 0.
                userData[2] = 0xFE;
                Buffer.BlockCopy(audioSpecificConfig, 0, userData, 12, audioSpecificConfig.Length);

                // MF_MT_USER_DATA
                var mfMtUserData = new Guid("B6BC765F-4C3B-40A4-BD51-2535B66FE09D");
                props.Properties[mfMtUserData] = userData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] ApplyAacUserData failed: " + ex.Message);
            }
        }
    }

    // Wires a MediaStreamSource to two DashTracks and services sample/seek requests.
    internal sealed class DashMediaSource
    {
        private readonly DashTrack _video;
        private readonly DashTrack _audio;
        private readonly IMediaStreamDescriptor _videoDescriptor;
        private readonly IMediaStreamDescriptor _audioDescriptor;
        private volatile bool _closed;

        public MediaStreamSource MediaStreamSource { get; }

        public DashMediaSource(DashTrack video, DashTrack audio, IMediaStreamDescriptor videoDescriptor, IMediaStreamDescriptor audioDescriptor)
        {
            _video = video;
            _audio = audio;
            _videoDescriptor = videoDescriptor;
            _audioDescriptor = audioDescriptor;

            MediaStreamSource = new MediaStreamSource(videoDescriptor, audioDescriptor);
            MediaStreamSource.CanSeek = true;
            MediaStreamSource.Duration = video.TotalDuration > audio.TotalDuration ? video.TotalDuration : audio.TotalDuration;
            // Buffer a few seconds so the pipeline pulls samples well ahead of the renderer;
            // combined with the per-track prefetch this keeps the video track from starving
            // (which showed up as stutter/artifacts right after un-pausing).
            MediaStreamSource.BufferTime = TimeSpan.FromSeconds(3);
            MediaStreamSource.Starting += OnStarting;
            MediaStreamSource.SampleRequested += OnSampleRequested;
            MediaStreamSource.Closed += OnClosed;
        }

        // Measured on the device: MediaPlaybackSession.PlaybackRate is accepted for a
        // MediaStreamSource but never applied — the media clock kept advancing at exactly 1.00x
        // even at 144p, so throughput was never the limit. Widening the read-ahead therefore
        // bought nothing and only made every quality switch fetch more up front, so it is gone.
        // Speed for demuxed sources needs a different mechanism entirely.
        public void ApplyPlaybackRate(double rate)
        {
        }

        private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            try
            {
                if (_closed)
                {
                    return;
                }

                var request = args.Request;
                var start = request.StartPosition;

                // A null StartPosition means "continue from wherever the source already is"
                // (this is what the pipeline sends when playback resumes after a pause). Seeking
                // to zero here restarted the video from the beginning and fed the renderer
                // samples timestamped ~0 while it sat at the old position — visible as the
                // picture breaking up, audio restarting, and then a hard crash.
                if (!start.HasValue)
                {
                    return;
                }

                var actual = _video.SeekTo(start.Value);
                _audio.SeekTo(start.Value);
                request.SetActualStartPosition(actual);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] OnStarting failed: " + ex.Message);
            }
        }

        private async void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_closed)
            {
                return;
            }

            var track = ReferenceEquals(args.Request.StreamDescriptor, _videoDescriptor) ? _video : _audio;

            // GetDeferral / Sample / Complete must be resilient to the source being torn down
            // mid-flight (quality switch, navigation, rotation). Any exception escaping this
            // async void handler would crash the process, so everything is guarded.
            MediaStreamSourceSampleRequestDeferral deferral = null;
            try
            {
                deferral = args.Request.GetDeferral();
                var sample = await track.GetNextSampleAsync().ConfigureAwait(false);
                if (!_closed && sample != null)
                {
                    args.Request.Sample = sample;
                }
                else if (!_closed)
                {
                    // Leaving Sample null is the only way to signal end-of-stream, so the player
                    // shows the replay button (and a playlist advances) either way. Say whether
                    // this was the real end of the media or a failure that looked like one.
                    System.Diagnostics.Debug.WriteLine("[Dash] EOS on "
                        + (track == _video ? "video" : "audio")
                        + " track: " + track.EndOfStreamReason);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] SampleRequested failed ("
                    + (track == _video ? "video" : "audio") + "), ending stream: " + ex.Message);
            }
            finally
            {
                try { if (deferral != null) deferral.Complete(); } catch { }
            }
        }

        private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
        {
            _closed = true;
            // Stop any fetch retries still running for these tracks.
            try { if (_video != null) _video.Abandon(); } catch { }
            try { if (_audio != null) _audio.Abandon(); } catch { }
            try
            {
                sender.Starting -= OnStarting;
                sender.SampleRequested -= OnSampleRequested;
                sender.Closed -= OnClosed;
            }
            catch { }
        }
    }

    // Codec + track parameters extracted from the init (moov) segment.
    internal sealed class DashTrackInit
    {
        public uint Timescale = 1;
        public int Width;
        public int Height;
        public int SampleRate;
        public int Channels;
        public int NalLengthSize = 4;          // video: AVCC NALU length prefix size
        public List<byte[]> Sps = new List<byte[]>();
        public List<byte[]> Pps = new List<byte[]>();
        public byte[] CodecPrivate;            // audio: AudioSpecificConfig
        // trex defaults (fragment sample defaults when trun/tfhd omit them)
        public uint DefaultSampleDuration;
        public uint DefaultSampleSize;
        public uint DefaultSampleFlags;
    }

    internal sealed class DashFragment
    {
        public long Offset;      // absolute byte offset in the stream
        public long Size;        // bytes (moof+mdat)
        public long StartTicks;  // presentation start time of this fragment (100ns)
    }

    // A sample described in place inside the fragment buffer. Keeping references instead of
    // materialising every MediaStreamSample up front matters a lot on Windows 10 Mobile: a
    // 1080p60 fragment holds hundreds of frames, and copying them all at once (plus the
    // Annex-B conversion buffers) blew the app's memory budget — playback crawled at a few
    // frames per second under GC pressure and then the process died.
    internal sealed class DashSampleRef
    {
        public int Offset;
        public int Size;
        public long PtsTicks;
        public long DtsTicks;
        public long DurTicks;
        public bool KeyFrame;
    }

    // One elementary stream (video or audio): fetches and parses fMP4 fragments on demand.
    internal sealed class DashTrack
    {
        private readonly HttpClient _http;
        private readonly string _url;
        private readonly bool _isVideo;
        private readonly List<DashFragment> _fragments;
        // Exactly one fragment is held in memory at a time; samples are cut out of it on demand.
        private byte[] _fragmentBytes;
        private List<DashSampleRef> _fragmentSamples;
        private int _sampleIndex;
        private int _fragmentIndex;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        // One-fragment lookahead: the next fragment is fetched in the background while the
        // current one is still being served, so the decoder never waits on the network.
        private readonly List<KeyValuePair<int, Task<byte[]>>> _prefetch =
            new List<KeyValuePair<int, Task<byte[]>>>();
        private int _lookahead = 1;
        // Set when the owning source is closed (quality switch, navigation), so in-flight fetch
        // retries give up instead of working on for a track nobody is listening to any more.
        private volatile bool _abandoned;

        public void Abandon()
        {
            _abandoned = true;
        }

        public DashTrackInit Init { get; }
        public TimeSpan TotalDuration { get; }

        private DashTrack(HttpClient http, string url, bool isVideo, DashTrackInit init, List<DashFragment> fragments, TimeSpan total)
        {
            _http = http;
            _url = url;
            _isVideo = isVideo;
            Init = init;
            _fragments = fragments;
            TotalDuration = total;
        }

        public static async Task<DashTrack> CreateAsync(HttpClient http, PlayerFormatModel format, bool isVideo)
        {
            long initStart = ParseLong(format.InitRangeStart);
            long initEnd = ParseLong(format.InitRangeEnd);
            long indexStart = ParseLong(format.IndexRangeStart);
            long indexEnd = ParseLong(format.IndexRangeEnd);
            if (string.IsNullOrWhiteSpace(format.Url) || initEnd <= 0 || indexEnd <= 0 || indexEnd < indexStart)
            {
                return null;
            }

            // init + sidx are contiguous at the head of the file; fetch them in one range.
            var head = await FetchRangeAsync(http, format.Url, 0, indexEnd).ConfigureAwait(false);
            if (head == null || head.Length <= indexEnd)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] head fetch too short (" + (head == null ? -1 : head.Length) + " <= " + indexEnd + ")");
                return null;
            }

            var init = Mp4.ParseInit(head, (int)initStart, (int)(initEnd - initStart + 1), isVideo);
            if (init == null)
            {
                return null;
            }

            long anchor = indexEnd + 1;
            TimeSpan totalDuration;
            var fragments = Mp4.ParseSidx(head, (int)indexStart, (int)(indexEnd - indexStart + 1), anchor, init.Timescale, out totalDuration);
            if (fragments == null || fragments.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] sidx produced no fragments");
                return null;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Dash] " + (isVideo ? "video" : "audio") + " init ok: timescale=" + init.Timescale
                + (isVideo ? (", " + init.Width + "x" + init.Height + ", sps=" + init.Sps.Count + ", pps=" + init.Pps.Count) : (", " + init.SampleRate + "Hz ch=" + init.Channels + ", asc=" + (init.CodecPrivate == null ? 0 : init.CodecPrivate.Length)))
                + ", fragments=" + fragments.Count);

            return new DashTrack(http, format.Url, isVideo, init, fragments, totalDuration);
        }

        public TimeSpan SeekTo(TimeSpan position)
        {
            _gate.Wait();
            try
            {
                _fragmentBytes = null;
                _fragmentSamples = null;
                _sampleIndex = 0;
                // Any in-flight prefetch belongs to the old position — drop it (the task itself
                // is exception-safe and its result is simply discarded).
                _prefetch.Clear();
                long ticks = position.Ticks;
                int idx = 0;
                for (int i = 0; i < _fragments.Count; i++)
                {
                    if (_fragments[i].StartTicks <= ticks)
                    {
                        idx = i;
                    }
                    else
                    {
                        break;
                    }
                }

                _fragmentIndex = idx;
                return TimeSpan.FromTicks(_fragments[idx].StartTicks);
            }
            finally
            {
                _gate.Release();
            }
        }

        // Why this track last reported end-of-stream: "completed" for the real end of the media,
        // or a description of the failure that was forced to look like one.
        public string EndOfStreamReason { get; private set; }

        public async Task<MediaStreamSample> GetNextSampleAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (true)
                {
                    while (_fragmentSamples == null || _sampleIndex >= _fragmentSamples.Count)
                    {
                        if (_fragmentIndex >= _fragments.Count)
                        {
                            EndOfStreamReason = "completed";
                            return null; // genuine end of stream
                        }

                        var fragment = _fragments[_fragmentIndex];

                        // Use the already in-flight prefetch when it is the fragment we need.
                        var prefetched = TakePrefetch(_fragmentIndex);
                        var bytes = prefetched != null
                            ? await prefetched.ConfigureAwait(false)
                            : await SafeFetchAsync(fragment).ConfigureAwait(false);

                        // A prefetch started earlier may have failed under a condition that has
                        // since cleared, so give the fragment its own retries before giving up.
                        if ((bytes == null || bytes.Length == 0) && prefetched != null && !_abandoned)
                        {
                            bytes = await SafeFetchAsync(fragment).ConfigureAwait(false);
                        }

                        // Only consume the fragment once its bytes are in hand — advancing before
                        // the check skipped the fragment that failed and made retrying pointless.
                        _fragmentIndex++;

                        if (bytes == null || bytes.Length == 0)
                        {
                            EndOfStreamReason = "fragment fetch failed at offset " + fragment.Offset
                                + " (" + (_fragmentIndex - 1) + " of " + _fragments.Count + ")";
                            System.Diagnostics.Debug.WriteLine("[Dash] " + EndOfStreamReason);
                            return null;
                        }

                        // Release the previous fragment before taking the new one.
                        _fragmentBytes = null;
                        _fragmentSamples = Mp4.ParseFragment(bytes, Init, _isVideo);
                        _fragmentBytes = bytes;
                        _sampleIndex = 0;

                        // Start pulling the following fragment while these samples are consumed.
                        StartPrefetch();
                    }

                    // Skip an unusable sample rather than ending the video on it; the loop moves
                    // on to the next sample, and to the next fragment once this one is exhausted.
                    var sample = BuildSample(_fragmentSamples[_sampleIndex++]);
                    if (sample != null)
                    {
                        return sample;
                    }
                }
            }
            finally
            {
                _gate.Release();
            }
        }

        // Cuts one sample out of the fragment buffer. Only this sample's bytes are copied, so
        // memory stays flat regardless of the fragment's frame count / bitrate.
        private MediaStreamSample BuildSample(DashSampleRef reference)
        {
            if (reference == null || _fragmentBytes == null)
            {
                return null;
            }

            if (reference.Offset < 0 || reference.Size <= 0 || reference.Offset + reference.Size > _fragmentBytes.Length)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] sample out of range: off=" + reference.Offset + " size=" + reference.Size + " buf=" + _fragmentBytes.Length);
                return null;
            }

            byte[] payload;
            if (_isVideo)
            {
                payload = Mp4.ToAnnexB(_fragmentBytes, reference.Offset, reference.Size, Init, reference.KeyFrame);
            }
            else
            {
                payload = new byte[reference.Size];
                Buffer.BlockCopy(_fragmentBytes, reference.Offset, payload, 0, reference.Size);
            }

            var sample = MediaStreamSample.CreateFromBuffer(
                payload.AsBuffer(),
                TimeSpan.FromTicks(reference.PtsTicks < 0 ? 0 : reference.PtsTicks));
            sample.Duration = TimeSpan.FromTicks(reference.DurTicks);
            sample.DecodeTimestamp = TimeSpan.FromTicks(reference.DtsTicks < 0 ? 0 : reference.DtsTicks);
            sample.KeyFrame = reference.KeyFrame;
            return sample;
        }

        private void StartPrefetch()
        {
            // Drop entries the playhead has already passed.
            for (int i = _prefetch.Count - 1; i >= 0; i--)
            {
                if (_prefetch[i].Key < _fragmentIndex)
                {
                    _prefetch.RemoveAt(i);
                }
            }

            var next = _fragmentIndex;
            while (_prefetch.Count < _lookahead && next < _fragments.Count)
            {
                var alreadyQueued = false;
                for (int i = 0; i < _prefetch.Count; i++)
                {
                    if (_prefetch[i].Key == next)
                    {
                        alreadyQueued = true;
                        break;
                    }
                }

                if (!alreadyQueued)
                {
                    _prefetch.Add(new KeyValuePair<int, Task<byte[]>>(next, SafeFetchAsync(_fragments[next])));
                }

                next++;
            }
        }

        // Hands over the in-flight fetch for a fragment, if one was started.
        private Task<byte[]> TakePrefetch(int index)
        {
            for (int i = 0; i < _prefetch.Count; i++)
            {
                if (_prefetch[i].Key == index)
                {
                    var task = _prefetch[i].Value;
                    _prefetch.RemoveAt(i);
                    return task;
                }
            }

            return null;
        }

        // Never lets a background fetch fault escape as an unobserved task exception.
        // A failed fragment fetch used to be indistinguishable from the end of the video: the
        // sample request returned null, and null is exactly how MediaStreamSource is told
        // "end of stream" — so a dropped connection ended playback (replay button, and the next
        // playlist item started) instead of stalling and recovering. Transient failures are
        // retried here so a blip costs a moment of buffering rather than the rest of the video.
        private const int FragmentFetchAttempts = 3;

        private async Task<byte[]> SafeFetchAsync(DashFragment frag)
        {
            for (int attempt = 1; attempt <= FragmentFetchAttempts; attempt++)
            {
                try
                {
                    var bytes = await FetchRangeAsync(_http, _url, frag.Offset, frag.Offset + frag.Size - 1)
                        .ConfigureAwait(false);
                    if (bytes != null && bytes.Length > 0)
                    {
                        if (attempt > 1)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[Dash] fragment at " + frag.Offset + " recovered on attempt " + attempt);
                        }
                        return bytes;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Dash] fragment fetch error at " + frag.Offset
                        + " (attempt " + attempt + "): " + ex.Message);
                }

                if (_abandoned)
                {
                    return null;
                }

                if (attempt < FragmentFetchAttempts)
                {
                    await Task.Delay(200 * attempt).ConfigureAwait(false);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "[Dash] fragment at " + frag.Offset + " failed after " + FragmentFetchAttempts + " attempts");
            return null;
        }

        private static long ParseLong(string s)
        {
            long v;
            return long.TryParse(s, out v) ? v : 0;
        }

        private static async Task<byte[]> FetchRangeAsync(HttpClient http, string url, long start, long end)
        {
            using (var req = new HttpRequestMessage(HttpMethod.Get, url))
            {
                req.Headers.Range = new RangeHeaderValue(start, end);
                using (var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead).ConfigureAwait(false))
                {
                    if (!resp.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Dash] range " + start + "-" + end + " => " + (int)resp.StatusCode);
                        return null;
                    }

                    return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
        }
    }

    // Minimal big-endian ISO-BMFF (MP4) box reader — only the boxes YouTube's fMP4 uses.
    internal static class Mp4
    {
        private static uint U16(byte[] b, int p) { return (uint)((b[p] << 8) | b[p + 1]); }
        private static uint U24(byte[] b, int p) { return (uint)((b[p] << 16) | (b[p + 1] << 8) | b[p + 2]); }
        private static uint U32(byte[] b, int p) { return ((uint)b[p] << 24) | ((uint)b[p + 1] << 16) | ((uint)b[p + 2] << 8) | b[p + 3]; }
        private static ulong U64(byte[] b, int p) { return ((ulong)U32(b, p) << 32) | U32(b, p + 4); }
        private static string Type(byte[] b, int p) { return "" + (char)b[p] + (char)b[p + 1] + (char)b[p + 2] + (char)b[p + 3]; }

        private static long ToTicks(long value, uint timescale)
        {
            if (timescale == 0) timescale = 1;
            return (long)((decimal)value * 10000000m / timescale);
        }

        // Iterate direct child boxes in region [start, start+len). Calls visitor(type, payloadStart, payloadLen).
        private static void ForEachBox(byte[] b, int start, int len, Action<string, int, int> visitor)
        {
            int pos = start;
            int end = start + len;
            while (pos + 8 <= end)
            {
                long size = U32(b, pos);
                int header = 8;
                if (size == 1)
                {
                    if (pos + 16 > end) break;
                    size = (long)U64(b, pos + 8);
                    header = 16;
                }
                else if (size == 0)
                {
                    size = end - pos;
                }

                if (size < header || pos + size > end)
                {
                    break;
                }

                string type = Type(b, pos + 4);
                visitor(type, pos + header, (int)(size - header));
                pos += (int)size;
            }
        }

        public static DashTrackInit ParseInit(byte[] b, int start, int len, bool isVideo)
        {
            try
            {
                var init = new DashTrackInit();
                ForEachBox(b, start, len, (type, ps, pl) =>
                {
                    if (type == "moov")
                    {
                        ForEachBox(b, ps, pl, (t2, ps2, pl2) =>
                        {
                            if (t2 == "trak")
                            {
                                ParseTrak(b, ps2, pl2, isVideo, init);
                            }
                            else if (t2 == "mvex")
                            {
                                ForEachBox(b, ps2, pl2, (t3, ps3, pl3) =>
                                {
                                    if (t3 == "trex" && pl3 >= 24)
                                    {
                                        init.DefaultSampleDuration = U32(b, ps3 + 12);
                                        init.DefaultSampleSize = U32(b, ps3 + 16);
                                        init.DefaultSampleFlags = U32(b, ps3 + 20);
                                    }
                                });
                            }
                        });
                    }
                });

                if (init.Timescale == 0) init.Timescale = 1;

                // Audio config can hide behind an unusual sample-entry / 'wave' layout — do a
                // whole-init brute-force search before giving up.
                if (!isVideo && (init.CodecPrivate == null || init.CodecPrivate.Length == 0))
                {
                    ScanForEsds(b, start, len, init);
                }

                if (isVideo && init.Sps.Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[Dash] init: no SPS found");
                    return null;
                }
                if (!isVideo && (init.CodecPrivate == null || init.CodecPrivate.Length == 0))
                {
                    System.Diagnostics.Debug.WriteLine("[Dash] init: no AudioSpecificConfig found");
                    return null;
                }
                return init;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] ParseInit failed: " + ex.Message);
                return null;
            }
        }

        private static void ParseTrak(byte[] b, int start, int len, bool isVideo, DashTrackInit init)
        {
            ForEachBox(b, start, len, (type, ps, pl) =>
            {
                if (type == "mdia")
                {
                    ForEachBox(b, ps, pl, (t2, ps2, pl2) =>
                    {
                        if (t2 == "mdhd")
                        {
                            int v = b[ps2];
                            init.Timescale = v == 1 ? U32(b, ps2 + 20) : U32(b, ps2 + 12);
                        }
                        else if (t2 == "minf")
                        {
                            ForEachBox(b, ps2, pl2, (t3, ps3, pl3) =>
                            {
                                if (t3 == "stbl")
                                {
                                    ForEachBox(b, ps3, pl3, (t4, ps4, pl4) =>
                                    {
                                        if (t4 == "stsd")
                                        {
                                            // stsd: fullbox(4) + entry_count(4) then sample entries
                                            ForEachBox(b, ps4 + 8, pl4 - 8, (t5, ps5, pl5) =>
                                            {
                                                if (isVideo && (t5 == "avc1" || t5 == "avc3"))
                                                {
                                                    ParseAvcSampleEntry(b, ps5, pl5, init);
                                                }
                                                else if (!isVideo && (t5 == "mp4a"))
                                                {
                                                    ParseAacSampleEntry(b, ps5, pl5, init);
                                                }
                                            });
                                        }
                                    });
                                }
                            });
                        }
                    });
                }
            });
        }

        private static void ParseAvcSampleEntry(byte[] b, int start, int len, DashTrackInit init)
        {
            // VisualSampleEntry: width@24, height@26; child boxes start @78
            if (len >= 28)
            {
                init.Width = (int)U16(b, start + 24);
                init.Height = (int)U16(b, start + 26);
            }
            ForEachBox(b, start + 78, len - 78, (type, ps, pl) =>
            {
                if (type == "avcC")
                {
                    ParseAvcC(b, ps, pl, init);
                }
            });
        }

        private static void ParseAvcC(byte[] b, int start, int len, DashTrackInit init)
        {
            // AVCDecoderConfigurationRecord
            int p = start;
            // configurationVersion(1) profile(1) compat(1) level(1)
            init.NalLengthSize = (b[p + 4] & 0x03) + 1;
            int numSps = b[p + 5] & 0x1F;
            p += 6;
            for (int i = 0; i < numSps; i++)
            {
                int nalLen = (int)U16(b, p); p += 2;
                var sps = new byte[nalLen];
                Buffer.BlockCopy(b, p, sps, 0, nalLen); p += nalLen;
                init.Sps.Add(sps);
            }
            int numPps = b[p]; p += 1;
            for (int i = 0; i < numPps; i++)
            {
                int nalLen = (int)U16(b, p); p += 2;
                var pps = new byte[nalLen];
                Buffer.BlockCopy(b, p, pps, 0, nalLen); p += nalLen;
                init.Pps.Add(pps);
            }
        }

        private static void ParseAacSampleEntry(byte[] b, int start, int len, DashTrackInit init)
        {
            // AudioSampleEntry: channelcount@16, samplesize@18, samplerate(16.16)@24; child @28
            if (len >= 28)
            {
                init.Channels = (int)U16(b, start + 16);
                init.SampleRate = (int)U16(b, start + 24); // upper 16 bits of 16.16 fixed point
            }
            ForEachBox(b, start + 28, len - 28, (type, ps, pl) =>
            {
                if (type == "esds")
                {
                    ParseEsdsDescriptors(b, ps + 4, ps + pl, init); // skip fullbox header
                }
                else if (type == "wave")
                {
                    // Some encoders wrap esds inside a QuickTime 'wave' atom.
                    ForEachBox(b, ps, pl, (t2, ps2, pl2) =>
                    {
                        if (t2 == "esds")
                        {
                            ParseEsdsDescriptors(b, ps2 + 4, ps2 + pl2, init);
                        }
                    });
                }
            });
        }

        // Walk the MPEG-4 descriptor chain ES_Descriptor(0x03) -> DecoderConfigDescriptor(0x04)
        // -> DecoderSpecificInfo(0x05) and copy the AudioSpecificConfig. DescriptorLength
        // advances p past the length bytes (by ref); we must NOT add its value to p.
        private static bool ParseEsdsDescriptors(byte[] b, int p, int end, DashTrackInit init)
        {
            try
            {
                if (p < end && b[p] == 0x03)
                {
                    p++;
                    DescriptorLength(b, ref p);
                    p += 2;                              // ES_ID
                    if (p >= end) return false;
                    byte flags = b[p++];
                    if ((flags & 0x80) != 0) p += 2;                          // streamDependenceFlag
                    if ((flags & 0x40) != 0) { if (p >= end) return false; p += 1 + b[p]; } // URL_Flag
                    if ((flags & 0x20) != 0) p += 2;                          // OCRstreamFlag
                }

                if (p < end && b[p] == 0x04)
                {
                    p++;
                    DescriptorLength(b, ref p);
                    p += 13;                             // objType+streamType+bufferSize(3)+max(4)+avg(4)
                }

                if (p < end && b[p] == 0x05)
                {
                    p++;
                    int ascLen = DescriptorLength(b, ref p);
                    if (ascLen > 0 && p + ascLen <= end)
                    {
                        init.CodecPrivate = new byte[ascLen];
                        Buffer.BlockCopy(b, p, init.CodecPrivate, 0, ascLen);
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        // Robust fallback: locate the 'esds' box anywhere in the init and parse it, then the
        // raw 0x05 tag as a last resort. Independent of the exact sample-entry layout.
        private static void ScanForEsds(byte[] b, int start, int len, DashTrackInit init)
        {
            int end = start + len;
            for (int i = start; i + 8 <= end; i++)
            {
                if (b[i] == (byte)'e' && b[i + 1] == (byte)'s' && b[i + 2] == (byte)'d' && b[i + 3] == (byte)'s')
                {
                    if (ParseEsdsDescriptors(b, i + 8, end, init)) return; // header(4)+fullbox(4)
                    if (ParseEsdsDescriptors(b, i + 4, end, init)) return;
                }
            }

            TryFindDecSpecific(b, start, len, init);
        }

        // Fallback: scan for the 0x05 DecoderSpecificInfo tag if the descriptor layout differs.
        private static void TryFindDecSpecific(byte[] b, int start, int len, DashTrackInit init)
        {
            int end = start + len;
            for (int p = start; p < end - 1; p++)
            {
                if (b[p] == 0x05)
                {
                    int q = p + 1;
                    int ascLen = DescriptorLength(b, ref q);
                    if (ascLen > 0 && ascLen <= 8 && q + ascLen <= end)
                    {
                        init.CodecPrivate = new byte[ascLen];
                        Buffer.BlockCopy(b, q, init.CodecPrivate, 0, ascLen);
                        return;
                    }
                }
            }
        }

        private static int DescriptorLength(byte[] b, ref int p)
        {
            int len = 0;
            for (int i = 0; i < 4; i++)
            {
                byte x = b[p++];
                len = (len << 7) | (x & 0x7F);
                if ((x & 0x80) == 0) break;
            }
            return len;
        }

        public static List<DashFragment> ParseSidx(byte[] b, int start, int len, long anchor, uint fallbackTimescale, out TimeSpan total)
        {
            total = TimeSpan.Zero;
            var frags = new List<DashFragment>();
            int sidxPayload = -1;
            int sidxLen = 0;
            ForEachBox(b, start, len, (type, ps, pl) =>
            {
                if (type == "sidx" && sidxPayload < 0)
                {
                    sidxPayload = ps;
                    sidxLen = pl;
                }
            });
            if (sidxPayload < 0)
            {
                return null;
            }

            int p = sidxPayload;
            int version = b[p];
            p += 4; // version+flags
            p += 4; // reference_ID
            uint timescale = U32(b, p); p += 4;
            if (timescale == 0) timescale = fallbackTimescale;
            long firstOffset;
            if (version == 0)
            {
                p += 4; // earliest_presentation_time
                firstOffset = U32(b, p); p += 4;
            }
            else
            {
                p += 8; // earliest_presentation_time
                firstOffset = (long)U64(b, p); p += 8;
            }
            p += 2; // reserved
            int refCount = (int)U16(b, p); p += 2;

            long offset = anchor + firstOffset;
            long cumDuration = 0;
            for (int i = 0; i < refCount; i++)
            {
                uint w0 = U32(b, p); p += 4;
                uint subDuration = U32(b, p); p += 4;
                p += 4; // SAP
                bool referenceIsSidx = (w0 & 0x80000000) != 0;
                long refSize = w0 & 0x7FFFFFFF;
                if (referenceIsSidx)
                {
                    // Nested sidx is not expected for YouTube VOD; skip its bytes.
                    offset += refSize;
                    continue;
                }

                frags.Add(new DashFragment
                {
                    Offset = offset,
                    Size = refSize,
                    StartTicks = ToTicks(cumDuration, timescale)
                });
                offset += refSize;
                cumDuration += subDuration;
            }

            total = TimeSpan.FromTicks(ToTicks(cumDuration, timescale));
            return frags;
        }

        public static List<DashSampleRef> ParseFragment(byte[] b, DashTrackInit init, bool isVideo)
        {
            var result = new List<DashSampleRef>();
            try
            {
                ForEachBox(b, 0, b.Length, (type, ps, pl) =>
                {
                    if (type == "moof")
                    {
                        ParseMoof(b, ps, pl, init, isVideo, result);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Dash] ParseFragment failed: " + ex.Message);
            }
            return result;
        }

        private static void ParseMoof(byte[] b, int start, int len, DashTrackInit init, bool isVideo, List<DashSampleRef> outSamples)
        {
            // moof box begins 8 bytes before its payload (size+type). data_offset in trun is
            // relative to the first byte of the moof box.
            int moofBoxStart = start - 8;

            ForEachBox(b, start, len, (type, ps, pl) =>
            {
                if (type != "traf") return;

                uint tfhdFlags = 0;
                uint tfhdDefaultDuration = 0, tfhdDefaultSize = 0, tfhdDefaultFlags = 0;
                bool hasTfhdDuration = false, hasTfhdSize = false, hasTfhdFlags = false;
                ulong baseMediaDecodeTime = 0;

                // First pass: tfhd + tfdt
                ForEachBox(b, ps, pl, (t2, ps2, pl2) =>
                {
                    if (t2 == "tfhd")
                    {
                        tfhdFlags = U24(b, ps2 + 1);
                        int q = ps2 + 4 + 4; // fullbox + track_ID
                        if ((tfhdFlags & 0x000001) != 0) q += 8; // base_data_offset
                        if ((tfhdFlags & 0x000002) != 0) q += 4; // sample_description_index
                        if ((tfhdFlags & 0x000008) != 0) { tfhdDefaultDuration = U32(b, q); q += 4; hasTfhdDuration = true; }
                        if ((tfhdFlags & 0x000010) != 0) { tfhdDefaultSize = U32(b, q); q += 4; hasTfhdSize = true; }
                        if ((tfhdFlags & 0x000020) != 0) { tfhdDefaultFlags = U32(b, q); q += 4; hasTfhdFlags = true; }
                    }
                    else if (t2 == "tfdt")
                    {
                        int v = b[ps2];
                        baseMediaDecodeTime = v == 1 ? U64(b, ps2 + 4) : U32(b, ps2 + 4);
                    }
                });

                uint defDuration = hasTfhdDuration ? tfhdDefaultDuration : init.DefaultSampleDuration;
                uint defSize = hasTfhdSize ? tfhdDefaultSize : init.DefaultSampleSize;
                uint defFlags = hasTfhdFlags ? tfhdDefaultFlags : init.DefaultSampleFlags;
                ulong runningDts = baseMediaDecodeTime;

                // Second pass: trun(s)
                ForEachBox(b, ps, pl, (t2, ps2, pl2) =>
                {
                    if (t2 != "trun") return;

                    int v = b[ps2];
                    uint flags = U24(b, ps2 + 1);
                    int q = ps2 + 4;
                    uint sampleCount = U32(b, q); q += 4;
                    int dataOffset = 0;
                    if ((flags & 0x000001) != 0) { dataOffset = (int)U32(b, q); q += 4; }
                    uint firstSampleFlags = 0;
                    bool hasFirstSampleFlags = (flags & 0x000004) != 0;
                    if (hasFirstSampleFlags) { firstSampleFlags = U32(b, q); q += 4; }

                    bool hasDuration = (flags & 0x000100) != 0;
                    bool hasSize = (flags & 0x000200) != 0;
                    bool hasFlags = (flags & 0x000400) != 0;
                    bool hasCto = (flags & 0x000800) != 0;

                    int dataPos = moofBoxStart + dataOffset;
                    for (uint i = 0; i < sampleCount; i++)
                    {
                        uint sDuration = hasDuration ? U32(b, q) : defDuration; if (hasDuration) q += 4;
                        uint sSize = hasSize ? U32(b, q) : defSize; if (hasSize) q += 4;
                        uint sFlags = hasFlags ? U32(b, q) : defFlags; if (hasFlags) q += 4;
                        int cto = 0;
                        if (hasCto)
                        {
                            cto = v == 0 ? (int)U32(b, q) : (int)U32(b, q); // v1 is signed; U32 cast to int handles it
                            q += 4;
                        }

                        uint effectiveFlags = (i == 0 && hasFirstSampleFlags) ? firstSampleFlags : sFlags;
                        bool keyFrame = !isVideo || (effectiveFlags & 0x00010000) == 0;

                        if (dataPos < 0 || dataPos + (int)sSize > b.Length)
                        {
                            System.Diagnostics.Debug.WriteLine("[Dash] sample out of range: pos=" + dataPos + " size=" + sSize + " buf=" + b.Length);
                            return;
                        }

                        // Describe the sample in place — no copying here.
                        var reference = new DashSampleRef
                        {
                            Offset = dataPos,
                            Size = (int)sSize,
                            PtsTicks = ToTicks((long)runningDts + cto, init.Timescale),
                            DtsTicks = ToTicks((long)runningDts, init.Timescale),
                            DurTicks = ToTicks(sDuration, init.Timescale),
                            KeyFrame = keyFrame
                        };

                        dataPos += (int)sSize;
                        runningDts += sDuration;
                        outSamples.Add(reference);
                    }
                });
            });
        }

        private static readonly byte[] StartCode = { 0x00, 0x00, 0x00, 0x01 };

        // Convert AVCC (length-prefixed NALUs) to Annex-B (start-code delimited), prepending
        // SPS/PPS to key frames so the MF H.264 decoder configures in-band. Reads the sample in
        // place out of the fragment buffer and writes exactly one output array.
        public static byte[] ToAnnexB(byte[] src, int offset, int length, DashTrackInit init, bool keyFrame)
        {
            int n = init.NalLengthSize;
            int end = offset + length;

            // Size the output exactly: each NALU's length prefix becomes a 4-byte start code.
            int outSize = 0;
            if (keyFrame)
            {
                for (int i = 0; i < init.Sps.Count; i++) outSize += StartCode.Length + init.Sps[i].Length;
                for (int i = 0; i < init.Pps.Count; i++) outSize += StartCode.Length + init.Pps[i].Length;
            }

            int p = offset;
            while (p + n <= end)
            {
                long nalLen = 0;
                for (int i = 0; i < n; i++) { nalLen = (nalLen << 8) | src[p + i]; }
                p += n;
                if (nalLen <= 0 || p + nalLen > end) break;
                outSize += StartCode.Length + (int)nalLen;
                p += (int)nalLen;
            }

            var outBuf = new byte[outSize];
            int w = 0;

            if (keyFrame)
            {
                for (int i = 0; i < init.Sps.Count; i++)
                {
                    Buffer.BlockCopy(StartCode, 0, outBuf, w, StartCode.Length); w += StartCode.Length;
                    Buffer.BlockCopy(init.Sps[i], 0, outBuf, w, init.Sps[i].Length); w += init.Sps[i].Length;
                }
                for (int i = 0; i < init.Pps.Count; i++)
                {
                    Buffer.BlockCopy(StartCode, 0, outBuf, w, StartCode.Length); w += StartCode.Length;
                    Buffer.BlockCopy(init.Pps[i], 0, outBuf, w, init.Pps[i].Length); w += init.Pps[i].Length;
                }
            }

            p = offset;
            while (p + n <= end && w < outSize)
            {
                long nalLen = 0;
                for (int i = 0; i < n; i++) { nalLen = (nalLen << 8) | src[p + i]; }
                p += n;
                if (nalLen <= 0 || p + nalLen > end) break;
                Buffer.BlockCopy(StartCode, 0, outBuf, w, StartCode.Length); w += StartCode.Length;
                Buffer.BlockCopy(src, p, outBuf, w, (int)nalLen); w += (int)nalLen;
                p += (int)nalLen;
            }

            return outBuf;
        }
    }
}
