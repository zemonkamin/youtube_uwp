using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using YouTube.Innertube;

namespace YouTube
{
    internal sealed class LiveMediaSource : IDisposable
    {
        private sealed class Fragment
        {
            internal long Sequence, SourceStartMs, SourceDurationMs, StartMs, EndMs, TimestampOffsetTicks;
            internal int Itag, MappedSamples;
            internal byte[] Bytes;
            internal DashTrackInit Init;
            internal List<DashSampleRef> Samples;
        }

        private sealed class Track
        {
            internal readonly List<Fragment> Fragments = new List<Fragment>();
            internal readonly List<Fragment> Pending = new List<Fragment>();
            internal readonly SemaphoreSlim Reading = new SemaphoreSlim(1, 1);
            internal Fragment Current;
            internal int Index;
            internal long LastEndMs = -1;
            internal int Itag;
            internal bool Discontinuous = true;
        }

        private readonly object _gate = new object();
        private readonly Track _video = new Track(), _audio = new Track();
        private readonly Dictionary<int, DashTrackInit> _inits = new Dictionary<int, DashTrackInit>();
        private readonly CancellationTokenSource _cancel = new CancellationTokenSource();
        private readonly TaskCompletionSource<bool> _stopped = new TaskCompletionSource<bool>();
        private readonly Func<string, Task<JsonObject>> _refresh;
        private readonly LiveSabrSession _sabr;
        private readonly LiveTimeline _timeline = new LiveTimeline();
        private TaskCompletionSource<bool> _changed = new TaskCompletionSource<bool>();
        private VideoStreamDescriptor _videoDescriptor;
        private DateTime _startWaitAt = DateTime.MinValue;
        private bool _ended;
        private bool _started;
        private long _initialStartMs;
        private int _deliveryGeneration;
        private volatile bool _closed;
        private Task _pump;
        internal MediaStreamSource Source;
        internal bool Ended { get { return _ended; } }
        internal double BufferedStart { get { lock (_gate) return _video.Fragments.Count == 0 ? 0 : _video.Fragments[0].StartMs / 1000.0; } }
        internal double LiveEdge { get { lock (_gate) return _video.Fragments.Count == 0 ? 0 : _video.Fragments.Last().EndMs / 1000.0; } }
        internal double LivePosition
        {
            get
            {
                lock (_gate)
                {
                    if (_video.Fragments.Count == 0 || _audio.Fragments.Count == 0) return BufferedStart;
                    var audioStart = _audio.Fragments[0].StartMs;
                    var audioEnd = _audio.Fragments.Last().EndMs;
                    // Start on the newest complete video piece with matching audio.
                    // Its first sample is decodable; the exclusive buffer end has no sample.
                    var latest = _video.Fragments.LastOrDefault(f => f.StartMs >= audioStart - 50 && f.EndMs <= audioEnd + 100)
                        ?? _video.Fragments.LastOrDefault(f => f.StartMs < audioEnd && f.EndMs > audioStart);
                    return latest == null ? BufferedStart : latest.StartMs / 1000.0;
                }
            }
        }

        private static LiveTimeline.Piece TimelinePiece(Fragment fragment)
        { return new LiveTimeline.Piece { Sequence = fragment.Sequence, SourceStartMs = fragment.SourceStartMs, DurationMs = fragment.SourceDurationMs }; }

        private LiveMediaSource(string videoId, JsonObject root, int height, string poToken,
            DateTime watchedFrom, Func<string, Task<JsonObject>> refresh)
        {
            _refresh = refresh;
            _sabr = new LiveSabrSession(videoId, root, height, poToken, watchedFrom);
            _sabr.Received = Receive;
        }

        internal static async Task<LiveMediaSource> CreateAsync(string videoId, JsonObject root, int height,
            string poToken, DateTime watchedFrom, Func<string, Task<JsonObject>> refresh, CancellationToken cancellation,
            Action<int> initializationFailed = null)
        {
            LiveMediaSource owner = null;
            try
            {
                owner = new LiveMediaSource(videoId, root, height, poToken, watchedFrom, refresh);
                using (var registration = cancellation.Register(owner.Dispose))
                {
                var begun = DateTime.UtcNow;
                var restarts = 0;
                for (var attempt = 0; attempt < 24; attempt++)
                {
                    var remaining = Math.Max(250, 4000 - (DateTime.UtcNow - begun).TotalMilliseconds);
                    try { await owner._sabr.FetchAsync(owner._cancel.Token, attempt == 0 ? 8000 : (int)remaining).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        owner._cancel.Token.ThrowIfCancellationRequested();
                        if (owner._video.Pending.Count == 0 && owner._video.Fragments.Count == 0 && owner._sabr.HeadSeconds <= 0) throw;
                    }
                    var now = DateTime.UtcNow;
                    lock (owner._gate)
                    {
                        owner._cancel.Token.ThrowIfCancellationRequested();
                        if (!owner._timeline.Started)
                        {
                            var waited = owner._startWaitAt == DateTime.MinValue ? 0 : (now - owner._startWaitAt).TotalSeconds;
                            var first = LiveTimeline.ChooseStart(owner._video.Pending.Select(TimelinePiece), waited);
                            // Evaluate the selected row, not an isolated old fragment which
                            // can arrive before the row at the current broadcast head.
                            var stale = first != null && owner._sabr.HeadSeconds > 0
                                && owner._sabr.HeadSeconds - first.SourceStartMs / 1000.0 > 180;
                            var empty = owner._video.Pending.Count == 0 && owner._sabr.HeadSeconds > 0;
                            if ((stale || empty) && restarts < 3)
                            {
                                var target = Math.Max(0, owner._sabr.HeadSeconds - (restarts == 0 ? 120 : restarts == 1 ? 30 : 0));
                                restarts++;
                                owner._video.Pending.Clear(); owner._audio.Pending.Clear(); owner._startWaitAt = DateTime.MinValue;
                                owner._sabr.Restart(target);
                                System.Diagnostics.Debug.WriteLine("[Live] Startup requested near head: " + target.ToString("F1"));
                            }
                            else if (first != null && !stale)
                            {
                                owner._timeline.Begin(first.Sequence);
                                System.Diagnostics.Debug.WriteLine("[Live] Buffered row: sequence=" + first.Sequence
                                    + ", source=" + (first.SourceStartMs / 1000.0).ToString("F1")
                                    + ", head=" + owner._sabr.HeadSeconds.ToString("F1") + ", local=10");
                            }
                        }
                        owner.PromotePending(now);
                    }
                    // Only the selected, ordered row counts toward prefill readiness.
                    if (owner._video.Fragments.Count > 0 && owner._audio.Fragments.Count > 0
                        && (owner._video.Fragments.Count >= 4 || (now - begun).TotalSeconds >= 4)) break;
                    if (owner._sabr.NeedsReload) await owner.RenewAsync().ConfigureAwait(false);
                    // Allow the six-second row wait even if the first HTTP response
                    // was held open. Do not force a start on its oldest isolated piece.
                    if ((DateTime.UtcNow - begun).TotalSeconds >= 14) break;
                    await Task.Delay((int)Math.Max(100, Math.Min(1000, owner._sabr.BackoffMs)), owner._cancel.Token).ConfigureAwait(false);
                }
                if (owner._video.Fragments.Count == 0 || owner._audio.Fragments.Count == 0)
                    throw new InvalidOperationException("Live SABR did not deliver audio and video");
                var v = owner._video.Fragments[0].Init;
                var a = owner._audio.Fragments[0].Init;
                var props = VideoEncodingProperties.CreateH264();
                props.Width = (uint)Math.Max(1, v.Width); props.Height = (uint)Math.Max(1, v.Height);
                owner._videoDescriptor = new VideoStreamDescriptor(props);
                var audioProps = AudioEncodingProperties.CreateAac((uint)Math.Max(1, a.SampleRate), (uint)Math.Max(1, a.Channels), 128000);
                DashDemuxer.ApplyAacUserData(audioProps, a.CodecPrivate);
                var audioDescriptor = new AudioStreamDescriptor(audioProps);
                owner.Source = new MediaStreamSource(owner._videoDescriptor, audioDescriptor);
                // The 14393 MediaStreamSource requires a nonzero Duration for CanSeek.
                // Keep the native horizon ahead of delivery; the UI uses the actual local
                // DVR bounds, and only a confirmed broadcast end supplies the final duration.
                owner.Source.Duration = TimeSpan.FromSeconds(owner.LiveEdge + 86400);
                owner.Source.CanSeek = true;
                owner.Source.BufferTime = TimeSpan.FromSeconds(3);
                owner.Source.Starting += owner.Starting;
                owner.Source.SampleRequested += owner.SampleRequested;
                owner.Source.Closed += owner.Closed;
                owner._pump = owner.PumpAsync();
                return owner;
                }
            }
            catch (Exception ex)
            {
                if (initializationFailed != null) initializationFailed(owner == null ? 0 : owner._sabr.LastHttpStatus);
                if (owner != null) owner.Dispose();
                System.Diagnostics.Debug.WriteLine("[Live] SABR initialization failed: " + ex.GetType().Name + " - " + ex.Message);
                return null;
            }
        }

        private void Receive(LiveSabrSession.Piece piece)
        {
            lock (_gate)
            {
                if (_closed) return;
                // Live pieces often prepend moov, even when the UMP header says media.
                // Keep each track's init: a new itag must use its own SPS/PPS and timescale.
                var parsed = Mp4.ParseInit(piece.Bytes, 0, piece.Bytes.Length, piece.Video);
                if (parsed != null && (piece.Video ? parsed.Sps.Count > 0 : parsed.CodecPrivate != null))
                    _inits[piece.Itag] = parsed;
                if (piece.Init) return;
                DashTrackInit init;
                if (!_inits.TryGetValue(piece.Itag, out init)) return;
                var samples = Mp4.ParseFragment(piece.Bytes, init, piece.Video);
                if (samples.Count == 0) return;
                if (piece.DurationMs <= 0) piece.DurationMs = Math.Max(1, samples.Sum(s => s.DurTicks) / 10000);
                var track = piece.Video ? _video : _audio;
                if (piece.StartMs <= 0) piece.StartMs = samples[0].DtsTicks / 10000;
                if (piece.Video && _timeline.Started && piece.Sequence < _timeline.NextSequence) return;
                if (track.Pending.Concat(track.Fragments).Any(f => f.Sequence == piece.Sequence && f.SourceStartMs == piece.StartMs)) return;
                track.Pending.Add(new Fragment { Itag = piece.Itag, Sequence = piece.Sequence, SourceStartMs = piece.StartMs,
                    SourceDurationMs = piece.DurationMs, Init = init, Bytes = piece.Bytes, Samples = samples });
                track.Pending.Sort((a, b) => piece.Video ? a.Sequence.CompareTo(b.Sequence) : a.SourceStartMs.CompareTo(b.SourceStartMs));
                if (piece.Video && _startWaitAt == DateTime.MinValue) _startWaitAt = DateTime.UtcNow;
                PromotePending(DateTime.UtcNow);
                // Out-of-order pieces waiting for a row must also have a memory bound.
                long pendingBytes = track.Pending.Sum(f => (long)f.Bytes.Length);
                while (track.Pending.Count > 3 && (track.Pending.Count > 64 || pendingBytes > 24 * 1024 * 1024))
                { pendingBytes -= track.Pending[0].Bytes.Length; track.Pending.RemoveAt(0); }
                if (Source != null && !_ended && Source.Duration.TotalSeconds < LiveEdge + 3600)
                    Source.Duration = TimeSpan.FromSeconds(LiveEdge + 86400);
                Signal();
            }
        }

        // Called under _gate. A local timestamp is assigned once, when a video
        // piece enters the ordered row. Renewal and late delivery never reorder it.
        private void PromotePending(DateTime now)
        {
            if (!_timeline.Started) return;
            while (_video.Pending.Count > 0)
            {
                var fragment = _video.Pending[0];
                if (fragment.Sequence < _timeline.NextSequence) { _video.Pending.RemoveAt(0); continue; }
                long start;
                if (!_timeline.TryAppend(TimelinePiece(fragment), now, out start)) break;
                _video.Pending.RemoveAt(0);
                fragment.StartMs = start; fragment.EndMs = start + fragment.SourceDurationMs;
                fragment.TimestampOffsetTicks = start * 10000 - fragment.Samples[0].DtsTicks;
                _video.Fragments.Add(fragment);
            }
            foreach (var fragment in _audio.Pending.ToList())
            {
                // An AAC fragment can overlap two or more video pieces. Split its
                // sample list by their mapping; do not repeat the whole audio chunk
                // for each video sequence or carry audio across a skipped source hole.
                var anchor = fragment.SourceStartMs * 10000 - fragment.Samples[0].DtsTicks;
                List<DashSampleRef> slice = null;
                long sliceOffset = 0, slicePieceStart = 0;
                while (fragment.MappedSamples < fragment.Samples.Count)
                {
                    var sample = fragment.Samples[fragment.MappedSamples];
                    long shift, pieceStart;
                    var mapping = _timeline.MapSample(sample.PtsTicks + anchor, out shift, out pieceStart);
                    if (mapping == LiveTimeline.Mapping.Wait) break;
                    fragment.MappedSamples++;
                    if (mapping == LiveTimeline.Mapping.Discard) continue;
                    var offset = anchor + shift;
                    if (slice != null && (offset != sliceOffset || pieceStart != slicePieceStart))
                    { AppendAudioSlice(fragment, slice, sliceOffset); slice = null; }
                    if (slice == null) { slice = new List<DashSampleRef>(); sliceOffset = offset; slicePieceStart = pieceStart; }
                    slice.Add(sample);
                }
                if (slice != null) AppendAudioSlice(fragment, slice, sliceOffset);
                if (fragment.MappedSamples == fragment.Samples.Count) _audio.Pending.Remove(fragment);
            }
            _audio.Fragments.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
            TrimTrack(_video); TrimTrack(_audio);
        }

        private void AppendAudioSlice(Fragment original, List<DashSampleRef> samples, long offset)
        {
            _audio.Fragments.Add(new Fragment { Sequence = original.Sequence, SourceStartMs = original.SourceStartMs,
                SourceDurationMs = original.SourceDurationMs, Itag = original.Itag, Init = original.Init,
                Bytes = original.Bytes, Samples = samples, TimestampOffsetTicks = offset,
                StartMs = Math.Max(0, samples[0].DtsTicks + offset) / 10000,
                EndMs = Math.Max(0, samples.Max(s => s.DtsTicks + s.DurTicks) + offset) / 10000 });
        }

        private static void TrimTrack(Track track)
        {
            if (track.Fragments.Count == 0) return;
            var end = track.Fragments.Last().EndMs;
            var bytes = track.Fragments.Select(f => f.Bytes).Distinct().Sum(b => (long)b.Length);
            while (track.Fragments.Count > 3 && (track.Fragments[0].EndMs < end - 90000 || bytes > 24 * 1024 * 1024))
            {
                var removed = track.Fragments[0]; track.Fragments.RemoveAt(0);
                if (!track.Fragments.Any(f => ReferenceEquals(f.Bytes, removed.Bytes))) bytes -= removed.Bytes.Length;
            }
        }

        private void Signal()
        { var before = _changed; _changed = new TaskCompletionSource<bool>(); before.TrySetResult(true); }

        private async Task RenewAsync()
        {
            var fresh = await _refresh(_sabr.ReloadToken).ConfigureAwait(false);
            if (_closed || fresh == null) return;
            if (!PlaybackResolver.IsLive(fresh))
            {
                // Network failures do not end the broadcast. Only an explicit successful
                // player response describing a finished broadcast closes sample delivery.
                if (PlaybackResolver.IsFinishedLive(fresh))
                { lock (_gate) { _ended = true; if (Source != null) Source.Duration = TimeSpan.FromSeconds(LiveEdge); Signal(); } }
                return;
            }
            if (LiveSabrSession.HasConfig(fresh) && !LiveJson.Bool(fresh, "_uwpSkipLiveSabr"))
                _sabr.Adopt(fresh, LiveJson.String(fresh, "_uwpPoToken"));
        }

        private async Task PumpAsync()
        {
            var failures = 0;
            var lastRenew = DateTime.MinValue;
            try
            {
                while (!_closed && !_ended)
                {
                    try
                    {
                        await _sabr.FetchAsync(_cancel.Token).ConfigureAwait(false);
                        failures = 0;
                    }
                    catch (OperationCanceledException) { if (_cancel.IsCancellationRequested) break; failures++; }
                    catch (Exception ex)
                    { failures++; System.Diagnostics.Debug.WriteLine("[Live] Feed retry: " + ex.GetType().Name); }
                    var starved = (DateTime.UtcNow - _sabr.LastMediaUtc).TotalSeconds;
                    if ((_sabr.NeedsReload || failures >= 2 || starved >= 20) && (DateTime.UtcNow - lastRenew).TotalSeconds >= 10)
                    {
                        lastRenew = DateTime.UtcNow;
                        try { await RenewAsync().ConfigureAwait(false); }
                        catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Live] Player renewal failed: " + ex.GetType().Name); }
                        // Keep buffered media and the original watch clock across renewals.
                        if (!_ended && (failures >= 2 || starved >= 40)) _sabr.RestartDelivery();
                    }
                    await Task.Delay((int)Math.Max(failures > 0 ? 2000 : 100, Math.Min(30000, _sabr.BackoffMs)), _cancel.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { lock (_gate) Signal(); }
        }

        private void Starting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            try
            {
                if (_closed) return;
                lock (_gate)
                {
                    // Starting cancels outstanding native sample requests, even
                    // when it only resumes playback. Wake and retire those deferrals.
                    _deliveryGeneration++;
                    Signal();
                    // Choose the live edge once. A null native request after that
                    // resumes existing delivery without rewinding its sample cursors.
                    if (_started && !args.Request.StartPosition.HasValue) return;
                    var requested = !_started ? LivePosition : args.Request.StartPosition.Value.TotalSeconds;
                    // Some native opening stages ask for zero again before Play.
                    // Keep the chosen initial piece rather than chasing newer arrivals.
                    if (_started && !_ended && requested <= 0) requested = _initialStartMs / 1000.0;
                    var seconds = Math.Max(BufferedStart, Math.Min(LiveEdge, requested));
                    var local = (long)(seconds * 1000);
                    var fragment = _video.Fragments.LastOrDefault(f => f.StartMs <= local) ?? _video.Fragments.FirstOrDefault();
                    if (fragment == null) return;
                    if (!_started) { _initialStartMs = fragment.StartMs; _started = true; }
                    SetTrackStart(_video, fragment.StartMs); SetTrackStart(_audio, fragment.StartMs, true);
                    args.Request.SetActualStartPosition(TimeSpan.FromMilliseconds(fragment.StartMs));
                    System.Diagnostics.Debug.WriteLine("[Live] Starting at piece: local=" + (fragment.StartMs / 1000.0).ToString("F2")
                        + ", source=" + (fragment.SourceStartMs / 1000.0).ToString("F2"));
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Live] Seek failed: " + ex.GetType().Name); }
        }

        private static void SetTrackStart(Track track, long start, bool audio = false)
        {
            track.Current = audio ? track.Fragments.FirstOrDefault(f => f.EndMs > start)
                : track.Fragments.LastOrDefault(f => f.StartMs <= start) ?? track.Fragments.FirstOrDefault();
            track.Index = 0;
            if (audio && track.Current != null)
            {
                // Keep at most one AAC frame of preroll, not a whole earlier chunk.
                while (track.Index < track.Current.Samples.Count)
                {
                    var sample = track.Current.Samples[track.Index];
                    if (sample.PtsTicks + sample.DurTicks + track.Current.TimestampOffsetTicks > start * 10000) break;
                    track.Index++;
                }
            }
            track.LastEndMs = track.Current != null ? track.Current.StartMs - 1 : -1; track.Discontinuous = true;
        }

        private async void SampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            MediaStreamSourceSampleRequestDeferral deferral = null;
            var track = ReferenceEquals(args.Request.StreamDescriptor, _videoDescriptor) ? _video : _audio;
            var acquired = false;
            int generation;
            lock (_gate) generation = _deliveryGeneration;
            try
            {
                deferral = args.Request.GetDeferral();
                await track.Reading.WaitAsync(_cancel.Token).ConfigureAwait(false); acquired = true;
                while (!_closed)
                {
                    Task wait;
                    lock (_gate)
                    {
                        // A canceled request must not consume the next frame meant
                        // for the new native request after a start/seek/resume.
                        if (generation != _deliveryGeneration) return;
                        PromotePending(DateTime.UtcNow);
                        if (track.Current == null || track.Index >= track.Current.Samples.Count)
                        {
                            if (track.Current != null) track.LastEndMs = track.Current.EndMs;
                            track.Current = track.Fragments.FirstOrDefault(f => f.StartMs >= track.LastEndMs - 2 && f.EndMs > track.LastEndMs);
                            track.Index = 0;
                            if (track.Current != null && (track.Current.Itag != track.Itag || track.Current.StartMs > track.LastEndMs + 50))
                                track.Discontinuous = true;
                        }
                        if (track.Current != null)
                        {
                            var f = track.Current; var s = f.Samples[track.Index++];
                            track.Itag = f.Itag;
                            if (s.Offset < 0 || s.Size <= 0 || s.Offset > f.Bytes.Length - s.Size) continue;
                            byte[] payload;
                            if (track == _video) payload = Mp4.ToAnnexB(f.Bytes, s.Offset, s.Size, f.Init, s.KeyFrame);
                            else { payload = new byte[s.Size]; Buffer.BlockCopy(f.Bytes, s.Offset, payload, 0, s.Size); }
                            // Each selected piece has a stable local clock. Audio slices
                            // use that same shift, preserving their offsets from the video.
                            var delta = f.TimestampOffsetTicks;
                            var sample = MediaStreamSample.CreateFromBuffer(payload.AsBuffer(), TimeSpan.FromTicks(Math.Max(0, s.PtsTicks + delta)));
                            sample.DecodeTimestamp = TimeSpan.FromTicks(Math.Max(0, s.DtsTicks + delta));
                            sample.Duration = TimeSpan.FromTicks(s.DurTicks); sample.KeyFrame = s.KeyFrame;
                            sample.Discontinuous = track.Discontinuous; track.Discontinuous = false;
                            args.Request.Sample = sample;
                            return;
                        }
                        if (_ended) return;
                        wait = _changed.Task;
                    }
                    // No null sample on a temporary empty response: null means end-of-stream.
                    // Wait for another fragment or disposal while the native pipeline buffers.
                    // A confirmed sequence hole can expire even without another UMP response.
                    await Task.WhenAny(wait, _stopped.Task, Task.Delay(500, _cancel.Token)).ConfigureAwait(false);
                    _cancel.Token.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Live] Sample delivery failed: " + ex.GetType().Name);
                if (!_closed) try { sender.NotifyError(MediaStreamSourceErrorStatus.Other); } catch { }
            }
            finally
            {
                if (acquired) track.Reading.Release();
                try { if (deferral != null) deferral.Complete(); } catch { }
            }
        }

        private void Closed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args) { Dispose(); }

        public void Dispose()
        {
            if (_closed) return;
            _closed = true; _cancel.Cancel(); _stopped.TrySetResult(true);
            lock (_gate)
            {
                _video.Fragments.Clear(); _audio.Fragments.Clear(); _video.Pending.Clear(); _audio.Pending.Clear();
                _video.Current = null; _audio.Current = null; _inits.Clear(); Signal();
            }
            if (Source != null)
            { Source.Starting -= Starting; Source.SampleRequested -= SampleRequested; Source.Closed -= Closed; }
        }
    }
}
