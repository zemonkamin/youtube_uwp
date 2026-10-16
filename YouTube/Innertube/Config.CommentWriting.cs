using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using YouTube;
using YouTube.Innertube;

public static partial class Config
{
    internal sealed class CommentComposerContext
    {
        internal string CreateParams = string.Empty;
        internal string ContinuationToken = string.Empty;
        internal string AvatarUrl = string.Empty;
        internal string Refusal = string.Empty;
        internal string ClientName = "WEB";
        internal bool CommentsDisabled;
        internal DateTime ExpiresUtc = DateTime.UtcNow.AddMinutes(5);
    }

    internal sealed class CommentPostResult
    {
        internal bool Succeeded;
        internal bool BrowserSignInRequired;
        internal string ErrorMessage;
        internal CommentItem Comment;
    }

    private static readonly object CommentComposerGate = new object();
    private static readonly Dictionary<string, CommentComposerContext> CommentComposerContexts =
        new Dictionary<string, CommentComposerContext>(StringComparer.Ordinal);

    private static string CommentComposerKey(string videoId)
    {
        return WebAccountSession.CacheKey + "|" + Hl + "|" + Gl + "|" + videoId;
    }

    internal static CommentComposerContext GetCachedCommentComposer(string videoId)
    {
        lock (CommentComposerGate)
        {
            CommentComposerContext context;
            return CommentComposerContexts.TryGetValue(CommentComposerKey(videoId), out context)
                ? context : null;
        }
    }

    // Like Troubadour, take the opaque write permission from the comments panel,
    // including the submit-button shape fallback. Never manufacture it from a video id.
    private static void RememberCommentComposer(string videoId, string json, string token, string sessionKey, string clientName = "WEB")
    {
        if (sessionKey != WebAccountSession.CacheKey) return;
        JsonObject root;
        if (!FastJson.TryParseObject(json, out root)) return;
        var context = new CommentComposerContext { ContinuationToken = token ?? string.Empty, ClientName = clientName };
        var hasComposer = false;
        var guestComposer = false;
        foreach (var obj in EnumerateObjects(root, 200000))
        {
            var create = GetJsonString(obj, "createCommentParams");
            if (!string.IsNullOrWhiteSpace(create)) context.CreateParams = create;
            if (obj.ContainsKey("commentsDisabledRenderer")) context.CommentsDisabled = true;
            IJsonValue boxValue;
            if (!obj.TryGetValue("commentSimpleboxRenderer", out boxValue)
                || boxValue.ValueType != JsonValueType.Object) continue;
            var box = boxValue.GetObject();
            hasComposer = true;
            guestComposer = box.ContainsKey("prepareAccountEndpoint") && !box.ContainsKey("submitButton");
            context.AvatarUrl = ExtractThumbnailUrl(box, "authorThumbnail");
            IJsonValue submit;
            if (box.TryGetValue("submitButton", out submit) && string.IsNullOrWhiteSpace(context.CreateParams))
                context.CreateParams = FindCommentSubmitParams(submit, 8);
        }
        context.Refusal = ExtractCommentRefusal(root);
        // Shape/status diagnostics only: never log cookies, text or opaque write tokens.
        System.Diagnostics.Debug.WriteLine("[Comments] Composer " + clientName + ": field=" + hasComposer
            + ", guest=" + guestComposer + ", writeParams=" + !string.IsNullOrWhiteSpace(context.CreateParams)
            + ", continuation=" + !string.IsNullOrWhiteSpace(context.ContinuationToken));
        lock (CommentComposerGate)
        {
            if (sessionKey != WebAccountSession.CacheKey) return;
            CommentComposerContext previous;
            if (!hasComposer && string.IsNullOrWhiteSpace(context.CreateParams)
                && CommentComposerContexts.TryGetValue(CommentComposerKey(videoId), out previous)
                && previous.ExpiresUtc > DateTime.UtcNow && !string.IsNullOrWhiteSpace(previous.CreateParams)) return;
            if (CommentComposerContexts.Count >= 24) CommentComposerContexts.Clear();
            CommentComposerContexts[CommentComposerKey(videoId)] = context;
        }
    }

    private static string FindCommentSubmitParams(IJsonValue node, int depth)
    {
        if (node == null || depth <= 0) return string.Empty;
        if (node.ValueType == JsonValueType.Object)
        {
            var obj = node.GetObject();
            foreach (var pair in obj)
                if (pair.Key.EndsWith("Params", StringComparison.Ordinal)
                    && pair.Key != "trackingParams" && pair.Key != "clickTrackingParams"
                    && pair.Value.ValueType == JsonValueType.String
                    && !string.IsNullOrWhiteSpace(pair.Value.GetString())) return pair.Value.GetString();
            foreach (var pair in obj)
            {
                var found = FindCommentSubmitParams(pair.Value, depth - 1);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        }
        else if (node.ValueType == JsonValueType.Array)
            foreach (var child in node.GetArray())
            {
                var found = FindCommentSubmitParams(child, depth - 1);
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
        return string.Empty;
    }

    private static async Task<CommentComposerContext> ResolveCommentComposerAsync(string videoId, string sessionKey)
    {
        var context = GetCachedCommentComposer(videoId);
        if (context != null && context.ExpiresUtc > DateTime.UtcNow
            && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
        // The comments page may already have a compatible token. Refetch its header
        // with the browser transport before starting a new watch-next request.
        if (context != null && context.ExpiresUtc > DateTime.UtcNow
            && !string.IsNullOrWhiteSpace(context.ContinuationToken))
        {
            context = await LoadBrowserCommentPanelAsync(videoId, context.ContinuationToken, context.ClientName, sessionKey);
            if (context != null && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
        }
        var watch = await WebAccountSession.GetSignedPageDataAsync(videoId);
        if (sessionKey != WebAccountSession.CacheKey) return null;
        if (watch != null)
        {
            var token = ExtractCommentsContinuationToken(watch);
            RememberCommentComposer(videoId, watch.Stringify(), token, sessionKey);
            context = GetCachedCommentComposer(videoId);
            if (context != null && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
            if (!string.IsNullOrWhiteSpace(token))
            {
                context = await LoadBrowserCommentPanelAsync(videoId, token, "WEB", sessionKey);
                if (context != null && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
            }
        }
        // A token read before login (or by another client) can keep returning a guest
        // simplebox. Obtain both the page token and write permission with this account.
        foreach (var clientName in new[] { "WEB", "MWEB" })
        {
            if (sessionKey != WebAccountSession.CacheKey) return null;
            var pageBody = new JsonObject();
            pageBody["context"] = BuildBrowserCommentContext(clientName);
            pageBody["videoId"] = JsonValue.CreateStringValue(videoId);
            var page = await PostBrowserCommentsNextAsync(videoId, pageBody);
            var token = ExtractCommentsContinuationToken(page);
            RememberCommentComposer(videoId, page, token, sessionKey, clientName);
            context = GetCachedCommentComposer(videoId);
            if (context != null && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
            if (sessionKey != WebAccountSession.CacheKey) return null;
            if (string.IsNullOrWhiteSpace(token)) continue;
            context = await LoadBrowserCommentPanelAsync(videoId, token, clientName, sessionKey);
            if (context != null && !string.IsNullOrWhiteSpace(context.CreateParams)) return context;
        }
        return context;
    }

    private static async Task<CommentComposerContext> LoadBrowserCommentPanelAsync(
        string videoId, string token, string clientName, string sessionKey)
    {
        if (sessionKey != WebAccountSession.CacheKey) return null;
        var body = new JsonObject();
        body["context"] = BuildBrowserCommentContext(clientName);
        body["continuation"] = JsonValue.CreateStringValue(token);
        var panel = await PostBrowserCommentsNextAsync(videoId, body);
        RememberCommentComposer(videoId, panel, token, sessionKey, clientName);
        return sessionKey == WebAccountSession.CacheKey ? GetCachedCommentComposer(videoId) : null;
    }

    private static JsonObject BuildBrowserCommentContext(string clientName)
    {
        var context = JsonObject.Parse("{" + BuildShortsClientJson(clientName) + "}");
        WebAccountSession.ApplyClientContext(context);
        return context;
    }

    private static HttpRequestMessage CreateBrowserCommentRequest(string endpoint, string videoId, JsonObject body)
    {
        var client = body.GetNamedObject("context").GetNamedObject("client");
        var clientName = GetJsonString(client, "clientName");
        var request = new HttpRequestMessage(HttpMethod.Post,
            "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey);
        request.Content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", clientName == "WEB" ? WebUserAgent
            : "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientName == "WEB" ? "1" : "2");
        request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", GetJsonString(client, "clientVersion"));
        request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId));
        if (WebAccountSession.ApplyHeaders(request)) return request;
        request.Dispose();
        return null;
    }

    private static async Task<string> PostBrowserCommentsNextAsync(string videoId, JsonObject body)
    {
        using (var request = CreateBrowserCommentRequest("next", videoId, body))
        {
            if (request == null) return string.Empty;
            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                System.Diagnostics.Debug.WriteLine("[Comments] Panel request: " + (int)response.StatusCode);
                return response.IsSuccessStatusCode ? await response.Content.ReadAsStringAsync() : string.Empty;
            }
        }
    }

    internal static async Task<CommentPostResult> PostCommentAsync(string videoId, string text)
    {
        var failure = new CommentPostResult { ErrorMessage = Localization.GetString("CommentSendFailed") };
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(text)) return failure;
        await WebAccountSession.InitializeAsync();
        if (!WebAccountSession.IsSignedIn)
        {
            failure.BrowserSignInRequired = true;
            failure.ErrorMessage = Localization.GetString("CommentBrowserSignInRequired");
            return failure;
        }
        var sessionKey = WebAccountSession.CacheKey;
        try
        {
            var context = await ResolveCommentComposerAsync(videoId, sessionKey);
            if (sessionKey != WebAccountSession.CacheKey || !WebAccountSession.IsSignedIn) return failure;
            if (context == null || string.IsNullOrWhiteSpace(context.CreateParams))
            {
                failure.ErrorMessage = context != null && !string.IsNullOrWhiteSpace(context.Refusal)
                    ? context.Refusal : Localization.GetString(context != null && context.CommentsDisabled
                        ? "CommentWriteUnavailable" : "CommentSendFailed");
                return failure;
            }

            // The write permission belongs to the browser account.
            // Use that same account for posting, even if QR login selects another channel.
            foreach (var clientName in context.ClientName == "MWEB" ? new[] { "MWEB", "WEB" } : new[] { "WEB", "MWEB" })
            {
                if (sessionKey != WebAccountSession.CacheKey) return failure;
                var body = new JsonObject();
                body["context"] = BuildBrowserCommentContext(clientName);
                body["commentText"] = JsonValue.CreateStringValue(text);
                body["createCommentParams"] = JsonValue.CreateStringValue(context.CreateParams);
                using (var request = CreateBrowserCommentRequest("comment/create_comment", videoId, body))
                {
                    if (request == null) return failure;
                    using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        System.Diagnostics.Debug.WriteLine("[Comments] Create request: " + (int)response.StatusCode);
                        var json = await response.Content.ReadAsStringAsync();
                        JsonObject root;
                        if (!FastJson.TryParseObject(json, out root)) return failure;
                        if (response.IsSuccessStatusCode && CommentWasAccepted(root))
                        {
                            var comment = new CommentItem
                            {
                                Author = Localization.GetString("CommentOwnAuthor"),
                                AuthorThumbnail = context.AvatarUrl,
                                Text = text,
                                PublishedAt = string.Empty,
                                LikeCount = "0"
                            };
                            try
                            {
                                var comments = VideoParser.ParseComments(json);
                                if (comments.Count > 0) comment = comments[0];
                            }
                            catch (Exception)
                            {
                                // The server already accepted it. A display parser error
                                // must not keep the draft and invite a duplicate submission.
                                System.Diagnostics.Debug.WriteLine("[Comments] Accepted comment uses local display data.");
                            }
                            return new CommentPostResult { Succeeded = true, Comment = comment };
                        }
                        var reason = ExtractCommentRefusal(root);
                        if (!string.IsNullOrWhiteSpace(reason)) failure.ErrorMessage = reason;
                        if (!response.IsSuccessStatusCode || CommentWasRejected(root)) context.ExpiresUtc = DateTime.MinValue;
                        // Retry another client only after a definite rejection. A timeout or
                        // unrecognized success response might already have created the comment.
                        if ((int)response.StatusCode >= 500
                            || (response.IsSuccessStatusCode && !CommentWasRejected(root))) return failure;
                    }
                }
            }
        }
        catch (Exception)
        {
            System.Diagnostics.Debug.WriteLine("[Comments] Posting failed; draft retained.");
        }
        return failure;
    }

    private static bool CommentWasAccepted(JsonObject root)
    {
        foreach (var obj in EnumerateObjects(root, 20000))
        {
            IJsonValue result;
            if (obj.TryGetValue("actionResult", out result) && result.ValueType == JsonValueType.Object)
            {
                var status = GetJsonString(result.GetObject(), "status");
                if (status == "STATUS_SUCCEEDED") return true;
                if (!string.IsNullOrEmpty(status)) return false;
            }
        }
        foreach (var obj in EnumerateObjects(root, 20000))
            if (obj.ContainsKey("commentEntityPayload") || obj.ContainsKey("createCommentAction")) return true;
        return false;
    }

    private static bool CommentWasRejected(JsonObject root)
    {
        foreach (var obj in EnumerateObjects(root, 20000))
        {
            IJsonValue result;
            if (obj.TryGetValue("actionResult", out result) && result.ValueType == JsonValueType.Object
                && GetJsonString(result.GetObject(), "status") == "STATUS_FAILED") return true;
            if (obj.ContainsKey("openPopupAction")) return true;
        }
        return false;
    }

    private static string ExtractCommentRefusal(JsonObject root)
    {
        var renderers = new[] { "notificationTextRenderer", "alertRenderer", "messageRenderer",
            "confirmDialogRenderer", "backstagePostDialogRenderer" };
        var fields = new[] { "successResponseText", "errorMessage", "text", "title", "message", "dialogMessage" };
        foreach (var obj in EnumerateObjects(root, 20000))
            foreach (var name in renderers)
            {
                IJsonValue value;
                if (!obj.TryGetValue(name, out value) || value.ValueType != JsonValueType.Object) continue;
                foreach (var field in fields)
                {
                    var text = ExtractTextFromField(value.GetObject(), field, string.Empty);
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
        return string.Empty;
    }
}
