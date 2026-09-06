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
        // Ensures the track list is populated even when the current playback path never demuxed
        // (e.g. Auto/progressive) — it fetches the ANDROID formats just to enumerate languages.
        private async Task EnsureAudioTracksLoadedAsync()
        {
            if (_availableAudioTracks != null && _availableAudioTracks.Count > 0)
            {
                return;
            }
            try
            {
                var androidJson = await PostAndroidPlayerAsync(currentVideoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return;
                }
                var root = JsonValue.Parse(androidJson).GetObject();
                var formats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(root, formats);
                _availableAudioTracks = Config.EnumerateAudioTracks(formats);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] EnsureAudioTracksLoaded failed: " + ex.Message);
            }
        }

        // AAC (mp4a) audio-only track for the combined DASH manifest. AAC is the WP-friendly
        // codec. On a multi-language video the Windows system language is preferred, then the
        // server default; passing a specific trackId overrides that. Within the chosen
        // track, the highest-bitrate m4a wins.
        private static PlayerFormatModel SelectAudioOnlyAacFormat(
            IList<PlayerFormatModel> formats, string preferredTrackId = null)
        {
            if (formats == null)
            {
                return null;
            }

            // All usable AAC audio-only formats.
            var audio = new List<PlayerFormatModel>();
            foreach (var f in formats)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Url) || !f.IsAdaptive || !f.HasAudio || f.HasVideo)
                {
                    continue;
                }
                var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                if (mime.IndexOf("audio/mp4") < 0 && mime.IndexOf("mp4a") < 0)
                {
                    continue;
                }
                audio.Add(f);
            }

            if (audio.Count == 0)
            {
                return null;
            }

            // Explicit choice, Windows system language, YouTube's default, then highest bitrate.
            List<PlayerFormatModel> pool = null;
            if (!string.IsNullOrEmpty(preferredTrackId))
            {
                pool = audio.FindAll(f => string.Equals(f.AudioTrackId, preferredTrackId, StringComparison.Ordinal));
            }
            if (pool == null || pool.Count == 0)
            {
                var systemScore = 0;
                foreach (var f in audio)
                {
                    systemScore = Math.Max(systemScore, Config.SystemAudioTrackMatchScore(f.AudioTrackId));
                }
                if (systemScore > 0)
                {
                    pool = audio.FindAll(f => Config.SystemAudioTrackMatchScore(f.AudioTrackId) == systemScore);
                }
                else
                {
                    var defaults = audio.FindAll(f => f.AudioIsDefault);
                    pool = defaults.Count > 0 ? defaults : audio;
                }
            }

            PlayerFormatModel best = null;
            foreach (var f in pool)
            {
                if (best == null || f.Bitrate > best.Bitrate)
                {
                    best = f;
                }
            }

            return best;
        }

        // Exact screen-tier part of youtube-ios YTSabr.tierForScreen:. UIKit passes the
        // physical short edge (points * scale), not the current video rectangle.
        private static int GetAutomaticScreenQualityCap()
        {
            var tiers = new[] { 144, 240, 360, 480, 720, 1080 };
            double scale = 1.0;
            double shortEdge = 0.0;
            try
            {
                scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
                if (scale <= 0.0)
                {
                    scale = 1.0;
                }
                var bounds = Window.Current.Bounds;
                shortEdge = Math.Min(bounds.Width, bounds.Height) * scale;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video][Auto] screen query failed: " + ex.Message);
            }

            var nearest = AutomaticVideoQualityCap;
            if (shortEdge > 0.0)
            {
                nearest = tiers[0];
                var nearestDistance = Math.Abs(shortEdge - nearest);
                for (int i = 1; i < tiers.Length; i++)
                {
                    var distance = Math.Abs(shortEdge - tiers[i]);
                    if (distance < nearestDistance)
                    {
                        nearest = tiers[i];
                        nearestDistance = distance;
                    }
                }
            }

            nearest = Math.Min(nearest, AutomaticVideoQualityCap);
            System.Diagnostics.Debug.WriteLine(
                "[Video][Auto] screen=" + shortEdge.ToString("0", CultureInfo.InvariantCulture)
                + "px scale=" + scale.ToString("0.##", CultureInfo.InvariantCulture)
                + " cap=" + nearest + "p");
            return nearest;
        }

        private async Task<PlayerFormatModel> SelectAutomaticH264VideoOnlyFormatAsync(
            IList<PlayerFormatModel> formats,
            string screenQualityTag,
            ISet<int> excludedItags)
        {
            var screenChoice = SelectPreferredH264VideoOnlyFormat(
                formats,
                screenQualityTag,
                excludedItags);
            if (screenChoice == null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (_autoNetworkBitsPerSecond <= 0.0
                || (now - _autoNetworkEstimateTime).TotalSeconds > 30.0)
            {
                _autoNetworkBitsPerSecond = await MeasurePlaybackBandwidthAsync(screenChoice.Url);
                _autoNetworkEstimateTime = now;
            }

            if (_autoNetworkBitsPerSecond <= 0.0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][Auto] bandwidth unavailable; using screen cap "
                    + screenChoice.QualityTier + "p");
                return screenChoice;
            }

            var usableVideoBitsPerSecond = (_autoNetworkBitsPerSecond * AutoNetworkSafetyFactor)
                - AutoAudioBandwidthReserve;
            var screenCap = ParseInt(screenQualityTag);
            var tiersDescending = new[] { 1080, 720, 480, 360, 240, 144 };
            PlayerFormatModel lowest = null;
            for (int i = 0; i < tiersDescending.Length; i++)
            {
                var tier = tiersDescending[i];
                if (screenCap > 0 && tier > screenCap)
                {
                    continue;
                }

                var candidate = SelectPreferredH264VideoOnlyFormat(
                    formats,
                    tier.ToString(CultureInfo.InvariantCulture),
                    excludedItags);
                if (candidate == null)
                {
                    continue;
                }

                if (lowest == null || candidate.QualityTier < lowest.QualityTier)
                {
                    lowest = candidate;
                }

                var bitrate = candidate.AverageBitrate > 0
                    ? candidate.AverageBitrate
                    : candidate.Bitrate;
                if (bitrate <= 0 || bitrate <= usableVideoBitsPerSecond)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video][Auto] bandwidth="
                        + (_autoNetworkBitsPerSecond / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture)
                        + "Mbps usableVideo="
                        + (Math.Max(0.0, usableVideoBitsPerSecond) / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture)
                        + "Mbps selected=" + candidate.QualityTier + "p"
                        + " bitrate=" + bitrate);
                    return candidate;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video][Auto] connection is below the lowest advertised bitrate; selected "
                + (lowest == null ? screenChoice.QualityTier : lowest.QualityTier) + "p");
            return lowest ?? screenChoice;
        }

        private async Task<double> MeasurePlaybackBandwidthAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return 0.0;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new RangeHeaderValue(0, AutoNetworkProbeBytes - 1);
                    if (!string.IsNullOrWhiteSpace(_playbackMediaUserAgent))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", _playbackMediaUserAgent);
                    }

                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    using (var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[Video][Auto] bandwidth probe HTTP " + (int)response.StatusCode);
                            return 0.0;
                        }

                        long received = 0;
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[32768];
                            while (received < AutoNetworkProbeBytes)
                            {
                                var wanted = (int)Math.Min(buffer.Length, AutoNetworkProbeBytes - received);
                                var count = await stream.ReadAsync(buffer, 0, wanted);
                                if (count <= 0)
                                {
                                    break;
                                }
                                received += count;
                            }
                        }
                        timer.Stop();

                        if (received < 32768 || timer.Elapsed.TotalSeconds <= 0.0)
                        {
                            return 0.0;
                        }
                        return received * 8.0 / timer.Elapsed.TotalSeconds;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video][Auto] bandwidth probe failed: " + ex.Message);
                return 0.0;
            }
        }

        // Build a combined video+audio on-demand DASH manifest (two AdaptationSets, each an
        // indexed single-file SegmentBase representation). AdaptiveMediaSource muxes/syncs the
        // two remote streams itself. Requires init/index byte ranges on both formats.
        private async Task<Uri> CreateCombinedDashManifestUriAsync(
            string videoId,
            PlayerFormatModel video,
            PlayerFormatModel audio
        )
        {
            if (
                video == null
                || audio == null
                || string.IsNullOrWhiteSpace(video.Url)
                || string.IsNullOrWhiteSpace(audio.Url)
                || string.IsNullOrWhiteSpace(video.InitRangeStart)
                || string.IsNullOrWhiteSpace(video.InitRangeEnd)
                || string.IsNullOrWhiteSpace(video.IndexRangeStart)
                || string.IsNullOrWhiteSpace(video.IndexRangeEnd)
                || string.IsNullOrWhiteSpace(audio.InitRangeStart)
                || string.IsNullOrWhiteSpace(audio.InitRangeEnd)
                || string.IsNullOrWhiteSpace(audio.IndexRangeStart)
                || string.IsNullOrWhiteSpace(audio.IndexRangeEnd)
            )
            {
                System.Diagnostics.Debug.WriteLine("[Video] Combined DASH manifest skipped: missing byte ranges");
                return null;
            }

            try
            {
                var durationSeconds = GetDashDurationSeconds(video.Url);
                var videoCodecs = ExtractCodecsFromMimeType(video.MimeType);
                var audioCodecs = ExtractCodecsFromMimeType(audio.MimeType);
                var videoBandwidth = video.AverageBitrate > 0
                    ? video.AverageBitrate
                    : (video.Bitrate > 0 ? video.Bitrate : 1500000);
                var audioBandwidth = audio.AverageBitrate > 0
                    ? audio.AverageBitrate
                    : (audio.Bitrate > 0 ? audio.Bitrate : 128000);
                var frameRate = video.Fps > 0 ? " frameRate=\"" + video.Fps + "\"" : string.Empty;

                var mpd = new StringBuilder();
                mpd.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                mpd.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT" + durationSeconds + "S\" minBufferTime=\"PT1.5S\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\">");
                mpd.AppendLine("  <Period>");
                mpd.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\" startWithSAP=\"1\">");
                mpd.AppendLine("      <Representation id=\"" + video.Itag + "\" codecs=\"" + EscapeXml(videoCodecs) + "\" bandwidth=\"" + videoBandwidth + "\" width=\"" + video.Width + "\" height=\"" + video.Height + "\"" + frameRate + ">");
                mpd.AppendLine("        <BaseURL>" + EscapeXml(video.Url) + "</BaseURL>");
                mpd.AppendLine("        <SegmentBase indexRange=\"" + EscapeXml(video.IndexRangeStart) + "-" + EscapeXml(video.IndexRangeEnd) + "\">");
                mpd.AppendLine("          <Initialization range=\"" + EscapeXml(video.InitRangeStart) + "-" + EscapeXml(video.InitRangeEnd) + "\" />");
                mpd.AppendLine("        </SegmentBase>");
                mpd.AppendLine("      </Representation>");
                mpd.AppendLine("    </AdaptationSet>");
                mpd.AppendLine("    <AdaptationSet mimeType=\"audio/mp4\" segmentAlignment=\"true\" startWithSAP=\"1\">");
                mpd.AppendLine("      <Representation id=\"" + audio.Itag + "\" codecs=\"" + EscapeXml(audioCodecs) + "\" bandwidth=\"" + audioBandwidth + "\" audioSamplingRate=\"44100\">");
                mpd.AppendLine("        <AudioChannelConfiguration schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\" />");
                mpd.AppendLine("        <BaseURL>" + EscapeXml(audio.Url) + "</BaseURL>");
                mpd.AppendLine("        <SegmentBase indexRange=\"" + EscapeXml(audio.IndexRangeStart) + "-" + EscapeXml(audio.IndexRangeEnd) + "\">");
                mpd.AppendLine("          <Initialization range=\"" + EscapeXml(audio.InitRangeStart) + "-" + EscapeXml(audio.InitRangeEnd) + "\" />");
                mpd.AppendLine("        </SegmentBase>");
                mpd.AppendLine("      </Representation>");
                mpd.AppendLine("    </AdaptationSet>");
                mpd.AppendLine("  </Period>");
                mpd.AppendLine("</MPD>");

                var safeVideoId = Regex.Replace(videoId ?? string.Empty, @"[^A-Za-z0-9_-]+", "_");
                if (string.IsNullOrWhiteSpace(safeVideoId))
                {
                    safeVideoId = "video";
                }

                var fileName = "youtube_av_" + safeVideoId + "_" + video.Itag + "_" + audio.Itag + ".mpd";
                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.ReplaceExisting
                );
                await FileIO.WriteTextAsync(file, mpd.ToString());

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Created combined A/V DASH manifest: " + fileName
                    + " (video itag=" + video.Itag + ", audio itag=" + audio.Itag + ")"
                );
                return new Uri("ms-appdata:///temp/" + fileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to create combined DASH manifest: " + ex.Message);
                return null;
            }
        }

        private static string GetDashDurationSeconds(string streamUrl)
        {
            var dur = GetQueryParameterFromUrl(streamUrl, "dur");
            double duration;
            if (
                !string.IsNullOrWhiteSpace(dur)
                && double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                && duration > 0
            )
            {
                return duration.ToString("0.###", CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string ExtractCodecsFromMimeType(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                return "avc1.4d401e";
            }

            var match = Regex.Match(mimeType, "codecs=\"(?<codecs>[^\"]+)\"");
            return match.Success ? match.Groups["codecs"].Value : "avc1.4d401e";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private async Task<string> GetPlayerScriptUrlAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            var watchUrl = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);
            using (var request = new HttpRequestMessage(HttpMethod.Get, watchUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36"
                );
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var match = Regex.Match(
                    html,
                    @"(?<path>(?:https?:)?(?:\\?/\\?/www\.youtube\.com)?\\?/s\\?/player\\?/[0-9a-fA-F]+\\?/[^""']+?base\.js)"
                );
                if (!match.Success)
                {
                    match = Regex.Match(
                        html,
                        @"(?<path>/s/player/[0-9a-fA-F]+/[^""'\\]+base\.js)"
                    );
                }

                if (!match.Success)
                {
                    return string.Empty;
                }

                var playerPath = match.Groups["path"].Value.Replace("\\/", "/");
                if (playerPath.StartsWith("//", StringComparison.Ordinal))
                {
                    return "https:" + playerPath;
                }

                if (playerPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || playerPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return playerPath;
                }

                return "https://www.youtube.com" + playerPath;
            }
        }

        private async Task<string> GetPlayerScriptAsync(string playerScriptUrl)
        {
            if (string.IsNullOrWhiteSpace(playerScriptUrl))
            {
                return string.Empty;
            }

            Task<string> requestTask;
            lock (_playerScriptGate)
            {
                if (string.Equals(_lastPlayerScriptUrl, playerScriptUrl, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_lastPlayerScript))
                {
                    return _lastPlayerScript;
                }

                if (_playerScriptRequestTask != null
                    && string.Equals(_playerScriptRequestUrl, playerScriptUrl, StringComparison.Ordinal))
                {
                    requestTask = _playerScriptRequestTask;
                }
                else
                {
                    _playerScriptRequestUrl = playerScriptUrl;
                    _playerScriptRequestTask = DownloadPlayerScriptCoreAsync(playerScriptUrl);
                    requestTask = _playerScriptRequestTask;
                }
            }

            string script;
            try
            {
                script = await requestTask;
            }
            finally
            {
                lock (_playerScriptGate)
                {
                    _playerScriptRequestTask = null;
                }
            }

            lock (_playerScriptGate)
            {
                _lastPlayerScriptUrl = playerScriptUrl;
                _lastPlayerScript = script;
            }

            return script;
        }

        private async Task<string> DownloadPlayerScriptCoreAsync(string playerScriptUrl)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, playerScriptUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36"
                );
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<string> ExecuteNFunctionInWebViewAsync(
            string playerScript,
            string nFunctionExpression,
            string nValue
        )
        {
            var tcs = new TaskCompletionSource<string>();
            var dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                WebView webView = null;
                try
                {
                    webView = new WebView();
                    var ready = new TaskCompletionSource<bool>();
                    Windows.Foundation.TypedEventHandler<WebView, WebViewNavigationCompletedEventArgs> handler = null;
                    handler = (sender, args) =>
                    {
                        webView.NavigationCompleted -= handler;
                        ready.TrySetResult(true);
                    };
                    webView.NavigationCompleted += handler;
                    webView.NavigateToString("<html><head><meta charset=\"utf-8\" /></head><body></body></html>");
                    await ready.Task;

                    var scriptToEvaluate = BuildPlayerScriptWithNExport(playerScript, nFunctionExpression);
                    await webView.InvokeScriptAsync("eval", new[] { scriptToEvaluate });

                    var callScript = "window.__yt_nsig(" + ToJavaScriptStringLiteral(nValue) + ");";
                    var result = await webView.InvokeScriptAsync("eval", new[] { callScript });
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] WebView n function failed: " + ex.Message);
                    tcs.TrySetResult(string.Empty);
                }
            });

            return await tcs.Task;
        }

        private static string BuildPlayerScriptWithNExport(string playerScript, string nFunctionExpression)
        {
            var exportScript =
                ";window.__yt_nsig=function(n){return "
                + nFunctionExpression
                + "(n);};";
            var closingIndex = playerScript.LastIndexOf(";})(_yt_player);", StringComparison.Ordinal);
            if (closingIndex >= 0)
            {
                return playerScript.Insert(closingIndex, exportScript);
            }

            closingIndex = playerScript.LastIndexOf("})(_yt_player);", StringComparison.Ordinal);
            if (closingIndex >= 0)
            {
                return playerScript.Insert(closingIndex, exportScript);
            }

            return playerScript + exportScript;
        }

        private static string ExtractNFunctionExpression(string playerScript)
        {
            if (string.IsNullOrWhiteSpace(playerScript))
            {
                return string.Empty;
            }

            var patterns = new[]
            {
                @"\.get\(""n""\)\)&&\([a-zA-Z_$][\w$]*=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\([a-zA-Z_$][\w$]*\)",
                @"[a-zA-Z_$][\w$]*=String\.fromCharCode\(110\),[a-zA-Z_$][\w$]*=[a-zA-Z_$][\w$]*\.get\([a-zA-Z_$][\w$]*\)\)&&\([a-zA-Z_$][\w$]*=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\([a-zA-Z_$][\w$]*\)",
                @"\b(?<var>[a-zA-Z_$][\w$]*)&&\(\k<var>=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\(\k<var>\)",
                @"(?<name>[a-zA-Z_$][\w$]*)=function\([a-zA-Z_$][\w$]*\)\{(?=[\s\S]{0,1200}\.split\(""""\))(?=[\s\S]{0,2400}\.join\(""""\))"
            };

            for (int i = 0; i < patterns.Length; i++)
            {
                var match = Regex.Match(playerScript, patterns[i]);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var indexGroup = match.Groups["idx"];
                if (indexGroup != null && indexGroup.Success && !string.IsNullOrWhiteSpace(indexGroup.Value))
                {
                    return name + "[" + indexGroup.Value + "]";
                }

                return name;
            }

            return string.Empty;
        }

        private async Task<PlayerFormatModel> GetAndroidSingleMediaSourceForMobileAsync(string videoId)
        {
            try
            {
                var selectedFormat = await GetAndroidAudioVideoTrackFormatAsync(videoId, false, false);
                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    return null;
                }

                return selectedFormat;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Failed to get Android single-source media for mobile: " + ex.Message
                );
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidAudioVideoTrackFormatAsync(
            string videoId,
            bool preferSmallest,
            bool preferMobileCarrier
        )
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
            if (string.IsNullOrWhiteSpace(androidPlayerJson))
            {
                return null;
            }

            var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
            var androidFormats = new List<PlayerFormatModel>();
            CollectFormatsFromStreamingData(androidRoot, androidFormats);

            if (preferSmallest)
            {
                if (preferMobileCarrier)
                {
                    var mobileCarrier = SelectPreferredMobileAudioCarrierFormat(androidFormats);
                    if (mobileCarrier != null)
                    {
                        return mobileCarrier;
                    }
                }

                return SelectPreferredAudioVideoTrackFormat(androidFormats);
            }

            // Not used for the main Mobile path anymore. If this fallback is called,
            // still prefer the best progressive MP4 available.
            return SelectPreferredProgressiveFormat(androidFormats, string.Empty, false)
                ?? SelectPreferredAudioVideoTrackFormat(androidFormats);
        }

        private static string ExtractLastThumbnailUrl(JsonObject objectWithThumbs)
        {
            if (objectWithThumbs == null || !objectWithThumbs.ContainsKey("thumbnails"))
            {
                return string.Empty;
            }

            var thumbs = objectWithThumbs.GetNamedArray("thumbnails");
            if (thumbs.Count == 0)
            {
                return string.Empty;
            }

            for (int i = (int)thumbs.Count - 1; i >= 0; i--)
            {
                var thumbObj = thumbs[i].GetObject();
                var url = GetJsonString(thumbObj, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private void CollectFormatsFromStreamingData(JsonObject root)
        {
            CollectFormatsFromStreamingData(root, availableFormats);
        }

        private static void CollectFormatsFromStreamingData(
            JsonObject root,
            IList<PlayerFormatModel> targetFormats
        )
        {
            if (
                root == null
                || targetFormats == null
                || !root.ContainsKey("streamingData")
            )
            {
                return;
            }

            var streamingData = root.GetNamedObject("streamingData");
            AddFormatsFromArray(streamingData, "formats", targetFormats, false);
            AddFormatsFromArray(streamingData, "adaptiveFormats", targetFormats, true);
        }

        private static void AddFormatsFromArray(
            JsonObject streamingData,
            string arrayName,
            IList<PlayerFormatModel> targetFormats,
            bool isAdaptive
        )
        {
            if (
                streamingData == null
                || targetFormats == null
                || !streamingData.ContainsKey(arrayName)
            )
            {
                return;
            }

            var formatsArray = streamingData.GetNamedArray(arrayName);
            for (int i = 0; i < formatsArray.Count; i++)
            {
                if (formatsArray[i].ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var formatObj = formatsArray[i].GetObject();
                var streamUrl = GetStreamUrlFromFormat(formatObj);
                if (string.IsNullOrWhiteSpace(streamUrl))
                {
                    continue;
                }

                var mimeType = GetJsonString(formatObj, "mimeType");
                targetFormats.Add(
                    new PlayerFormatModel
                    {
                        Url = streamUrl,
                        Width = ParseIntOrDefault(formatObj, "width"),
                        Height = ParseIntOrDefault(formatObj, "height"),
                        MimeType = mimeType,
                        Itag = ParseIntOrDefault(formatObj, "itag"),
                        Fps = ParseIntOrDefault(formatObj, "fps"),
                        Bitrate = ParseIntOrDefault(formatObj, "bitrate"),
                        AverageBitrate = ParseIntOrDefault(formatObj, "averageBitrate"),
                        ContentLength = ParseLongOrDefault(formatObj, "contentLength"),
                        InitRangeStart = GetRangeValue(formatObj, "initRange", "start"),
                        InitRangeEnd = GetRangeValue(formatObj, "initRange", "end"),
                        IndexRangeStart = GetRangeValue(formatObj, "indexRange", "start"),
                        IndexRangeEnd = GetRangeValue(formatObj, "indexRange", "end"),
                        HasAudio = FormatHasAudio(formatObj, mimeType),
                        HasVideo = FormatHasVideo(formatObj, mimeType),
                        IsAdaptive = isAdaptive,
                        AudioTrackId = GetAudioTrackField(formatObj, "id"),
                        AudioTrackName = GetAudioTrackField(formatObj, "displayName"),
                        AudioIsDefault = GetAudioTrackIsDefault(formatObj),
                    }
                );
            }
        }

        // The per-format "audioTrack" block appears only on multi-language videos.
        private static string GetAudioTrackField(JsonObject formatObj, string key)
        {
            try
            {
                if (formatObj != null && formatObj.ContainsKey("audioTrack"))
                {
                    var track = formatObj.GetNamedObject("audioTrack");
                    if (track.ContainsKey(key))
                    {
                        return track.GetNamedString(key);
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static bool GetAudioTrackIsDefault(JsonObject formatObj)
        {
            try
            {
                if (formatObj != null && formatObj.ContainsKey("audioTrack"))
                {
                    var track = formatObj.GetNamedObject("audioTrack");
                    if (track.ContainsKey("audioIsDefault"))
                    {
                        return track.GetNamedBoolean("audioIsDefault");
                    }
                }
            }
            catch { }
            return false;
        }

        private static string GetStreamUrlFromFormat(JsonObject formatObj)
        {
            if (formatObj == null)
            {
                return string.Empty;
            }

            var url = GetJsonString(formatObj, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            var signatureCipher = FirstNonEmpty(
                GetJsonString(formatObj, "signatureCipher"),
                GetJsonString(formatObj, "cipher")
            );

            if (string.IsNullOrWhiteSpace(signatureCipher))
            {
                return string.Empty;
            }

            var parsed = ParseQueryString(signatureCipher);
            string cipherUrl;
            if (!parsed.TryGetValue("url", out cipherUrl) || string.IsNullOrWhiteSpace(cipherUrl))
            {
                return string.Empty;
            }

            string signature;
            if (
                parsed.TryGetValue("sig", out signature)
                || parsed.TryGetValue("signature", out signature)
            )
            {
                if (!string.IsNullOrWhiteSpace(signature))
                {
                    return AppendQueryParameter(cipherUrl, "sig", signature);
                }
            }

            string s;
            if (parsed.TryGetValue("s", out s) && !string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            return cipherUrl;
        }

    }
}
