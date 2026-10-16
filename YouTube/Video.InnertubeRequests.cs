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
        private const string InnertubeWebSafariUserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)";
        private string _playbackAccountCacheKey;

        private void SynchronizePlaybackAccount(string videoId)
        {
            var key = GetPlaybackAccountCacheKey();
            if (string.Equals(_playbackAccountCacheKey, key, StringComparison.Ordinal)) return;
            var hadPreviousAccount = !string.IsNullOrEmpty(_playbackAccountCacheKey);
            _playbackAccountCacheKey = key;
            if (!hadPreviousAccount) return;
            InvalidatePlaybackUrlCaches(videoId);
            lock (_androidPlayerRequestGate)
            {
                _lastAndroidPlayerVideoId = string.Empty;
                _lastAndroidPlayerJson = string.Empty;
                _androidPlayerRequestVideoId = string.Empty;
                _androidPlayerRequestTask = null;
            }
            ResetHlsPlaybackRecovery();
            InvalidateSessionVisitorData();
        }

        private static string GetPlaybackAccountCacheKey()
        {
            if (WebAccountSession.IsSignedIn) return WebAccountSession.CacheKey;
            Config.LoadUserToken();
            return WebAccountSession.CacheKey + "|"
                + (string.IsNullOrWhiteSpace(Config.UserToken) ? "guest"
                    : "qr:" + GetRequestTokenFingerprint(Config.UserToken)
                        + ":" + Config.SelectedYouTubeAccountBrandId);
        }

        private async Task<string> PostInnertubeAsync(string endpoint, string payload)
        {
            var response = await PostInnertubeJsonAsync(endpoint, payload).ConfigureAwait(false);
            return response.Text;
        }

        private async Task<InnertubeRequestCoordinator.JsonResponse> PostInnertubeJsonAsync(
            string endpoint,
            string payload)
        {
            await WebAccountSession.InitializeAsync();
            if (string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase))
                SynchronizePlaybackAccount(currentVideoId);
            var cacheAge = string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromSeconds(45);
            var key = BuildInnertubeCoordinatorKey(endpoint, payload);
            return await InnertubeRequestCoordinator.GetJsonAsync(
                key,
                delegate { return PostInnertubeCoreAsync(endpoint, payload); },
                cacheAge);
        }

        private static string BuildInnertubeCoordinatorKey(string endpoint, string payload)
        {
            return "video|" + GetPlaybackAccountCacheKey() + "|" + endpoint + "|" + (payload ?? string.Empty);
        }

        private void InvalidatePlaybackUrlCaches(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return;

            PlaybackResolver.Invalidate(videoId);
            InnertubeRequestCoordinator.Invalidate(
                BuildInnertubeCoordinatorKey("player", BuildPlayerPayload(videoId)));

            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_lastAndroidPlayerVideoId, videoId, StringComparison.Ordinal))
                {
                    _lastAndroidPlayerVideoId = string.Empty;
                    _lastAndroidPlayerJson = string.Empty;
                }

                if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                {
                    // The old request may still complete, but after its id is detached it cannot
                    // overwrite the newly refreshed cache entry.
                    _androidPlayerRequestVideoId = string.Empty;
                    _androidPlayerRequestTask = null;
                }
            }

            _lastPlayerJson = string.Empty;
            lock (_playerJsonParseGate)
            {
                _lastParsedPlayerJson = string.Empty;
                _lastParsedPlayerRoot = null;
            }
        }

        private async Task<string> PostInnertubeCoreAsync(string endpoint, string payload)
        {
            if (string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase))
            {
                JsonObject body;
                if (!JsonObject.TryParse(payload, out body)) throw new FormatException("Invalid player request");
                return await PostAndroidPlayerAsync(body.GetNamedString("videoId", string.Empty)).ConfigureAwait(false);
            }
            using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl(endpoint)))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", PlaybackResolver.WebUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20250101");
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                WebAccountSession.ApplyHeaders(request);
                using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) LogPlayerRequestFailure(endpoint, response, json);
                    response.EnsureSuccessStatusCode();
                    return json;
                }
            }
        }
        private static string BuildPlayerPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            // Match get_url.py exactly: IOS client → streamingData.hlsManifestUrl
            client["clientName"] = JsonValue.CreateStringValue("IOS");
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeIosPlayerClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("iPhone16,2");
            client["osName"] = JsonValue.CreateStringValue("iOS");
            client["osVersion"] = JsonValue.CreateStringValue("18.0");
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

        private async Task<string> PostAndroidPlayerAsync(string videoId)
        {
            await WebAccountSession.InitializeAsync();
            SynchronizePlaybackAccount(videoId);
            var requestAccountKey = GetPlaybackAccountCacheKey();
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            if (string.Equals(_lastAndroidPlayerVideoId, videoId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_lastAndroidPlayerJson))
            {
                _playbackMediaUserAgent = _lastAndroidPlayerUserAgent;
                return _lastAndroidPlayerJson;
            }

            Task<string> requestTask;
            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_lastAndroidPlayerVideoId, videoId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_lastAndroidPlayerJson))
                {
                    _playbackMediaUserAgent = _lastAndroidPlayerUserAgent;
                    return _lastAndroidPlayerJson;
                }

                if (_androidPlayerRequestTask == null || !string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                {
                    _androidPlayerRequestVideoId = videoId;
                    _androidPlayerRequestTask = PostAndroidPlayerCoreAsync(videoId);
                }

                requestTask = _androidPlayerRequestTask;
            }

            string json;
            try
            {
                json = await requestTask.ConfigureAwait(false);
                if (!string.Equals(requestAccountKey, GetPlaybackAccountCacheKey(), StringComparison.Ordinal))
                    return string.Empty;
            }
            catch
            {
                lock (_androidPlayerRequestGate)
                {
                    if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal)
                        && ReferenceEquals(_androidPlayerRequestTask, requestTask))
                    {
                        _androidPlayerRequestTask = null;
                    }
                }
                throw;
            }

            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal)
                    && ReferenceEquals(_androidPlayerRequestTask, requestTask))
                {
                    // Only cache a response that actually carries streams. A stream-less
                    // (bot-gated / throttled) response must NOT stick, or every later itag18 /
                    // muxed lookup for this video reuses the broken player response.
                    if (PlayerJsonHasStreams(json) || PlayerJsonHasHlsManifest(json))
                    {
                        _lastAndroidPlayerVideoId = videoId;
                        _lastAndroidPlayerJson = json;
                        _lastAndroidPlayerUserAgent = PlaybackResolver.MediaUserAgent(ParsePlayerJsonObject(json));
                        _playbackMediaUserAgent = _lastAndroidPlayerUserAgent;
                    }
                    else
                    {
                        _lastAndroidPlayerJson = string.Empty;
                    }

                    _androidPlayerRequestTask = null;
                }
            }

            return json;
        }

        private async Task<string> PostAndroidPlayerCoreAsync(string videoId)
        {
            var response = await PlaybackResolver.GetAsync(videoId).ConfigureAwait(false);
            if (response == null) return string.Empty;
            if (IsStillCurrentVideo(videoId)) CaptureVisitorData(response.Root);
            return response.Json;
        }
        private static void LogPlayerRequestFailure(string endpoint, HttpResponseMessage response, string json)
        {
            // Only report the server's symbolic status. Response bodies
            // can contain account data, cookies or signed URLs and must not be logged.
            var detail = string.Empty;
            JsonObject root;
            IJsonValue value;
            if (JsonObject.TryParse(json ?? string.Empty, out root)
                && root.TryGetValue("error", out value) && value.ValueType == JsonValueType.Object)
            {
                var error = value.GetObject();
                var status = error.TryGetValue("status", out value) && value.ValueType == JsonValueType.String
                    ? value.GetString() : string.Empty;
                if (Regex.IsMatch(status, "^[A-Z_]{1,64}$")) detail = ", server=" + status;
            }
            System.Diagnostics.Debug.WriteLine("[Video] /" + endpoint + " failed: HTTP "
                + (int)response.StatusCode + detail);
        }

    }
}
