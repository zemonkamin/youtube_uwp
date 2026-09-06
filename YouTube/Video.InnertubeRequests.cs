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
        private async Task<string> PostInnertubeAsync(string endpoint, string payload)
        {
            var response = await PostInnertubeJsonAsync(endpoint, payload).ConfigureAwait(false);
            return response.Text;
        }

        private Task<InnertubeRequestCoordinator.JsonResponse> PostInnertubeJsonAsync(
            string endpoint,
            string payload)
        {
            var cacheAge = string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase)
                ? TimeSpan.FromMinutes(2)
                : TimeSpan.FromSeconds(45);
            var key = BuildInnertubeCoordinatorKey(endpoint, payload);
            return InnertubeRequestCoordinator.GetJsonAsync(
                key,
                delegate { return PostInnertubeCoreAsync(endpoint, payload); },
                cacheAge);
        }

        private static string BuildInnertubeCoordinatorKey(string endpoint, string payload)
        {
            return "video|anonymous|" + endpoint + "|" + (payload ?? string.Empty);
        }

        private void InvalidatePlaybackUrlCaches(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
                return;

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
            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (endpoint == "player")
                {
                    // Exact IOS primary /player request from youtube-ios.
                    request.Headers.TryAddWithoutValidation("User-Agent", InnertubeIosPlayerUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", InnertubeIosPlayerClientHeaderName);
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", InnertubeIosPlayerClientVersion);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
                    );
                    request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                }

                using (var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
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
            }
            catch
            {
                lock (_androidPlayerRequestGate)
                {
                    if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                    {
                        _androidPlayerRequestTask = null;
                    }
                }
                throw;
            }

            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                {
                    // Only cache a response that actually carries streams. A stream-less
                    // (bot-gated / throttled) response must NOT stick, or every later itag18 /
                    // muxed lookup for this video reuses the broken player response.
                    if (PlayerJsonHasStreams(json))
                    {
                        _lastAndroidPlayerVideoId = videoId;
                        _lastAndroidPlayerJson = json;
                        _lastAndroidPlayerUserAgent = _playbackMediaUserAgent;
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
            // Same chain as YTApi.androidVrPlayerResponse:primary:. The IOS response has
            // already been requested by LoadVideoDetailsFastAsync and is deliberately held
            // for the last fallback while its responseContext seeds visitorData.
            var primary = _lastPlayerJson;
            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = await FetchIosPlayerAsync(videoId, null).ConfigureAwait(false);
            }
            CaptureVisitorDataFromString(primary);

            // VISIONOS is first: unlike the other anonymous app clients its media session is
            // not cut off around the first minute.
            var vision = await FetchVisionPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(vision))
            {
                _playbackMediaUserAgent = InnertubeVisionUserAgent;
                System.Diagnostics.Debug.WriteLine("[Video] Playback URLs from VISIONOS");
                return vision;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] VISIONOS returned no ready URLs (" + GetPlayabilityReason(vision)
                + "); trying ANDROID_VR"
            );

            var json = await FetchAndroidVrPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(json))
            {
                _playbackMediaUserAgent = InnertubeAndroidVrUserAgent;
                return json;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] ANDROID_VR returned no ready URLs (" + GetPlayabilityReason(json)
                + "); refreshing visitorData and retrying"
            );
            InvalidateSessionVisitorData();

            var retryJson = await FetchAndroidVrPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(retryJson))
            {
                _playbackMediaUserAgent = InnertubeAndroidVrUserAgent;
                System.Diagnostics.Debug.WriteLine("[Video] ANDROID_VR retry with fresh visitorData succeeded");
                return retryJson;
            }

            string progressiveFallback = string.Empty;
            string progressiveFallbackAgent = string.Empty;
            var accessToken = await GetTvAccessTokenAsync(false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var tv = await FetchTvPlaybackPlayerAsync(
                    videoId,
                    InnertubePlaybackTvClientVersion,
                    accessToken
                ).ConfigureAwait(false);
                CaptureVisitorDataFromString(tv);
                if (PlayerJsonHasAdaptiveStreams(tv))
                {
                    _playbackMediaUserAgent = InnertubePlaybackTvUserAgent;
                    return tv;
                }
                if (PlayerJsonHasStreams(tv))
                {
                    progressiveFallback = tv;
                    progressiveFallbackAgent = InnertubePlaybackTvUserAgent;
                }

                var legacy = await FetchTvPlaybackPlayerAsync(
                    videoId,
                    InnertubePlaybackTvLegacyClientVersion,
                    accessToken
                ).ConfigureAwait(false);
                if (PlayerJsonHasAdaptiveStreams(legacy))
                {
                    _playbackMediaUserAgent = InnertubePlaybackTvUserAgent;
                    return legacy;
                }
                if (string.IsNullOrWhiteSpace(progressiveFallback) && PlayerJsonHasStreams(legacy))
                {
                    progressiveFallback = legacy;
                    progressiveFallbackAgent = InnertubePlaybackTvUserAgent;
                }
            }

            if (PlayerJsonHasStreams(primary))
            {
                _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                return primary;
            }

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var signedInIos = await FetchIosPlayerAsync(videoId, accessToken).ConfigureAwait(false);
                if (PlayerJsonHasStreams(signedInIos))
                {
                    _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                    return signedInIos;
                }
            }

            if (!string.IsNullOrWhiteSpace(progressiveFallback))
            {
                _playbackMediaUserAgent = progressiveFallbackAgent;
                return progressiveFallback;
            }

            // youtube-ios keeps the primary IOS response when it at least contains ready HLS.
            if (PlayerJsonHasHlsManifest(primary))
            {
                _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                return primary;
            }

            return string.IsNullOrWhiteSpace(retryJson) ? json : retryJson;
        }

        private async Task<string> FetchAndroidVrPlayerAsync(string videoId)
        {
            // Ported from MeeTube (core::fetchPlayer / ContextBuilder): the ANDROID_VR client
            // WITH the session visitorData is the anonymous default that returns direct,
            // ready-to-fetch progressive + adaptive URLs (no signature cipher, no &n=, no Po
            // token). No InnerTube API key is sent — the X-YouTube-Client-* headers identify
            // the client, exactly as MeeTube's transport does.
            var visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
            return await PostDirectPlayerPayloadAsync(
                BuildAndroidVrPlayerPayload(videoId, visitorData),
                InnertubeAndroidVrUserAgent,
                InnertubeAndroidVrClientHeaderName,
                InnertubeAndroidVrClientVersion,
                visitorData,
                null,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchVisionPlayerAsync(string videoId)
        {
            var visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeVisionClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeVisionClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("RealityDevice14,1");
            client["osName"] = JsonValue.CreateStringValue("visionOS");
            client["osVersion"] = JsonValue.CreateStringValue("1.0.2.21O209");
            client["userAgent"] = JsonValue.CreateStringValue(InnertubeVisionUserAgent);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                client["visitorData"] = JsonValue.CreateStringValue(visitorData);
            }
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubeVisionUserAgent,
                InnertubeVisionClientHeaderName,
                InnertubeVisionClientVersion,
                visitorData,
                null,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchIosPlayerAsync(string videoId, string accessToken)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("IOS");
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeIosPlayerClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("iPhone16,2");
            client["osName"] = JsonValue.CreateStringValue("iOS");
            client["osVersion"] = JsonValue.CreateStringValue("18.0");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);

            string visitorData = null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(visitorData))
                {
                    client["visitorData"] = JsonValue.CreateStringValue(visitorData);
                }
            }
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubeIosPlayerUserAgent,
                InnertubeIosPlayerClientHeaderName,
                InnertubeIosPlayerClientVersion,
                visitorData,
                accessToken,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchTvPlaybackPlayerAsync(
            string videoId,
            string clientVersion,
            string accessToken
        )
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(clientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["deviceMake"] = JsonValue.CreateStringValue("Samsung");
            client["deviceModel"] = JsonValue.CreateStringValue("SmartTV");
            client["osName"] = JsonValue.CreateStringValue("Tizen");
            client["osVersion"] = JsonValue.CreateStringValue("5.0");

            var context = new JsonObject();
            context["client"] = client;

            var contentPlaybackContext = new JsonObject();
            contentPlaybackContext["html5Preference"] = JsonValue.CreateStringValue("HTML5_PREF_WANTS");
            var sts = await global::Config.GetSignatureTimestampAsync(false).ConfigureAwait(false);
            if (sts > 0)
            {
                contentPlaybackContext["signatureTimestamp"] = JsonValue.CreateNumberValue(sts);
            }

            var playbackContext = new JsonObject();
            playbackContext["contentPlaybackContext"] = contentPlaybackContext;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["playbackContext"] = playbackContext;

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubePlaybackTvUserAgent,
                InnertubeTvClientHeaderName,
                clientVersion,
                null,
                accessToken,
                true
            ).ConfigureAwait(false);
        }

        private async Task<string> PostDirectPlayerPayloadAsync(
            string payload,
            string userAgent,
            string clientName,
            string clientVersion,
            string visitorData,
            string accessToken,
            bool includeInnertubeKey
        )
        {
            try
            {
                var url = InnertubeEndpoints.BuildWithoutKey("player?")
                    + (includeInnertubeKey ? "key=" + InnertubeApiKey + "&" : string.Empty)
                    + "prettyPrint=false";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    if (!includeInnertubeKey)
                    {
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(
                            "Accept-Language",
                            Config.Hl + "," + Config.Hl + ";q=0.9"
                        );
                    }
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientName);
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                    if (!includeInnertubeKey)
                    {
                        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    }
                    if (!string.IsNullOrWhiteSpace(visitorData))
                    {
                        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
                    }
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    }

                    var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] " + clientName + " /player failed: "
                            + (int)response.StatusCode + " " + response.ReasonPhrase
                        );
                        return string.Empty;
                    }

                    return json;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] " + clientName + " /player error: " + ex.Message
                );
                return string.Empty;
            }
        }

    }
}
