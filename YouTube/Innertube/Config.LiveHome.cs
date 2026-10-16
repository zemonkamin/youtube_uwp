using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using YouTube.Innertube;

public static partial class Config
{
    // Troubadour YTApi.liveFeed: TV's live topic for signed-in accounts,
    // and the public youtube.com/live channel for guests.
    internal static bool IsLiveHomeCategory(HomeCategoryItem category)
    {
        return category != null && string.Equals(category.BrowseId, "FEtopics_live", StringComparison.Ordinal);
    }

    private static async Task<List<VideoCardItem>> GetLiveHomeVideosAsync(string refreshToken)
    {
        var page = await GetLiveHomePageAsync(refreshToken, null).ConfigureAwait(false);
        return page.Videos;
    }

    public static async Task<HomeRecommendationsPage> GetLiveHomePageAsync(string refreshToken, string continuationToken)
    {
        await WebAccountSession.InitializeAsync().ConfigureAwait(false);
        var owner = PlaybackResolver.AccountKey;
        var browser = WebAccountSession.IsSignedIn;
        var accessToken = browser || string.IsNullOrWhiteSpace(refreshToken)
            ? string.Empty : await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (owner != PlaybackResolver.AccountKey) throw new OperationCanceledException();
        if (!browser && !string.IsNullOrWhiteSpace(refreshToken) && string.IsNullOrWhiteSpace(accessToken))
            throw new InvalidOperationException("Live Home account session unavailable.");

        var signedIn = browser || !string.IsNullOrWhiteSpace(accessToken);
        var version = signedIn ? "7.20250209.19.00" : HomeWebClientVersion;
        var client = new JsonObject
        {
            ["clientName"] = JsonValue.CreateStringValue(signedIn ? "TVHTML5" : "WEB"),
            ["clientVersion"] = JsonValue.CreateStringValue(version),
            ["hl"] = JsonValue.CreateStringValue(Hl),
            ["gl"] = JsonValue.CreateStringValue(Gl),
            ["platform"] = JsonValue.CreateStringValue(signedIn ? "TV" : "DESKTOP")
        };
        if (signedIn)
        {
            client["deviceMake"] = JsonValue.CreateStringValue("Samsung");
            client["deviceModel"] = JsonValue.CreateStringValue("SmartTV");
            client["osName"] = JsonValue.CreateStringValue("Tizen");
            client["osVersion"] = JsonValue.CreateStringValue("5.0");
        }
        var payload = new JsonObject { ["context"] = new JsonObject { ["client"] = client } };
        if (string.IsNullOrWhiteSpace(continuationToken))
            payload["browseId"] = JsonValue.CreateStringValue(signedIn ? "FEtopics_live" : "UC4R8DWoMoI7CAwX8_LjQHig");
        else payload["continuation"] = JsonValue.CreateStringValue(continuationToken);

        JsonObject root;
        using (var request = new HttpRequestMessage(HttpMethod.Post, InnertubeEndpoints.Build("browse", true)))
        {
            request.Content = new StringContent(ApplySelectedAccountContext(payload.Stringify(), !browser && signedIn), Encoding.UTF8, "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", signedIn ? TvUserAgent : PlaybackResolver.WebUserAgent);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", signedIn ? "85" : "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", version);
            request.Headers.TryAddWithoutValidation("Accept-Language", Hl);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/live");
            if (browser)
            {
                if (!WebAccountSession.ApplyHeaders(request)) throw new InvalidOperationException("Live Home browser session unavailable.");
            }
            else if (signedIn)
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                ApplySelectedAccountHeader(request, true);
            }
            using (var response = await httpClient.SendAsync(request).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                root = JsonObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }
        }
        if (owner != PlaybackResolver.AccountKey) throw new OperationCanceledException();
        if (root.ContainsKey("error")) throw new InvalidOperationException("Live Home returned an error.");
        if (signedIn && IsHistoryResponseSignedOut(root))
            throw new InvalidOperationException("Live Home account session rejected.");

        var contents = LiveHomeContents(root);
        var page = new HomeRecommendationsPage();
        var shorts = new List<VideoCardItem>();
        var added = 0;
        // Parse the complete page. A section continuation advances to other shelves;
        // truncating to Home's usual 12 cards would silently lose the rest of this page.
        ExtractHistoryItemsFromValue(contents, new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            page.Videos, shorts, ref added, 0);
        page.ContinuationToken = LiveHomeContinuation(contents);
        System.Diagnostics.Debug.WriteLine("[Home][Live] Parsed " + page.Videos.Count + " videos; hasNext="
            + !string.IsNullOrWhiteSpace(page.ContinuationToken));
        return page;
    }

    private static IJsonValue LiveHomeContents(JsonObject root)
    {
        IJsonValue value;
        if (root.TryGetValue("continuationContents", out value)) return value;
        var items = new JsonArray();
        var hasItems = false;
        foreach (var key in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints", "onResponseReceivedCommands" })
        {
            if (!root.TryGetValue(key, out value)) continue;
            foreach (var obj in EnumerateObjects(value, 200000))
            {
                var command = GetObjectFromJsonObject(obj, "appendContinuationItemsAction")
                    ?? GetObjectFromJsonObject(obj, "reloadContinuationItemsCommand");
                IJsonValue continuationItems;
                if (command == null || !command.TryGetValue("continuationItems", out continuationItems)
                    || continuationItems.ValueType != JsonValueType.Array) continue;
                hasItems = true;
                foreach (var item in continuationItems.GetArray()) items.Add(item);
            }
        }
        if (hasItems) return items;
        if (!root.TryGetValue("contents", out value)) return root;
        IJsonValue firstTab = null;
        foreach (var obj in EnumerateObjects(value, 200000))
        {
            var tab = GetObjectFromJsonObject(obj, "tabRenderer");
            IJsonValue content;
            if (tab == null || !tab.TryGetValue("content", out content)) continue;
            if (tab.GetNamedBoolean("selected", false)) return content;
            if (firstTab == null) firstTab = content;
        }
        return firstTab ?? value;
    }

    private static string LiveHomeContinuation(IJsonValue contents)
    {
        // Follow the page's vertical cursor, never a horizontal shelf's cursor.
        if (contents != null && contents.ValueType == JsonValueType.Array)
        {
            var direct = GetHistoryListContinuation(contents.GetArray());
            if (!string.IsNullOrWhiteSpace(direct)) return direct;
        }
        foreach (var obj in EnumerateObjects(contents, 200000))
        {
            foreach (var name in new[] { "sectionListRenderer", "sectionListContinuation",
                "richGridRenderer", "richGridContinuation", "gridRenderer", "gridContinuation" })
            {
                var list = GetObjectFromJsonObject(obj, name);
                if (list == null) continue;
                foreach (var field in new[] { "continuations", "contents", "items" })
                {
                    IJsonValue values;
                    if (!list.TryGetValue(field, out values) || values.ValueType != JsonValueType.Array) continue;
                    var token = GetHistoryListContinuation(values.GetArray());
                    if (!string.IsNullOrWhiteSpace(token)) return token;
                }
                // The main list exists and has no tail: do not descend into its shelves.
                return string.Empty;
            }
        }
        return string.Empty;
    }
}
