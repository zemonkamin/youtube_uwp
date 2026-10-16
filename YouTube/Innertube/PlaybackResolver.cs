using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    // Ready-URL delivery from Troubadour/src/net/YTApi.m:
    // playerResponse, androidVrPlayerResponse:primary:, shortsPlayerResponse.
    // SABR-only formats are deliberately not treated as ready media URLs.
    internal static class PlaybackResolver
    {
        internal const string IosUserAgent = "com.google.ios.youtube/19.16.3 (iPhone16,2; U; CPU iOS 18_0 like Mac OS X)";
        internal const string VisionUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
        internal const string VrUserAgent = "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";
        internal const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        private const string TvUserAgent = "Mozilla/5.0 (SMART-TV; LINUX; Tizen 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Version/5.0 TV Safari/537.36";
        private const string AndroidUserAgent = "com.google.android.youtube/20.10.38 (Linux; U; Android 11) gzip";
        private const string MediaAgentField = "_uwpMediaUserAgent";
        private const string FallbackVisitorData = "CgtjTS00dGRYTXhBOCif8OnOBjIoCgJQTBIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicgSA%3D%3D";
        private static readonly object VisitorGate = new object();
        private static string _visitorOwner = string.Empty;
        private static string _visitorData = string.Empty;
        private static string _nonceOwner = string.Empty;
        private static readonly Dictionary<string, string> PlaybackNonces = new Dictionary<string, string>();

        private static string PlaybackNonce(string videoId)
        {
            var owner = AccountKey;
            lock (VisitorGate)
            {
                if (_nonceOwner != owner) { _nonceOwner = owner; PlaybackNonces.Clear(); }
                string nonce;
                if (PlaybackNonces.TryGetValue(videoId, out nonce)) return nonce;
                if (PlaybackNonces.Count >= 16) PlaybackNonces.Clear();
                nonce = Convert.ToBase64String(Guid.NewGuid().ToByteArray()).Replace("+", "-").Replace("/", "_").Substring(0, 16);
                PlaybackNonces[videoId] = nonce;
                return nonce;
            }
        }

        internal sealed class Response
        {
            internal string Json;
            internal JsonObject Root;
            internal string UserAgent;
        }

        internal static string AccountKey
        {
            get
            {
                if (WebAccountSession.IsSignedIn) return WebAccountSession.CacheKey;
                global::Config.LoadUserToken();
                uint fingerprint = 2166136261;
                var token = global::Config.UserToken ?? string.Empty;
                for (var i = 0; i < token.Length; i++) fingerprint = unchecked((fingerprint ^ token[i]) * 16777619);
                return WebAccountSession.CacheKey + (token.Length == 0 ? "|guest" : "|qr:" + fingerprint.ToString("X8")
                    + ":" + global::Config.SelectedYouTubeAccountBrandId);
            }
        }

        private static string CacheKey(string videoId, bool shorts)
        {
            return "playback|" + AccountKey + "|" + global::Config.Hl + "|" + global::Config.Gl
                + "|" + videoId + (shorts ? "|shorts" : "|video");
        }

        internal static async Task<Response> GetAsync(string videoId, bool shorts = false)
        {
            await WebAccountSession.InitializeAsync();
            if (string.IsNullOrWhiteSpace(videoId)) return null;
            var owner = AccountKey;
            var key = CacheKey(videoId, shorts);
            var response = await InnertubeRequestCoordinator.GetJsonAsync(key,
                delegate { return shorts ? ResolveShortsAsync(videoId, owner) : ResolveCoreAsync(videoId, owner); }, TimeSpan.FromMinutes(2));
            if (owner != AccountKey) return null;
            if (!HasStreams(response.Root) && !HasHls(response.Root) && !LiveSabrSession.HasConfig(response.Root)) InnertubeRequestCoordinator.Invalidate(key);
            return new Response { Json = response.Text, Root = response.Root, UserAgent = MediaUserAgent(response.Root) };
        }

        internal static void Invalidate(string videoId)
        {
            InnertubeRequestCoordinator.Invalidate(CacheKey(videoId, false));
            InnertubeRequestCoordinator.Invalidate(CacheKey(videoId, true));
        }

        internal static string MediaUserAgent(JsonObject root)
        {
            return Read(root, MediaAgentField);
        }

        internal static bool HasStreams(JsonObject root)
        {
            var data = Object(root, "streamingData");
            return HasReadyUrl(data, "adaptiveFormats") || HasReadyUrl(data, "formats");
        }

        private static bool HasAdaptive(JsonObject root)
        {
            return HasReadyUrl(Object(root, "streamingData"), "adaptiveFormats");
        }

        private static bool HasHls(JsonObject root)
        {
            return !string.IsNullOrWhiteSpace(Read(Object(root, "streamingData"), "hlsManifestUrl"));
        }

        internal static bool UsesLiveTransport(JsonObject root)
        {
            // An ended broadcast can still use live CDN delivery until YouTube
            // finishes processing it. Its UI is VOD, but these URLs have no finite sidx.
            var data = Object(root, "streamingData");
            if (IsLiveTransportUrl(Read(data, "hlsManifestUrl"))) return true;
            foreach (var field in new[] { "formats", "adaptiveFormats" })
                foreach (var value in LiveJson.Array(data, field))
                    if (value.ValueType == JsonValueType.Object && IsLiveTransportUrl(Read(value.GetObject(), "url")))
                        return true;
            return false;
        }

        internal static bool IsLiveTransportUrl(string url)
        {
            return !string.IsNullOrEmpty(url) && (url.IndexOf("source=yt_live_broadcast", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("/source/yt_live_broadcast/", StringComparison.OrdinalIgnoreCase) >= 0
                || url.IndexOf("&live=1&", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        internal static bool IsLive(JsonObject root)
        {
            var details = LiveJson.Object(root, "videoDetails");
            var broadcast = LiveJson.Find(root, "liveBroadcastDetails");
            if (HasFinishedLiveState(root, details, broadcast)) return false;
            return LiveJson.Bool(root, "_uwpLiveBroadcast") || LiveJson.Bool(details, "isLive") || LiveJson.Bool(details, "isLiveNow")
                || LiveJson.Bool(broadcast, "isLiveNow") || LiveJson.Bool(details, "isUpcoming")
                || (LiveJson.String(LiveJson.Object(root, "playabilityStatus"), "status") == "LIVE_STREAM_OFFLINE"
                    && LiveJson.Bool(details, "isLiveContent"));
        }

        internal static bool IsFinishedLive(JsonObject root)
        {
            var details = LiveJson.Object(root, "videoDetails");
            var broadcast = LiveJson.Find(root, "liveBroadcastDetails");
            return HasFinishedLiveState(root, details, broadcast);
        }

        private static bool HasFinishedLiveState(JsonObject root, JsonObject details, JsonObject broadcast)
        {
            if (LiveJson.Bool(broadcast, "isLiveNow") || LiveJson.Bool(details, "isLiveNow")) return false;
            if (!string.IsNullOrEmpty(LiveJson.String(broadcast, "endTimestamp"))) return true;
            IJsonValue live;
            // An explicit false in a successful archived-live response is evidence of
            // completion. A missing flag, network failure or scheduled offline stream is not.
            return details != null && LiveJson.Bool(details, "isLiveContent") && !LiveJson.Bool(details, "isUpcoming")
                && details.TryGetValue("isLive", out live) && live.ValueType == JsonValueType.Boolean && !live.GetBoolean()
                && LiveJson.String(LiveJson.Object(root, "playabilityStatus"), "status") == "OK";
        }

        internal static async Task<JsonObject> GetLivePlayerAsync(string videoId, string reloadToken = null, string playerProof = null)
        {
            await WebAccountSession.InitializeAsync();
            var owner = AccountKey;
            var bearer = await BearerAsync();
            var tv = await SendAsync(videoId, "TVHTML5", "7.20250209.19.00", "85", TvUserAgent,
                null, bearer, WebAccountSession.IsSignedIn, true, false, reloadToken, playerProof);
            if (owner != AccountKey) return null;
            if (LiveSabrSession.HasConfig(tv) || HasHls(tv) || IsFinishedLive(tv)) return PreserveLiveIdentity(tv);
            if (WebAccountSession.IsSignedIn)
            {
                var web = await SendAsync(videoId, "WEB", "2.20260626.01.00", "1", WebUserAgent,
                    null, null, true, true, false, reloadToken, playerProof);
                if (owner != AccountKey) return null;
                if (LiveSabrSession.HasConfig(web) || HasHls(web) || IsFinishedLive(web)) return PreserveLiveIdentity(web);
            }
            return tv;
        }

        internal static string LiveSessionBinding(JsonObject root)
        {
            // YTApi.sessionBinding: the player proof belongs to the account/visitor,
            // whereas the SABR media proof belongs to the video ID.
            var response = Object(root, "responseContext");
            var datasync = Read(Object(response, "mainAppWebResponseContext"), "datasyncId");
            if (!string.IsNullOrEmpty(datasync)) return datasync.Split('|')[0];
            var visitor = Read(response, "visitorData");
            if (!string.IsNullOrEmpty(visitor)) return visitor;
            var owner = AccountKey;
            lock (VisitorGate) return _visitorOwner == owner ? _visitorData : string.Empty;
        }

        internal static async Task<JsonObject> GetLivePlayerForClientAsync(string videoId, int clientNumber,
            string reloadToken = null, string playerProof = null)
        {
            if (clientNumber != 5 && clientNumber != 101 && clientNumber != 28 && clientNumber != 1)
                return await GetLivePlayerAsync(videoId, reloadToken, playerProof);
            await WebAccountSession.InitializeAsync();
            var owner = AccountKey;
            var visitor = clientNumber == 5 ? null : await VisitorAsync(owner, videoId);
            if (owner != AccountKey) return null;
            var root = clientNumber == 1
                ? await SendAsync(videoId, "WEB", "2.20260626.01.00", "1", WebUserAgent, null, null,
                    WebAccountSession.IsSignedIn, true, false, reloadToken, playerProof)
                : clientNumber == 5
                ? await SendAsync(videoId, "IOS", "20.49.6", "5", IosUserAgent, null, null, false, false, false, reloadToken)
                : clientNumber == 101
                ? await SendAsync(videoId, "VISIONOS", "1.02", "101", VisionUserAgent, visitor, null, false, false, false, reloadToken)
                : await SendAsync(videoId, "ANDROID_VR", "1.65.10", "28", VrUserAgent, visitor, null, false, false, false, reloadToken);
            if (owner != AccountKey) return null;
            CaptureVisitor(owner, root);
            return PreserveLiveIdentity(root);
        }

        private static JsonObject PreserveLiveIdentity(JsonObject root)
        {
            // This request is used only for an already identified broadcast. TV can
            // omit isLive on renewal; that omission must not switch SABR to a VOD source.
            if (root != null && !IsFinishedLive(root)) root["_uwpLiveBroadcast"] = JsonValue.CreateBooleanValue(true);
            return root;
        }

        private static JsonObject MergeLiveResponse(JsonObject live, JsonObject primary)
        {
            if (live == null) return primary;
            var finished = IsFinishedLive(live);
            // TV supplies the live delivery config but may omit the IOS page metadata.
            foreach (var key in new[] { "videoDetails", "microformat", "captions", "playbackTracking" })
            {
                if (finished && key == "microformat") continue;
                IJsonValue value;
                if (primary == null || !primary.TryGetValue(key, out value)) continue;
                if (!live.ContainsKey(key)) live[key] = value;
                else if (key == "videoDetails")
                {
                    var details = Object(live, key); var original = Object(primary, key);
                    if (details != null && original != null)
                        foreach (var field in original)
                        {
                            // Keep broadcast state from the delivery response. Combining
                            // a TV isLive=false with IOS isLiveContent=true can falsely
                            // mark an ongoing broadcast as an archived video.
                            if (field.Key == "isLive" || field.Key == "isLiveNow"
                                || field.Key == "isLiveContent" || field.Key == "isUpcoming") continue;
                            IJsonValue current;
                            if (!details.TryGetValue(field.Key, out current) || current.ValueType == JsonValueType.Null
                                || (current.ValueType == JsonValueType.String && string.IsNullOrWhiteSpace(current.GetString())))
                                details[field.Key] = field.Value;
                        }
                }
            }
            // Keep the complete seed response: IOS can supply SABR without HLS.
            // Never combine one client's delivery config with another client's identity.
            if (primary != null && (LiveSabrSession.HasConfig(primary) || HasHls(primary)))
                live["_uwpLiveFallback"] = primary;
            if (HasHls(primary)) live["_uwpLiveHlsFallback"] = primary;
            return live;
        }

        internal static async Task<JsonObject> GetLiveHlsAsync(string videoId)
        {
            await WebAccountSession.InitializeAsync();
            var owner = AccountKey;
            // Match the successful IOS seed request. Attaching a TV OAuth token to
            // this client can turn an otherwise playable broadcast into HTTP 400.
            var root = await SendAsync(videoId, "IOS", "20.49.6", "5", IosUserAgent, null, null, false, false);
            return owner == AccountKey ? root : null;
        }

        private static bool HasReadyUrl(JsonObject data, string field)
        {
            IJsonValue value;
            if (data == null || !data.TryGetValue(field, out value) || value.ValueType != JsonValueType.Array) return false;
            foreach (var format in value.GetArray())
                if (format.ValueType == JsonValueType.Object && !string.IsNullOrWhiteSpace(Read(format.GetObject(), "url"))) return true;
            return false;
        }

        private static string Read(JsonObject root, string name)
        {
            IJsonValue value;
            return root != null && root.TryGetValue(name, out value) && value.ValueType == JsonValueType.String
                ? value.GetString() : string.Empty;
        }

        private static JsonObject Object(JsonObject root, string name)
        {
            IJsonValue value;
            return root != null && root.TryGetValue(name, out value) && value.ValueType == JsonValueType.Object
                ? value.GetObject() : null;
        }

        private static string Finish(JsonObject root, string agent)
        {
            if (root == null) return string.Empty;
            root[MediaAgentField] = JsonValue.CreateStringValue(agent);
            System.Diagnostics.Debug.WriteLine("[Playback] Selected delivery: "
                + (agent == VisionUserAgent ? "VISIONOS" : agent == VrUserAgent ? "ANDROID_VR"
                    : agent == TvUserAgent ? "TVHTML5" : agent == WebUserAgent ? "WEB"
                    : agent == AndroidUserAgent ? "ANDROID" : "IOS"));
            return root.Stringify();
        }

        private static void CaptureVisitor(string owner, JsonObject root)
        {
            var visitor = Read(Object(root, "responseContext"), "visitorData");
            if (string.IsNullOrWhiteSpace(visitor) || owner != AccountKey) return;
            lock (VisitorGate)
                if (_visitorOwner != owner || string.IsNullOrWhiteSpace(_visitorData))
                { _visitorOwner = owner; _visitorData = visitor; }
        }

        private static async Task<string> VisitorAsync(string owner, string videoId, bool refresh = false)
        {
            if (refresh)
                lock (VisitorGate) { _visitorOwner = owner; _visitorData = string.Empty; }
            if (!refresh)
                lock (VisitorGate)
                    if (_visitorOwner == owner && !string.IsNullOrWhiteSpace(_visitorData)) return _visitorData;
            var seed = await SendAsync(videoId, "WEB", "2.20260626.01.00", "1", WebUserAgent, null, null, false, false);
            CaptureVisitor(owner, seed);
            var fresh = Read(Object(seed, "responseContext"), "visitorData");
            return !string.IsNullOrWhiteSpace(fresh) ? fresh : Uri.UnescapeDataString(FallbackVisitorData);
        }

        private static async Task<string> ResolveCoreAsync(string videoId, string owner)
        {
            var primary = await SendAsync(videoId, "IOS", "20.49.6", "5", IosUserAgent, null, null, false, false);
            CaptureVisitor(owner, primary);
            // Live media has no finite MP4/sidx. Resolve it before the ready-file chain.
            if (IsLive(primary))
            {
                var live = await GetLivePlayerAsync(videoId);
                if (LiveSabrSession.HasConfig(live) || HasHls(live))
                    return Finish(MergeLiveResponse(live, primary), MediaUserAgent(live));
                // Upcoming/offline broadcasts wait for a fresh live response instead of
                // trying every finite-video client before showing the waiting state.
                return Finish(primary, IosUserAgent);
            }
            var visitor = await VisitorAsync(owner, videoId);
            var vision = await SendAsync(videoId, "VISIONOS", "1.02", "101", VisionUserAgent, visitor, null, false, false);
            CaptureVisitor(owner, vision);
            if (HasStreams(vision)) return Finish(vision, VisionUserAgent);

            var vr = await SendAsync(videoId, "ANDROID_VR", "1.65.10", "28", VrUserAgent, visitor, null, false, false);
            CaptureVisitor(owner, vr);
            if (HasStreams(vr)) return Finish(vr, VrUserAgent);
            visitor = await VisitorAsync(owner, videoId, true);
            var retry = await SendAsync(videoId, "ANDROID_VR", "1.65.10", "28", VrUserAgent, visitor, null, false, false);
            CaptureVisitor(owner, retry);
            if (HasStreams(retry)) return Finish(retry, VrUserAgent);

            JsonObject fallback = null;
            string fallbackAgent = null;
            // A browser login must not silently switch to a different saved QR account.
            var bearer = await BearerAsync();
            if (WebAccountSession.IsSignedIn || !string.IsNullOrWhiteSpace(bearer))
            {
                var tv = await SendAsync(videoId, "TVHTML5", "7.20250209.19.00", "85", TvUserAgent, null, bearer, false, true);
                CaptureVisitor(owner, tv);
                if (HasAdaptive(tv)) return Finish(tv, TvUserAgent);
                if (HasStreams(tv)) { fallback = tv; fallbackAgent = TvUserAgent; }
                var legacy = await SendAsync(videoId, "TVHTML5", "7.20220918.10.00", "85", TvUserAgent, null, bearer, false, true);
                CaptureVisitor(owner, legacy);
                if (HasAdaptive(legacy)) return Finish(legacy, TvUserAgent);
                if (fallback == null && HasStreams(legacy)) { fallback = legacy; fallbackAgent = TvUserAgent; }
            }

            if (WebAccountSession.IsSignedIn)
            {
                var web = await SendAsync(videoId, "WEB", "2.20260430.08.00", "1", WebUserAgent, null, null, true, true);
                CaptureVisitor(owner, web);
                if (HasAdaptive(web)) return Finish(web, WebUserAgent);
                // As in Troubadour, a WEB muxed URL does not end the client search.
                if (HasStreams(web)) { fallback = web; fallbackAgent = WebUserAgent; }
            }

            if (HasStreams(primary)) return Finish(primary, IosUserAgent);
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                var signedIos = await SendAsync(videoId, "IOS", "20.49.6", "5", IosUserAgent, visitor, bearer, false, false);
                CaptureVisitor(owner, signedIos);
                if (HasStreams(signedIos)) return Finish(signedIos, IosUserAgent);
            }
            if (fallback != null) return Finish(fallback, fallbackAgent);
            if (HasHls(primary)) return Finish(primary, IosUserAgent);

            return Finish(primary ?? retry ?? vr, IosUserAgent);
        }

        private static async Task<string> ResolveShortsAsync(string videoId, string owner)
        {
            var main = await GetAsync(videoId);
            if (main == null || owner != AccountKey) return string.Empty;
            if (HasStreams(main.Root) || HasHls(main.Root)) return main.Json;
            // shortsPlayerResponse uses the common chain before the old reel-specific clients.
            var android = await SendAsync(videoId, "ANDROID", "20.10.38", "3", AndroidUserAgent, null, null, false, false, true);
            if (HasStreams(android) || HasHls(android)) return Finish(android, AndroidUserAgent);
            var web = await SendAsync(videoId, "WEB", "2.20260206.01.00", "1", WebUserAgent, null, null, WebAccountSession.IsSignedIn, true, true);
            if (HasStreams(web) || HasHls(web)) return Finish(web, WebUserAgent);
            var bearer = await BearerAsync();
            if (!string.IsNullOrWhiteSpace(bearer))
            {
                web = await SendAsync(videoId, "WEB", "2.20260206.01.00", "1", WebUserAgent, null, bearer, false, true, true);
                if (HasStreams(web) || HasHls(web)) return Finish(web, WebUserAgent);
            }
            return main.Json;
        }

        private static async Task<string> BearerAsync()
        {
            if (WebAccountSession.IsSignedIn || string.IsNullOrWhiteSpace(global::Config.UserToken)) return string.Empty;
            try { return await global::Config.RefreshAccessTokenAsync(global::Config.UserToken); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playback] QR token unavailable: " + ex.GetType().Name);
                return string.Empty;
            }
        }

        private static async Task<JsonObject> SendAsync(string videoId, string name, string version, string number,
            string agent, string visitor, string bearer, bool browser, bool playbackContext, bool shortsProtocol = false,
            string reloadToken = null, string playerProof = null)
        {
            try
            {
                var client = new JsonObject();
                client["clientName"] = JsonValue.CreateStringValue(name);
                client["clientVersion"] = JsonValue.CreateStringValue(version);
                client["hl"] = JsonValue.CreateStringValue(global::Config.Hl);
                client["gl"] = JsonValue.CreateStringValue(global::Config.Gl);
                if (!string.IsNullOrWhiteSpace(visitor)) client["visitorData"] = JsonValue.CreateStringValue(visitor);
                if (name == "IOS" || name == "VISIONOS")
                {
                    client["deviceMake"] = JsonValue.CreateStringValue("Apple");
                    client["deviceModel"] = JsonValue.CreateStringValue(name == "IOS" ? "iPhone16,2" : "RealityDevice14,1");
                    client["osName"] = JsonValue.CreateStringValue(name == "IOS" ? "iOS" : "visionOS");
                    client["osVersion"] = JsonValue.CreateStringValue(name == "IOS" ? "18.0" : "1.0.2.21O209");
                    if (name == "VISIONOS") client["userAgent"] = JsonValue.CreateStringValue(agent);
                }
                else if (name == "ANDROID_VR")
                {
                    client["deviceMake"] = JsonValue.CreateStringValue("Oculus");
                    client["deviceModel"] = JsonValue.CreateStringValue("Quest 3");
                    client["androidSdkVersion"] = JsonValue.CreateNumberValue(32);
                    client["osName"] = JsonValue.CreateStringValue("Android");
                    client["osVersion"] = JsonValue.CreateStringValue("12L");
                    client["platform"] = JsonValue.CreateStringValue("MOBILE");
                }
                else if (name == "ANDROID") client["androidSdkVersion"] = JsonValue.CreateNumberValue(30);
                else if (name == "TVHTML5")
                {
                    client["platform"] = JsonValue.CreateStringValue("TV");
                    client["deviceMake"] = JsonValue.CreateStringValue("Samsung");
                    client["deviceModel"] = JsonValue.CreateStringValue("SmartTV");
                    client["osName"] = JsonValue.CreateStringValue("Tizen");
                    client["osVersion"] = JsonValue.CreateStringValue("5.0");
                }
                var context = new JsonObject();
                context["client"] = client;
                var payload = new JsonObject();
                payload["context"] = context;
                payload["videoId"] = JsonValue.CreateStringValue(videoId);
                payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
                payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
                var nonce = PlaybackNonce(videoId);
                if (!string.IsNullOrEmpty(playerProof)) payload["serviceIntegrityDimensions"] = new JsonObject
                { ["poToken"] = JsonValue.CreateStringValue(playerProof) };
                if (playbackContext)
                {
                    payload["cpn"] = JsonValue.CreateStringValue(nonce);
                    var content = new JsonObject();
                    content["html5Preference"] = JsonValue.CreateStringValue("HTML5_PREF_WANTS");
                    if (name == "TVHTML5")
                    {
                        var sts = await global::Config.GetSignatureTimestampAsync(false);
                        if (sts > 0) content["signatureTimestamp"] = JsonValue.CreateNumberValue(sts);
                    }
                    var playback = new JsonObject();
                    playback["contentPlaybackContext"] = content;
                    if (!string.IsNullOrEmpty(reloadToken)) playback["reloadPlaybackContext"] = new JsonObject
                    { ["reloadPlaybackParams"] = new JsonObject { ["token"] = JsonValue.CreateStringValue(reloadToken) } };
                    payload["playbackContext"] = playback;
                }
                else if (!string.IsNullOrEmpty(reloadToken)) payload["playbackContext"] = new JsonObject
                { ["reloadPlaybackContext"] = new JsonObject
                    { ["reloadPlaybackParams"] = new JsonObject { ["token"] = JsonValue.CreateStringValue(reloadToken) } } };
                var json = global::Config.ApplySelectedAccountContext(payload.Stringify(), !string.IsNullOrWhiteSpace(bearer));
                var url = shortsProtocol ? InnertubeEndpoints.Build("player")
                    : name == "TVHTML5" || name == "ANDROID" || (name == "WEB" && playbackContext)
                    ? InnertubeEndpoints.Build("player", true) : InnertubeEndpoints.BuildWithoutKey("player?prettyPrint=false");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", agent);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("Accept-Language", shortsProtocol
                        ? global::Config.Hl + "," + global::Config.Hl + ";q=0.9" : global::Config.Hl);
                    if (name == "IOS" || name == "VISIONOS" || name == "ANDROID_VR" || (name == "WEB" && !playbackContext))
                        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", number);
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", version);
                    if (!string.IsNullOrWhiteSpace(visitor)) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitor);
                    if (shortsProtocol && name == "WEB")
                    {
                        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", FallbackVisitorData);
                        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                        request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/shorts/");
                    }
                    if (!string.IsNullOrWhiteSpace(bearer))
                    {
                        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + bearer);
                        global::Config.ApplySelectedAccountHeader(request, true);
                    }
                    else if (browser && !WebAccountSession.ApplyHeaders(request)) return null;
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                    using (var response = await YouTubeHttpClient.Shared.SendAsync(request, timeout.Token).ConfigureAwait(false))
                    {
                        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        JsonObject root;
                        if (!JsonObject.TryParse(text, out root)) return null;
                        var status = Read(Object(root, "playabilityStatus"), "status");
                        System.Diagnostics.Debug.WriteLine("[Playback] " + name + " HTTP " + (int)response.StatusCode
                            + ", status=" + status + ", ready=" + HasStreams(root)
                            + ", hls=" + HasHls(root) + ", sabr=" + LiveSabrSession.HasConfig(root));
                        if (!response.IsSuccessStatusCode) return null;
                        root[MediaAgentField] = JsonValue.CreateStringValue(agent);
                        root["_uwpClientInfo"] = client;
                        root["_uwpHasPlayerProof"] = JsonValue.CreateBooleanValue(!string.IsNullOrEmpty(playerProof));
                        if (!string.IsNullOrEmpty(nonce)) root["_uwpPlaybackNonce"] = JsonValue.CreateStringValue(nonce);
                        root["_uwpClientVersion"] = JsonValue.CreateStringValue(browser && name == "WEB"
                            && !string.IsNullOrEmpty(WebAccountSession.ClientVersion) ? WebAccountSession.ClientVersion : version);
                        root["_uwpClientNumber"] = JsonValue.CreateNumberValue(int.Parse(number));
                        return root;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playback] " + name + " request failed: " + ex.GetType().Name);
                return null;
            }
        }
    }
}
