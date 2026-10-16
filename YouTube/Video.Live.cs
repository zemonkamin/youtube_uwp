using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class Video
    {
        private readonly LivePoToken _liveProof = new LivePoToken();
        private JsonObject _currentLiveRoot;
        private string _liveVideoId, _liveChatSeed;
        private DateTime _liveWatchedFrom;
        private DispatcherTimer _liveRetryTimer;
        private bool _liveRecoveryPending;
        private bool _livePreferHls;
        private int _livePreferredClient;
        private CancellationTokenSource _liveBindingCancel;
        private bool IsLivePage { get { return _liveVideoId == currentVideoId && !string.IsNullOrEmpty(_liveVideoId); } }

        private async Task<JsonObject> PrepareLiveRootAsync(string videoId, JsonObject root, string owner, int generation)
        {
            if (root == null || !IsStillCurrentVideo(videoId) || owner != PlaybackResolver.AccountKey || generation != _hlsRecoveryGeneration) return null;
            var prepared = JsonObject.Parse(root.Stringify());
            if (LiveSabrSession.HasConfig(prepared))
            {
                var streaming = LiveJson.Object(prepared, "streamingData");
                var url = LiveJson.String(prepared, "_uwpOriginalSabrUrl");
                if (string.IsNullOrEmpty(url)) url = LiveJson.String(streaming, "serverAbrStreamingUrl");
                prepared["_uwpOriginalSabrUrl"] = JsonValue.CreateStringValue(url);
                var proofTask = _liveProof.GetAsync(VideoRoot, videoId);
                var transform = FixNParameterWithPlayerScriptAsync(videoId, url);
                // Troubadour allows the first solver boot to finish on old devices.
                // Subsequent stream URLs reuse its WebView and cached n answers.
                var transformWait = ResponsiveLayout.IsPhoneDevice ? 30000 : 15000;
                var transformCompleted = await Task.WhenAny(transform, Task.Delay(transformWait)) == transform;
                var fixedUrl = transformCompleted
                    ? await transform : string.Empty;
                if (string.IsNullOrEmpty(fixedUrl) && !string.IsNullOrEmpty(GetQueryParameterFromUrl(url, "n")))
                {
                    // Do not submit an unchanged signed URL. A slow preparation
                    // stays alive so the scheduled retry can use its ready solver.
                    prepared["_uwpSkipLiveSabr"] = JsonValue.CreateBooleanValue(true);
                    System.Diagnostics.Debug.WriteLine(transformCompleted
                        ? "[Live] SABR n transform failed; trying HLS"
                        : "[Live] SABR n solver still preparing; trying HLS while it finishes");
                    var ignoredProof = ObserveBackgroundTaskAsync(proofTask, "live proof after HLS fallback");
                    var ignoredTransform = ObserveBackgroundTaskAsync(transform, "live n transform after HLS fallback");
                    return IsStillCurrentVideo(videoId) && owner == PlaybackResolver.AccountKey
                        && generation == _hlsRecoveryGeneration ? prepared : null;
                }
                if (!string.IsNullOrEmpty(fixedUrl)) streaming["serverAbrStreamingUrl"] = JsonValue.CreateStringValue(fixedUrl);
                prepared.Remove("_uwpSkipLiveSabr");
                var proof = await proofTask;
                System.Diagnostics.Debug.WriteLine("[Live] SABR proof ready=" + !string.IsNullOrEmpty(proof));
                prepared["_uwpPoToken"] = JsonValue.CreateStringValue(proof ?? string.Empty);
            }
            if (!IsStillCurrentVideo(videoId) || owner != PlaybackResolver.AccountKey || generation != _hlsRecoveryGeneration) return null;
            return prepared;
        }

        private Task<JsonObject> RefreshLiveRootAsync(string videoId, string owner, int generation, string reloadToken,
            int clientNumber, string playerBinding)
        {
            var complete = new TaskCompletionSource<JsonObject>();
            var work = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                try
                {
                    if (!IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey)
                    { complete.TrySetResult(null); return; }
                    var proof = string.IsNullOrEmpty(playerBinding) ? null : await _liveProof.GetAsync(VideoRoot, playerBinding);
                    if (!IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey)
                    { complete.TrySetResult(null); return; }
                    var fresh = await PlaybackResolver.GetLivePlayerForClientAsync(videoId, clientNumber, reloadToken, proof);
                    complete.TrySetResult(await PrepareLiveRootAsync(videoId, fresh, owner, generation));
                }
                catch (Exception ex) { complete.TrySetException(ex); }
            });
            return complete.Task;
        }

        private async Task BindLivePlaybackAsync(JsonObject root, bool autoPlay, string quality)
        {
            if (_liveBindingCancel != null) _liveBindingCancel.Cancel();
            var cancellation = new CancellationTokenSource(); _liveBindingCancel = cancellation;
            var videoId = currentVideoId; var generation = _hlsRecoveryGeneration; var owner = PlaybackResolver.AccountKey;
            try { await BindLivePlaybackCoreAsync(root, autoPlay, quality, cancellation.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Live] Source setup failed: " + ex.GetType().Name + " - " + ex.Message);
                if (!cancellation.IsCancellationRequested && IsStillCurrentVideo(videoId)
                    && generation == _hlsRecoveryGeneration && owner == PlaybackResolver.AccountKey)
                {
                    CustomVideoPlayer.Stop(); CustomVideoPlayer.SetLivePlayback(true);
                    CustomVideoPlayer.EndSourceLoading();
                    LivePlaybackNotice.Text = Localization.GetString("LiveWaiting");
                    LivePlaybackNotice.Visibility = Visibility.Visible;
                    StartLiveRetry();
                }
            }
            finally
            {
                if (ReferenceEquals(_liveBindingCancel, cancellation)) _liveBindingCancel = null;
                cancellation.Dispose();
            }
        }

        private async Task BindLivePlaybackCoreAsync(JsonObject root, bool autoPlay, string quality, CancellationToken cancellation)
        {
            var videoId = currentVideoId; var generation = _hlsRecoveryGeneration; var owner = PlaybackResolver.AccountKey;
            if (_liveVideoId != videoId) { _liveVideoId = videoId; _liveWatchedFrom = DateTime.UtcNow; }
            LiveChatButtonText.Text = Localization.GetString("LiveChatTitle");
            RefreshVideoActionVisibility();
            CustomVideoPlayer.SetLivePlayback(true);
            CustomVideoPlayer.BeginSourceLoading();
            CustomVideoPlayer.SetSkipSegments(null); CustomVideoPlayer.SetChapters(null); CustomVideoPlayer.SetStoryboardSpec(null);
            CustomVideoPlayer.PrepareResumeAfterSourceReload(TimeSpan.Zero, autoPlay);
            var candidate = root;
            if (!_livePreferHls && !LiveSabrSession.HasConfig(candidate))
            {
                var fresh = await PlaybackResolver.GetLivePlayerAsync(videoId);
                if (LiveSabrSession.HasConfig(fresh)) candidate = fresh;
            }
            if (cancellation.IsCancellationRequested || !IsStillCurrentVideo(videoId)
                || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey) return;
            if (candidate == null) throw new InvalidOperationException("Live player response unavailable");
            if (PlaybackResolver.IsFinishedLive(candidate))
            { StopLiveRetry(); CustomVideoPlayer.ConfirmLiveEnded(); return; }
            _currentLiveRoot = candidate;
            availableFormats.Clear(); CollectLiveFormats(candidate, availableFormats);
            int height; if (!int.TryParse(quality, out height) || height <= 0) height = GetAutomaticScreenQualityCap();
            CustomVideoPlayer.CurrentQuality = string.IsNullOrEmpty(GetEffectiveVideoQualityTag()) ? null : quality;
            var deliveries = new List<JsonObject> { candidate };
            var seed = LiveJson.Object(root, "_uwpLiveFallback") ?? LiveJson.Object(root, "_uwpLiveHlsFallback");
            if (seed != null) deliveries.Add(seed);
            if (_livePreferHls)
            {
                var haveHls = false;
                foreach (var delivery in deliveries)
                    if (!string.IsNullOrEmpty(ExtractHlsManifestUrl(delivery))) { haveHls = true; break; }
                if (!haveHls) _livePreferHls = false;
            }
            if (!_livePreferHls)
            {
                var first = await TryBindLiveSabrAsync(candidate, videoId, owner, generation, height, autoPlay, cancellation);
                if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation) || first.Opened) return;
                var firstClient = (int)LiveJson.Number(candidate, "_uwpClientNumber");
                if (first.HttpStatus == 403 && (firstClient == 85 || firstClient == 1)
                    && !LiveJson.Bool(candidate, "_uwpHasPlayerProof"))
                {
                    // YTApi.fetchTvPlayer includes a session proof in /player.
                    // It differs from the video-bound proof in the SABR body.
                    var proof = await _liveProof.GetAsync(VideoRoot, PlaybackResolver.LiveSessionBinding(candidate));
                    if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) return;
                    if (!string.IsNullOrEmpty(proof))
                    {
                        if (firstClient == 85)
                        {
                            // A persisted sts can belong to an older player revision.
                            // Refresh it once after rejection, then rediscover the n script.
                            await global::Config.GetSignatureTimestampAsync(true);
                            lock (_playerScriptGate) _discoveredPlayerScriptUrl = null;
                            if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) return;
                        }
                        var renewed = await PlaybackResolver.GetLivePlayerForClientAsync(videoId, firstClient, null, proof);
                        if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) return;
                        if (renewed != null)
                        {
                            deliveries.Add(renewed);
                            System.Diagnostics.Debug.WriteLine("[Live] Retrying player with session proof, client=" + firstClient);
                            var retry = await TryBindLiveSabrAsync(renewed, videoId, owner, generation, height, autoPlay, cancellation);
                            if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation) || retry.Opened) return;
                        }
                    }
                    // A rejected TV delivery must not trap every timer retry on TV.
                    _livePreferredClient = 101;
                }
                // The IOS seed is already available. Then use Troubadour's ready
                // delivery order: VISIONOS and ANDROID_VR, with each client's identity.
                foreach (var client in new[] { 5, 101, 28 })
                {
                    if (client == firstClient) continue;
                    var alternative = client == 5 && seed != null && (int)LiveJson.Number(seed, "_uwpClientNumber") == 5
                        ? seed : await PlaybackResolver.GetLivePlayerForClientAsync(videoId, client);
                    if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) return;
                    if (alternative == null) continue;
                    if (!deliveries.Contains(alternative)) deliveries.Add(alternative);
                    System.Diagnostics.Debug.WriteLine("[Live] Trying alternate delivery, client=" + client);
                    var attempt = await TryBindLiveSabrAsync(alternative, videoId, owner, generation, height, autoPlay, cancellation);
                    if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation) || attempt.Opened) return;
                }
            }
            // Troubadour's final live fallback is HLS, never finite MP4 ranges.
            JsonObject hlsRoot = null;
            foreach (var delivery in deliveries)
                if (!string.IsNullOrEmpty(ExtractHlsManifestUrl(delivery))) { hlsRoot = delivery; break; }
            if (hlsRoot == null)
            {
                // Do not repeat an IOS request whose response already had no HLS.
                var haveIos = false;
                foreach (var delivery in deliveries)
                    if ((int)LiveJson.Number(delivery, "_uwpClientNumber") == 5) { haveIos = true; break; }
                if (!haveIos) hlsRoot = await PlaybackResolver.GetLiveHlsAsync(videoId);
            }
            if (cancellation.IsCancellationRequested || !IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey) return;
            var hls = ExtractHlsManifestUrl(hlsRoot);
            if (string.IsNullOrEmpty(hls)) System.Diagnostics.Debug.WriteLine("[Live] No HLS manifest in fallback response; waiting for SABR retry");
            if (!string.IsNullOrEmpty(hls))
            {
                CustomVideoPlayer.SetLivePlayback(true); CustomVideoPlayer.MediaRequestUserAgent = PlaybackResolver.MediaUserAgent(hlsRoot);
                var ready = await CustomVideoPlayer.TrySetHlsSourceAsync(new Uri(hls), autoPlay, quality, cancellation);
                if (ready) ready = await CustomVideoPlayer.WaitForLiveSourceOpenedAsync(cancellation);
                if (cancellation.IsCancellationRequested || !IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey) return;
                if (ready)
                {
                    _livePreferHls = true;
                    _livePreferredClient = (int)LiveJson.Number(hlsRoot, "_uwpClientNumber");
                    _currentLiveRoot = hlsRoot;
                    System.Diagnostics.Debug.WriteLine("[Live] HLS source opened");
                    LivePlaybackNotice.Visibility = Visibility.Collapsed; StopLiveRetry(); return;
                }
            }
            // Invalidate a source that timed out so a late MediaOpened cannot start
            // it while the retry task is binding a replacement.
            CustomVideoPlayer.Stop(); CustomVideoPlayer.SetLivePlayback(true);
            CustomVideoPlayer.EndSourceLoading(); LivePlaybackNotice.Text = Localization.GetString("LiveWaiting");
            LivePlaybackNotice.Visibility = Visibility.Visible; StartLiveRetry();
        }

        private sealed class LiveBindAttempt
        {
            internal bool Opened;
            internal int HttpStatus;
        }

        private bool IsLiveBindingCurrent(string videoId, string owner, int generation, CancellationToken cancellation)
        {
            return !cancellation.IsCancellationRequested && IsStillCurrentVideo(videoId)
                && generation == _hlsRecoveryGeneration && owner == PlaybackResolver.AccountKey;
        }

        private async Task<LiveBindAttempt> TryBindLiveSabrAsync(JsonObject root, string videoId, string owner,
            int generation, int height, bool autoPlay, CancellationToken cancellation)
        {
            var result = new LiveBindAttempt();
            if (!LiveSabrSession.HasConfig(root) || PlaybackResolver.IsFinishedLive(root)) return result;
            var prepared = await PrepareLiveRootAsync(videoId, root, owner, generation);
            if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation) || prepared == null
                || LiveJson.Bool(prepared, "_uwpSkipLiveSabr")) return result;
            var client = (int)LiveJson.Number(prepared, "_uwpClientNumber");
            var binding = LiveJson.Bool(prepared, "_uwpHasPlayerProof") ? PlaybackResolver.LiveSessionBinding(prepared) : null;
            _currentLiveRoot = prepared;
            availableFormats.Clear(); CollectLiveFormats(prepared, availableFormats);
            var stream = await LiveMediaSource.CreateAsync(videoId, prepared, height, LiveJson.String(prepared, "_uwpPoToken"),
                _liveWatchedFrom, token => RefreshLiveRootAsync(videoId, owner, generation, token, client, binding), cancellation,
                status => result.HttpStatus = status);
            if (stream == null) return result;
            if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) { stream.Dispose(); return result; }
            try
            {
                CustomVideoPlayer.SetLivePlayback(true, stream);
                var opened = CustomVideoPlayer.SetDemuxedSource(stream.Source, autoPlay)
                    && await CustomVideoPlayer.WaitForLiveSourceOpenedAsync(cancellation);
                if (!IsLiveBindingCurrent(videoId, owner, generation, cancellation)) { stream.Dispose(); return result; }
                if (opened)
                {
                    _livePreferredClient = client;
                    _livePreferHls = false;
                    result.Opened = true;
                    System.Diagnostics.Debug.WriteLine("[Live] SABR source opened, client=" + client);
                    LivePlaybackNotice.Visibility = Visibility.Collapsed; StopLiveRetry();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Live] Native SABR source did not open, client=" + client);
                    stream.Dispose(); CustomVideoPlayer.Stop(); CustomVideoPlayer.SetLivePlayback(true);
                    CustomVideoPlayer.BeginSourceLoading();
                }
            }
            catch (Exception ex)
            {
                stream.Dispose();
                System.Diagnostics.Debug.WriteLine("[Live] Native SABR binding failed, client=" + client + ": " + ex.Message);
                if (IsLiveBindingCurrent(videoId, owner, generation, cancellation))
                {
                    CustomVideoPlayer.Stop(); CustomVideoPlayer.SetLivePlayback(true);
                    CustomVideoPlayer.BeginSourceLoading();
                }
            }
            return result;
        }

        private static void CollectLiveFormats(JsonObject root, IList<PlayerFormatModel> target)
        {
            foreach (var value in LiveJson.Array(LiveJson.Object(root, "streamingData"), "adaptiveFormats"))
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var f = value.GetObject(); var mime = LiveJson.String(f, "mimeType");
                if (mime.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) < 0 && mime.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) < 0) continue;
                target.Add(new PlayerFormatModel { Itag = (int)LiveJson.Number(f, "itag"), Width = (int)LiveJson.Number(f, "width"),
                    Height = (int)LiveJson.Number(f, "height"), MimeType = mime, IsAdaptive = true,
                    Fps = (int)LiveJson.Number(f, "fps"), HasVideo = mime.StartsWith("video/"), HasAudio = mime.StartsWith("audio/"),
                    Bitrate = (int)LiveJson.Number(f, "bitrate"), MediaUserAgent = PlaybackResolver.MediaUserAgent(root) });
            }
        }

        private void StartLiveRetry()
        {
            if (_liveRetryTimer == null)
            {
                _liveRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                _liveRetryTimer.Tick += async (s, e) => { _liveRetryTimer.Stop(); await RecoverLivePlaybackAsync(); };
            }
            _liveRetryTimer.Start();
        }
        private void StopLiveRetry() { if (_liveRetryTimer != null) _liveRetryTimer.Stop(); }

        private async Task RecoverLivePlaybackAsync()
        {
            if (_liveRecoveryPending || !IsLivePage) return;
            _liveRecoveryPending = true;
            var videoId = currentVideoId; var generation = _hlsRecoveryGeneration; var owner = PlaybackResolver.AccountKey;
            try
            {
                var client = _livePreferredClient > 0 ? _livePreferredClient : (int)LiveJson.Number(_currentLiveRoot, "_uwpClientNumber");
                var binding = LiveJson.Bool(_currentLiveRoot, "_uwpHasPlayerProof") ? PlaybackResolver.LiveSessionBinding(_currentLiveRoot) : null;
                var proof = string.IsNullOrEmpty(binding) ? null : await _liveProof.GetAsync(VideoRoot, binding);
                if (!IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey) return;
                var fresh = await PlaybackResolver.GetLivePlayerForClientAsync(videoId, client, null, proof);
                if (!IsStillCurrentVideo(videoId) || generation != _hlsRecoveryGeneration || owner != PlaybackResolver.AccountKey) return;
                if (PlaybackResolver.IsFinishedLive(fresh)) { StopLiveRetry(); CustomVideoPlayer.ConfirmLiveEnded(); return; }
                CustomVideoPlayer.Stop(); CustomVideoPlayer.BeginSourceLoading();
                // New live sessions have a new clock; a VOD resume position cannot be reused.
                await BindLivePlaybackAsync(fresh ?? _currentLiveRoot, true, GetEffectiveVideoQualityTag());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Live] Recovery deferred: " + ex.GetType().Name);
                if (IsStillCurrentVideo(videoId) && generation == _hlsRecoveryGeneration && owner == PlaybackResolver.AccountKey)
                { CustomVideoPlayer.EndSourceLoading(); StartLiveRetry(); }
            }
            finally { _liveRecoveryPending = false; }
        }

        private void ConfigureLiveChatFromNext(JsonObject root)
        {
            if (!IsLivePage) return;
            var token = LiveChatService.TokenFromNext(root); if (!string.IsNullOrEmpty(token)) _liveChatSeed = token;
        }
        private void LiveChatButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsLivePage) return;
            if (LiveChatWindow.IsOpen) { CloseLiveChat(); return; }
            LiveChatWindow.Open();
            LiveChatContent.Open(currentVideoId, _liveChatSeed);
        }
        private void LiveChatWindow_Closed(object sender, EventArgs e)
        {
            LiveChatContent.Stop();
            CollapseVideoOverlayIfNoSheetOpen();
        }
        private void CloseLiveChat(bool immediately = false)
        {
            if (LiveChatContent != null) LiveChatContent.Stop();
            if (LiveChatWindow == null || LiveChatWindow.Visibility != Visibility.Visible) return;
            if (immediately) LiveChatWindow.CloseImmediately();
            else LiveChatWindow.Close();
        }
        private void ResetLivePage()
        {
            if (_liveBindingCancel != null) { _liveBindingCancel.Cancel(); _liveBindingCancel = null; }
            CloseLiveChat(true); StopLiveRetry(); _liveVideoId = null; _liveChatSeed = null; _currentLiveRoot = null;
            _livePreferHls = false;
            _livePreferredClient = 0;
            RefreshVideoActionVisibility();
            if (LivePlaybackNotice != null) LivePlaybackNotice.Visibility = Visibility.Collapsed;
        }
    }
}
