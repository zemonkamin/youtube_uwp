using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    internal sealed class LiveChatItem
    {
        public string Id { get; set; }
        public string ChannelId { get; set; }
        public string Author { get; set; }
        public string Message { get; set; }
        public string Avatar { get; set; }
        public string Amount { get; set; }
    }

    internal sealed class LiveChatPage
    {
        internal readonly List<LiveChatItem> Items = new List<LiveChatItem>();
        internal readonly List<string> RemovedIds = new List<string>();
        internal readonly List<string> RemovedAuthors = new List<string>();
        internal string Token, Banner;
        internal bool ClearBanner;
        internal double WaitSeconds = 10;
    }

    internal sealed class LiveChatStart
    {
        internal string Token, TopToken, AllToken;
    }

    internal static class LiveChatService
    {
        internal static string TokenFromNext(JsonObject root)
        {
            var chat = LiveJson.Find(root, "liveChatRenderer");
            if (LiveJson.Bool(chat, "isReplay")) return string.Empty;
            return Continuation(chat);
        }

        private static string Continuation(JsonObject renderer)
        {
            foreach (var value in LiveJson.Array(renderer, "continuations"))
            {
                var data = LiveJson.Find(value, "reloadContinuationData") ?? LiveJson.Find(value, "invalidationContinuationData")
                    ?? LiveJson.Find(value, "timedContinuationData");
                var token = LiveJson.String(data, "continuation");
                if (!string.IsNullOrEmpty(token)) return token;
            }
            return string.Empty;
        }

        // Troubadour fetches full filter tokens from the popout page. /next tokens
        // can be shortened and cannot be reused to switch between Top chat and All chat.
        internal static async Task<LiveChatStart> OpenAsync(string videoId, string token, CancellationToken cancellation)
        {
            var result = new LiveChatStart { Token = token };
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/live_chat?v="
                + Uri.EscapeDataString(videoId) + "&is_popout=1&hl=" + Uri.EscapeDataString(global::Config.Hl)))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", PlaybackResolver.WebUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", global::Config.Hl);
                if (WebAccountSession.IsSignedIn) WebAccountSession.ApplyHeaders(request);
                using (var response = await YouTubeHttpClient.Shared.SendAsync(request, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync();
                    var root = WebAccountSession.ReadInitialPageData(html);
                    var menu = LiveJson.Find(root, "sortFilterSubMenuRenderer");
                    var items = LiveJson.Array(menu, "subMenuItems");
                    if (items.Count >= 2)
                    {
                        result.TopToken = LiveJson.String(LiveJson.Find(items[0], "reloadContinuationData"), "continuation");
                        result.AllToken = LiveJson.String(LiveJson.Find(items[1], "reloadContinuationData"), "continuation");
                    }
                    if (string.IsNullOrEmpty(result.Token)) result.Token = Continuation(LiveJson.Find(root, "liveChatRenderer"));
                    if (string.IsNullOrEmpty(result.Token)) result.Token = Continuation(LiveJson.Find(root, "liveChatContinuation"));
                    if (string.IsNullOrEmpty(result.Token)) result.Token = result.TopToken;
                }
            }
            return result;
        }

        internal static async Task<LiveChatPage> FetchAsync(string token, CancellationToken cancellation)
        {
            var version = WebAccountSession.IsSignedIn && !string.IsNullOrEmpty(WebAccountSession.ClientVersion)
                ? WebAccountSession.ClientVersion : "2.20260626.01.00";
            var client = new JsonObject { ["clientName"] = JsonValue.CreateStringValue("WEB"),
                ["clientVersion"] = JsonValue.CreateStringValue(version), ["hl"] = JsonValue.CreateStringValue(global::Config.Hl),
                ["gl"] = JsonValue.CreateStringValue(global::Config.Gl) };
            var body = new JsonObject { ["context"] = new JsonObject { ["client"] = client },
                ["continuation"] = JsonValue.CreateStringValue(token) };
            using (var request = new HttpRequestMessage(HttpMethod.Post, InnertubeEndpoints.BuildWithoutKey("live_chat/get_live_chat?prettyPrint=false")))
            {
                request.Content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("User-Agent", PlaybackResolver.WebUserAgent);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", version);
                if (WebAccountSession.IsSignedIn) WebAccountSession.ApplyHeaders(request);
                using (var response = await YouTubeHttpClient.Shared.SendAsync(request, cancellation))
                {
                    response.EnsureSuccessStatusCode();
                    var root = JsonObject.Parse(await response.Content.ReadAsStringAsync());
                    var feed = LiveJson.Find(root, "liveChatContinuation");
                    if (feed == null) throw new InvalidOperationException("Live chat continuation absent");
                    return Parse(feed);
                }
            }
        }

        internal static LiveChatPage Parse(JsonObject feed)
        {
            var page = new LiveChatPage();
            foreach (var action in LiveJson.Array(feed, "actions"))
            {
                if (LiveJson.Find(action, "removeBannerForLiveChatCommand") != null) page.ClearBanner = true;
                var banner = LiveJson.Find(action, "addBannerToLiveChatCommand");
                if (banner != null)
                {
                    var said = LiveJson.Find(banner, "liveChatTextMessageRenderer");
                    if (said != null) page.Banner = Text(LiveJson.Object(said, "authorName")) + ": " + Text(LiveJson.Object(said, "message"));
                    continue;
                }
                var remove = LiveJson.Find(action, "removeChatItemAction") ?? LiveJson.Find(action, "markChatItemAsDeletedAction");
                var removedId = LiveJson.String(remove, "targetItemId");
                if (!string.IsNullOrEmpty(removedId)) page.RemovedIds.Add(removedId);
                var author = LiveJson.Find(action, "markChatItemsByAuthorAsDeletedAction");
                var removedAuthor = LiveJson.String(author, "externalChannelId");
                if (!string.IsNullOrEmpty(removedAuthor)) page.RemovedAuthors.Add(removedAuthor);
                var add = LiveJson.Find(action, "addChatItemAction") ?? LiveJson.Find(action, "replaceChatItemAction");
                if (add == null) continue;
                var renderer = LiveJson.Find(add, "liveChatTextMessageRenderer") ?? LiveJson.Find(add, "liveChatPaidMessageRenderer")
                    ?? LiveJson.Find(add, "liveChatMembershipItemRenderer") ?? LiveJson.Find(add, "liveChatPaidStickerRenderer");
                if (renderer == null) continue;
                var text = Text(LiveJson.Object(renderer, "message"));
                if (string.IsNullOrEmpty(text)) text = Text(LiveJson.Object(renderer, "headerSubtext"));
                var amount = Text(LiveJson.Object(renderer, "purchaseAmountText"));
                if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(amount)) continue;
                var avatar = string.Empty;
                foreach (var photo in LiveJson.Array(LiveJson.Object(renderer, "authorPhoto"), "thumbnails"))
                { if (photo.ValueType == JsonValueType.Object) { avatar = LiveJson.String(photo.GetObject(), "url"); break; } }
                page.Items.Add(new LiveChatItem { Id = LiveJson.String(renderer, "id"), ChannelId = LiveJson.String(renderer, "authorExternalChannelId"),
                    Author = Text(LiveJson.Object(renderer, "authorName")), Message = text, Avatar = avatar, Amount = amount });
            }
            foreach (var step in LiveJson.Array(feed, "continuations"))
            {
                var data = LiveJson.Find(step, "invalidationContinuationData") ?? LiveJson.Find(step, "timedContinuationData")
                    ?? LiveJson.Find(step, "reloadContinuationData");
                var next = LiveJson.String(data, "continuation");
                if (string.IsNullOrEmpty(next)) continue;
                page.Token = next;
                var timeout = LiveJson.Number(data, "timeoutMs");
                if (timeout > 0) page.WaitSeconds = Math.Max(2, timeout / 1000);
                break;
            }
            return page;
        }

        private static string Text(JsonObject value)
        {
            var simple = LiveJson.String(value, "simpleText"); if (!string.IsNullOrEmpty(simple)) return simple;
            var text = new StringBuilder();
            foreach (var run in LiveJson.Array(value, "runs"))
            {
                if (run.ValueType != JsonValueType.Object) continue;
                var r = run.GetObject(); var part = LiveJson.String(r, "text");
                if (string.IsNullOrEmpty(part))
                {
                    var emoji = LiveJson.Object(r, "emoji"); part = LiveJson.String(emoji, "emojiId");
                    var shortcuts = LiveJson.Array(emoji, "shortcuts");
                    if (part.Length > 4) part = shortcuts.Count > 0 && shortcuts[0].ValueType == JsonValueType.String ? shortcuts[0].GetString() : string.Empty;
                }
                text.Append(part);
            }
            return text.ToString();
        }
    }
}
