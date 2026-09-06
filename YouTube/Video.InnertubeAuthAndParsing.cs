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
        private void CaptureVisitorDataFromString(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                CaptureVisitorData(ParsePlayerJsonObject(json));
            }
            catch
            {
            }
        }

        private void RememberParsedPlayerRoot(string json, JsonObject root)
        {
            if (string.IsNullOrWhiteSpace(json) || root == null) return;
            lock (_playerJsonParseGate)
            {
                _lastParsedPlayerJson = json;
                _lastParsedPlayerRoot = root;
            }
        }

        private JsonObject ParsePlayerJsonObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            lock (_playerJsonParseGate)
            {
                if (_lastParsedPlayerRoot != null
                    && (ReferenceEquals(_lastParsedPlayerJson, json)
                        || string.Equals(_lastParsedPlayerJson, json, StringComparison.Ordinal)))
                    return _lastParsedPlayerRoot;

                JsonObject root;
                if (!FastJson.TryParseObject(json, out root)) return null;
                _lastParsedPlayerJson = json;
                _lastParsedPlayerRoot = root;
                return root;
            }
        }

        private bool PlayerJsonHasStreams(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = ParsePlayerJsonObject(json);
                if (root == null) return false;
                if (!root.ContainsKey("streamingData"))
                {
                    return false;
                }

                var streamingData = root.GetNamedObject("streamingData");
                return JsonFormatArrayHasReadyUrl(streamingData, "adaptiveFormats")
                    || JsonFormatArrayHasReadyUrl(streamingData, "formats");
            }
            catch
            {
            }

            return false;
        }

        private bool PlayerJsonHasAdaptiveStreams(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = ParsePlayerJsonObject(json);
                if (root == null) return false;
                if (!root.ContainsKey("streamingData"))
                {
                    return false;
                }

                return JsonFormatArrayHasReadyUrl(
                    root.GetNamedObject("streamingData"),
                    "adaptiveFormats"
                );
            }
            catch
            {
                return false;
            }
        }

        private bool PlayerJsonHasHlsManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = ParsePlayerJsonObject(json);
                if (root == null) return false;
                return root.ContainsKey("streamingData")
                    && !string.IsNullOrWhiteSpace(
                        GetJsonString(root.GetNamedObject("streamingData"), "hlsManifestUrl")
                    );
            }
            catch
            {
                return false;
            }
        }

        private static bool JsonFormatArrayHasReadyUrl(JsonObject streamingData, string name)
        {
            if (streamingData == null || !streamingData.ContainsKey(name))
            {
                return false;
            }

            var formats = streamingData.GetNamedArray(name);
            for (int i = 0; i < formats.Count; i++)
            {
                if (formats[i].ValueType == JsonValueType.Object
                    && !string.IsNullOrWhiteSpace(GetJsonString(formats[i].GetObject(), "url")))
                {
                    return true;
                }
            }

            return false;
        }

        private string GetPlayabilityReason(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "empty response";
            }

            try
            {
                var root = ParsePlayerJsonObject(json);
                if (root == null) return "invalid response";
                if (root.ContainsKey("playabilityStatus"))
                {
                    var status = root.GetNamedObject("playabilityStatus");
                    var s = GetJsonString(status, "status");
                    var reason = GetJsonString(status, "reason");
                    return (string.IsNullOrWhiteSpace(s) ? "?" : s) + (string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason);
                }
            }
            catch
            {
            }

            return "no streamingData";
        }

        private void InvalidateSessionVisitorData()
        {
            lock (_visitorDataGate)
            {
                _sessionVisitorData = string.Empty;
                _visitorDataFetchTask = null;
            }
        }

        private static string BuildAndroidVrPlayerPayload(string videoId, string visitorData)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeAndroidVrClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeAndroidVrClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Oculus");
            client["deviceModel"] = JsonValue.CreateStringValue("Quest 3");
            client["androidSdkVersion"] = JsonValue.CreateNumberValue(32);
            client["osName"] = JsonValue.CreateStringValue("Android");
            client["osVersion"] = JsonValue.CreateStringValue("12L");
            client["platform"] = JsonValue.CreateStringValue("MOBILE");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                client["visitorData"] = JsonValue.CreateStringValue(visitorData);
            }
            context["client"] = client;

            // ANDROID_VR sends no playbackContext (that is the TVHTML5-only "reloaded" fix in
            // MeeTube). contentCheckOk / racyCheckOk keep age/consent-gated videos playable.
            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private static string BuildWebSafariPlayerPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20260114.08.00");
            client["userAgent"] = JsonValue.CreateStringValue(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)"
            );
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["visitorData"] = JsonValue.CreateStringValue(GetFallbackInnertubeVisitorData());
            context["client"] = client;

            var contentPlaybackContext = new JsonObject();
            contentPlaybackContext["html5Preference"] = JsonValue.CreateStringValue("HTML5_PREF_WANTS");
            var playbackContext = new JsonObject();
            playbackContext["contentPlaybackContext"] = contentPlaybackContext;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["playbackContext"] = playbackContext;
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private async Task<string> PostWebSafariPlayerAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            var url = InnertubeEndpoints.Build("player", true);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildWebSafariPlayerPayload(videoId),
                    Encoding.UTF8,
                    "application/json"
                );
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)"
                );
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId));
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20260114.08.00");
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", GetFallbackInnertubeVisitorData());

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] WEB Safari player failed: "
                        + response.StatusCode
                        + " "
                        + json
                    );
                    return string.Empty;
                }

                return json;
            }
        }

        private static string BuildNextPayload(string videoId)
        {
            return BuildNextPayload(videoId, null);
        }

        private static string BuildNextPayload(string videoId, string playlistId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20250101");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);

            // With a playlistId the /next response also carries the watch queue
            // (playlistPanelRenderer). This is what drives playlists and auto-generated
            // mixes/"jams" (RD...), which exist only in this watch context.
            if (!string.IsNullOrWhiteSpace(playlistId))
            {
                payload["playlistId"] = JsonValue.CreateStringValue(playlistId);
            }

            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private static string BuildAuthenticatedNextPayload(string videoId, bool mobileWebClient)
        {
            return BuildAuthenticatedNextPayload(videoId, null, mobileWebClient);
        }

        private static string BuildAuthenticatedNextPayload(
            string videoId,
            string playlistId,
            bool mobileWebClient)
        {
            var context = new JsonObject();
            var client = new JsonObject();

            if (mobileWebClient)
            {
                client["clientName"] = JsonValue.CreateStringValue(InnertubeMwebClientName);
                client["clientVersion"] = JsonValue.CreateStringValue(InnertubeMwebClientVersion);
                client["hl"] = JsonValue.CreateStringValue(Config.Hl);
                client["gl"] = JsonValue.CreateStringValue(Config.Gl);
                client["osName"] = JsonValue.CreateStringValue("iOS");
                client["osVersion"] = JsonValue.CreateStringValue("18");
                client["platform"] = JsonValue.CreateStringValue("MOBILE");
            }
            else
            {
                client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
                client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
                client["hl"] = JsonValue.CreateStringValue(Config.Hl);
                client["gl"] = JsonValue.CreateStringValue(Config.Gl);
                client["platform"] = JsonValue.CreateStringValue("TV");
                client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            }

            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            if (!string.IsNullOrWhiteSpace(playlistId))
                payload["playlistId"] = JsonValue.CreateStringValue(playlistId);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private Task<InnertubeRequestCoordinator.JsonResponse> GetAuthenticatedNextResponseAsync(
            string videoId,
            string accessToken,
            bool mobileWebClient)
        {
            return GetAuthenticatedNextResponseAsync(
                videoId, null, accessToken, mobileWebClient);
        }

        private Task<InnertubeRequestCoordinator.JsonResponse> GetAuthenticatedNextResponseAsync(
            string videoId,
            string playlistId,
            string accessToken,
            bool mobileWebClient)
        {
            return GetAuthenticatedNextResponseAsync(
                videoId, playlistId, accessToken, mobileWebClient, false);
        }

        private Task<InnertubeRequestCoordinator.JsonResponse> GetAuthenticatedNextResponseAsync(
            string videoId,
            string playlistId,
            string accessToken,
            bool mobileWebClient,
            bool forceRefresh)
        {
            var payload = BuildAuthenticatedNextPayload(videoId, playlistId, mobileWebClient);
            var profile = mobileWebClient ? "MWEB" : "TVHTML5";
            var key = "video|authenticated|" + profile + "|"
                + GetRequestTokenFingerprint(accessToken) + "|" + payload;
            if (forceRefresh)
            {
                // Video is NavigationCacheMode.Required, so entering it again reuses the same
                // page instance. The signed-in /next contains mutable rating and Save state;
                // reusing the previous 30-second response made the freshly reset controls jump
                // back to the state from the preceding visit. Only the first request of a new
                // video load is invalidated; all consumers in that load still share its task.
                InnertubeRequestCoordinator.Invalidate(key);
            }
            return InnertubeRequestCoordinator.GetJsonAsync(
                key,
                delegate { return PostAuthenticatedNextCoreAsync(payload, accessToken, mobileWebClient); },
                TimeSpan.FromSeconds(30));
        }

        private sealed class WatchNextResponse
        {
            public InnertubeRequestCoordinator.JsonResponse Response { get; set; }
            public InnertubeRequestCoordinator.JsonResponse AuthenticatedResponse { get; set; }
            public bool PageIsAuthenticatedMweb { get; set; }
            public bool IsTvLayout { get; set; }
        }

        private static bool HasCompleteMwebWatchPage(
            InnertubeRequestCoordinator.JsonResponse response)
        {
            var text = response != null ? response.Text : null;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var hasMetadata = text.IndexOf("videoPrimaryInfoRenderer",
                    StringComparison.Ordinal) >= 0
                || text.IndexOf("slimVideoMetadataRenderer",
                    StringComparison.Ordinal) >= 0
                || text.IndexOf("videoMetadataRenderer",
                    StringComparison.Ordinal) >= 0;
            var hasComments = text.IndexOf("comment", StringComparison.OrdinalIgnoreCase) >= 0
                && text.IndexOf("continuationCommand", StringComparison.Ordinal) >= 0;
            var hasRating = text.IndexOf("LikeDislike", StringComparison.OrdinalIgnoreCase) >= 0
                || text.IndexOf("likeButton", StringComparison.OrdinalIgnoreCase) >= 0;
            return hasMetadata && hasComments && hasRating;
        }

        private async Task<WatchNextResponse> GetWatchNextResponseAsync(
            string videoId,
            string playlistId)
        {
            Config.LoadUserToken();
            var refreshToken = Config.UserToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                var webTask = PostInnertubeJsonAsync(
                    "next", BuildNextPayload(videoId, playlistId));
                InnertubeRequestCoordinator.JsonResponse tv = null;
                try
                {
                    var accessToken = await Config.RefreshAccessTokenAsync(refreshToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        tv = await GetAuthenticatedNextResponseAsync(
                            videoId, playlistId, accessToken, false, true).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Authenticated TVHTML5 /next failed; using WEB fallback: "
                        + ex.Message);
                }

                var web = await webTask.ConfigureAwait(false);
                if (tv != null && tv.Root != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Using WEB page sections plus authenticated TVHTML5 personalization");
                    return new WatchNextResponse
                    {
                        Response = web,
                        AuthenticatedResponse = tv,
                        PageIsAuthenticatedMweb = false,
                        IsTvLayout = true
                    };
                }

                return new WatchNextResponse
                {
                    Response = web,
                    AuthenticatedResponse = null,
                    PageIsAuthenticatedMweb = false,
                    IsTvLayout = false
                };
            }

            var anonymous = await PostInnertubeJsonAsync(
                "next", BuildNextPayload(videoId, playlistId)).ConfigureAwait(false);
            return new WatchNextResponse
            {
                Response = anonymous,
                AuthenticatedResponse = null,
                PageIsAuthenticatedMweb = false,
                IsTvLayout = false
            };
        }

        private async Task<string> PostAuthenticatedNextCoreAsync(
            string payload,
            string accessToken,
            bool mobileWebClient)
        {
            var url = BuildInnertubeUrl("next");
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (mobileWebClient)
                {
                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeMwebClientHeaderName,
                        InnertubeMwebClientVersion,
                        InnertubeMwebUserAgent);
                }
                else
                {
                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeTvClientHeaderName,
                        InnertubeTvClientVersion,
                        InnertubeTvUserAgent);
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

        private static string GetRequestTokenFingerprint(string value)
        {
            unchecked
            {
                uint hash = 2166136261;
                var safe = value ?? string.Empty;
                for (var i = 0; i < safe.Length; i++)
                {
                    hash ^= safe[i];
                    hash *= 16777619;
                }
                return hash.ToString("x8", CultureInfo.InvariantCulture);
            }
        }

        // The queue of a mix ("jam") is personalized: signed in, YouTube seeds it from the
        // account's listening history, so the anonymous WEB /next used for the rest of the page
        // returns a completely different — and to the user, wrong — set of videos. The queue is
        // therefore fetched separately with the TV client and the OAuth bearer, which is the only
        // client that bearer is accepted by.
        private static string BuildAuthenticatedPlaylistNextPayload(string videoId, string playlistId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["playlistId"] = JsonValue.CreateStringValue(playlistId);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private async Task<JsonObject> TryLoadAuthenticatedPlaylistNextAsync(string videoId, string playlistId)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(playlistId))
            {
                return null;
            }

            try
            {
                var accessToken = await GetTvAccessTokenAsync(false);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[PlaylistQueue] Not signed in; falling back to the anonymous queue"
                    );
                    return null;
                }

                var url = BuildInnertubeUrl("next");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedPlaylistNextPayload(videoId, playlistId),
                        Encoding.UTF8,
                        "application/json"
                    );

                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeTvClientHeaderName,
                        InnertubeTvClientVersion,
                        InnertubeTvUserAgent
                    );

                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[PlaylistQueue] Authenticated /next failed: "
                            + (int)response.StatusCode + " " + response.ReasonPhrase
                        );
                        return null;
                    }

                    return JsonValue.Parse(json).GetObject();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Authenticated /next error: " + ex.Message);
                return null;
            }
        }

        private static string BuildRatingPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var target = new JsonObject();
            target["videoId"] = JsonValue.CreateStringValue(videoId);

            var payload = new JsonObject();
            payload["context"] = context;
            payload["target"] = target;
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildInnertubeUrl(string endpoint)
        {
            if (string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase))
            {
                return InnertubeEndpoints.BuildWithoutKey("player?noauth=1&prettyPrint=false");
            }

            return InnertubeEndpoints.Build(endpoint);
        }

    }
}
