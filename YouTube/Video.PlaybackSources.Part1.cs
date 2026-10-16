using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Video
    {
        private string _activeHlsVideoId = string.Empty;
        private string _activeHlsMediaUserAgent = string.Empty;
        private string _failedHlsVideoId = string.Empty;
        private string _failedHlsMediaUserAgent = string.Empty;
        private int _hlsRecoveryGeneration;

        private void ResetHlsPlaybackRecovery()
        {
            _hlsRecoveryGeneration++;
            _activeHlsVideoId = string.Empty;
            _activeHlsMediaUserAgent = string.Empty;
            _failedHlsVideoId = string.Empty;
            _failedHlsMediaUserAgent = string.Empty;
        }

        private bool HasRejectedHlsSource(string videoId)
        {
            return !string.IsNullOrWhiteSpace(videoId)
                && string.Equals(_failedHlsVideoId, videoId, StringComparison.Ordinal);
        }

        private void RejectHlsSource(string videoId, string mediaUserAgent)
        {
            PlaybackResolver.Invalidate(videoId);
            _failedHlsVideoId = videoId;
            _failedHlsMediaUserAgent = mediaUserAgent ?? string.Empty;
            _activeHlsVideoId = string.Empty;
            _activeHlsMediaUserAgent = string.Empty;
            lock (_androidPlayerRequestGate)
            {
                _lastAndroidPlayerVideoId = string.Empty;
                _lastAndroidPlayerJson = string.Empty;
                _androidPlayerRequestVideoId = string.Empty;
                _androidPlayerRequestTask = null;
            }
            System.Diagnostics.Debug.WriteLine("[Video] HLS source rejected; resolving another playback source at the configured quality");
        }

        private async Task<bool> TryBindHlsSourceAsync(string videoId, string manifestUrl, string mediaUserAgent, string sourceQualityTag, bool autoPlay)
        {
            if (CustomVideoPlayer == null || !IsStillCurrentVideo(videoId)) return false;
            _activeHlsVideoId = videoId;
            _activeHlsMediaUserAgent = mediaUserAgent;
            CustomVideoPlayer.MediaRequestUserAgent = mediaUserAgent;
            if (await CustomVideoPlayer.TrySetSourceFromUriAsync(new Uri(manifestUrl), autoPlay, sourceQualityTag))
                return true;
            if (IsStillCurrentVideo(videoId)) RejectHlsSource(videoId, mediaUserAgent);
            return false;
        }

        private async Task BindPlayerSourceAsync(string playerJson)
        {
            await BindPlayerSourceAsync(playerJson, true);
        }

        private async Task BindPlayerSourceAsync(string playerJson, bool autoPlay)
        {
            var root = ParsePlayerJsonObject(playerJson);
            if (root == null) throw new FormatException("Player returned invalid JSON");
            var agent = PlaybackResolver.MediaUserAgent(root);
            await BindPlayerSourceAsync(root, autoPlay, agent);
        }

        private static void LogSelectedPlaybackUrls(
            string effectiveQualityTag,
            string requestedCapTag,
            PlayerFormatModel video,
            PlayerFormatModel audio)
        {
            var mode = string.IsNullOrWhiteSpace(effectiveQualityTag)
                ? "Auto"
                : effectiveQualityTag + "p";
            var cap = ParseInt(requestedCapTag);

            if (video != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] VIDEO mode=" + mode
                    + " cap=" + cap + "p"
                    + " actual=" + video.QualityTier + "p"
                    + " dimensions=" + video.Width + "x" + video.Height
                    + " fps=" + video.Fps
                    + " itag=" + video.Itag
                    + " mime=" + video.MimeType
                    + " URL=" + video.Url);
            }

            if (audio != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] AUDIO mode=" + mode
                    + " track=" + (string.IsNullOrWhiteSpace(audio.AudioTrackId) ? "default" : audio.AudioTrackId)
                    + " bitrate=" + audio.Bitrate
                    + " itag=" + audio.Itag
                    + " mime=" + audio.MimeType
                    + " URL=" + audio.Url);
            }
        }

        private async Task BindPlayerSourceAsync(JsonObject rootObject, bool autoPlay, string mediaUserAgent = null)
        {
            var bindingVideoId = currentVideoId;
            var bindingGeneration = _hlsRecoveryGeneration;
            _activeHlsVideoId = string.Empty;
            _activeHlsMediaUserAgent = string.Empty;
            var rootMediaUserAgent = PlaybackResolver.MediaUserAgent(rootObject);
            if (string.IsNullOrWhiteSpace(rootMediaUserAgent))
                rootMediaUserAgent = mediaUserAgent ?? InnertubeIosPlayerUserAgent;
            _playbackMediaUserAgent = rootMediaUserAgent;
            if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
            if (CustomVideoPlayer != null && (PlaybackResolver.IsLive(rootObject) || IsLivePage))
            {
                var liveQuality = GetEffectiveVideoQualityTag();
                await BindLivePlaybackAsync(rootObject, autoPlay, string.IsNullOrEmpty(liveQuality)
                    ? GetAutomaticScreenQualityCap().ToString(System.Globalization.CultureInfo.InvariantCulture) : liveQuality);
                return;
            }
            if (CustomVideoPlayer != null) CustomVideoPlayer.SetLivePlayback(false);
            CaptureVisitorData(rootObject);
            availableFormats.Clear();
            CollectFormatsFromStreamingData(rootObject);
            foreach (var format in availableFormats) format.MediaUserAgent = rootMediaUserAgent;
            _currentH264VideoOnlyItag = -1;
            _readyHeight = 0;

            var hlsManifestUrl = ExtractHlsManifestUrl(rootObject);
            var usesLiveTransport = PlaybackResolver.UsesLiveTransport(rootObject);
            if (HasRejectedHlsSource(bindingVideoId)) hlsManifestUrl = string.Empty;
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Total available formats: {availableFormats.Count}"
            );
            string effectiveQualityTag = GetEffectiveVideoQualityTag();
            bool isAutomaticQuality = string.IsNullOrWhiteSpace(effectiveQualityTag);
            string sourceQualityTag = string.IsNullOrWhiteSpace(effectiveQualityTag)
                ? GetAutomaticScreenQualityCap().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : effectiveQualityTag;
            if (!isAutomaticQuality)
            {
                // A user's explicit tier is authoritative even when it exceeds the physical
                // screen. Screen sizing only caps Auto.
                System.Diagnostics.Debug.WriteLine(
                    "[Video][Manual] exact requested cap=" + sourceQualityTag
                    + "p; screen/network Auto checks bypassed");
            }
            UpdateVideoPlayerAspectRatioFromFormats(sourceQualityTag);
            var selected = SelectPreferredProgressiveFormat(availableFormats, sourceQualityTag, false);
            bool isWindowsMobile = IsWindowsMobileDevice();

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetWindowsMobileAudioMode(isWindowsMobile);
                CustomVideoPlayer.CurrentQuality = string.IsNullOrWhiteSpace(effectiveQualityTag) ? null : effectiveQualityTag;

                // Preview work never triggers another player request before playback.
                var storyboardSpec = ExtractStoryboardSpec(rootObject);
                System.Diagnostics.Debug.WriteLine("[Video] Storyboard spec length: "
                    + (storyboardSpec == null ? 0 : storyboardSpec.Length));
                if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
                CustomVideoPlayer.SetStoryboardSpec(storyboardSpec);
            }

            if (usesLiveTransport && !string.IsNullOrWhiteSpace(hlsManifestUrl) && CustomVideoPlayer != null)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Archived broadcast uses HLS before finite MP4 sources");
                if (await TryBindHlsSourceAsync(bindingVideoId, hlsManifestUrl, rootMediaUserAgent, sourceQualityTag, autoPlay)) return;
                if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
                hlsManifestUrl = string.Empty;
            }

            // Troubadour tries ready H.264 + AAC before muxed files and IOS HLS.
            // Auto selects by screen size without a blocking CDN bandwidth probe.
            // 1) On-the-fly DASH demux: H.264 video-only + AAC audio-only fed into a
            //    MediaStreamSource that MediaPlayer decodes and A/V-syncs. This is the real
            //    >360p (up to 1080p) path on Windows 10 Mobile.
            if (CustomVideoPlayer != null && !usesLiveTransport)
            {
                var demuxVideo = await GetAndroidH264VideoOnlyFormatAsync(
                    bindingVideoId,
                    sourceQualityTag,
                    isAutomaticQuality);
                if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
                var demuxAudio = await GetAndroidAudioOnlyFormatAsync(bindingVideoId, _selectedAudioTrackId);
                if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
                if (demuxVideo != null && demuxAudio != null
                    && !string.IsNullOrWhiteSpace(demuxVideo.Url) && !string.IsNullOrWhiteSpace(demuxAudio.Url)
                    && !PlaybackResolver.IsLiveTransportUrl(demuxVideo.Url) && !PlaybackResolver.IsLiveTransportUrl(demuxAudio.Url))
                {
                    LogSelectedPlaybackUrls(effectiveQualityTag, sourceQualityTag, demuxVideo, demuxAudio);
                    try
                    {
                        var mss = await DashDemuxer.CreateAsync(
                            httpClient.RawClient,
                            demuxVideo,
                            demuxAudio,
                            _playbackMediaUserAgent
                        );
                        if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
                        if (mss != null)
                        {
                            _currentH264VideoOnlyItag = demuxVideo.Itag;
                            _readyHeight = demuxVideo.QualityTier;
                            SetVideoPlayerAspectRatioFromFormat(demuxVideo);
                            if (CustomVideoPlayer.SetDemuxedSource(mss, autoPlay))
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    "[Video][PlaybackURL] OPENED mode="
                                    + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
                                    + " actual=" + demuxVideo.QualityTier + "p"
                                    + " itag=" + demuxVideo.Itag);
                                return;
                            }
                        }

                        System.Diagnostics.Debug.WriteLine("[Video] DASH demux source unavailable; falling back");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] DASH demux failed: " + ex.Message + "; falling back");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Video] DASH demux not attempted (missing video-only or audio-only adaptive stream)");
                }
            }

            // 2) Best muxed progressive <= requested (single file, no
            //    separate audio, no crash). Tops out at whatever muxed YouTube offers
            //    (usually 720p itag22, else 360p itag18) — the reliable fallback.
            var muxed = await GetAndroidBestMuxedFormatAsync(bindingVideoId, sourceQualityTag);
            if (!IsStillCurrentVideo(bindingVideoId) || bindingGeneration != _hlsRecoveryGeneration) return;
            if (muxed == null || string.IsNullOrWhiteSpace(muxed.Url))
            {
                muxed = selected;
            }

            if (muxed != null && !string.IsNullOrWhiteSpace(muxed.Url) && !PlaybackResolver.IsLiveTransportUrl(muxed.Url))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Video][PlaybackURL] MUXED mode={(string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")} actual={muxed.QualityTier}p dimensions={muxed.Width}x{muxed.Height} itag={muxed.Itag} mime={muxed.MimeType} URL={muxed.Url}"
                );
                SetVideoPlayerAspectRatioFromFormat(muxed);
                _readyHeight = muxed.QualityTier;
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.MediaRequestUserAgent = string.IsNullOrWhiteSpace(muxed.MediaUserAgent)
                        ? _playbackMediaUserAgent : muxed.MediaUserAgent;
                    if (!await CustomVideoPlayer.TrySetSourceFromUriAsync(new Uri(muxed.Url), autoPlay, sourceQualityTag))
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] Progressive source rejected; trying HLS");
                    }
                    else return;
                }
            }

            // 3) HLS manifest (adaptive, already multiplexes audio). Rare in the SABR era
            //    but native on Windows Phone when present.
            if (!string.IsNullOrWhiteSpace(hlsManifestUrl) && CustomVideoPlayer != null)
            {
                _readyHeight = 0;
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] HLS mode="
                    + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
                    + " URL=" + hlsManifestUrl
                );
                if (!await TryBindHlsSourceAsync(bindingVideoId, hlsManifestUrl, rootMediaUserAgent, sourceQualityTag, autoPlay)
                    && IsStillCurrentVideo(bindingVideoId) && bindingGeneration == _hlsRecoveryGeneration)
                    await BindPlayerSourceAsync(rootObject, autoPlay);
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] No playable source found for requested quality "
                + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
            );

            // Nothing was handed to the player, so MediaOpened/MediaFailed will never fire —
            // release the loading lock here.
            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.EndSourceLoading();
            }
            throw new InvalidOperationException(Localization.GetString("PlaybackFailed"));
        }

        private async Task<string> GetNativeDashManifestUrlAsync(string videoId, JsonObject primaryRoot)
        {
            // YouTube's own DASH manifest (segment-list, self-muxed) if the player response
            // exposes one. Prefer the primary response, then the ANDROID_VR response.
            var url = ExtractDashManifestUrl(primaryRoot);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (!string.IsNullOrWhiteSpace(androidJson))
                {
                    var androidRoot = JsonValue.Parse(androidJson).GetObject();
                    url = ExtractDashManifestUrl(androidRoot);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetNativeDashManifestUrlAsync failed: " + ex.Message);
            }

            return url ?? string.Empty;
        }

        private static string ExtractDashManifestUrl(JsonObject root)
        {
            if (root == null || !root.ContainsKey("streamingData"))
            {
                return string.Empty;
            }

            var streamingData = root.GetNamedObject("streamingData");
            return GetJsonString(streamingData, "dashManifestUrl");
        }

        private async Task<PlayerFormatModel> GetAndroidBestMuxedFormatAsync(string videoId, string requestedQualityTag)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                foreach (var format in androidFormats) format.MediaUserAgent = _playbackMediaUserAgent;
                return SelectMuxedFormatForRequestedHeight(androidFormats, requestedQualityTag);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetAndroidBestMuxedFormatAsync failed: " + ex.Message);
                return null;
            }
        }

        // The muxed progressive format that best satisfies the requested height: the HIGHEST
        // muxed height <= requested (so "720p" gives itag 22, not itag 18). For Auto/empty it
        // returns the smallest muxed (bandwidth-friendly, always plays). Unlike
        // SelectPreferredProgressiveFormat this does not hard-prefer itag 18.
        private static PlayerFormatModel SelectMuxedFormatForRequestedHeight(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag
        )
        {
            if (formats == null)
            {
                return null;
            }

            int requestedHeight = ParseInt(requestedQualityTag);
            var muxed = new List<PlayerFormatModel>();
            foreach (var f in formats)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Url))
                {
                    continue;
                }

                if (f.IsAdaptive || !f.HasAudio || !f.HasVideo || f.QualityTier <= 0)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                if (mime.IndexOf("video/mp4") < 0)
                {
                    continue;
                }

                muxed.Add(f);
            }

            if (muxed.Count == 0)
            {
                return null;
            }

            PlayerFormatModel smallest = null;
            foreach (var f in muxed)
            {
                if (smallest == null || f.QualityTier < smallest.QualityTier)
                {
                    smallest = f;
                }
            }

            if (requestedHeight <= 0)
            {
                return smallest;
            }

            PlayerFormatModel best = null;
            foreach (var f in muxed)
            {
                if (f.QualityTier <= requestedHeight && (best == null || f.QualityTier > best.QualityTier))
                {
                    best = f;
                }
            }

            return best ?? smallest;
        }

        private async Task ReloadPlayerOnlyAsync(bool stopBeforeLoad)
        {
            // Pin the video this reload belongs to. Fetching a new quality takes seconds, and the
            // user may well open another video meanwhile — without this the finished reload bound
            // the OLD video's source and it started playing under the new one.
            var reloadVideoId = currentVideoId;
            if (string.IsNullOrWhiteSpace(reloadVideoId))
            {
                return;
            }

            try
            {
                if (IsLivePage)
                {
                    var liveGeneration = _hlsRecoveryGeneration;
                    var liveOwner = PlaybackResolver.AccountKey;
                    if (stopBeforeLoad && CustomVideoPlayer != null) CustomVideoPlayer.Stop();
                    var freshLiveRoot = await PlaybackResolver.GetLivePlayerAsync(reloadVideoId);
                    if (IsStillCurrentVideo(reloadVideoId) && liveGeneration == _hlsRecoveryGeneration && liveOwner == PlaybackResolver.AccountKey)
                        await BindLivePlaybackAsync(freshLiveRoot ?? _currentLiveRoot, true, GetEffectiveVideoQualityTag());
                    return;
                }
                if (stopBeforeLoad && CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.Stop();
                }

                var playerJson = _lastPlayerJson;
                if (string.IsNullOrWhiteSpace(playerJson))
                {
                    playerJson = await PostInnertubeAsync("player", BuildPlayerPayload(reloadVideoId));

                    if (!IsStillCurrentVideo(reloadVideoId))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] Quality reload for " + reloadVideoId + " abandoned - another video is open");
                        return;
                    }

                    _lastPlayerJson = playerJson;
                }

                if (!IsStillCurrentVideo(reloadVideoId))
                {
                    return;
                }

                await BindPlayerSourceAsync(playerJson, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Player-only reload failed: " + ex.Message);
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.EndSourceLoading();
                }
            }
        }

        private async Task<Uri> GetSeparateAudioUriForPlaybackAsync(string videoId, bool isWindowsMobile)
        {
            try
            {
                var selectedFormat = await GetAndroidItag18FormatAsync(videoId);
                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] itag18 separate audio carrier was not found; trying legacy audio carrier fallback"
                    );
                    selectedFormat = await GetAndroidAudioVideoTrackFormatAsync(videoId, true, isWindowsMobile);
                }

                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Android separate audio-video source was not found"
                    );
                    return null;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Android video-as-audio source selected: itag={selectedFormat.Itag}, height={selectedFormat.Height}, mime={selectedFormat.MimeType}"
                );
                return new Uri(selectedFormat.Url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Failed to get separate Android audio-video source: " + ex.Message
                );
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidItag18FormatAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidPlayerJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                foreach (var format in androidFormats) format.MediaUserAgent = _playbackMediaUserAgent;
                return SelectItag18ProgressiveFormat(androidFormats);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get Android itag18 format: " + ex.Message);
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidH264VideoOnlyFormatAsync(
            string videoId,
            string requestedQualityTag,
            bool useAutomaticNetworkSelection = false)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidPlayerJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                foreach (var format in androidFormats)
                    format.MediaUserAgent = PlaybackResolver.MediaUserAgent(androidRoot);
                return SelectPreferredH264VideoOnlyFormat(androidFormats, requestedQualityTag, _excludedH264VideoOnlyItags);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get Android H.264 video-only format: " + ex.Message);
                return null;
            }
        }

        private async Task<string> GetWebSafariHlsManifestUrlAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            try
            {
                var safariPlayerJson = await PostWebSafariPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(safariPlayerJson))
                {
                    return string.Empty;
                }

                var safariRoot = JsonValue.Parse(safariPlayerJson).GetObject();
                var hlsManifestUrl = ExtractHlsManifestUrl(safariRoot);
                if (string.IsNullOrWhiteSpace(hlsManifestUrl))
                {
                    var safariFormats = new List<PlayerFormatModel>();
                    CollectFormatsFromStreamingData(safariRoot, safariFormats);
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] yt-dlp WEB Safari player returned no HLS manifest; formats="
                        + safariFormats.Count
                    );

                    if (safariRoot.ContainsKey("playabilityStatus"))
                    {
                        var status = safariRoot.GetNamedObject("playabilityStatus");
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] WEB Safari playability: "
                            + GetJsonString(status, "status")
                            + " "
                            + GetJsonString(status, "reason")
                        );
                    }

                    return string.Empty;
                }

                System.Diagnostics.Debug.WriteLine("[Video] yt-dlp WEB Safari HLS manifest found");
                return hlsManifestUrl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get WEB Safari HLS manifest: " + ex.Message);
                return string.Empty;
            }
        }

        private static string GetFallbackInnertubeVisitorData()
        {
            try
            {
                return Uri.UnescapeDataString(InnertubeFallbackVisitorData);
            }
            catch
            {
                return InnertubeFallbackVisitorData;
            }
        }

        // Adopt the server-issued visitorData from any youtubei response's responseContext
        // (MeeTube's core::Http visitor sink). Only stores the first non-empty value so the
        // whole session presents one stable returning identity.
        private void CaptureVisitorData(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("responseContext"))
                {
                    return;
                }

                var responseContext = root.GetNamedObject("responseContext");
                var visitorData = GetJsonString(responseContext, "visitorData");
                if (string.IsNullOrWhiteSpace(visitorData))
                {
                    return;
                }

                lock (_visitorDataGate)
                {
                    if (string.IsNullOrWhiteSpace(_sessionVisitorData))
                    {
                        _sessionVisitorData = visitorData;
                        System.Diagnostics.Debug.WriteLine("[Video] Captured session visitorData from responseContext");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] CaptureVisitorData failed: " + ex.Message);
            }
        }

        // The visitorData ANDROID_VR needs. Returns the captured session value, else fetches
        // a fresh one from an anonymous WEB /player (deduped), else the hardcoded fallback.
        private async Task<string> GetSessionVisitorDataAsync()
        {
            lock (_visitorDataGate)
            {
                if (!string.IsNullOrWhiteSpace(_sessionVisitorData))
                {
                    return _sessionVisitorData;
                }
            }

            Task<string> fetchTask;
            lock (_visitorDataGate)
            {
                if (!string.IsNullOrWhiteSpace(_sessionVisitorData))
                {
                    return _sessionVisitorData;
                }

                if (_visitorDataFetchTask == null)
                {
                    _visitorDataFetchTask = FetchFreshVisitorDataAsync();
                }

                fetchTask = _visitorDataFetchTask;
            }

            string fetched = null;
            try
            {
                fetched = await fetchTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] visitorData fetch failed: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(fetched))
            {
                lock (_visitorDataGate)
                {
                    if (string.IsNullOrWhiteSpace(_sessionVisitorData))
                    {
                        _sessionVisitorData = fetched;
                    }

                    return _sessionVisitorData;
                }
            }

            return GetFallbackInnertubeVisitorData();
        }

        // One lightweight anonymous WEB /player call whose only purpose is to read a fresh
        // responseContext.visitorData (mirrors how MeeTube's first youtubei call seeds it).
        private async Task<string> FetchFreshVisitorDataAsync()
        {
            var videoId = currentVideoId;
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            try
            {
                var url = InnertubeEndpoints.BuildWithoutKey("player?prettyPrint=false");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildWebVisitorSeedPayload(videoId),
                        Encoding.UTF8,
                        "application/json"
                    );
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
                    );
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20260626.01.00");

                    var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return string.Empty;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    if (root.ContainsKey("responseContext"))
                    {
                        return GetJsonString(root.GetNamedObject("responseContext"), "visitorData");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] FetchFreshVisitorDataAsync failed: " + ex.Message);
            }

            return string.Empty;
        }

        private static string BuildWebVisitorSeedPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20260626.01.00");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private async Task<string> FixNParameterWithPlayerScriptAsync(string videoId, string streamUrl)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(streamUrl))
            {
                return streamUrl;
            }

            var originalN = GetQueryParameterFromUrl(streamUrl, "n");
            if (string.IsNullOrWhiteSpace(originalN))
            {
                System.Diagnostics.Debug.WriteLine("[Video] H.264 video-only URL has no n parameter; using yt-dlp JS-less client URL as-is");
                return streamUrl;
            }

            try
            {
                var playerScriptUrl = await GetPlayerScriptUrlAsync(videoId);
                if (string.IsNullOrWhiteSpace(playerScriptUrl))
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Could not find YouTube player base.js URL");
                    return string.Empty;
                }

                var build = Regex.Match(playerScriptUrl, @"/s/player/(?<id>[0-9a-fA-F]+)/");
                var tvScriptUrl = build.Success ? "https://www.youtube.com/s/player/"
                    + build.Groups["id"].Value + "/tv-player-ias.vflset/tv-player-ias.js" : playerScriptUrl;
                // Same order as YTNSig.obtainScript: lighter TV script, then the
                // ordinary script from the same player revision if TV fails.
                foreach (var scriptUrl in new[] { tvScriptUrl, playerScriptUrl })
                {
                    if (!IsStillCurrentVideo(videoId)) return string.Empty;
                    string fixedN;
                    try
                    {
                        var playerScript = await GetPlayerScriptAsync(scriptUrl);
                        if (!IsStillCurrentVideo(videoId)) return string.Empty;
                        if (string.IsNullOrWhiteSpace(playerScript)) continue;
                        fixedN = await _playerNSig.TransformAsync(VideoRoot, scriptUrl, playerScript, originalN);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[NSig] Player script unavailable: " + ex.Message);
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(fixedN) || fixedN == originalN) continue;
                    System.Diagnostics.Debug.WriteLine("[NSig] n parameter transformed");
                    return ReplaceQueryParameter(streamUrl, "n", fixedN);
                }
                return string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to fix n parameter: " + ex.Message);
                return string.Empty;
            }
        }

        private static string PrepareVideoOnlyUrlForUwpPlayback(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (!string.IsNullOrWhiteSpace(GetQueryParameterFromUrl(url, "range")))
            {
                return url;
            }

            System.Diagnostics.Debug.WriteLine("[Video] Added range=0- to H.264 video-only URL for UWP playback");
            return ReplaceQueryParameter(url, "range", "0-");
        }

    }
}
