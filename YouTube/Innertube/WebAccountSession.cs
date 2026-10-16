using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Web.Http;
using Windows.Web.Http.Filters;

namespace YouTube.Innertube
{
    // The EdgeHTML WebView and Windows.Web.Http share this app's cookie store.
    // Anonymous VR/VISIONOS and OAuth calls use a separate, cookie-free transport.
    internal static class WebAccountSession
    {
        private const string EnabledKey = "yt_web_account_enabled";
        private const string SessionFileName = "yt_web_account.bin";
        internal const string Origin = "https://www.youtube.com";
        internal const string LoginUrl = "https://accounts.google.com/ServiceLogin?service=youtube&continue=https%3A%2F%2Fwww.youtube.com%2F";
        private static readonly string[] CookieOrigins =
        {
            Origin + "/", "https://m.youtube.com/", "https://accounts.google.com/",
            "https://www.google.com/", "https://google.com/", "https://consent.youtube.com/"
        };
        private static readonly string[] SigningCookieNames = { "SAPISID", "__Secure-3PAPISID", "__Secure-1PAPISID" };
        private static readonly SemaphoreSlim StorageGate = new SemaphoreSlim(1, 1);
        private static readonly object InitializationGate = new object();
        private static Task _initialization;
        private static bool _enabled;
        private static int _authUser;
        private static string _pageId = string.Empty;
        private static string _visitorData = string.Empty;
        private static string _webClientVersion = string.Empty;
        private static string _userContext = string.Empty;
        private static string _revision = Guid.NewGuid().ToString("N");

        internal static string CacheKey { get { return _revision + (IsSignedIn ? "|web" : "|guest"); } }
        internal static string ClientVersion { get { return _webClientVersion; } }
        internal static bool IsSignedIn { get { return _enabled && HasSignInCookies; } }
        internal static bool HasSignInCookies { get { return !string.IsNullOrEmpty(FindSigningCookie(GetYouTubeCookies())); } }

        internal static Task InitializeAsync()
        {
            lock (InitializationGate)
            {
                if (_initialization == null) _initialization = RestoreAsync();
                return _initialization;
            }
        }

        internal static bool IsYouTubeUri(Uri uri)
        {
            return uri != null && uri.Scheme == "https" && IsDomain(uri.Host, "youtube.com");
        }

        private static bool IsDomain(string host, string domain)
        {
            return string.Equals(host, domain, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAccountDomain(string domain)
        {
            var host = (domain ?? string.Empty).TrimStart('.');
            return IsDomain(host, "youtube.com") || IsDomain(host, "google.com");
        }

        private static List<HttpCookie> GetYouTubeCookies()
        {
            using (var filter = new HttpBaseProtocolFilter())
            {
                return new List<HttpCookie>(filter.CookieManager.GetCookies(new Uri(Origin + "/")));
            }
        }

        private static string FindSigningCookie(List<HttpCookie> cookies)
        {
            // Sign with the cookie actually sent to YouTube, not a Google-domain cookie.
            foreach (var name in SigningCookieNames)
                foreach (var cookie in cookies)
                    if (cookie.Name == name && !string.IsNullOrEmpty(cookie.Value)) return cookie.Value;
            return string.Empty;
        }

        internal static bool ApplyHeaders(System.Net.Http.HttpRequestMessage request)
        {
            if (!_enabled || !IsYouTubeUri(request.RequestUri) || request.Headers.Contains("Authorization"))
                return false;

            List<HttpCookie> cookies;
            using (var filter = new HttpBaseProtocolFilter())
            {
                cookies = new List<HttpCookie>(filter.CookieManager.GetCookies(request.RequestUri));
            }
            var sapisid = FindSigningCookie(cookies);
            if (string.IsNullOrEmpty(sapisid)) return false;

            var seconds = (long)(DateTimeOffset.UtcNow - new DateTimeOffset(1970, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds;
            var source = seconds.ToString(CultureInfo.InvariantCulture) + " " + sapisid + " " + Origin;
            var sha1 = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha1);
            var digest = sha1.HashData(CryptographicBuffer.ConvertStringToBinary(source, BinaryStringEncoding.Utf8));
            var signature = "SAPISIDHASH " + seconds.ToString(CultureInfo.InvariantCulture) + "_"
                + CryptographicBuffer.EncodeToHexString(digest).ToLowerInvariant();
            var header = new StringBuilder();
            foreach (var cookie in cookies)
            {
                if (header.Length > 0) header.Append("; ");
                header.Append(cookie.Name).Append('=').Append(cookie.Value);
            }

            request.Headers.Remove("Cookie");
            request.Headers.Remove("Origin");
            request.Headers.Remove("X-Origin");
            request.Headers.Remove("X-Goog-AuthUser");
            request.Headers.Remove("X-Goog-PageId");
            request.Headers.Remove("X-Goog-Visitor-Id");
            request.Headers.TryAddWithoutValidation("Authorization", signature);
            request.Headers.TryAddWithoutValidation("Cookie", header.ToString());
            request.Headers.TryAddWithoutValidation("Origin", Origin);
            request.Headers.TryAddWithoutValidation("X-Origin", Origin);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", _authUser.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(_pageId)) request.Headers.TryAddWithoutValidation("X-Goog-PageId", _pageId);
            if (!string.IsNullOrEmpty(_visitorData)) request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", _visitorData);
            return true;
        }

        internal static void ApplyClientContext(JsonObject context)
        {
            if (!IsSignedIn || context == null) return;
            IJsonValue value;
            if (context.TryGetValue("client", out value) && value.ValueType == JsonValueType.Object)
            {
                var client = value.GetObject();
                if (!string.IsNullOrEmpty(_visitorData)) client["visitorData"] = JsonValue.CreateStringValue(_visitorData);
                if (ReadString(client, "clientName") == "WEB" && !string.IsNullOrEmpty(_webClientVersion))
                    client["clientVersion"] = JsonValue.CreateStringValue(_webClientVersion);
            }
            JsonObject user;
            if (!string.IsNullOrEmpty(_userContext) && JsonObject.TryParse(_userContext, out user))
                context["user"] = user;
        }

        private static string ReadString(JsonObject obj, string name)
        {
            IJsonValue value;
            if (obj == null || !obj.TryGetValue(name, out value)) return string.Empty;
            if (value.ValueType == JsonValueType.String) return value.GetString();
            if (value.ValueType == JsonValueType.Number) return value.GetNumber().ToString(CultureInfo.InvariantCulture);
            return string.Empty;
        }

        private static void ReadBrowserConfiguration(JsonObject configuration)
        {
            int authUser;
            if (int.TryParse(ReadString(configuration, "SESSION_INDEX"), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out authUser)) _authUser = Math.Max(0, authUser);
            _pageId = ReadString(configuration, "DELEGATED_SESSION_ID");
            _visitorData = ReadString(configuration, "VISITOR_DATA");
            _webClientVersion = ReadString(configuration, "INNERTUBE_CLIENT_VERSION");
            _userContext = string.Empty;
            IJsonValue value;
            if (configuration.TryGetValue("INNERTUBE_CONTEXT", out value) && value.ValueType == JsonValueType.Object)
            {
                var context = value.GetObject();
                if (context.TryGetValue("client", out value) && value.ValueType == JsonValueType.Object)
                {
                    var client = value.GetObject();
                    if (string.IsNullOrEmpty(_visitorData)) _visitorData = ReadString(client, "visitorData");
                    if (string.IsNullOrEmpty(_webClientVersion)) _webClientVersion = ReadString(client, "clientVersion");
                }
                if (context.TryGetValue("user", out value) && value.ValueType == JsonValueType.Object)
                    _userContext = value.GetObject().Stringify();
            }
            // A delegated channel must be selected in both the header and the body.
            if (!string.IsNullOrEmpty(_pageId))
            {
                JsonObject user;
                if (!JsonObject.TryParse(_userContext, out user)) user = new JsonObject();
                user["onBehalfOfUser"] = JsonValue.CreateStringValue(_pageId);
                _userContext = user.Stringify();
            }
        }

        internal static bool IsLoggedInConfiguration(JsonObject configuration)
        {
            IJsonValue value;
            return configuration != null && configuration.TryGetValue("LOGGED_IN", out value)
                && value.ValueType == JsonValueType.Boolean && value.GetBoolean();
        }

        internal static async Task RefreshBrowserContextAsync(string videoId)
        {
            await GetSignedPageDataAsync(videoId);
        }

        internal static async Task<JsonObject> GetSignedPageDataAsync(string videoId)
        {
            await InitializeAsync();
            if (!IsSignedIn) return null;
            var sessionKey = CacheKey;
            JsonObject page = null;
            // Use the browser cookie store here so refreshed Set-Cookie values are retained.
            // This also upgrades sessions saved before account/visitor context was captured.
            try
            {
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                using (var filter = new HttpBaseProtocolFilter())
                using (var client = new Windows.Web.Http.HttpClient(filter))
                using (var request = new Windows.Web.Http.HttpRequestMessage(HttpMethod.Get,
                    new Uri(Origin + (string.IsNullOrWhiteSpace(videoId) ? "/?authuser="
                        : "/watch?v=" + Uri.EscapeDataString(videoId) + "&authuser=")
                        + _authUser.ToString(CultureInfo.InvariantCulture))))
                {
                    request.Headers.TryAppendWithoutValidation("User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                    request.Headers.TryAppendWithoutValidation("Cache-Control", "no-cache");
                    using (var response = await client.SendRequestAsync(request).AsTask(timeout.Token))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var html = await response.Content.ReadAsStringAsync().AsTask(timeout.Token);
                        var configuration = ReadPageConfiguration(html);
                        page = ReadInitialPageData(html);
                        if (sessionKey != CacheKey) return null;
                        if (!IsLoggedInConfiguration(configuration))
                        {
                            System.Diagnostics.Debug.WriteLine("[WebAccount] Page did not confirm the browser account.");
                            return null;
                        }
                        ReadBrowserConfiguration(configuration);
                    }
                }
                if (sessionKey == CacheKey) await SaveAsync();
            }
            catch (Exception)
            {
                // Network failure does not turn an existing browser login into a guest.
                System.Diagnostics.Debug.WriteLine("[WebAccount] Browser context refresh unavailable.");
            }
            return sessionKey == CacheKey ? page : null;
        }

        private static JsonObject ReadPageConfiguration(string html)
        {
            var configuration = new JsonObject();
            var start = 0;
            while ((start = html.IndexOf("ytcfg.set(", start, StringComparison.Ordinal)) >= 0)
            {
                start += "ytcfg.set(".Length;
                while (start < html.Length && char.IsWhiteSpace(html[start])) start++;
                if (start >= html.Length || html[start] != '{') continue;
                int end;
                var part = ReadJsonObjectAt(html, start, out end);
                if (part != null) foreach (var pair in part) configuration[pair.Key] = pair.Value;
                start = end;
            }
            return configuration;
        }

        internal static JsonObject ReadInitialPageData(string html)
        {
            var index = 0;
            while ((index = html.IndexOf("ytInitialData", index, StringComparison.Ordinal)) >= 0)
            {
                index += "ytInitialData".Length;
                var equals = html.IndexOf('=', index);
                if (equals < index || equals - index > 32) continue;
                var start = equals + 1;
                while (start < html.Length && char.IsWhiteSpace(html[start])) start++;
                if (start >= html.Length || html[start] != '{') continue;
                int end;
                var data = ReadJsonObjectAt(html, start, out end);
                if (data != null) return data;
            }
            return null;
        }

        private static JsonObject ReadJsonObjectAt(string html, int start, out int next)
        {
            var depth = 0;
            var quoted = false;
            var escaped = false;
            next = html.Length;
            for (var end = start; end < html.Length; end++)
            {
                var c = html[end];
                if (quoted)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') quoted = false;
                    continue;
                }
                if (c == '"') quoted = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    next = end + 1;
                    JsonObject obj;
                    return JsonObject.TryParse(html.Substring(start, next - start), out obj) ? obj : null;
                }
            }
            return null;
        }

        private static List<HttpCookie> GetAccountCookies(HttpCookieManager manager)
        {
            var result = new List<HttpCookie>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var origin in CookieOrigins)
                foreach (var cookie in manager.GetCookies(new Uri(origin)))
                    if (IsAccountDomain(cookie.Domain)
                        && seen.Add(cookie.Domain + "\n" + cookie.Path + "\n" + cookie.Name)) result.Add(cookie);
            return result;
        }

        internal static async Task CompleteSignInAsync(JsonObject configuration)
        {
            await InitializeAsync();
            await StorageGate.WaitAsync();
            try
            {
                if (!HasSignInCookies || !IsLoggedInConfiguration(configuration))
                    throw new InvalidOperationException("YouTube sign-in has not completed.");
                ReadBrowserConfiguration(configuration);
                await WriteSessionAsync(_authUser);
                ApplicationData.Current.LocalSettings.Values[EnabledKey] = true;
                _enabled = true;
                _revision = Guid.NewGuid().ToString("N");
            }
            finally { StorageGate.Release(); }
        }

        internal static async Task SaveAsync()
        {
            await InitializeAsync();
            await StorageGate.WaitAsync();
            try { if (IsSignedIn) await WriteSessionAsync(_authUser); }
            finally { StorageGate.Release(); }
        }

        private static async Task WriteSessionAsync(int authUser)
        {
            var array = new JsonArray();
            using (var filter = new HttpBaseProtocolFilter())
            {
                foreach (var cookie in GetAccountCookies(filter.CookieManager))
                {
                    var item = new JsonObject();
                    item["name"] = JsonValue.CreateStringValue(cookie.Name);
                    item["value"] = JsonValue.CreateStringValue(cookie.Value);
                    item["domain"] = JsonValue.CreateStringValue(cookie.Domain);
                    item["path"] = JsonValue.CreateStringValue(cookie.Path);
                    item["secure"] = JsonValue.CreateBooleanValue(cookie.Secure);
                    item["httpOnly"] = JsonValue.CreateBooleanValue(cookie.HttpOnly);
                    if (cookie.Expires.HasValue)
                        item["expires"] = JsonValue.CreateStringValue(cookie.Expires.Value.ToString("O", CultureInfo.InvariantCulture));
                    array.Add(item);
                }
            }
            var root = new JsonObject();
            root["cookies"] = array;
            root["authUser"] = JsonValue.CreateNumberValue(authUser);
            root["pageId"] = JsonValue.CreateStringValue(_pageId);
            root["visitorData"] = JsonValue.CreateStringValue(_visitorData);
            root["webClientVersion"] = JsonValue.CreateStringValue(_webClientVersion);
            root["userContext"] = JsonValue.CreateStringValue(_userContext);
            var provider = new DataProtectionProvider("LOCAL=user");
            var encrypted = await provider.ProtectAsync(CryptographicBuffer.ConvertStringToBinary(root.Stringify(), BinaryStringEncoding.Utf8));
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(SessionFileName + ".tmp", CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBufferAsync(file, encrypted);
            await file.RenameAsync(SessionFileName, NameCollisionOption.ReplaceExisting);
        }

        private static async Task RestoreAsync()
        {
            object enabled;
            if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(EnabledKey, out enabled)
                || !(enabled is bool) || !(bool)enabled) return;
            await StorageGate.WaitAsync();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(SessionFileName);
                var encrypted = await FileIO.ReadBufferAsync(file);
                var provider = new DataProtectionProvider();
                var data = await provider.UnprotectAsync(encrypted);
                var root = JsonObject.Parse(CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, data));
                // Keep fresher live values, but restore missing HttpOnly cookies even when
                // SAPISID survived. A partial jar signs requests that YouTube treats as guests.
                using (var filter = new HttpBaseProtocolFilter())
                {
                    var live = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var cookie in GetAccountCookies(filter.CookieManager))
                        if (!string.IsNullOrEmpty(cookie.Value)) live.Add(cookie.Domain + "\n" + cookie.Path + "\n" + cookie.Name);
                    foreach (var value in root.GetNamedArray("cookies"))
                    {
                        var item = value.GetObject();
                        var domain = item.GetNamedString("domain");
                        if (!IsAccountDomain(domain)) continue;
                        var cookie = new HttpCookie(item.GetNamedString("name"), domain, item.GetNamedString("path"));
                        if (live.Contains(cookie.Domain + "\n" + cookie.Path + "\n" + cookie.Name)) continue;
                        cookie.Value = item.GetNamedString("value");
                        cookie.Secure = item.GetNamedBoolean("secure");
                        cookie.HttpOnly = item.GetNamedBoolean("httpOnly");
                        DateTimeOffset expires;
                        if (DateTimeOffset.TryParse(item.GetNamedString("expires", string.Empty), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expires))
                        {
                            if (expires <= DateTimeOffset.UtcNow) continue;
                            cookie.Expires = expires;
                        }
                        filter.CookieManager.SetCookie(cookie, false);
                    }
                }
                _authUser = Math.Max(0, (int)root.GetNamedNumber("authUser", 0));
                _pageId = root.GetNamedString("pageId", string.Empty);
                _visitorData = root.GetNamedString("visitorData", string.Empty);
                _webClientVersion = root.GetNamedString("webClientVersion", string.Empty);
                _userContext = root.GetNamedString("userContext", string.Empty);
                _enabled = HasSignInCookies;
            }
            catch (Exception)
            {
                // No exception text or cookie values: session material must not reach logs.
                _enabled = HasSignInCookies;
                System.Diagnostics.Debug.WriteLine("[WebAccount] Saved session could not be restored.");
            }
            finally { StorageGate.Release(); }
        }

        internal static async Task SignOutAsync()
        {
            await InitializeAsync();
            await StorageGate.WaitAsync();
            try
            {
                _enabled = false;
                _authUser = 0;
                _pageId = _visitorData = _webClientVersion = _userContext = string.Empty;
                _revision = Guid.NewGuid().ToString("N");
                ApplicationData.Current.LocalSettings.Values.Remove(EnabledKey);
                using (var filter = new HttpBaseProtocolFilter())
                    foreach (var cookie in GetAccountCookies(filter.CookieManager)) filter.CookieManager.DeleteCookie(cookie);
                var file = await ApplicationData.Current.LocalFolder.TryGetItemAsync(SessionFileName);
                if (file != null) await file.DeleteAsync();
            }
            finally { StorageGate.Release(); }
        }
    }
}
