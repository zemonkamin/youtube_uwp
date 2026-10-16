using System;
using System.Net.Http;
using System.Threading.Tasks;
using Windows.Data.Json;
using YouTube.Innertube;

public static partial class Config
{
    private static IJsonValue GetHistoryFeedContents(IJsonValue value)
    {
        if (value == null || value.ValueType != JsonValueType.Object) return value;
        var root = value.GetObject();
        if (root.ContainsKey("error")) throw new InvalidOperationException("History returned an error.");
        if (IsHistoryResponseSignedOut(root)) throw new InvalidOperationException("History session rejected.");

        IJsonValue contents;
        if (root.TryGetValue("continuationContents", out contents)) return contents;
        var continuationItems = new JsonArray();
        var hasContinuationItems = false;
        foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints", "onResponseReceivedCommands" })
        {
            IJsonValue actions;
            if (!root.TryGetValue(key, out actions)) continue;
            foreach (var obj in EnumerateObjects(actions))
            {
                var command = GetObjectFromJsonObject(obj, "appendContinuationItemsAction")
                    ?? GetObjectFromJsonObject(obj, "reloadContinuationItemsCommand");
                if (command == null || !command.TryGetValue("continuationItems", out contents)
                    || contents.ValueType != JsonValueType.Array) continue;
                hasContinuationItems = true;
                foreach (var item in contents.GetArray()) continuationItems.Add(item);
            }
        }
        if (hasContinuationItems) return continuationItems;

        if (!root.TryGetValue("contents", out contents)) return root;
        IJsonValue firstTab = null;
        foreach (var obj in EnumerateObjects(contents))
        {
            var tab = GetObjectFromJsonObject(obj, "tabRenderer");
            IJsonValue tabContent;
            if (tab == null || !tab.TryGetValue("content", out tabContent)) continue;
            if (tab.GetNamedBoolean("selected", false)) return tabContent;
            if (firstTab == null) firstTab = tabContent;
        }
        return firstTab ?? contents;
    }

    private static bool IsHistoryResponseSignedOut(JsonObject root)
    {
        var context = GetObjectFromJsonObject(root, "responseContext");
        var webContext = GetObjectFromJsonObject(context, "mainAppWebResponseContext");
        return webContext != null && webContext.GetNamedBoolean("loggedOut", false);
    }

    private static string ExtractHistoryPageContinuation(IJsonValue contents)
    {
        // Prefer the main list's cursor over a cursor belonging to a nested Shorts shelf.
        // Scan the whole server page: modern WEB cards can exceed the old 7000-object cap.
        if (contents != null && contents.ValueType == JsonValueType.Array)
        {
            var direct = GetHistoryListContinuation(contents.GetArray());
            if (!string.IsNullOrEmpty(direct)) return direct;
        }
        foreach (var obj in EnumerateObjects(contents))
        {
            foreach (var name in new[] { "sectionListRenderer", "sectionListContinuation",
                "richGridRenderer", "richGridContinuation", "gridRenderer", "gridContinuation" })
            {
                var list = GetObjectFromJsonObject(obj, name);
                if (list == null) continue;
                foreach (var key in new[] { "continuations", "contents", "items" })
                {
                    IJsonValue items;
                    if (!list.TryGetValue(key, out items) || items.ValueType != JsonValueType.Array) continue;
                    var token = GetHistoryListContinuation(items.GetArray());
                    if (!string.IsNullOrEmpty(token)) return token;
                }
            }
        }
        foreach (var obj in EnumerateObjects(contents))
        {
            var renderer = GetObjectFromJsonObject(obj, "continuationItemRenderer");
            if (renderer == null) continue;
            var token = ExtractContinuationTokenFromObject(renderer);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        foreach (var obj in EnumerateObjects(contents))
        {
            var token = ExtractContinuationTokenFromObject(obj);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        return string.Empty;
    }

    private static string GetHistoryListContinuation(JsonArray items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i].ValueType != JsonValueType.Object) continue;
            var obj = items[i].GetObject();
            var renderer = GetObjectFromJsonObject(obj, "continuationItemRenderer");
            var token = ExtractContinuationTokenFromObject(renderer ?? obj);
            if (!string.IsNullOrEmpty(token)) return token;
        }
        return string.Empty;
    }

    internal sealed class WatchHistorySession
    {
        internal string Owner;
        internal string RefreshToken;
        internal bool Web;
        internal string Version;
        internal string PlaybackUrl;
        internal string WatchtimeUrl;
        internal string Cpn;
        internal bool PlaybackSent;
        internal bool Started;
        internal double Length;
    }

    internal static async Task<WatchHistorySession> CreateWatchHistorySessionAsync(string videoId, string refreshToken)
    {
        await WebAccountSession.InitializeAsync().ConfigureAwait(false);
        var web = WebAccountSession.IsSignedIn;
        if (string.IsNullOrEmpty(videoId) || (!web && string.IsNullOrEmpty(refreshToken))) return null;
        var owner = PlaybackResolver.AccountKey;
        var accessToken = web ? string.Empty : await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (!web && string.IsNullOrEmpty(accessToken)) return null;

        JsonObject root = null;
        var version = web ? FirstNonEmpty(WebAccountSession.ClientVersion, HomeWebClientVersion) : TvPlayerClientVersion;
        var cpn = GenerateCpn();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (owner != PlaybackResolver.AccountKey) return null;
            var sts = web ? 0 : await GetSignatureTimestampAsync(attempt > 0).ConfigureAwait(false);
            if (owner != PlaybackResolver.AccountKey) return null;
            var client = web
                ? "\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + version + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"DESKTOP\"}"
                : "\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + version + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}";
            var payload = "{\"context\":{" + client + "},\"videoId\":\"" + JsonEscape(videoId)
                + "\",\"cpn\":\"" + cpn + "\",\"contentCheckOk\":true,\"racyCheckOk\":true,\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\""
                + (sts > 0 ? ",\"signatureTimestamp\":" + sts : string.Empty) + "}}}";
            var json = await PostInnertubeJsonAsync("player", payload, accessToken,
                web ? "WEB" : "TVHTML5", version, true, web).ConfigureAwait(false);
            if (owner != PlaybackResolver.AccountKey || string.IsNullOrEmpty(json)) return null;
            if (web) version = FirstNonEmpty(WebAccountSession.ClientVersion, version);
            root = JsonObject.Parse(json);
            if (IsHistoryResponseSignedOut(root)) return null;
            if (root.ContainsKey("playbackTracking")) break;
            var status = GetObjectFromJsonObject(root, "playabilityStatus");
            if (web || attempt > 0 || GetJsonString(status, "reason").IndexOf("reload", StringComparison.OrdinalIgnoreCase) < 0)
                return null;
        }
        var tracking = GetObjectFromJsonObject(root, "playbackTracking");
        if (tracking == null) return null;
        var playback = GetTrackingBaseUrl(tracking, "videostatsPlaybackUrl");
        var watchtime = GetTrackingBaseUrl(tracking, "videostatsWatchtimeUrl");
        if (string.IsNullOrEmpty(playback) && string.IsNullOrEmpty(watchtime)) return null;
        double length;
        double.TryParse(GetJsonString(GetObjectFromJsonObject(root, "videoDetails"), "lengthSeconds"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out length);
        return new WatchHistorySession { Owner = owner, RefreshToken = refreshToken, Web = web,
            Version = version, PlaybackUrl = playback, WatchtimeUrl = watchtime, Cpn = cpn, Length = length };
    }

    internal static async Task<bool> ReportWatchHistorySessionAsync(WatchHistorySession session,
        double position, double length, double from, double elapsed, bool final)
    {
        if (session == null || session.Owner != PlaybackResolver.AccountKey) return false;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var opening = from < 0 || !session.Started;
        var at = Math.Max(0, position);
        var begin = opening ? 0 : Math.Max(0, from);
        var end = Math.Max(begin, at);
        if (opening && end <= begin) end = begin + 1;
        var duration = length > 0 ? length : session.Length;
        var common = "&cpn=" + Uri.EscapeDataString(session.Cpn)
            + "&ver=2&fs=0&volume=100&muted=0&state=playing"
            + "&c=" + (session.Web ? "WEB" : "TVHTML5") + "&cver=" + session.Version + "&cplayer=UNIPLAYER"
            + (session.Web ? "&cos=Windows&cosver=10.0&cplatform=DESKTOP"
                : "&cmodel=SmartTV&cos=Tizen&cosver=5.0&cplatform=TV&ctheme=CLASSIC")
            + "&hl=" + Uri.EscapeDataString(Hl) + "&cr=" + Uri.EscapeDataString(Gl);
        var ok = false;
        if (opening && !session.PlaybackSent && !string.IsNullOrEmpty(session.PlaybackUrl))
        {
            session.PlaybackSent = await SendHistoryStatsPingAsync(
                session.PlaybackUrl + common + "&cmt=" + at.ToString("0.###", inv), session).ConfigureAwait(false);
            ok = session.PlaybackSent;
        }
        if (!string.IsNullOrEmpty(session.WatchtimeUrl))
        {
            var url = session.WatchtimeUrl + common + "&cmt=" + at.ToString("0.###", inv)
                + "&st=" + begin.ToString("0.###", inv) + "&et=" + end.ToString("0.###", inv)
                + "&rt=" + (opening ? end : Math.Max(0, elapsed)).ToString("0.###", inv)
                + (duration > 0 ? "&len=" + duration.ToString("0.###", inv) : string.Empty)
                + (final ? "&final=1" : string.Empty);
            var watched = await SendHistoryStatsPingAsync(url, session).ConfigureAwait(false);
            session.Started |= watched;
            ok |= watched;
        }
        System.Diagnostics.Debug.WriteLine("[History] Watch segment " + begin.ToString("0.#", inv)
            + "-" + end.ToString("0.#", inv) + "s, final=" + final + ", ok=" + ok);
        return ok;
    }

    private static async Task<bool> SendHistoryStatsPingAsync(string url, WatchHistorySession session)
    {
        try
        {
            if (session.Owner != PlaybackResolver.AccountKey) return false;
            var token = session.Web ? string.Empty : await RefreshAccessTokenAsync(session.RefreshToken).ConfigureAwait(false);
            if (session.Owner != PlaybackResolver.AccountKey || (!session.Web && string.IsNullOrEmpty(token))) return false;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", session.Web ? WebUserAgent : TvUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", session.Web ? WebAccountSession.Origin + "/" : WebAccountSession.Origin + "/tv");
                if (session.Web)
                {
                    if (!WebAccountSession.ApplyHeaders(request)) return false;
                }
                else
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
                    ApplySelectedAccountHeader(request, true);
                }
                using (var response = await httpClient.SendAsync(request).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        System.Diagnostics.Debug.WriteLine("[History] Stats status: " + (int)response.StatusCode);
                    return response.IsSuccessStatusCode;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[History] Stats failed: " + ex.Message);
            return false;
        }
    }
}
