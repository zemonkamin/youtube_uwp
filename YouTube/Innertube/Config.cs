using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using YouTube; // PlayerFormatModel lives in the YouTube namespace; Config is global.
using YouTube.Innertube;

public static partial class Config
{
    private static string _usertoken = "";
    private static bool _userTokenLoaded;
    private static readonly object _userTokenLoadGate = new object();
    private static readonly YouTubeHttpClient httpClient = YouTubeHttpClient.Shared;
    private static string _cachedAccessToken = "";
    private static string _cachedAccessTokenRefreshToken = "";
    private static DateTime _cachedAccessTokenExpiresUtc = DateTime.MinValue;
    private const string CachedAccessTokenSetting = "yt_cached_access_token";
    private const string CachedAccessTokenExpiresSetting = "yt_cached_access_token_expires";
    private const string CachedAccessTokenOwnerSetting = "yt_cached_access_token_owner";

    // Performance knobs for UWP/VS2015/C#6. Keep these conservative: fast, but without
    // changing public method signatures or requiring newer language/runtime features.
    private const int BrowseCacheSeconds = 180;
    private const int AccountCacheSeconds = 900;
    private const int SubscriptionsCacheSeconds = 900;
    private const int MaxObjectsToScanForVideoCards = 5000;
    private const int MaxObjectsToScanForSubscriptions = 10000;
    private const int MaxObjectsToScanForComments = 7000;
    private const int MaxCommentsToParse = 80;
    private const int MaxVideoListCacheEntries = 64;
    private const bool VerboseParserLogs = false;
    private static readonly bool UseMobileCardThumbnails = DetectWindowsMobileDevice();
    private const string AccountAvatarCacheFileName = "yt_account_avatar.jpg";
    private const string AccountAvatarTokenKeySetting = "yt_account_avatar_token_key";
    private const string AccountAvatarUrlSetting = "yt_account_avatar_url";
    private const string AccountAvatarReadySetting = "yt_account_avatar_ready";
    private const string SelectedYouTubeAccountBrandIdSetting = "yt_selected_account_brand_id";
    private const string SelectedYouTubeAccountKeySetting = "yt_selected_account_key";
    private const string SelectedYouTubeAccountInitializedSetting = "yt_selected_account_initialized";

    private static readonly SemaphoreSlim _tokenRefreshGate = new SemaphoreSlim(1, 1);
    private static readonly SemaphoreSlim _accountAvatarCacheGate = new SemaphoreSlim(1, 1);
    private static readonly object _cacheGate = new object();

    private sealed class VideoListCacheEntry
    {
        public string TokenKey { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public List<VideoCardItem> Items { get; set; }
    }

    private sealed class SubscriptionsCacheEntry
    {
        public string TokenKey { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public List<SubscriptionChannel> Items { get; set; }
    }

    private sealed class AccountCacheEntry
    {
        public string TokenKey { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public AccountInfo Account { get; set; }
    }

    private static readonly Dictionary<string, VideoListCacheEntry> _videoListCache = new Dictionary<string, VideoListCacheEntry>(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> _channelAvatarById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> _channelAvatarByTitle = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Task<string>> _channelAvatarLookupTasks = new Dictionary<string, Task<string>>(StringComparer.OrdinalIgnoreCase);
    private const string ChannelAvatarCacheSettingPrefix = "channel_avatar_";
    // Four compact MWEB metadata requests let visible cards fill progressively without the old
    // eight-wave wait on a 16-card phone feed. Actual avatar files use a different CDN host.
    private static readonly SemaphoreSlim _channelAvatarNetworkGate = new SemaphoreSlim(
        global::YouTube.ResponsiveLayout.IsPhoneDevice ? 4 : 6);
    private static bool _channelDataApiUnavailable;
    private static SubscriptionsCacheEntry _subscriptionsCache;
    private static AccountCacheEntry _accountCache;

    private static readonly string[] VideoRendererMarkers = new[]
    {
        "\"videoRenderer\"",
        "\"gridVideoRenderer\"",
        "\"compactVideoRenderer\"",
        "\"playlistVideoRenderer\"",
        "\"playlistPanelVideoRenderer\"",
        "\"lockupViewModel\"",
        "\"reelItemRenderer\"",
        "\"shortsLockupViewModel\"",
        "\"tileRenderer\""
    };

    private static readonly string[] NotificationRendererMarkers = new[]
    {
        "\"notificationRenderer\"",
        "\"notificationViewModel\""
    };

    // YouTube API constants
    private const string OAuthClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
    private const string OAuthClientSecret = "SboVhoG9s0rNafixCSGGKXAT";
    private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
    public static string InnertubeApiKeyValue => InnertubeApiKey;
    private const string UserAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0)";
    private const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const string ShortsWebClientVersion = "2.20260206.01.00";
    private const string ShortsMwebClientVersion = "2.20251222.01.00";
    // Ordered by how likely each is to accept the TV-minted bearer: TV first (it is the client the
    // token was issued for), then the two clients whose Shorts surface is native.
    private static readonly string[] ShortsAuthClients = new[] { "TVHTML5", "MWEB", "WEB" };
    private const string HomeWebClientVersion = "2.20260430.08.00";
    private const string ShortsVisitorId = "CgtjTS00dGRYTXhBOCif8OnOBjIoCgJQTBIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicgSA%3D%3D";

    public static string UserToken
    {
        get { return _usertoken; }
    }

    public static void SetUserToken(string token)
    {
        var tokenChanged = !string.Equals(_usertoken ?? string.Empty, token ?? string.Empty, StringComparison.Ordinal);
        _usertoken = token;
        _userTokenLoaded = true;
        // Save to local settings
        ApplicationData.Current.LocalSettings.Values["yt_refresh_token"] = token;
        if (tokenChanged)
        {
            ClearCachedAccessToken();
            ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountBrandIdSetting);
            ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountKeySetting);
            ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountInitializedSetting);
        }
        System.Diagnostics.Debug.WriteLine($"Config: Saved token to local settings");
    }

    public static void LoadUserToken()
    {
        if (_userTokenLoaded)
        {
            return;
        }

        lock (_userTokenLoadGate)
        {
            if (_userTokenLoaded)
            {
                return;
            }

            // LocalSettings is a WinRT boundary. Reading it on every card/page request was both
            // surprisingly expensive on phones and produced hundreds of debugger messages.
            // The refresh token changes only through Set/ClearUserToken, so load it once.
            var values = ApplicationData.Current.LocalSettings.Values;
            object stored;
            if (values.TryGetValue("yt_refresh_token", out stored))
            {
                _usertoken = stored as string ?? string.Empty;
            }
            else if (values.TryGetValue("AuthToken", out stored))
            {
                _usertoken = stored as string ?? string.Empty;
            }

            TryLoadCachedAccessToken(values, _usertoken);

            _userTokenLoaded = true;
            System.Diagnostics.Debug.WriteLine(
                "Config: token state loaded once (signedIn="
                + (!string.IsNullOrWhiteSpace(_usertoken)) + ")");
        }
    }

    public static string SelectedYouTubeAccountBrandId
    {
        get
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    SelectedYouTubeAccountBrandIdSetting, out value) && value != null)
                {
                    return value.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }
    }

    private static string SelectedYouTubeAccountKey
    {
        get
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    SelectedYouTubeAccountKeySetting, out value) && value != null)
                {
                    return value.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }
    }

    public static void SelectYouTubeAccount(YouTubeAccountItem account)
    {
        if (account == null)
        {
            return;
        }

        try
        {
            ApplicationData.Current.LocalSettings.Values[SelectedYouTubeAccountBrandIdSetting] =
                account.BrandId ?? string.Empty;
            ApplicationData.Current.LocalSettings.Values[SelectedYouTubeAccountKeySetting] =
                account.AccountKey ?? string.Empty;
            ApplicationData.Current.LocalSettings.Values[SelectedYouTubeAccountInitializedSetting] = true;
        }
        catch
        {
        }

        lock (_cacheGate)
        {
            _videoListCache.Clear();
            _subscriptionsCache = null;
            _accountCache = null;
            _channelAvatarById.Clear();
            _channelAvatarByTitle.Clear();
        }
    }

    // Brand channels share the same OAuth token as the primary Google identity. InnerTube
    // selects one of them through context.user.onBehalfOfUser; X-Goog-AuthUser alone does not.
    internal static string ApplySelectedAccountContext(string payload, bool authenticated)
    {
        var brandId = authenticated ? SelectedYouTubeAccountBrandId : string.Empty;
        if (string.IsNullOrWhiteSpace(brandId) || string.IsNullOrWhiteSpace(payload))
        {
            return payload;
        }

        try
        {
            var root = JsonObject.Parse(payload);
            IJsonValue contextValue;
            if (!root.TryGetValue("context", out contextValue)
                || contextValue.ValueType != JsonValueType.Object)
            {
                return payload;
            }

            var context = contextValue.GetObject();
            JsonObject user;
            IJsonValue userValue;
            if (context.TryGetValue("user", out userValue)
                && userValue.ValueType == JsonValueType.Object)
            {
                user = userValue.GetObject();
            }
            else
            {
                user = new JsonObject();
                context["user"] = user;
            }

            user["onBehalfOfUser"] = JsonValue.CreateStringValue(brandId);
            return root.Stringify();
        }
        catch
        {
            return payload;
        }
    }

    internal static void ApplySelectedAccountHeader(HttpRequestMessage request, bool authenticated)
    {
        if (request == null || !authenticated)
        {
            return;
        }

        var brandId = SelectedYouTubeAccountBrandId;
        if (!string.IsNullOrWhiteSpace(brandId))
        {
            request.Headers.TryAddWithoutValidation("X-Goog-PageId", brandId);
        }
    }

    public static void ClearUserToken()
    {
        _usertoken = "";
        _userTokenLoaded = true;
        ClearCachedAccessToken();
        ApplicationData.Current.LocalSettings.Values.Remove("yt_refresh_token");
        ApplicationData.Current.LocalSettings.Values.Remove("AuthToken");
        ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountBrandIdSetting);
        ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountKeySetting);
        ApplicationData.Current.LocalSettings.Values.Remove(SelectedYouTubeAccountInitializedSetting);
        System.Diagnostics.Debug.WriteLine("Config: Cleared token from local settings");
    }

    public static bool HasCachedAccountAvatar(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        var values = ApplicationData.Current.LocalSettings.Values;
        if (!values.ContainsKey(AccountAvatarReadySetting) ||
            !(values[AccountAvatarReadySetting] is bool) ||
            !(bool)values[AccountAvatarReadySetting] ||
            !values.ContainsKey(AccountAvatarTokenKeySetting))
        {
            return false;
        }

        var storedTokenKey = values[AccountAvatarTokenKeySetting] as string;
        return string.Equals(storedTokenKey, BuildTokenKey(refreshToken), StringComparison.Ordinal);
    }

    public static string GetCachedAccountAvatarUri(string refreshToken)
    {
        return HasCachedAccountAvatar(refreshToken)
            ? "ms-appdata:///local/" + AccountAvatarCacheFileName
            : string.Empty;
    }

    public static async Task<bool> UpdateCachedAccountAvatarAsync(string refreshToken, string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return false;
        }

        if (thumbnailUrl.StartsWith("//", StringComparison.Ordinal))
        {
            thumbnailUrl = "https:" + thumbnailUrl;
        }

        await _accountAvatarCacheGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var tokenKey = BuildTokenKey(refreshToken);
            var storedTokenKey = values.ContainsKey(AccountAvatarTokenKeySetting)
                ? values[AccountAvatarTokenKeySetting] as string
                : string.Empty;
            var storedUrl = values.ContainsKey(AccountAvatarUrlSetting)
                ? values[AccountAvatarUrlSetting] as string
                : string.Empty;

            if (HasCachedAccountAvatar(refreshToken) &&
                string.Equals(storedTokenKey, tokenKey, StringComparison.Ordinal) &&
                string.Equals(storedUrl, thumbnailUrl, StringComparison.Ordinal))
            {
                return false;
            }

            byte[] imageBytes;
            try
            {
                imageBytes = await YouTubeImageClient.DownloadAsync(thumbnailUrl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Account avatar download failed: " + ex.Message);
                return false;
            }

            if (imageBytes == null || imageBytes.Length == 0)
            {
                return false;
            }

            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    AccountAvatarCacheFileName,
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, imageBytes);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Account avatar cache write failed: " + ex.Message);
                return false;
            }

            // Metadata is written only after the file is fully replaced, so the tabbar
            // never treats a partial/failed write as a valid cached avatar.
            values[AccountAvatarTokenKeySetting] = tokenKey;
            values[AccountAvatarUrlSetting] = thumbnailUrl;
            values[AccountAvatarReadySetting] = true;
            return true;
        }
        finally
        {
            _accountAvatarCacheGate.Release();
        }
    }

    // Legacy Config facade. Server transport and new focused clients live beside this file.

    public static async Task<string> RefreshAccessTokenAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return string.Empty;
        }

        if (IsAccessTokenCacheValid(refreshToken))
        {
            return _cachedAccessToken;
        }

        await _tokenRefreshGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // If several screens start loading at once, only the first request should
            // hit oauth2.googleapis.com. Everyone else reuses the token refreshed above.
            if (IsAccessTokenCacheValid(refreshToken))
            {
                return _cachedAccessToken;
            }

            var token = await OAuthClient.RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
            if (token == null || string.IsNullOrWhiteSpace(token.AccessToken))
            {
                System.Diagnostics.Debug.WriteLine("[OAuth] Failed to refresh access token: "
                    + (token != null ? token.Error : "empty_response"));
                return string.Empty;
            }

            _cachedAccessToken = token.AccessToken;
            _cachedAccessTokenRefreshToken = refreshToken;
            _cachedAccessTokenExpiresUtc = DateTime.UtcNow.AddSeconds(
                Math.Max(60, token.ExpiresInSeconds - 60));
            PersistCachedAccessToken(refreshToken);
            return token.AccessToken;
        }
        finally
        {
            _tokenRefreshGate.Release();
        }
    }

    private static bool IsAccessTokenCacheValid(string refreshToken)
    {
        return !string.IsNullOrWhiteSpace(_cachedAccessToken) &&
            string.Equals(_cachedAccessTokenRefreshToken, refreshToken, StringComparison.Ordinal) &&
            DateTime.UtcNow < _cachedAccessTokenExpiresUtc;
    }

    private static void TryLoadCachedAccessToken(
        Windows.Foundation.Collections.IPropertySet values,
        string refreshToken)
    {
        if (values == null || string.IsNullOrWhiteSpace(refreshToken))
            return;

        try
        {
            object tokenValue;
            object expiryValue;
            object ownerValue;
            if (!values.TryGetValue(CachedAccessTokenSetting, out tokenValue)
                || !values.TryGetValue(CachedAccessTokenExpiresSetting, out expiryValue)
                || !values.TryGetValue(CachedAccessTokenOwnerSetting, out ownerValue))
                return;

            long ticks;
            var token = tokenValue as string;
            var owner = ownerValue as string;
            if (string.IsNullOrWhiteSpace(token)
                || !long.TryParse(expiryValue == null ? string.Empty : expiryValue.ToString(), out ticks)
                || !string.Equals(owner, BuildTokenKey(refreshToken), StringComparison.Ordinal))
                return;

            var expiresUtc = new DateTime(ticks, DateTimeKind.Utc);
            if (expiresUtc <= DateTime.UtcNow)
                return;

            _cachedAccessToken = token;
            _cachedAccessTokenRefreshToken = refreshToken;
            _cachedAccessTokenExpiresUtc = expiresUtc;
        }
        catch
        {
            ClearCachedAccessToken();
        }
    }

    private static void PersistCachedAccessToken(string refreshToken)
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values[CachedAccessTokenSetting] = _cachedAccessToken;
            values[CachedAccessTokenExpiresSetting] = _cachedAccessTokenExpiresUtc.Ticks.ToString();
            values[CachedAccessTokenOwnerSetting] = BuildTokenKey(refreshToken);
        }
        catch
        {
        }
    }

    private static void ClearCachedAccessToken()
    {
        _cachedAccessToken = string.Empty;
        _cachedAccessTokenRefreshToken = string.Empty;
        _cachedAccessTokenExpiresUtc = DateTime.MinValue;
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            values.Remove(CachedAccessTokenSetting);
            values.Remove(CachedAccessTokenExpiresSetting);
            values.Remove(CachedAccessTokenOwnerSetting);
        }
        catch
        {
        }
    }

    public static async Task<List<SubscriptionChannel>> GetSubscribedChannelsAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new List<SubscriptionChannel>();
        }

        List<SubscriptionChannel> cachedSubscriptions;
        if (TryGetCachedSubscriptions(refreshToken, out cachedSubscriptions))
        {
            RememberSubscriptionChannelAvatars(cachedSubscriptions);
            return cachedSubscriptions;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine("[Subscriptions] Failed to get access token");
            return new List<SubscriptionChannel>();
        }

        System.Diagnostics.Debug.WriteLine($"[Subscriptions] Got access token, fetching channels...");

        var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20250209.19.00\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\"}},\"browseId\":\"FEchannels\"}";
        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "85");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "7.20250209.19.00");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            ApplySelectedAccountHeader(request, true);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, true), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Subscriptions] Response: {response.StatusCode}");
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Error response: {errorContent}");
                return new List<SubscriptionChannel>();
            }
            
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[Subscriptions] Got response, length: {json.Length}");
            
            var parsed = ParseSubscribedChannels(json);
            RememberSubscriptionChannelAvatars(parsed);
            SaveCachedSubscriptions(refreshToken, parsed);
            return parsed;
        }
    }

    public static async Task<List<VideoCardItem>> GetChannelVideosAsync(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            System.Diagnostics.Debug.WriteLine($"[ChannelVideos] ChannelId is empty");
            return new List<VideoCardItem>();
        }

        LoadUserToken();
        var refreshToken = UserToken;
        
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            System.Diagnostics.Debug.WriteLine($"[ChannelVideos] No refresh token available");
            return new List<VideoCardItem>();
        }

        var cacheKey = BuildVideoListCacheKey("channel", channelId, null, 0);
        List<VideoCardItem> cachedVideos;
        if (TryGetCachedVideoList(refreshToken, cacheKey, out cachedVideos))
        {
            ApplyKnownChannelIdToVideos(cachedVideos, channelId);
            if (HasMissingChannelThumbnails(cachedVideos))
            {
                var cachedAccessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
                await HydrateMissingChannelThumbnailsAsync(cachedVideos, cachedAccessToken).ConfigureAwait(false);
            }
            return cachedVideos;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine($"[ChannelVideos] Failed to get access token");
            return new List<VideoCardItem>();
        }

        System.Diagnostics.Debug.WriteLine($"[ChannelVideos] Fetching videos for channel: {channelId}");

        var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20250209.19.00\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\"}},\"browseId\":\"" + channelId + "\",\"params\":\"EgZ2aWRlb3PyBgQKAjoA\"}";
        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "85");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "7.20250209.19.00");
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            ApplySelectedAccountHeader(request, true);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, true), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[ChannelVideos] Error: {response.StatusCode} - {errorContent}");
                return new List<VideoCardItem>();
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[ChannelVideos] Got response, length: {json.Length}");
            
            var videos = ParseVideoCards(json);
            ApplyKnownChannelIdToVideos(videos, channelId);
            await HydrateMissingChannelThumbnailsAsync(videos, accessToken).ConfigureAwait(false);
            SaveCachedVideoList(refreshToken, cacheKey, videos);
            System.Diagnostics.Debug.WriteLine($"[ChannelVideos] Total videos parsed: {videos.Count}");
            return videos;
        }
    }

    public sealed class HomeRecommendationsPage
    {
        public List<VideoCardItem> Videos { get; set; }
        public string ContinuationToken { get; set; }
        public Task DeferredEnrichment { get; set; }

        public HomeRecommendationsPage()
        {
            Videos = new List<VideoCardItem>();
            ContinuationToken = string.Empty;
        }
    }

    public static async Task<HomeRecommendationsPage> GetRecommendationsPageAsync(
        string refreshToken,
        string continuationToken,
        int count)
    {
        var page = new HomeRecommendationsPage();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return page;
        }

        if (count <= 0)
        {
            count = 24;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return page;
        }

        InnertubeRequestCoordinator.JsonResponse response;
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            // Initial Home request.
            response = await GetTvBrowseResponseAsync(
                accessToken,
                "FEwhat_to_watch",
                null,
                null).ConfigureAwait(false);
        }
        else
        {
            // True next page. Do not re-request FEwhat_to_watch with a larger count.
            response = await GetTvBrowseResponseAsync(
                accessToken,
                null,
                null,
                continuationToken).ConfigureAwait(false);
        }

        if (response == null || response.Root == null)
        {
            return page;
        }

        // The request coordinator already parsed this large response. Reusing its tree avoids
        // two extra full JSON parses on every Home page (cards and continuation).
        page.Videos = ParseVideoCards(response.Root, count);
        page.DeferredEnrichment = HydrateChannelThumbnailsSafeAsync(page.Videos, accessToken);

        try
        {
            page.ContinuationToken = ExtractHomeContinuationToken(response.Root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Home] Continuation parse failed: " + ex.Message);
        }

        System.Diagnostics.Debug.WriteLine(
            "[Home] Page parsed: videos=" + page.Videos.Count
            + ", continuation=" + (!string.IsNullOrWhiteSpace(page.ContinuationToken)));

        return page;
    }

    private static string ExtractHomeContinuationToken(IJsonValue root)
    {
        if (root == null)
        {
            return string.Empty;
        }

        try
        {
            // Prefer the explicit tail continuation of the feed. This prevents picking tokens
            // belonging to horizontal shelves or unrelated nested renderers.
            var fallbackToken = string.Empty;
            foreach (var obj in EnumerateObjects(root, 10000))
            {
                var continuationItem = GetObjectFromJsonObject(obj, "continuationItemRenderer");
                if (continuationItem != null)
                {
                    var token = ExtractContinuationTokenFromObject(continuationItem);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }

                // Some clients expose nextContinuationData directly instead of wrapping it in
                // continuationItemRenderer. Check it during the same tree walk.
                var next = GetObjectFromJsonObject(obj, "nextContinuationData");
                if (next != null && string.IsNullOrWhiteSpace(fallbackToken))
                {
                    var token = GetJsonString(next, "continuation");
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        fallbackToken = token;
                    }
                }
            }

            return fallbackToken;
        }
        catch
        {
        }

        return string.Empty;
    }

    public static async Task<List<VideoCardItem>> GetRecommendationsAsync(string refreshToken, int count)
    {
        var page = await GetRecommendationsPageAsync(refreshToken, null, count).ConfigureAwait(false);
        return page != null && page.Videos != null
            ? page.Videos
            : new List<VideoCardItem>();
    }

    public sealed class SubscriptionsFeedPage
    {
        public List<VideoCardItem> Videos { get; set; }
        public string ContinuationToken { get; set; }
        public Task DeferredEnrichment { get; set; }

        public SubscriptionsFeedPage()
        {
            Videos = new List<VideoCardItem>();
            ContinuationToken = string.Empty;
        }
    }

    public static async Task<SubscriptionsFeedPage> GetSubscriptionsFeedPageAsync(
        string refreshToken,
        string continuationToken,
        int count)
    {
        var page = new SubscriptionsFeedPage();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return page;
        }

        if (count <= 0)
        {
            count = 50;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return page;
        }

        var response = string.IsNullOrWhiteSpace(continuationToken)
            ? await GetTvBrowseResponseAsync(accessToken, "FEsubscriptions", null, null).ConfigureAwait(false)
            : await GetTvBrowseResponseAsync(accessToken, null, null, continuationToken).ConfigureAwait(false);

        if (response == null || response.Root == null)
        {
            return page;
        }

        page.Videos = ParseVideoCards(response.Root, count);
        page.DeferredEnrichment = HydrateChannelThumbnailsSafeAsync(page.Videos, accessToken);

        try
        {
            page.ContinuationToken = ExtractHomeContinuationToken(response.Root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Subscriptions] Continuation parse failed: " + ex.Message);
        }

        System.Diagnostics.Debug.WriteLine(
            "[Subscriptions] Page parsed: videos=" + page.Videos.Count
            + ", continuation=" + (!string.IsNullOrWhiteSpace(page.ContinuationToken)));

        return page;
    }

    public static async Task<List<VideoCardItem>> GetSubscriptionsFeedVideosAsync(string refreshToken, int count)
    {
        var page = await GetSubscriptionsFeedPageAsync(refreshToken, null, count).ConfigureAwait(false);
        return page != null && page.Videos != null
            ? page.Videos
            : new List<VideoCardItem>();
    }

    public static Task<List<HomeCategoryItem>> GetHomeCategoriesAsync(string refreshToken)
    {
        // Home chips are fixed locally. Do not request categories/chips from Innertube.
        var result = new List<HomeCategoryItem>();
        result.Add(new HomeCategoryItem
        {
            Title = Localization.GetString("CategoryAll"),
            IsAll = true,
            CategoryId = "all",
            UseWebClient = true,
            ClientName = "ALL"
        });

        AddFixedHomeCategory(result, "1", Localization.GetString("CategoryFilmAnimation"), "Film & Animation");
        AddFixedHomeCategory(result, "2", Localization.GetString("CategoryAutosVehicles"), "Autos & Vehicles");
        AddFixedHomeCategory(result, "10", Localization.GetString("CategoryMusic"), "Music");
        AddFixedHomeCategory(result, "15", Localization.GetString("CategoryPetsAnimals"), "Pets & Animals");
        AddFixedHomeCategory(result, "17", Localization.GetString("CategorySports"), "Sports");
        AddFixedHomeCategory(result, "18", Localization.GetString("CategoryShortMovies"), "Short Movies");
        AddFixedHomeCategory(result, "19", Localization.GetString("CategoryTravelEvents"), "Travel & Events");
        AddFixedHomeCategory(result, "20", Localization.GetString("CategoryGaming"), "Gaming");
        AddFixedHomeCategory(result, "21", Localization.GetString("CategoryVideoblogging"), "Videoblogging");
        AddFixedHomeCategory(result, "22", Localization.GetString("CategoryPeopleBlogs"), "People & Blogs");
        AddFixedHomeCategory(result, "23", Localization.GetString("CategoryComedy"), "Comedy videos");
        AddFixedHomeCategory(result, "24", Localization.GetString("CategoryEntertainment"), "Entertainment");
        AddFixedHomeCategory(result, "25", Localization.GetString("CategoryNewsPolitics"), "News & Politics");
        AddFixedHomeCategory(result, "26", Localization.GetString("CategoryHowtoStyle"), "Howto & Style");
        AddFixedHomeCategory(result, "27", Localization.GetString("CategoryEducation"), "Education");
        AddFixedHomeCategory(result, "28", Localization.GetString("CategoryScienceTechnology"), "Science & Technology");
        AddFixedHomeCategory(result, "29", Localization.GetString("CategoryNonprofitsActivism"), "Nonprofits & Activism");
        AddFixedHomeCategory(result, "30", Localization.GetString("CategoryMovies"), "Movies");
        AddFixedHomeCategory(result, "31", Localization.GetString("CategoryAnimeAnimation"), "Anime Animation");
        AddFixedHomeCategory(result, "32", Localization.GetString("CategoryActionAdventure"), "Action Adventure movies");
        AddFixedHomeCategory(result, "33", Localization.GetString("CategoryClassics"), "Classic movies");
        AddFixedHomeCategory(result, "34", Localization.GetString("CategoryComedy"), "Comedy movies");
        AddFixedHomeCategory(result, "35", Localization.GetString("CategoryDocumentary"), "Documentary movies");
        AddFixedHomeCategory(result, "36", Localization.GetString("CategoryDrama"), "Drama movies");
        AddFixedHomeCategory(result, "37", Localization.GetString("CategoryFamily"), "Family movies");
        AddFixedHomeCategory(result, "38", Localization.GetString("CategoryForeign"), "Foreign movies");
        AddFixedHomeCategory(result, "39", Localization.GetString("CategoryHorror"), "Horror movies");
        AddFixedHomeCategory(result, "40", Localization.GetString("CategorySciFiFantasy"), "Sci-Fi Fantasy movies");
        AddFixedHomeCategory(result, "41", Localization.GetString("CategoryThriller"), "Thriller movies");
        AddFixedHomeCategory(result, "42", Localization.GetString("CategoryShorts"), "YouTube Shorts");
        AddFixedHomeCategory(result, "43", Localization.GetString("CategoryShows"), "Shows");
        AddFixedHomeCategory(result, "44", Localization.GetString("CategoryTrailers"), "Trailers");

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Using fixed local category list: " + result.Count);
        return Task.FromResult(result);
    }

    private static void AddFixedHomeCategory(List<HomeCategoryItem> result, string id, string title, string query)
    {
        if (result == null || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        result.Add(new HomeCategoryItem
        {
            CategoryId = id,
            Title = title,
            SearchQuery = string.IsNullOrWhiteSpace(query) ? title : query,
            ClientName = "FIXED",
            UseWebClient = true
        });
    }

    public static Task<List<VideoCardItem>> GetHomeCategoryVideosAsync(string refreshToken, HomeCategoryItem category, int count)
    {
        if (category == null || category.IsAll)
        {
            return GetRecommendationsAsync(refreshToken, count);
        }

        if (!string.IsNullOrWhiteSpace(category.SearchQuery))
        {
            return GetAnonymousSearchVideosAsync(category.SearchQuery, count);
        }

        if (string.Equals(category.ClientName, "ANDROID", StringComparison.OrdinalIgnoreCase))
        {
            return GetAndroidHomeCategoryVideosAsync(refreshToken, category, count);
        }

        if (category.UseWebClient || string.Equals(category.ClientName, "WEB", StringComparison.OrdinalIgnoreCase))
        {
            return GetWebHomeCategoryVideosAsync(refreshToken, category, count);
        }

        if (!string.IsNullOrWhiteSpace(category.ContinuationToken))
        {
            return GetBrowseVideosWithContinuationAsync(refreshToken, category.ContinuationToken, count);
        }

        if (!string.IsNullOrWhiteSpace(category.BrowseId))
        {
            return GetBrowseVideosWithParamsAsync(refreshToken, category.BrowseId, category.Params, count);
        }

        return GetAnonymousSearchVideosAsync(category.Title, count);
    }

    public static async Task<List<VideoCardItem>> GetHistoryAsync(string refreshToken, int count, string continuation = null)
    {
        var page = await GetHistoryFeedPageAsync(refreshToken, continuation, count).ConfigureAwait(false);
        var result = new List<VideoCardItem>();

        if (page == null || page.Groups == null)
        {
            return result;
        }

        foreach (var group in page.Groups)
        {
            if (group == null || group.Videos == null)
            {
                continue;
            }

            foreach (var video in group.Videos)
            {
                if (video != null)
                {
                    result.Add(video);
                    if (count > 0 && result.Count >= count)
                    {
                        return result;
                    }
                }
            }
        }

        return result;
    }

    // Lightweight signed-in TV history read used to merge resume positions into search.
    // Unlike GetHistoryAsync it deliberately skips channel-avatar hydration: search needs only
    // VideoId + percentDurationWatched and should not wait for unrelated image requests.
    internal static async Task<List<VideoCardItem>> GetHistoryProgressItemsAsync(string refreshToken, int count)
    {
        var result = new List<VideoCardItem>();
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return result;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return result;
        }

        var json = await PostHistoryBrowseAsync(accessToken, null).ConfigureAwait(false);
        var page = ParseHistoryFeedPage(json, count);
        if (page == null || page.Groups == null)
        {
            return result;
        }

        foreach (var group in page.Groups)
        {
            if (group == null || group.Videos == null)
            {
                continue;
            }

            foreach (var video in group.Videos)
            {
                if (video == null)
                {
                    continue;
                }

                result.Add(video);
                if (count > 0 && result.Count >= count)
                {
                    return result;
                }
            }
        }

        return result;
    }

    public static async Task<HistoryFeedPage> GetHistoryFeedPageAsync(string refreshToken, string continuationToken, int count)
    {
        var empty = new HistoryFeedPage();

        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            System.Diagnostics.Debug.WriteLine("[History] RefreshToken is empty");
            return empty;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine("[History] Failed to get access token");
            return empty;
        }

        var json = await PostHistoryBrowseAsync(accessToken, continuationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return empty;
        }

        var page = ParseHistoryFeedPage(json, count);
        if (page != null && page.Groups != null)
        {
            var allVideos = new List<VideoCardItem>();
            foreach (var group in page.Groups)
            {
                if (group != null && group.Videos != null)
                    allVideos.AddRange(group.Videos);
            }
            await HydrateMissingChannelThumbnailsAsync(allVideos, accessToken).ConfigureAwait(false);
        }
        return page;
    }

    private static async Task<string> PostHistoryBrowseAsync(string accessToken, string continuationToken)
    {
        var clientVersion = "7.20250209.19.00";
        var clientJson = "\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + clientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}";

        var payload = string.IsNullOrWhiteSpace(continuationToken)
            ? "{\"context\":{" + clientJson + "},\"browseId\":\"FEhistory\"}"
            : "{\"context\":{" + clientJson + "},\"continuation\":\"" + JsonEscape(continuationToken) + "\"}";

        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "7");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/tv");
            ApplySelectedAccountHeader(request, true);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, true), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine("[History] Browse response: " + response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine("[History] Error response: " + json);
                return string.Empty;
            }

            return json;
        }
    }

    private static async Task<List<VideoCardItem>> GetWebHomeCategoryVideosAsync(string refreshToken, HomeCategoryItem category, int count)
    {
        if (category == null)
        {
            return new List<VideoCardItem>();
        }

        const string anonymousCacheToken = "anonymous-home-categories";
        var cacheKey = BuildVideoListCacheKey("webhomecategory", category.BrowseId + "|" + (category.Params ?? string.Empty), category.ContinuationToken, count);
        List<VideoCardItem> cachedVideos;
        if (TryGetCachedVideoList(anonymousCacheToken, cacheKey, out cachedVideos))
        {
            await HydrateChannelThumbnailsWithRefreshTokenAsync(cachedVideos, refreshToken).ConfigureAwait(false);
            return cachedVideos;
        }

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Loading WEB category videos anonymously: " + category.Title);
        var session = await GetAnonymousHomeWebSessionAsync().ConfigureAwait(false);
        var json = !string.IsNullOrWhiteSpace(category.ContinuationToken)
            ? await PostWebBrowseAsync(null, null, null, category.ContinuationToken, session).ConfigureAwait(false)
            : await PostWebBrowseAsync(null, category.BrowseId, category.Params, null, session).ConfigureAwait(false);

        var videos = ParseVideoCards(json, count);
        if (videos.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[HomeCategories] WEB category empty, falling back to anonymous search: " + category.Title);
            videos = await GetAnonymousSearchVideosAsync(category.Title, count).ConfigureAwait(false);
        }
        await HydrateChannelThumbnailsWithRefreshTokenAsync(videos, refreshToken).ConfigureAwait(false);
        SaveCachedVideoList(anonymousCacheToken, cacheKey, videos);
        return videos;
    }

    private static async Task<List<VideoCardItem>> GetAndroidHomeCategoryVideosAsync(string refreshToken, HomeCategoryItem category, int count)
    {
        if (category == null)
        {
            return new List<VideoCardItem>();
        }

        const string anonymousCacheToken = "anonymous-home-categories";
        var cacheKey = BuildVideoListCacheKey("androidhomecategory", category.BrowseId + "|" + (category.Params ?? string.Empty), category.ContinuationToken, count);
        List<VideoCardItem> cachedVideos;
        if (TryGetCachedVideoList(anonymousCacheToken, cacheKey, out cachedVideos))
        {
            await HydrateChannelThumbnailsWithRefreshTokenAsync(cachedVideos, refreshToken).ConfigureAwait(false);
            return cachedVideos;
        }

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Loading ANDROID category videos anonymously: " + category.Title);
        var json = !string.IsNullOrWhiteSpace(category.ContinuationToken)
            ? await PostAndroidBrowseAsync(null, null, null, category.ContinuationToken).ConfigureAwait(false)
            : await PostAndroidBrowseAsync(null, category.BrowseId, category.Params, null).ConfigureAwait(false);

        var videos = ParseVideoCards(json, count);
        if (videos.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[HomeCategories] ANDROID category empty, falling back to anonymous search: " + category.Title);
            videos = await GetAnonymousSearchVideosAsync(category.Title, count).ConfigureAwait(false);
        }
        await HydrateChannelThumbnailsWithRefreshTokenAsync(videos, refreshToken).ConfigureAwait(false);
        SaveCachedVideoList(anonymousCacheToken, cacheKey, videos);
        return videos;
    }

    private static async Task<List<VideoCardItem>> GetAnonymousSearchVideosAsync(string query, int count)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new List<VideoCardItem>();
        }

        const string anonymousCacheToken = "anonymous-home-category-search";
        var cacheKey = BuildVideoListCacheKey("search", query, null, count);
        List<VideoCardItem> cachedVideos;
        if (TryGetCachedVideoList(anonymousCacheToken, cacheKey, out cachedVideos))
        {
            return cachedVideos;
        }

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Loading anonymous search fallback: " + query);
        var context = "{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + HomeWebClientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}}";
        var payload = "{\"context\":" + context + ",\"query\":\"" + JsonEscape(query) + "\",\"params\":\"EgIQAQ==\"}";
        var url = "https://www.youtube.com/youtubei/v1/search?key=" + InnertubeApiKey + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", HomeWebClientVersion);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine("[HomeCategories] Search fallback error: " + response.StatusCode + " - " + json);
                return new List<VideoCardItem>();
            }

            var videos = ParseVideoCards(json, count);
            SaveCachedVideoList(anonymousCacheToken, cacheKey, videos);
            return videos;
        }
    }

    private static async Task<List<VideoCardItem>> GetBrowseVideosWithParamsAsync(string refreshToken, string browseId, string browseParams, int count)
    {
        if (string.IsNullOrWhiteSpace(browseId))
        {
            return new List<VideoCardItem>();
        }

        const string anonymousCacheToken = "anonymous-home-categories";
        var cacheKey = BuildVideoListCacheKey("homecategory", browseId + "|" + (browseParams ?? string.Empty), null, count);
        List<VideoCardItem> cachedVideos;
        if (TryGetCachedVideoList(anonymousCacheToken, cacheKey, out cachedVideos))
        {
            await HydrateChannelThumbnailsWithRefreshTokenAsync(cachedVideos, refreshToken).ConfigureAwait(false);
            return cachedVideos;
        }

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Loading TV category videos anonymously: " + browseId);
        var json = await PostTvBrowseAsync(null, browseId, browseParams, null).ConfigureAwait(false);
        var videos = ParseVideoCards(json, count);
        if (videos.Count == 0)
        {
            System.Diagnostics.Debug.WriteLine("[HomeCategories] TV category empty, falling back to anonymous search: " + browseId);
            videos = await GetAnonymousSearchVideosAsync(browseId, count).ConfigureAwait(false);
        }
        await HydrateChannelThumbnailsWithRefreshTokenAsync(videos, refreshToken).ConfigureAwait(false);
        SaveCachedVideoList(anonymousCacheToken, cacheKey, videos);
        return videos;
    }

    private static async Task<string> PostAndroidBrowseAsync(string accessToken, string browseId, string browseParams, string continuationToken)
    {
        var clientVersion = "19.09.37";
        var clientJson = "\"client\":{\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"clientName\":\"ANDROID\",\"clientVersion\":\"" + clientVersion + "\",\"platform\":\"MOBILE\",\"osName\":\"Android\",\"osVersion\":\"11\",\"androidSdkVersion\":30,\"deviceMake\":\"Google\",\"deviceModel\":\"Pixel 5\"}";
        string payload;
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            payload = "{\"context\":{" + clientJson + "},\"continuation\":\"" + JsonEscape(continuationToken) + "\"}";
        }
        else
        {
            payload = "{\"context\":{" + clientJson + "},\"browseId\":\"" + JsonEscape(browseId) + "\"";
            if (!string.IsNullOrWhiteSpace(browseParams))
            {
                payload += ",\"params\":\"" + JsonEscape(browseParams) + "\"";
            }
            payload += "}";
        }

        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            }
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/" + clientVersion + " (Linux; U; Android 11) gzip");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "3");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            var authenticated = !string.IsNullOrWhiteSpace(accessToken);
            ApplySelectedAccountHeader(request, authenticated);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, authenticated), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine("[PostAndroidBrowse] Error: " + response.StatusCode + " - " + json);
                return string.Empty;
            }

            return json;
        }
    }

    private static async Task<string> PostTvBrowseAsync(string accessToken, string browseId, string browseParams, string continuationToken)
    {
        var response = await GetTvBrowseResponseAsync(
            accessToken, browseId, browseParams, continuationToken).ConfigureAwait(false);
        return response != null ? response.Text : string.Empty;
    }

    private static async Task<InnertubeRequestCoordinator.JsonResponse> GetTvBrowseResponseAsync(
        string accessToken,
        string browseId,
        string browseParams,
        string continuationToken)
    {
        var authenticated = !string.IsNullOrWhiteSpace(accessToken);
        var key = BuildBrowseCoordinatorKey(
            "tv",
            authenticated,
            browseId,
            browseParams,
            continuationToken);
        var maxAge = string.IsNullOrWhiteSpace(continuationToken)
            ? TimeSpan.FromMinutes(2)
            : TimeSpan.FromSeconds(30);
        try
        {
            var response = await InnertubeRequestCoordinator.GetJsonAsync(
                key,
                delegate
                {
                    return PostTvBrowseCoreAsync(
                        accessToken,
                        browseId,
                        browseParams,
                        continuationToken);
                },
                maxAge).ConfigureAwait(false);
            return response;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[PostTvBrowse] Coordinated request failed: " + ex.Message);
            return null;
        }
    }

    private static async Task<string> PostTvBrowseCoreAsync(string accessToken, string browseId, string browseParams, string continuationToken)
    {
        var clientVersion = "7.20250209.19.00";
        var clientJson = "\"client\":{\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + clientVersion + "\",\"platform\":\"TV\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}";
        string payload;
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            payload = "{\"context\":{" + clientJson + "},\"continuation\":\"" + JsonEscape(continuationToken) + "\"}";
        }
        else
        {
            payload = "{\"context\":{" + clientJson + "},\"browseId\":\"" + JsonEscape(browseId) + "\"";
            if (!string.IsNullOrWhiteSpace(browseParams))
            {
                payload += ",\"params\":\"" + JsonEscape(browseParams) + "\"";
            }
            payload += "}";
        }

        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            }
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "85");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            var authenticated = !string.IsNullOrWhiteSpace(accessToken);
            ApplySelectedAccountHeader(request, authenticated);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, authenticated), Encoding.UTF8, "application/json");

            using (var response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[PostTvBrowse] Error: " + response.StatusCode + " - " + json);
                    return string.Empty;
                }

                return json;
            }
        }
    }

    private static string BuildBrowseCoordinatorKey(
        string client,
        bool authenticated,
        string browseId,
        string browseParams,
        string continuationToken)
    {
        browseId = browseId ?? string.Empty;
        browseParams = browseParams ?? string.Empty;
        continuationToken = continuationToken ?? string.Empty;
        var brandId = authenticated ? SelectedYouTubeAccountBrandId : string.Empty;
        return "browse:" + client + ":"
            + (authenticated ? "auth:" : "guest:")
            + Hl + ":" + Gl + ":"
            + brandId.Length + ":" + brandId + ":"
            + browseId.Length + ":" + browseId + ":"
            + browseParams.Length + ":" + browseParams + ":"
            + continuationToken.Length + ":" + continuationToken;
    }

    private static Task<string> PostWebBrowseAsync(string accessToken, string browseId, string browseParams, string continuationToken)
    {
        return PostWebBrowseAsync(accessToken, browseId, browseParams, continuationToken, null);
    }

    private static async Task<string> PostWebBrowseAsync(string accessToken, string browseId, string browseParams, string continuationToken, NotificationsWebSession webSession)
    {
        if (webSession == null)
        {
            webSession = BuildFallbackNotificationsWebSession();
        }

        var contextJson = BuildNotificationsWebContextJson(webSession);
        string payload;
        if (!string.IsNullOrWhiteSpace(continuationToken))
        {
            payload = "{\"context\":" + contextJson + ",\"continuation\":\"" + JsonEscape(continuationToken) + "\"}";
        }
        else
        {
            payload = "{\"context\":" + contextJson + ",\"browseId\":\"" + JsonEscape(browseId) + "\"";
            if (!string.IsNullOrWhiteSpace(browseParams))
            {
                payload += ",\"params\":\"" + JsonEscape(browseParams) + "\"";
            }
            payload += "}";
        }

        var apiKey = FirstNonEmpty(webSession.ApiKey, InnertubeApiKey);
        var clientVersion = FirstNonEmpty(webSession.ClientVersion, HomeWebClientVersion);
        var visitorData = FirstNonEmpty(webSession.VisitorData, GetFallbackVisitorData());
        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + apiKey + "&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            }
            var authenticated = !string.IsNullOrWhiteSpace(accessToken);
            ApplySelectedAccountHeader(request, authenticated);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, authenticated), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                System.Diagnostics.Debug.WriteLine("[PostWebBrowse] Error: " + response.StatusCode + " - " + json);
                return string.Empty;
            }

            return json;
        }
    }

    private static void AddFallbackHomeCategoryChips(List<HomeCategoryItem> result)
    {
        if (result == null)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in result)
        {
            if (existing != null)
            {
                seen.Add(existing.Key);
            }
        }

        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryVideoGames"), "video games");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryMusic"), "music videos");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryNews"), "news");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryLive"), "live streams");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategorySports"), "sports");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryLearning"), "learning videos");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryMovies"), "movies");
        AddFallbackHomeCategoryChip(result, seen, Localization.GetString("CategoryPodcasts"), "podcasts");
    }

    private static void AddFallbackHomeCategoryChip(List<HomeCategoryItem> result, HashSet<string> seen, string title, string query)
    {
        var item = new HomeCategoryItem
        {
            Title = title,
            SearchQuery = query,
            ClientName = "SEARCH",
            UseWebClient = true
        };

        if (seen.Add(item.Key))
        {
            result.Add(item);
        }
    }

    private static List<HomeCategoryItem> ParseHomeCategoryChips(string json, string clientName)
    {
        var result = new List<HomeCategoryItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var root = JsonValue.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // YouTube.js reads HomeFeed.filter_chips from FeedFilterChipBar. Parse that
            // exact renderer first instead of relying only on a full JSON scan.
            AddHomeCategoryChipsFromFeedFilterBars(root, result, seen, clientName);

            foreach (var obj in EnumerateObjects(root, 120000))
            {
                IJsonValue rendererValue;
                HomeCategoryItem chip = null;

                if (obj.TryGetValue("chipCloudChipRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    chip = ParseHomeCategoryChipObject(rendererValue.GetObject(), clientName);
                }
                else if (obj.TryGetValue("chipCloudChipViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    chip = ParseHomeCategoryChipObject(rendererValue.GetObject(), clientName);
                }
                else if (obj.TryGetValue("feedFilterChipBarRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    continue;
                }

                if (chip == null || string.IsNullOrWhiteSpace(chip.Title) || chip.IsAll)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(chip.ContinuationToken) && string.IsNullOrWhiteSpace(chip.BrowseId))
                {
                    continue;
                }

                if (seen.Add(chip.Key))
                {
                    result.Add(chip);
                    if (result.Count >= 16)
                    {
                        break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[HomeCategories] Parse error: " + ex.Message);
        }

        return result;
    }

    private static void AddHomeCategoryChipsFromFeedFilterBars(IJsonValue root, List<HomeCategoryItem> result, HashSet<string> seen, string clientName)
    {
        if (root == null || result == null || seen == null)
        {
            return;
        }

        try
        {
            foreach (var obj in EnumerateObjects(root, 120000))
            {
                var bar = GetObjectFromJsonObject(obj, "feedFilterChipBarRenderer");
                if (bar == null)
                {
                    continue;
                }

                IJsonValue contentsValue;
                if (!bar.TryGetValue("contents", out contentsValue) || contentsValue == null || contentsValue.ValueType != JsonValueType.Array)
                {
                    continue;
                }

                var contents = contentsValue.GetArray();
                for (int i = 0; i < contents.Count; i++)
                {
                    var wrapperToken = contents[i];
                    if (wrapperToken == null || wrapperToken.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var wrapperValue = wrapperToken.GetObject();
                    IJsonValue rendererValue;
                    HomeCategoryItem chip = null;
                    if (wrapperValue.TryGetValue("chipCloudChipRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                    {
                        chip = ParseHomeCategoryChipObject(rendererValue.GetObject(), clientName);
                    }
                    else if (wrapperValue.TryGetValue("chipCloudChipViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                    {
                        chip = ParseHomeCategoryChipObject(rendererValue.GetObject(), clientName);
                    }

                    if (chip == null || string.IsNullOrWhiteSpace(chip.Title) || chip.IsAll)
                    {
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(chip.ContinuationToken) && string.IsNullOrWhiteSpace(chip.BrowseId))
                    {
                        continue;
                    }

                    if (seen.Add(chip.Key))
                    {
                        result.Add(chip);
                        if (result.Count >= 16)
                        {
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[HomeCategories] FeedFilterChipBar parse error: " + ex.Message);
        }
    }

    private static HomeCategoryItem ParseHomeCategoryChipObject(JsonObject chip, string clientName)
    {
        if (chip == null)
        {
            return null;
        }

        var title = FirstNonEmpty(
            ExtractTextFromField(chip, "text", string.Empty),
            ExtractTextFromField(chip, "title", string.Empty),
            ExtractTextFromField(chip, "label", string.Empty),
            ExtractChipViewModelText(chip));

        title = NormalizeHomeCategoryTitle(title);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var item = new HomeCategoryItem
        {
            Title = title,
            UseWebClient = string.Equals(clientName, "WEB", StringComparison.OrdinalIgnoreCase),
            ClientName = clientName,
            IsAll = string.Equals(title, "All", StringComparison.OrdinalIgnoreCase) || GetJsonBool(chip, "isSelected", false)
        };

        JsonObject endpoint = FirstNonNull(
            GetObjectFromJsonObject(chip, "navigationEndpoint"),
            GetObjectFromJsonObject(chip, "endpoint"),
            GetObjectFromJsonObject(chip, "onTap"),
            GetObjectFromJsonObject(chip, "innertubeCommand"),
            GetObjectFromJsonObject(chip, "command"),
            GetObjectFromJsonObject(chip, "clickCommand"),
            GetObjectFromJsonObject(chip, "onClickCommand"));

        if (endpoint == null)
        {
            foreach (var obj in EnumerateObjects(chip, 80))
            {
                endpoint = FirstNonNull(
                    GetObjectFromJsonObject(obj, "navigationEndpoint"),
                    GetObjectFromJsonObject(obj, "endpoint"),
                    GetObjectFromJsonObject(obj, "onTap"),
                    GetObjectFromJsonObject(obj, "innertubeCommand"),
                    GetObjectFromJsonObject(obj, "command"),
                    GetObjectFromJsonObject(obj, "clickCommand"),
                    GetObjectFromJsonObject(obj, "onClickCommand"));
                if (endpoint != null)
                {
                    break;
                }
            }
        }

        if (endpoint != null)
        {
            item.ContinuationToken = ExtractContinuationTokenFromObject(endpoint);
            string browseId;
            string browseParams;
            if (TryExtractBrowseEndpoint(endpoint, out browseId, out browseParams))
            {
                item.BrowseId = browseId;
                item.Params = browseParams;
            }
        }

        return item;
    }

    private static string ExtractChipViewModelText(JsonObject chip)
    {
        if (chip == null)
        {
            return string.Empty;
        }

        var textObj = GetObjectFromJsonObject(chip, "text");
        if (textObj != null)
        {
            var value = FirstNonEmpty(
                GetJsonString(textObj, "content"),
                GetJsonString(textObj, "text"),
                GetJsonString(textObj, "label"),
                GetJsonString(textObj, "simpleText"));
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        var direct = FirstNonEmpty(
            GetJsonString(chip, "text"),
            GetJsonString(chip, "title"),
            GetJsonString(chip, "label"),
            GetJsonString(chip, "content"),
            GetJsonString(chip, "accessibilityText"));
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var obj in EnumerateObjects(chip, 120))
        {
            var value = FirstNonEmpty(
                GetJsonString(obj, "content"),
                GetJsonString(obj, "simpleText"));
            if (!string.IsNullOrWhiteSpace(value) && value.Length <= 40)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string NormalizeHomeCategoryTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var value = title.Replace("\r", " ").Replace("\n", " ").Trim();
        while (value.Contains("  "))
        {
            value = value.Replace("  ", " ");
        }

        var lower = value.ToLowerInvariant();
        if (lower == "все" || lower == "all")
        {
            return "All";
        }
        if (lower == "видеоигры" || lower == "video games")
        {
            return "Video games";
        }
        if (lower == "музыка" || lower == "music")
        {
            return "Music";
        }
        if (lower == "новости" || lower == "news")
        {
            return "News";
        }
        if (lower == "спорт" || lower == "sports")
        {
            return "Sports";
        }
        if (lower == "фильмы" || lower == "movies")
        {
            return "Movies";
        }
        if (lower == "обучение" || lower == "learning")
        {
            return "Learning";
        }
        if (lower == "прямые трансляции" || lower == "live")
        {
            return "Live";
        }

        return value;
    }

    private static bool TryExtractBrowseEndpoint(JsonObject root, out string browseId, out string browseParams)
    {
        browseId = string.Empty;
        browseParams = string.Empty;
        if (root == null)
        {
            return false;
        }

        foreach (var obj in EnumerateObjects(root, 120))
        {
            var browse = GetObjectFromJsonObject(obj, "browseEndpoint");
            if (browse == null)
            {
                continue;
            }

            browseId = GetJsonString(browse, "browseId");
            browseParams = GetJsonString(browse, "params");
            if (!string.IsNullOrWhiteSpace(browseId))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject FirstNonNull(params JsonObject[] items)
    {
        if (items == null)
        {
            return null;
        }

        for (var i = 0; i < items.Length; i++)
        {
            if (items[i] != null)
            {
                return items[i];
            }
        }

        return null;
    }

    private static async Task<List<VideoCardItem>> GetBrowseVideosAsync(string refreshToken, string browseId, int count)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] RefreshToken is empty for browseId: {browseId}");
            return new List<VideoCardItem>();
        }

        List<VideoCardItem> cachedVideos;
        var cacheKey = BuildVideoListCacheKey("browse", browseId, null, count);
        if (TryGetCachedVideoList(refreshToken, cacheKey, out cachedVideos))
        {
            if (HasMissingChannelThumbnails(cachedVideos))
            {
                var cachedAccessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
                await HydrateMissingChannelThumbnailsAsync(cachedVideos, cachedAccessToken).ConfigureAwait(false);
            }
            return cachedVideos;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] Failed to get access token for browseId: {browseId}");
            return new List<VideoCardItem>();
        }

        System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] Fetching browseId: {browseId}");

        var payload = "{\"context\":{\"client\":{\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20250209.19.00\",\"platform\":\"TV\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}},\"browseId\":\"" + browseId + "\"}";
        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            ApplySelectedAccountHeader(request, true);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, true), Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] Error for {browseId}: {response.StatusCode} - {errorContent}");
                return new List<VideoCardItem>();
            }
            
            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] Got response for {browseId}, length: {json.Length}");
            
            var videos = ParseVideoCards(json, count);
            await HydrateMissingChannelThumbnailsAsync(videos, accessToken).ConfigureAwait(false);
            SaveCachedVideoList(refreshToken, cacheKey, videos);
            System.Diagnostics.Debug.WriteLine($"[GetBrowseVideos] Total videos parsed for {browseId}: {videos.Count}");
            return videos;
        }
    }

    private static async Task<List<VideoCardItem>> GetBrowseVideosWithContinuationAsync(string refreshToken, string continuationToken, int count)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            return new List<VideoCardItem>();
        }

        const string anonymousCacheToken = "anonymous-home-categories";
        List<VideoCardItem> cachedVideos;
        var cacheKey = BuildVideoListCacheKey("continuation", string.Empty, continuationToken, count);
        if (TryGetCachedVideoList(anonymousCacheToken, cacheKey, out cachedVideos))
        {
            return cachedVideos;
        }

        System.Diagnostics.Debug.WriteLine("[HomeCategories] Loading TV continuation anonymously");
        var json = await PostTvBrowseAsync(null, null, null, continuationToken).ConfigureAwait(false);
        var videos = ParseVideoCards(json, count);
        if (!string.IsNullOrWhiteSpace(refreshToken) && HasMissingChannelThumbnails(videos))
        {
            var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
            await HydrateMissingChannelThumbnailsAsync(videos, accessToken).ConfigureAwait(false);
        }
        else
        {
            await HydrateMissingChannelThumbnailsAsync(videos, string.Empty).ConfigureAwait(false);
        }
        SaveCachedVideoList(anonymousCacheToken, cacheKey, videos);
        return videos;
    }

    public static async Task<List<NotificationItem>> GetNotificationsAsync(string refreshToken, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new List<NotificationItem>();
        }

        if (maxResults <= 0)
        {
            maxResults = 50;
        }

        // Notifications page intentionally uses only the subscriptions feed now.
        // Do not call /notification/get_notification_menu here: TV OAuth tokens can
        // return 400 INVALID_ARGUMENT, and the requested behavior is to show uploads
        // from subscriptions only.
        System.Diagnostics.Debug.WriteLine("[Notifications] Loading from subscriptions feed only");
        return await GetNotificationUploadsFallbackAsync(refreshToken, maxResults).ConfigureAwait(false);
    }

    private sealed class NotificationsClientAttempt
    {
        public string ClientName { get; set; }
        public string HeaderClientName { get; set; }
        public string ClientVersion { get; set; }
    }

    private static string BuildNotificationsTvContextJson(string clientName, string clientVersion)
    {
        clientName = FirstNonEmpty(clientName, "TVHTML5");
        clientVersion = FirstNonEmpty(clientVersion, "7.20250209.19.00");

        return "{"
            + "\"client\":{"
            + "\"hl\":\"" + Hl + "\","
            + "\"gl\":\"" + Gl + "\","
            + "\"clientName\":\"" + JsonEscape(clientName) + "\","
            + "\"clientVersion\":\"" + JsonEscape(clientVersion) + "\","
            + "\"platform\":\"TV\","
            + "\"clientFormFactor\":\"UNKNOWN_FORM_FACTOR\","
            + "\"deviceMake\":\"Samsung\","
            + "\"deviceModel\":\"SmartTV\","
            + "\"osName\":\"Tizen\","
            + "\"osVersion\":\"6.0\","
            + "\"userAgent\":\"" + JsonEscape(UserAgent) + "\""
            + "},"
            + "\"user\":{\"enableSafetyMode\":false},"
            + "\"request\":{\"useSsl\":true}"
            + "}";
    }

    private static async Task<List<NotificationItem>> GetNotificationUploadsFallbackAsync(string refreshToken, int maxResults)
    {
        var result = new List<NotificationItem>();

        try
        {
            var videos = await GetBrowseVideosAsync(refreshToken, "FEsubscriptions", maxResults).ConfigureAwait(false);
            if (videos == null || videos.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[Notifications] Fallback subscriptions feed is empty");
                return result;
            }

            Dictionary<string, string> channelAvatars = null;
            try
            {
                var channels = await GetSubscribedChannelsAsync(refreshToken).ConfigureAwait(false);
                if (channels != null && channels.Count > 0)
                {
                    channelAvatars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var channel in channels)
                    {
                        if (channel == null || string.IsNullOrWhiteSpace(channel.ChannelName) || string.IsNullOrWhiteSpace(channel.ThumbnailUrl))
                        {
                            continue;
                        }

                        if (!channelAvatars.ContainsKey(channel.ChannelName))
                        {
                            channelAvatars.Add(channel.ChannelName, channel.ThumbnailUrl);
                        }
                    }
                }
            }
            catch
            {
                channelAvatars = null;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var video in videos)
            {
                if (video == null || string.IsNullOrWhiteSpace(video.VideoId))
                {
                    continue;
                }

                if (seen.Contains(video.VideoId))
                {
                    continue;
                }

                seen.Add(video.VideoId);

                var channelTitle = FirstNonEmpty(video.ChannelTitle, "YouTube");
                var avatarUrl = string.Empty;
                if (channelAvatars != null && channelAvatars.ContainsKey(channelTitle))
                {
                    avatarUrl = channelAvatars[channelTitle];
                }

                result.Add(new NotificationItem
                {
                    Title = channelTitle,
                    Message = string.IsNullOrWhiteSpace(video.Title)
                        ? Localization.GetString("UploadedVideo")
                        : Localization.Format("UploadedVideoTitleFormat", video.Title),
                    TimeText = Localization.GetString("FromSubscriptions"),
                    AvatarUrl = avatarUrl,
                    ThumbnailUrl = video.ThumbnailUrl,
                    VideoId = video.VideoId,
                    IsRead = true,
                    VideoTitle = video.Title,
                    Author = channelTitle
                });

                if (result.Count >= maxResults)
                {
                    break;
                }
            }

            System.Diagnostics.Debug.WriteLine("[Notifications] Fallback subscriptions items: " + result.Count);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Notifications] Fallback error: " + ex.Message);
        }

        return result;
    }

    private sealed class NotificationsWebSession
    {
        public string ApiKey { get; set; }
        public string VisitorData { get; set; }
        public string ClientVersion { get; set; }
        public string Hl { get; set; }
        public string Gl { get; set; }
        public string RemoteHost { get; set; }
        public string OsName { get; set; }
        public string OsVersion { get; set; }
        public string BrowserName { get; set; }
        public string BrowserVersion { get; set; }
        public string TimeZone { get; set; }
        public string AppInstallData { get; set; }
        public string DeviceExperimentId { get; set; }
        public string RolloutToken { get; set; }
    }

    private static async Task<NotificationsWebSession> GetAnonymousHomeWebSessionAsync()
    {
        var session = await GetNotificationsWebSessionAsync().ConfigureAwait(false);
        if (session == null)
        {
            session = BuildFallbackNotificationsWebSession();
        }

        // Keep fresh visitor/client data but force English chips.
        session.Hl = "en";
        session.Gl = "US";
        return session;
    }

    private static async Task<NotificationsWebSession> GetNotificationsWebSessionAsync()
    {
        try
        {
            var visitorId = GenerateRandomVisitorId();
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/sw.js_data"))
            {
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/sw.js");
                request.Headers.TryAddWithoutValidation("Cookie", "PREF=tz=UTC;VISITOR_INFO1_LIVE=" + visitorId + ";");

                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(text))
                {
                    System.Diagnostics.Debug.WriteLine("[Notifications] sw.js_data failed: " + response.StatusCode);
                    return BuildFallbackNotificationsWebSession();
                }

                if (text.StartsWith(")]}'", StringComparison.Ordinal))
                {
                    text = text.Substring(4).TrimStart();
                }

                var root = JsonValue.Parse(text).GetArray();
                var first = GetJsonArrayAt(root, 0);
                var ytcfg = GetJsonArrayAt(first, 2);
                var ytcfgFirst = GetJsonArrayAt(ytcfg, 0);
                var deviceInfo = GetJsonArrayAt(ytcfgFirst, 0);
                if (deviceInfo == null)
                {
                    return BuildFallbackNotificationsWebSession();
                }

                var session = new NotificationsWebSession
                {
                    ApiKey = FirstNonEmpty(GetJsonArrayString(ytcfg, 1), InnertubeApiKey),
                    Hl = FirstNonEmpty(GetJsonArrayString(deviceInfo, 0), "en"),
                    Gl = FirstNonEmpty(GetJsonArrayString(deviceInfo, 1), "US"),
                    RemoteHost = GetJsonArrayString(deviceInfo, 3),
                    VisitorData = FirstNonEmpty(GetJsonArrayString(deviceInfo, 13), GetFallbackVisitorData()),
                    ClientVersion = FirstNonEmpty(GetJsonArrayString(deviceInfo, 16), ShortsWebClientVersion),
                    OsName = FirstNonEmpty(GetJsonArrayString(deviceInfo, 17), "Windows"),
                    OsVersion = FirstNonEmpty(GetJsonArrayString(deviceInfo, 18), "10.0"),
                    TimeZone = FirstNonEmpty(GetJsonArrayString(deviceInfo, 79), "UTC"),
                    BrowserName = FirstNonEmpty(GetJsonArrayString(deviceInfo, 86), "Chrome"),
                    BrowserVersion = FirstNonEmpty(GetJsonArrayString(deviceInfo, 87), "125.0.0.0"),
                    DeviceExperimentId = GetJsonArrayString(deviceInfo, 103),
                    RolloutToken = GetJsonArrayString(deviceInfo, 107)
                };

                var configInfo = GetJsonArrayAt(deviceInfo, 61);
                if (configInfo != null && configInfo.Count > 0)
                {
                    session.AppInstallData = GetJsonArrayString(configInfo, (uint)(configInfo.Count - 1));
                }

                System.Diagnostics.Debug.WriteLine("[Notifications] WEB session loaded: clientVersion=" + session.ClientVersion);
                return session;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Notifications] sw.js_data parse error: " + ex.Message);
            return BuildFallbackNotificationsWebSession();
        }
    }

    private static NotificationsWebSession BuildFallbackNotificationsWebSession()
    {
        return new NotificationsWebSession
        {
            ApiKey = InnertubeApiKey,
            Hl = "en",
            Gl = "US",
            RemoteHost = string.Empty,
            VisitorData = GetFallbackVisitorData(),
            ClientVersion = ShortsWebClientVersion,
            OsName = "Windows",
            OsVersion = "10.0",
            TimeZone = "UTC",
            BrowserName = "Chrome",
            BrowserVersion = "125.0.0.0",
            AppInstallData = string.Empty,
            DeviceExperimentId = string.Empty,
            RolloutToken = string.Empty
        };
    }

    private static string BuildNotificationsWebContextJson(NotificationsWebSession session)
    {
        if (session == null)
        {
            session = BuildFallbackNotificationsWebSession();
        }

        var client = "{"
            + "\"hl\":\"" + JsonEscape(FirstNonEmpty(session.Hl, "en")) + "\","
            + "\"gl\":\"" + JsonEscape(FirstNonEmpty(session.Gl, "US")) + "\","
            + "\"remoteHost\":\"" + JsonEscape(session.RemoteHost) + "\","
            + "\"screenDensityFloat\":1,"
            + "\"screenHeightPoints\":1440,"
            + "\"screenPixelDensity\":1,"
            + "\"screenWidthPoints\":2560,"
            + "\"visitorData\":\"" + JsonEscape(FirstNonEmpty(session.VisitorData, GetFallbackVisitorData())) + "\","
            + "\"clientName\":\"WEB\","
            + "\"clientVersion\":\"" + JsonEscape(FirstNonEmpty(session.ClientVersion, ShortsWebClientVersion)) + "\","
            + "\"osName\":\"" + JsonEscape(FirstNonEmpty(session.OsName, "Windows")) + "\","
            + "\"osVersion\":\"" + JsonEscape(FirstNonEmpty(session.OsVersion, "10.0")) + "\","
            + "\"userAgent\":\"" + JsonEscape(WebUserAgent) + "\","
            + "\"platform\":\"DESKTOP\","
            + "\"clientFormFactor\":\"UNKNOWN_FORM_FACTOR\","
            + "\"userInterfaceTheme\":\"USER_INTERFACE_THEME_DARK\","
            + "\"timeZone\":\"" + JsonEscape(FirstNonEmpty(session.TimeZone, "UTC")) + "\","
            + "\"originalUrl\":\"https://www.youtube.com/\","
            + "\"deviceMake\":\"\","
            + "\"deviceModel\":\"\","
            + "\"browserName\":\"" + JsonEscape(FirstNonEmpty(session.BrowserName, "Chrome")) + "\","
            + "\"browserVersion\":\"" + JsonEscape(FirstNonEmpty(session.BrowserVersion, "125.0.0.0")) + "\","
            + "\"utcOffsetMinutes\":0,"
            + "\"memoryTotalKbytes\":\"8000000\"";

        if (!string.IsNullOrWhiteSpace(session.RolloutToken))
        {
            client += ",\"rolloutToken\":\"" + JsonEscape(session.RolloutToken) + "\"";
        }

        if (!string.IsNullOrWhiteSpace(session.DeviceExperimentId))
        {
            client += ",\"deviceExperimentId\":\"" + JsonEscape(session.DeviceExperimentId) + "\"";
        }

        client += ",\"mainAppWebInfo\":{"
            + "\"graftUrl\":\"https://www.youtube.com/\","
            + "\"pwaInstallabilityStatus\":\"PWA_INSTALLABILITY_STATUS_UNKNOWN\","
            + "\"webDisplayMode\":\"WEB_DISPLAY_MODE_BROWSER\","
            + "\"isWebNativeShareAvailable\":true"
            + "}";

        if (!string.IsNullOrWhiteSpace(session.AppInstallData))
        {
            client += ",\"configInfo\":{\"appInstallData\":\"" + JsonEscape(session.AppInstallData) + "\"}";
        }

        client += "}";

        return "{"
            + "\"client\":" + client + ","
            + "\"user\":{\"enableSafetyMode\":false,\"lockedSafetyMode\":false},"
            + "\"request\":{\"useSsl\":true,\"internalExperimentFlags\":[]}"
            + "}";
    }

    private static string GetFallbackVisitorData()
    {
        try
        {
            return Uri.UnescapeDataString(ShortsVisitorId);
        }
        catch
        {
            return ShortsVisitorId;
        }
    }

    private static string GenerateRandomVisitorId()
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
        var random = new Random();
        var chars = new char[11];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[random.Next(alphabet.Length)];
        }

        return new string(chars);
    }

    private static JsonArray GetJsonArrayAt(JsonArray array, uint index)
    {
        if (array == null || array.Count <= index)
        {
            return null;
        }

        try
        {
            return array.GetArrayAt(index);
        }
        catch
        {
            return null;
        }
    }

    private static string GetJsonArrayString(JsonArray array, uint index)
    {
        if (array == null || array.Count <= index)
        {
            return string.Empty;
        }

        try
        {
            return array.GetStringAt(index);
        }
        catch
        {
        }

        try
        {
            return array.GetNumberAt(index).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
        }

        return string.Empty;
    }

    private static List<NotificationItem> ParseNotifications(string json, int maxCount)
    {
        var result = new List<NotificationItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            if (!ContainsAnyOrdinal(json, NotificationRendererMarkers))
            {
                return result;
            }

            var root = JsonValue.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxObjects = maxCount > 0 ? Math.Min(12000, Math.Max(1200, maxCount * 220)) : 12000;

            foreach (var obj in EnumerateObjects(root, maxObjects))
            {
                if (maxCount > 0 && result.Count >= maxCount)
                {
                    break;
                }

                NotificationItem item = null;

                if (obj.ContainsKey("notificationRenderer"))
                {
                    var value = obj.GetNamedValue("notificationRenderer");
                    if (value.ValueType == JsonValueType.Object)
                    {
                        item = ParseNotificationRenderer(value.GetObject());
                    }
                }
                else if (obj.ContainsKey("notificationViewModel"))
                {
                    var value = obj.GetNamedValue("notificationViewModel");
                    if (value.ValueType == JsonValueType.Object)
                    {
                        item = ParseNotificationViewModel(value.GetObject());
                    }
                }

                if (item == null || (string.IsNullOrWhiteSpace(item.Title) && string.IsNullOrWhiteSpace(item.Message)))
                {
                    continue;
                }

                var key = FirstNonEmpty(item.VideoId, string.Empty) + "|" + item.Title + "|" + item.Message + "|" + item.TimeText;
                if (seen.Contains(key))
                {
                    continue;
                }

                seen.Add(key);
                result.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Notifications] Parse error: " + ex.Message);
        }

        return result;
    }

    private static NotificationItem ParseNotificationRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        string title;
        string message;
        ExtractNotificationTitleAndMessage(renderer, out title, out message);

        var fullText = FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "shortMessage"),
            ExtractTextFromNamedValue(renderer, "message"),
            ExtractTextFromNamedValue(renderer, "title"),
            ExtractTextFromAnyValue(renderer));

        if (string.IsNullOrWhiteSpace(title))
        {
            title = ExtractFirstUsefulLine(fullText);
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            message = RemoveLeadingText(fullText, title);
        }

        var videoId = ExtractVideoIdFromAnyValue(renderer, 800);
        var avatarUrl = FirstNonEmpty(
            ExtractBestThumbnailUrl(renderer, "thumbnail"),
            ExtractBestThumbnailUrl(renderer, "channelThumbnail"),
            ExtractBestThumbnailUrl(renderer, "authorThumbnail"),
            ExtractThumbnailFromFirstObjectWithKey(renderer, "channelThumbnail"),
            ExtractThumbnailFromFirstObjectWithKey(renderer, "avatar"));

        var videoThumbnail = FirstNonEmpty(
            ExtractThumbnailFromFirstObjectWithKey(renderer, "videoThumbnail"),
            ExtractBestThumbnailUrl(renderer, "videoThumbnail"),
            ExtractBestThumbnailUrl(renderer, "image"),
            IsValidYouTubeVideoId(videoId) ? BuildMqThumbnailUrl(videoId) : string.Empty);

        var parsedVideoTitle = BuildNotificationVideoTitle(message, title, fullText);
        var parsedAuthor = BuildNotificationAuthor(title, fullText);

        return new NotificationItem
        {
            Title = FirstNonEmpty(title, "YouTube"),
            Message = FirstNonEmpty(message, fullText),
            TimeText = FirstNonEmpty(
                ExtractTextFromNamedValue(renderer, "sentTimeText"),
                ExtractTextFromNamedValue(renderer, "timestampText"),
                ExtractTextFromNamedValue(renderer, "timeText"),
                ExtractTextFromNamedValue(renderer, "publishedTimeText")),
            AvatarUrl = avatarUrl,
            ThumbnailUrl = videoThumbnail,
            VideoId = videoId,
            IsRead = GetJsonBool(renderer, "read", GetJsonBool(renderer, "isRead", false)),
            VideoTitle = parsedVideoTitle,
            Author = parsedAuthor
        };
    }

    private static NotificationItem ParseNotificationViewModel(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var fullText = FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "notificationText"),
            ExtractTextFromNamedValue(renderer, "shortMessage"),
            ExtractTextFromNamedValue(renderer, "message"),
            ExtractTextFromNamedValue(renderer, "title"),
            ExtractTextFromAnyValue(renderer));

        var title = ExtractFirstUsefulLine(fullText);
        var message = RemoveLeadingText(fullText, title);
        var videoId = ExtractVideoIdFromAnyValue(renderer, 800);
        var parsedVideoTitle = BuildNotificationVideoTitle(message, title, fullText);
        var parsedAuthor = BuildNotificationAuthor(title, fullText);

        return new NotificationItem
        {
            Title = FirstNonEmpty(title, "YouTube"),
            Message = FirstNonEmpty(message, fullText),
            TimeText = FirstNonEmpty(
                ExtractTextFromNamedValue(renderer, "sentTimeText"),
                ExtractTextFromNamedValue(renderer, "timestampText"),
                ExtractTextFromNamedValue(renderer, "timeText"),
                ExtractTextFromNamedValue(renderer, "publishedTimeText")),
            AvatarUrl = FirstNonEmpty(
                ExtractThumbnailFromFirstObjectWithKey(renderer, "avatar"),
                ExtractThumbnailFromFirstObjectWithKey(renderer, "channelThumbnail"),
                ExtractBestThumbnailUrl(renderer, "thumbnail")),
            ThumbnailUrl = FirstNonEmpty(
                ExtractThumbnailFromFirstObjectWithKey(renderer, "videoThumbnail"),
                ExtractThumbnailFromFirstObjectWithKey(renderer, "image"),
                IsValidYouTubeVideoId(videoId) ? BuildMqThumbnailUrl(videoId) : string.Empty),
            VideoId = videoId,
            IsRead = GetJsonBool(renderer, "read", GetJsonBool(renderer, "isRead", false)),
            VideoTitle = parsedVideoTitle,
            Author = parsedAuthor
        };
    }

    private static readonly string[] NotificationActionMarkers = new[]
    {
        " uploaded a video:",
        " uploaded:",
        " uploaded ",
        " premiered:",
        " is live:",
        " started streaming:",
        " posted:",
        " published:",
        " released:",
        " shared:",
        " added:"
    };

    private static string BuildNotificationVideoTitle(string message, string title, string fullText)
    {
        var fromMessage = CleanNotificationVideoTitle(message);
        if (!string.IsNullOrWhiteSpace(fromMessage) && !LooksLikeNotificationActionOnly(fromMessage))
        {
            return fromMessage;
        }

        var afterTitleMarker = ExtractAfterNotificationAction(title);
        if (!string.IsNullOrWhiteSpace(afterTitleMarker))
        {
            return afterTitleMarker;
        }

        var afterFullMarker = ExtractAfterNotificationAction(fullText);
        if (!string.IsNullOrWhiteSpace(afterFullMarker))
        {
            return afterFullMarker;
        }

        var extracted = CleanNotificationVideoTitle(ExtractVideoTitleFromNotificationText(fullText));
        if (!string.IsNullOrWhiteSpace(extracted) && !LooksLikeNotificationActionOnly(extracted))
        {
            return extracted;
        }

        return FirstNonEmpty(CleanNotificationVideoTitle(title), CleanNotificationVideoTitle(fullText), "New video");
    }

    private static string BuildNotificationAuthor(string title, string fullText)
    {
        var cleanTitle = NormalizeWhitespace(title);
        var cleanFullText = NormalizeWhitespace(fullText);

        var authorFromTitle = ExtractBeforeNotificationAction(cleanTitle);
        if (!string.IsNullOrWhiteSpace(authorFromTitle))
        {
            return authorFromTitle;
        }

        var authorFromFullText = ExtractBeforeNotificationAction(cleanFullText);
        if (!string.IsNullOrWhiteSpace(authorFromFullText))
        {
            return authorFromFullText;
        }

        if (!string.IsNullOrWhiteSpace(cleanTitle) && !LooksLikeNotificationActionOnly(cleanTitle))
        {
            return cleanTitle;
        }

        return "YouTube";
    }

    private static string ExtractBeforeNotificationAction(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var index = FindNotificationActionIndex(value);
        if (index > 0)
        {
            return value.Substring(0, index).Trim(' ', ':', '-', '·');
        }

        return string.Empty;
    }

    private static string ExtractAfterNotificationAction(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var bestIndex = -1;
        string bestMarker = null;
        for (var i = 0; i < NotificationActionMarkers.Length; i++)
        {
            var marker = NotificationActionMarkers[i];
            var index = value.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
                bestMarker = marker;
            }
        }

        if (bestIndex < 0 || string.IsNullOrEmpty(bestMarker))
        {
            return string.Empty;
        }

        var after = value.Substring(bestIndex + bestMarker.Length);
        return CleanNotificationVideoTitle(after);
    }

    private static int FindNotificationActionIndex(string value)
    {
        var bestIndex = -1;
        if (string.IsNullOrWhiteSpace(value))
        {
            return bestIndex;
        }

        for (var i = 0; i < NotificationActionMarkers.Length; i++)
        {
            var index = value.IndexOf(NotificationActionMarkers[i], StringComparison.OrdinalIgnoreCase);
            if (index >= 0 && (bestIndex < 0 || index < bestIndex))
            {
                bestIndex = index;
            }
        }

        return bestIndex;
    }

    private static string CleanNotificationVideoTitle(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        while (value.StartsWith(":", StringComparison.Ordinal) ||
               value.StartsWith("-", StringComparison.Ordinal) ||
               value.StartsWith("·", StringComparison.Ordinal))
        {
            value = value.Substring(1).Trim();
        }

        foreach (var prefix in new[]
        {
            "uploaded a video:",
            "uploaded:",
            "uploaded",
            "premiered:",
            "is live:",
            "started streaming:",
            "posted:",
            "published:",
            "released:",
            "shared:",
            "added:"
        })
        {
            if (value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(prefix.Length).Trim(' ', ':', '-', '·');
                break;
            }
        }

        return value.Trim();
    }

    private static bool LooksLikeNotificationActionOnly(string value)
    {
        value = NormalizeWhitespace(value).Trim(' ', ':', '-', '·');
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        foreach (var marker in NotificationActionMarkers)
        {
            var cleanMarker = marker.Trim(' ', ':', '-', '·');
            if (value.Equals(cleanMarker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractVideoTitleFromNotificationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var quoteStart = trimmed.IndexOf('"');
        var quoteEnd = quoteStart >= 0 ? trimmed.IndexOf('"', quoteStart + 1) : -1;
        if (quoteStart >= 0 && quoteEnd > quoteStart)
        {
            return trimmed.Substring(quoteStart + 1, quoteEnd - quoteStart - 1).Trim();
        }

        var uploaded = "uploaded: ";
        var uploadedIndex = trimmed.IndexOf(uploaded, StringComparison.OrdinalIgnoreCase);
        if (uploadedIndex >= 0)
        {
            return trimmed.Substring(uploadedIndex + uploaded.Length).Trim();
        }

        return trimmed;
    }

    private static void ExtractNotificationTitleAndMessage(JsonObject renderer, out string title, out string message)
    {
        title = string.Empty;
        message = string.Empty;

        if (renderer == null)
        {
            return;
        }

        foreach (var key in new[] { "shortMessage", "message", "title" })
        {
            if (!renderer.ContainsKey(key))
            {
                continue;
            }

            var value = renderer.GetNamedValue(key);
            if (value.ValueType != JsonValueType.Object)
            {
                continue;
            }

            var obj = value.GetObject();
            if (!obj.ContainsKey("runs"))
            {
                continue;
            }

            var runsValue = obj.GetNamedValue("runs");
            if (runsValue.ValueType != JsonValueType.Array)
            {
                continue;
            }

            var runs = runsValue.GetArray();
            var messageBuilder = new StringBuilder();
            for (var i = 0; i < runs.Count; i++)
            {
                if (runs[i].ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var run = runs[i].GetObject();
                var runText = GetJsonString(run, "text");
                if (string.IsNullOrWhiteSpace(runText))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    title = runText.Trim();
                }
                else
                {
                    messageBuilder.Append(runText);
                }
            }

            message = NormalizeNotificationMessage(messageBuilder.ToString());
            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(message))
            {
                return;
            }
        }
    }

    private static string NormalizeNotificationMessage(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        while (value.StartsWith(":", StringComparison.Ordinal) ||
               value.StartsWith("-", StringComparison.Ordinal) ||
               value.StartsWith("·", StringComparison.Ordinal))
        {
            value = value.Substring(1).Trim();
        }

        return value;
    }

    private static string RemoveLeadingText(string text, string leadingText)
    {
        text = NormalizeWhitespace(text);
        leadingText = NormalizeWhitespace(leadingText);
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(leadingText) && text.StartsWith(leadingText, StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeNotificationMessage(text.Substring(leadingText.Length));
        }

        return text;
    }

    private static bool GetJsonBool(JsonObject obj, string key, bool fallback)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
        {
            return fallback;
        }

        try
        {
            var value = obj.GetNamedValue(key);
            if (value.ValueType == JsonValueType.Boolean)
            {
                return value.GetBoolean();
            }

            if (value.ValueType == JsonValueType.String)
            {
                bool parsed;
                if (bool.TryParse(value.GetString(), out parsed))
                {
                    return parsed;
                }
            }
        }
        catch
        {
        }

        return fallback;
    }

    public static Task<AccountInfo> GetAccountInfoAsync(string refreshToken)
    {
        return GetAccountInfoInternalAsync(refreshToken, false);
    }

    public static async Task<AccountInfo> GetAccountInfoFreshAsync(string refreshToken)
    {
        var freshAccount = await GetAccountInfoInternalAsync(refreshToken, true).ConfigureAwait(false);
        if (freshAccount != null)
        {
            return freshAccount;
        }

        // Keep the profile usable offline/after a transient API failure. The fresh call
        // is still attempted first so avatar changes are detected whenever possible.
        AccountInfo cachedAccount;
        return TryGetCachedAccount(refreshToken, out cachedAccount) ? cachedAccount : null;
    }

    public static async Task<List<YouTubeAccountItem>> GetYouTubeAccountsAsync(string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new List<YouTubeAccountItem>();
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return new List<YouTubeAccountItem>();
        }

        return await RequestYouTubeAccountsAsync(accessToken).ConfigureAwait(false);
    }

    private static async Task<AccountInfo> GetAccountInfoInternalAsync(string refreshToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return null;
        }

        AccountInfo cachedAccount;
        if (!forceRefresh && TryGetCachedAccount(refreshToken, out cachedAccount))
        {
            return cachedAccount;
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var accounts = await RequestYouTubeAccountsAsync(accessToken).ConfigureAwait(false);
        var selected = accounts.FirstOrDefault(item => item != null && item.IsSelected);
        if (selected == null)
        {
            return null;
        }

        var account = new AccountInfo
        {
            DisplayName = selected.DisplayName,
            ChannelHandle = selected.ChannelHandle,
            SubscribersCount = 0,
            ThumbnailUrl = selected.ThumbnailUrl
        };
        SaveCachedAccount(refreshToken, account);
        return account;
    }

    private static async Task<List<YouTubeAccountItem>> RequestYouTubeAccountsAsync(string accessToken)
    {
        var clientVersion = "7.20251217.19.00";
        var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + clientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\"},\"user\":{\"enableSafetyMode\":false}},\"accountReadMask\":{\"returnOwner\":true,\"returnBrandAccounts\":true,\"returnPersonaAccounts\":true,\"returnFamilyChildAccounts\":true,\"returnFamilyMembersAccounts\":false}}";
        var url = "https://www.youtube.com/youtubei/v1/account/accounts_list?prettyPrint=false";

        System.Diagnostics.Debug.WriteLine("[Accounts] Requesting accounts list");

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("X-Youtube-Client-Name", "7");
            request.Headers.TryAddWithoutValidation("X-Youtube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/tv");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine("[Accounts] Response status = " + response.StatusCode);
            
            if (!response.IsSuccessStatusCode)
            {
                return new List<YouTubeAccountItem>();
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ApplyPersistedAccountSelection(ParseYouTubeAccountsList(json));
        }
    }


    public static async Task<List<PlaylistItem>> GetMyPlaylistsAsync(string refreshToken, int maxResults)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return new List<PlaylistItem>();
        }

        if (maxResults <= 0)
        {
            maxResults = 25;
        }
        maxResults = Math.Min(maxResults, 50);

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine("[Playlists] Failed to get access token");
            return new List<PlaylistItem>();
        }

        var playlists = await GetMyPlaylistsViaInnertubeAsync(accessToken, maxResults).ConfigureAwait(false);
        if (playlists.Count > 0)
        {
            return playlists;
        }

        return await GetMyPlaylistsViaDataApiAsync(accessToken, maxResults).ConfigureAwait(false);
    }

    public static async Task<List<PlaylistSaveState>> GetSavePlaylistStatesAsync(
        string refreshToken,
        string videoId,
        int maxResults)
    {
        var result = new List<PlaylistSaveState>();
        if (string.IsNullOrWhiteSpace(refreshToken) || string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }

        if (maxResults <= 0)
        {
            maxResults = 25;
        }
        maxResults = Math.Min(maxResults, 50);

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return result;
        }

        var playlistsTask = GetMyPlaylistsAsync(refreshToken, maxResults);

        // The OAuth bearer belongs to the TV device flow. Query membership with a matching
        // TVHTML5 body and headers first; WEB can return the playlist rows but omit the signed-in
        // containsSelectedVideos state for this kind of token.
        var tvPayload = "{\"context\":{" + BuildSavePlaylistTvClientJson()
            + "},\"videoIds\":[\"" + JsonEscape(videoId.Trim())
            + "\"],\"excludeWatchLater\":false}";
        string json = await PostSavePlaylistTvJsonAsync(
            "playlist/get_add_to_playlist",
            tvPayload,
            accessToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(json)
            && json.IndexOf("\"playlistAddToOptionRenderer\"", StringComparison.Ordinal) >= 0)
        {
            System.Diagnostics.Debug.WriteLine("[SavePlaylist] Membership loaded via TVHTML5/85");
        }
        else
        {
            json = null;
        }

        var membershipClients = new[] { "MWEB", "WEB" };
        for (var clientIndex = 0; clientIndex < membershipClients.Length; clientIndex++)
        {
            if (!string.IsNullOrWhiteSpace(json))
            {
                break;
            }

            var clientName = membershipClients[clientIndex];
            var clientVersion = ShortsClientVersion(clientName);
            var payload = "{\"context\":{" + BuildShortsClientJson(clientName)
                + "},\"videoIds\":[\"" + JsonEscape(videoId.Trim())
                + "\"],\"excludeWatchLater\":false}";
            var attempt = await PostInnertubeJsonAsync(
                "playlist/get_add_to_playlist",
                payload,
                accessToken,
                clientName,
                clientVersion,
                true).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(attempt)
                && attempt.IndexOf("\"playlistAddToOptionRenderer\"", StringComparison.Ordinal) >= 0)
            {
                json = attempt;
                System.Diagnostics.Debug.WriteLine("[SavePlaylist] Membership loaded via " + clientName);
                break;
            }
        }

        var playlists = await playlistsTask.ConfigureAwait(false) ?? new List<PlaylistItem>();
        var playlistById = new Dictionary<string, PlaylistItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in playlists)
        {
            if (playlist == null || string.IsNullOrWhiteSpace(playlist.PlaylistId))
            {
                continue;
            }
            playlistById[NormalizeSavePlaylistId(playlist.PlaylistId)] = playlist;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var root = JsonValue.Parse(json);
                foreach (var obj in EnumerateObjects(root, 16000))
                {
                    IJsonValue rendererValue;
                    if (!obj.TryGetValue("playlistAddToOptionRenderer", out rendererValue)
                        || rendererValue == null
                        || rendererValue.ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var renderer = rendererValue.GetObject();
                    var playlistId = GetJsonString(renderer, "playlistId");
                    var normalizedId = NormalizeSavePlaylistId(playlistId);
                    if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId))
                    {
                        continue;
                    }

                    PlaylistItem playlist;
                    if (!playlistById.TryGetValue(normalizedId, out playlist))
                    {
                        playlist = new PlaylistItem
                        {
                            PlaylistId = playlistId,
                            Title = ExtractTextFromField(renderer, "title", Localization.GetString("Playlist")),
                            ThumbnailUrl = ExtractBestThumbnailUrl(renderer, "thumbnail"),
                            VideoCountText = string.Empty,
                            PrivacyText = ExtractTextFromField(renderer, "privacy", Localization.GetString("Playlist"))
                        };
                    }

                    result.Add(new PlaylistSaveState
                    {
                        Playlist = playlist,
                        ContainsVideo = PlaylistContainsSelectedVideo(renderer)
                    });

                    if (result.Count >= maxResults)
                    {
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SavePlaylist] get_add_to_playlist parse failed: " + ex.Message);
            }
        }

        foreach (var playlist in playlists)
        {
            if (result.Count >= maxResults || playlist == null)
            {
                break;
            }

            var normalizedId = NormalizeSavePlaylistId(playlist.PlaylistId);
            if (string.IsNullOrWhiteSpace(normalizedId) || !seen.Add(normalizedId))
            {
                continue;
            }

            var playlistItemId = await GetPlaylistItemIdForVideoAsync(
                accessToken,
                normalizedId,
                videoId).ConfigureAwait(false);
            result.Add(new PlaylistSaveState
            {
                Playlist = playlist,
                ContainsVideo = !string.IsNullOrWhiteSpace(playlistItemId)
            });
        }

        return result;
    }

    private const string SavePlaylistTvClientVersion = "7.20250209.19.00";

    private static string BuildSavePlaylistTvClientJson()
    {
        return "\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\""
            + SavePlaylistTvClientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl
            + "\",\"platform\":\"TV\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\""
            + ",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}";
    }

    private static async Task<string> PostSavePlaylistTvJsonAsync(
        string endpoint,
        string payload,
        string accessToken)
    {
        var url = "https://www.youtube.com/youtubei/v1/" + endpoint
            + "?key=" + InnertubeApiKey + "&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "85");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", SavePlaylistTvClientVersion);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            ApplySelectedAccountHeader(request, true);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, true), Encoding.UTF8, "application/json");

            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[SavePlaylist][TV85] " + endpoint + " failed: " + (int)response.StatusCode);
                    return string.Empty;
                }
                return json;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SavePlaylist][TV85] " + endpoint + " error: " + ex.Message);
                return string.Empty;
            }
        }
    }

    private static bool PlaylistContainsSelectedVideo(JsonObject renderer)
    {
        if (renderer == null || !renderer.ContainsKey("containsSelectedVideos"))
        {
            return false;
        }

        try
        {
            var value = renderer["containsSelectedVideos"];
            if (value == null)
            {
                return false;
            }

            if (value.ValueType == JsonValueType.Boolean)
            {
                return value.GetBoolean();
            }

            if (value.ValueType == JsonValueType.String)
            {
                var state = value.GetString();
                return string.Equals(state, "ALL", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "SOME", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(state, "true", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SavePlaylist] Membership state parse failed: " + ex.Message);
        }

        return false;
    }

    public static async Task<bool> SetVideoSavedToPlaylistAsync(
        string refreshToken,
        string playlistId,
        string videoId,
        bool shouldSave)
    {
        if (string.IsNullOrWhiteSpace(refreshToken)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(videoId))
        {
            return false;
        }

        var normalizedPlaylistId = NormalizeSavePlaylistId(playlistId);
        if (string.Equals(normalizedPlaylistId, "LL", StringComparison.OrdinalIgnoreCase))
        {
            return await SetVideoRatingAsync(videoId, shouldSave ? "like" : "none").ConfigureAwait(false);
        }

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var action = shouldSave ? "ACTION_ADD_VIDEO" : "ACTION_REMOVE_VIDEO_BY_VIDEO_ID";
        var videoKey = shouldSave ? "addedVideoId" : "removedVideoId";

        // The command metadata returned by YouTube points to /youtubei/v1/browse/edit_playlist.
        // /playlist/edit is not the playlistEditEndpoint and always rejects this action shape.
        // Try the TV client first because the stored bearer was minted by the TV device flow;
        // each fallback keeps the client in the JSON body and HTTP headers identical.
        var tvPayload = "{\"context\":{" + BuildSavePlaylistTvClientJson() + "},\"playlistId\":\""
            + JsonEscape(normalizedPlaylistId) + "\",\"actions\":[{\"action\":\""
            + action + "\",\"" + videoKey + "\":\"" + JsonEscape(videoId.Trim()) + "\"}]}";
        var tvJson = await PostSavePlaylistTvJsonAsync(
            "browse/edit_playlist",
            tvPayload,
            accessToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(tvJson))
        {
            System.Diagnostics.Debug.WriteLine("[SavePlaylist] Updated via TVHTML5/85");
            InvalidatePlaylistCaches(normalizedPlaylistId);
            return true;
        }

        var clients = new[] { "MWEB", "WEB" };
        for (var index = 0; index < clients.Length; index++)
        {
            var clientName = clients[index];
            var clientVersion = ShortsClientVersion(clientName);
            var clientJson = BuildShortsClientJson(clientName);
            var payload = "{\"context\":{" + clientJson + "},\"playlistId\":\""
                + JsonEscape(normalizedPlaylistId) + "\",\"actions\":[{\"action\":\""
                + action + "\",\"" + videoKey + "\":\"" + JsonEscape(videoId.Trim()) + "\"}]}";
            var json = await PostInnertubeJsonAsync(
                "browse/edit_playlist",
                payload,
                accessToken,
                clientName,
                clientVersion,
                true).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(json))
            {
                System.Diagnostics.Debug.WriteLine("[SavePlaylist] Updated via " + clientName);
                InvalidatePlaylistCaches(normalizedPlaylistId);
                return true;
            }
        }

        // This standards-based fallback works for sessions that were authorized with the
        // official youtube/youtube.force-ssl OAuth scope.
        var updated = await SetVideoSavedToPlaylistViaDataApiAsync(
            accessToken,
            normalizedPlaylistId,
            videoId,
            shouldSave).ConfigureAwait(false);
        if (updated)
            InvalidatePlaylistCaches(normalizedPlaylistId);
        return updated;
    }

    private static void InvalidatePlaylistCaches(string playlistId)
    {
        InnertubeRequestCoordinator.Invalidate(
            BuildPlaylistBrowseCacheKey(playlistId, true));
        InnertubeRequestCoordinator.Invalidate(
            BuildPlaylistBrowseCacheKey(playlistId, false));
        InnertubeRequestCoordinator.Invalidate(
            BuildBrowseCoordinatorKey("tv", true, "FEplaylist_aggregation", null, null));
    }

    private static string NormalizeSavePlaylistId(string playlistId)
    {
        var value = (playlistId ?? string.Empty).Trim();
        if (value.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && value.Length > 2)
        {
            value = value.Substring(2);
        }
        return value;
    }

    private static async Task<string> GetPlaylistItemIdForVideoAsync(
        string accessToken,
        string playlistId,
        string videoId)
    {
        if (string.IsNullOrWhiteSpace(accessToken)
            || string.IsNullOrWhiteSpace(playlistId)
            || string.IsNullOrWhiteSpace(videoId))
        {
            return string.Empty;
        }

        var url = "https://www.googleapis.com/youtube/v3/playlistItems?part=id&playlistId="
            + Uri.EscapeDataString(playlistId)
            + "&videoId=" + Uri.EscapeDataString(videoId)
            + "&maxResults=1&prettyPrint=false";
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(responseJson))
                {
                    return string.Empty;
                }

                var root = JsonObject.Parse(responseJson);
                var items = root.GetNamedArray("items", new JsonArray());
                if (items.Count == 0 || items[0].ValueType != JsonValueType.Object)
                {
                    return string.Empty;
                }
                return GetJsonString(items[0].GetObject(), "id");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SavePlaylist] Membership check failed: " + ex.Message);
            return string.Empty;
        }
    }

    private static async Task<bool> SetVideoSavedToPlaylistViaDataApiAsync(
        string accessToken,
        string playlistId,
        string videoId,
        bool shouldSave)
    {
        try
        {
            if (!shouldSave)
            {
                var itemId = await GetPlaylistItemIdForVideoAsync(accessToken, playlistId, videoId).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(itemId))
                {
                    return true;
                }

                using (var request = new HttpRequestMessage(
                    HttpMethod.Delete,
                    "https://www.googleapis.com/youtube/v3/playlistItems?id=" + Uri.EscapeDataString(itemId)))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                    request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                    var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    return response.IsSuccessStatusCode;
                }
            }

            var url = "https://www.googleapis.com/youtube/v3/playlistItems?part=snippet&prettyPrint=false";
            var body = "{\"snippet\":{\"playlistId\":\"" + JsonEscape(playlistId)
                + "\",\"resourceId\":{\"kind\":\"youtube#video\",\"videoId\":\""
                + JsonEscape(videoId) + "\"}}}";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[SavePlaylist] Data API update failed: " + ex.Message);
            return false;
        }
    }

    private static async Task<List<PlaylistItem>> GetMyPlaylistsViaInnertubeAsync(string accessToken, int maxResults)
    {
        // Reuse the common TV browse path: it coalesces simultaneous callers from Me,
        // save-to-playlist and playlist pages and keeps a short parsed response cache.
        var json = await PostTvBrowseAsync(
            accessToken,
            "FEplaylist_aggregation",
            null,
            null).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(json)
            ? new List<PlaylistItem>()
            : ParsePlaylistCards(json, maxResults);
    }

    private static async Task<List<PlaylistItem>> GetMyPlaylistsViaDataApiAsync(string accessToken, int maxResults)
    {
        var url = "https://www.googleapis.com/youtube/v3/playlists?part=snippet,contentDetails,status&mine=true&maxResults=" + maxResults.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            System.Diagnostics.Debug.WriteLine("[Playlists] Data API response: " + response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[Playlists] Data API error: " + error);
                return new List<PlaylistItem>();
            }

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ParseDataApiPlaylists(json, maxResults);
        }
    }


    // Playlists tab of a channel. The "params" value is the protobuf tab selector for
    // Playlists. Anonymous WEB client — no sign-in required.
    public static async Task<List<PlaylistItem>> GetChannelPlaylistsAsync(string channelId, int maxResults)
    {
        var result = new List<PlaylistItem>();
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return result;
        }

        try
        {
            var payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20250101\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}},\"browseId\":\""
                + JsonEscape(channelId)
                + "\",\"params\":\"EglwbGF5bGlzdHPyBgQKAjoA\"}";

            var json = await PostInnertubeJsonAsync(
                "browse",
                payload,
                string.Empty,
                "WEB",
                "2.20250101").ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return result;
            }

            result = ParsePlaylistCards(json, maxResults);
            System.Diagnostics.Debug.WriteLine("[ChannelPlaylists] Parsed " + result.Count + " playlist(s) for " + channelId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ChannelPlaylists] Failed: " + ex.Message);
        }

        return result;
    }

    private static async Task<string> GetPlaylistBrowseJsonAsync(string playlistId, string accessToken)
    {
        var browseId = NormalizePlaylistBrowseId(playlistId);
        if (string.IsNullOrWhiteSpace(browseId))
            return string.Empty;

        var authenticated = !string.IsNullOrWhiteSpace(accessToken);
        var key = BuildPlaylistBrowseCacheKey(playlistId, authenticated);
        try
        {
            var response = await InnertubeRequestCoordinator.GetJsonAsync(
                key,
                delegate { return GetPlaylistBrowseJsonCoreAsync(playlistId, accessToken); },
                TimeSpan.FromMinutes(3)).ConfigureAwait(false);
            return response.Text;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Playlist] Coordinated browse failed: " + ex.Message);
            return string.Empty;
        }
    }

    private static async Task<string> GetPlaylistBrowseJsonCoreAsync(string playlistId, string accessToken)
    {
        var browseId = NormalizePlaylistBrowseId(playlistId);
        if (string.IsNullOrWhiteSpace(browseId))
        {
            return string.Empty;
        }

        // YouTube.js loads a playlist through browseId = "VL" + playlistId.
        // For OAuth refresh-token sessions in this UWP app the TV client is more reliable;
        // sending Authorization with the WEB client often returns BadRequest.
        var useTvClient = !string.IsNullOrWhiteSpace(accessToken);
        var clientName = useTvClient ? "TVHTML5" : "WEB";
        var clientVersion = useTvClient ? "7.20250209.19.00" : "2.20250101";
        var clientHeaderName = useTvClient ? "85" : "1";
        var userAgent = useTvClient ? UserAgent : WebUserAgent;
        var platformJson = useTvClient ? ",\"platform\":\"TV\"" : string.Empty;

        var payload = "{\"context\":{\"client\":{\"clientName\":\"" + clientName + "\",\"clientVersion\":\"" + clientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"" + platformJson + "}},\"browseId\":\"" + JsonEscape(browseId) + "\"}";
        var url = "https://www.youtube.com/youtubei/v1/browse?key=" + InnertubeApiKey + "&prettyPrint=false";

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientHeaderName);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            }

            var authenticated = !string.IsNullOrWhiteSpace(accessToken);
            ApplySelectedAccountHeader(request, authenticated);
            request.Content = new StringContent(ApplySelectedAccountContext(payload, authenticated), Encoding.UTF8, "application/json");

            try
            {
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[Playlist] Browse " + clientName + " response: " + response.StatusCode);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine("[Playlist] Browse " + clientName + " error: " + error);
                    return string.Empty;
                }

                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playlist] Browse " + clientName + " exception: " + ex.Message);
                return string.Empty;
            }
        }
    }

    private static string BuildPlaylistBrowseCacheKey(string playlistId, bool authenticated)
    {
        var browseId = NormalizePlaylistBrowseId(playlistId);
        return "playlist:browse:"
            + (authenticated ? "auth:" : "guest:")
            + Hl + ":" + Gl + ":"
            + (authenticated ? SelectedYouTubeAccountBrandId : string.Empty) + ":"
            + browseId;
    }


    private static async Task<PlaylistDetails> GetPlaylistMetadataViaDataApiAsync(string playlistId, string accessToken)
    {
        var cleanPlaylistId = NormalizePlaylistDataApiId(playlistId);
        if (string.IsNullOrWhiteSpace(cleanPlaylistId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        var url = "https://www.googleapis.com/youtube/v3/playlists?part=snippet,contentDetails,status&id=" + Uri.EscapeDataString(cleanPlaylistId) + "&maxResults=1&prettyPrint=false";
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);

            try
            {
                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                System.Diagnostics.Debug.WriteLine("[Playlist] Data API metadata response: " + response.StatusCode);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                return ParseDataApiPlaylistDetails(json, cleanPlaylistId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playlist] Data API metadata exception: " + ex.Message);
                return null;
            }
        }
    }

    private static PlaylistDetails ParseDataApiPlaylistDetails(string json, string playlistId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var root = JsonObject.Parse(json);
            if (!root.ContainsKey("items"))
            {
                return null;
            }

            var items = root.GetNamedArray("items");
            if (items.Count == 0 || items[0].ValueType != JsonValueType.Object)
            {
                return null;
            }

            var item = items[0].GetObject();
            var result = new PlaylistDetails
            {
                PlaylistId = playlistId,
                Title = string.Empty,
                OwnerName = string.Empty,
                OwnerThumbnailUrl = string.Empty,
                Description = string.Empty,
                ThumbnailUrl = string.Empty,
                MetadataText = string.Empty,
                Videos = new List<VideoCardItem>()
            };

            if (item.ContainsKey("snippet") && item.GetNamedValue("snippet").ValueType == JsonValueType.Object)
            {
                var snippet = item.GetNamedObject("snippet");
                result.Title = FirstNonEmpty(GetJsonString(snippet, "title"), string.Empty);
                result.Description = FirstNonEmpty(GetJsonString(snippet, "description"), string.Empty);
                result.OwnerName = FirstNonEmpty(GetJsonString(snippet, "channelTitle"), string.Empty);
                result.ThumbnailUrl = ExtractDataApiPlaylistThumbnail(snippet);
            }

            var metadataParts = new List<string>();
            if (item.ContainsKey("status") && item.GetNamedValue("status").ValueType == JsonValueType.Object)
            {
                AddMetadata(metadataParts, FormatPrivacyText(GetJsonString(item.GetNamedObject("status"), "privacyStatus")));
            }

            if (item.ContainsKey("contentDetails") && item.GetNamedValue("contentDetails").ValueType == JsonValueType.Object)
            {
                var contentDetails = item.GetNamedObject("contentDetails");
                var itemCount = GetJsonString(contentDetails, "itemCount");
                if (!string.IsNullOrWhiteSpace(itemCount))
                {
                    AddMetadata(metadataParts, Localization.Format("VideosSuffixFormat", itemCount));
                }
            }

            result.MetadataText = JoinDistinctMetadata(metadataParts);
            return result;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Playlist] Data API metadata parse error: " + ex.Message);
            return null;
        }
    }

    private static bool NeedsPlaylistMetadataFallback(PlaylistDetails details)
    {
        if (details == null)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(details.Title) ||
               string.Equals(details.Title, "Playlist", StringComparison.OrdinalIgnoreCase) ||
               string.IsNullOrWhiteSpace(details.OwnerName) ||
               string.IsNullOrWhiteSpace(details.MetadataText) ||
               string.IsNullOrWhiteSpace(details.ThumbnailUrl);
    }

    private static void MergePlaylistMetadata(PlaylistDetails target, PlaylistDetails source)
    {
        if (target == null || source == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(source.Title))
        {
            target.Title = source.Title.Trim();
        }
        if (!string.IsNullOrWhiteSpace(source.OwnerName))
        {
            target.OwnerName = source.OwnerName.Trim();
        }
        target.OwnerThumbnailUrl = PreferExisting(target.OwnerThumbnailUrl, source.OwnerThumbnailUrl);
        target.Description = PreferExisting(target.Description, source.Description);
        if (!string.IsNullOrWhiteSpace(source.ThumbnailUrl))
        {
            target.ThumbnailUrl = source.ThumbnailUrl.Trim();
        }
        if (!string.IsNullOrWhiteSpace(source.MetadataText))
        {
            target.MetadataText = source.MetadataText.Trim();
        }
    }

    private static async Task<List<VideoCardItem>> GetPlaylistVideosViaDataApiAsync(string playlistId, string accessToken, int maxVideos)
    {
        var result = new List<VideoCardItem>();
        var cleanPlaylistId = NormalizePlaylistDataApiId(playlistId);
        if (string.IsNullOrWhiteSpace(cleanPlaylistId) || string.IsNullOrWhiteSpace(accessToken))
        {
            return result;
        }

        if (maxVideos <= 0)
        {
            maxVideos = 80;
        }
        maxVideos = Math.Min(maxVideos, 120);

        var pageToken = string.Empty;
        while (result.Count < maxVideos)
        {
            var pageSize = Math.Min(50, maxVideos - result.Count);
            var url = "https://www.googleapis.com/youtube/v3/playlistItems?part=snippet,contentDetails,status&playlistId=" + Uri.EscapeDataString(cleanPlaylistId) + "&maxResults=" + pageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + "&prettyPrint=false";
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                url += "&pageToken=" + Uri.EscapeDataString(pageToken);
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);

                try
                {
                    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    System.Diagnostics.Debug.WriteLine("[Playlist] Data API items response: " + response.StatusCode);
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        System.Diagnostics.Debug.WriteLine("[Playlist] Data API items error: " + error);
                        return result;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JsonObject.Parse(json);
                    if (!root.ContainsKey("items"))
                    {
                        return result;
                    }

                    var items = root.GetNamedArray("items");
                    for (var i = 0; i < items.Count && result.Count < maxVideos; i++)
                    {
                        if (items[i].ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        var item = ParseDataApiPlaylistVideo(items[i].GetObject());
                        if (item != null && !string.IsNullOrWhiteSpace(item.VideoId))
                        {
                            result.Add(item);
                        }
                    }

                    pageToken = GetJsonString(root, "nextPageToken");
                    if (string.IsNullOrWhiteSpace(pageToken) || items.Count == 0)
                    {
                        break;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Playlist] Data API items exception: " + ex.Message);
                    return result;
                }
            }
        }

        return result;
    }

    private static VideoCardItem ParseDataApiPlaylistVideo(JsonObject item)
    {
        if (item == null)
        {
            return null;
        }

        JsonObject snippet = null;
        JsonObject contentDetails = null;
        if (item.ContainsKey("snippet") && item.GetNamedValue("snippet").ValueType == JsonValueType.Object)
        {
            snippet = item.GetNamedObject("snippet");
        }
        if (item.ContainsKey("contentDetails") && item.GetNamedValue("contentDetails").ValueType == JsonValueType.Object)
        {
            contentDetails = item.GetNamedObject("contentDetails");
        }

        var videoId = string.Empty;
        if (contentDetails != null)
        {
            videoId = GetJsonString(contentDetails, "videoId");
        }
        if (string.IsNullOrWhiteSpace(videoId) && snippet != null && snippet.ContainsKey("resourceId"))
        {
            var resourceValue = snippet.GetNamedValue("resourceId");
            if (resourceValue.ValueType == JsonValueType.Object)
            {
                videoId = GetJsonString(resourceValue.GetObject(), "videoId");
            }
        }

        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        var title = snippet == null ? Localization.GetString("Untitled") : FirstNonEmpty(GetJsonString(snippet, "title"), Localization.GetString("Untitled"));
        var channelTitle = snippet == null ? Localization.GetString("Unknown") : FirstNonEmpty(
            GetJsonString(snippet, "videoOwnerChannelTitle"),
            GetJsonString(snippet, "channelTitle"),
            Localization.GetString("Unknown"));

        var thumbnailUrl = string.Empty;
        if (snippet != null && snippet.ContainsKey("thumbnails"))
        {
            var thumbsValue = snippet.GetNamedValue("thumbnails");
            if (thumbsValue.ValueType == JsonValueType.Object)
            {
                thumbnailUrl = ExtractDataApiThumbnailUrl(thumbsValue.GetObject());
            }
        }

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = title,
            ChannelTitle = channelTitle,
            ChannelId = snippet == null ? string.Empty : FirstNonEmpty(
                GetJsonString(snippet, "videoOwnerChannelId"),
                GetJsonString(snippet, "channelId")),
            Duration = string.Empty,
            ThumbnailUrl = FirstNonEmpty(thumbnailUrl, BuildMqThumbnailUrl(videoId))
        };
    }

    private static string ExtractDataApiThumbnailUrl(JsonObject thumbnails)
    {
        if (thumbnails == null)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "maxres", "standard", "high", "medium", "default" })
        {
            if (!thumbnails.ContainsKey(key))
            {
                continue;
            }

            var value = thumbnails.GetNamedValue(key);
            if (value.ValueType != JsonValueType.Object)
            {
                continue;
            }

            var url = GetJsonString(value.GetObject(), "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url.StartsWith("//") ? "https:" + url : url;
            }
        }

        return string.Empty;
    }

    private static string NormalizePlaylistDataApiId(string playlistId)
    {
        var id = ExtractPlaylistIdFromText(playlistId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        if (id.StartsWith("VL", StringComparison.OrdinalIgnoreCase) && id.Length > 2)
        {
            return id.Substring(2);
        }

        return id;
    }

    private static string NormalizePlaylistBrowseId(string playlistId)
    {
        var id = ExtractPlaylistIdFromText(playlistId);
        if (string.IsNullOrWhiteSpace(id))
        {
            return string.Empty;
        }

        if (id.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
        {
            return id;
        }

        // Auto-generated mixes / "jams" (RD..., including RDMM radio and RDCLAK station
        // playlists) are browsed by their raw id — prefixing "VL" makes YouTube return an
        // empty page for them. Only real (stored) playlists take the VL prefix.
        if (IsMixPlaylistId(id))
        {
            return id;
        }

        return "VL" + id;
    }

    // Device UI language / region, sent as hl+gl on every InnerTube request. Without this
    // YouTube answers in English and even auto-translates video titles, ignoring the language
    // the phone is set to.
    private static string _hl;
    private static string _gl;

    public static string Hl { get { EnsureLocale(); return _hl; } }
    public static string Gl { get { EnsureLocale(); return _gl; } }

    public static void RefreshLocale()
    {
        _hl = null;
        _gl = null;
    }

    private static void EnsureLocale()
    {
        if (!string.IsNullOrEmpty(_hl) && !string.IsNullOrEmpty(_gl))
        {
            return;
        }

        var language = "en";
        var region = "US";

        try
        {
            // Follow the app language override when the user selected one; otherwise this
            // resolves to the language chosen by Windows for the app.
            var tag = Localization.EffectiveLanguageTag;
            language = Localization.EffectiveYouTubeLanguageCode;
            if (!string.IsNullOrWhiteSpace(tag))
            {
                // Use a real region subtag only as a fallback. Script tags such as sr-Latn
                // must never become gl=LATN, and es-419 is a language variant rather than a
                // YouTube country code. HomeGeographicRegion below remains authoritative.
                var parts = tag.Split('-');
                for (int i = 1; i < parts.Length; i++)
                {
                    if (parts[i].Length == 2)
                    {
                        region = parts[i];
                        break;
                    }
                }
            }

            var home = Windows.System.UserProfile.GlobalizationPreferences.HomeGeographicRegion;
            if (!string.IsNullOrWhiteSpace(home))
            {
                region = home;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Locale] Falling back to en/US: " + ex.Message);
        }

        _hl = string.IsNullOrWhiteSpace(language) ? "en" : language;
        _gl = string.IsNullOrWhiteSpace(region) ? "US" : region.ToUpperInvariant();
        System.Diagnostics.Debug.WriteLine("[Locale] hl=" + _hl + " gl=" + _gl);
    }

    // ---------------------------------------------------------------------------------------
    // Watch history
    //
    // There is no youtubei/v1 JSON endpoint that "adds a video to history". YouTube records it
    // through the legacy playback-stats pings, exactly as youtube.com/tv does:
    //
    //   1. /player returns a "playbackTracking" block holding server-signed base URLs:
    //        videostatsPlaybackUrl  -> s.youtube.com/api/stats/playback
    //        videostatsWatchtimeUrl -> s.youtube.com/api/stats/watchtime
    //      Their query already carries ns, el, cl, docid, ei, of, plid, vm, len, fexp — those
    //      are issued by the server and must be passed through untouched.
    //   2. The client generates a "cpn" (client playback nonce, 16 chars) and appends the
    //      playback state: cpn, ver, cmt (current time), st/et (watched segment), etc.
    //   3. The pings must be sent AUTHENTICATED — that is what attributes the view to the
    //      account. The tracking URLs must come from an authenticated /player request too,
    //      otherwise they belong to a signed-out session.
    // ---------------------------------------------------------------------------------------

    // FRAGILE: YouTube rejects a stale TVHTML5 build with "UNPLAYABLE: The page needs to be
    // reloaded". Taken from a live youtube.com/tv session (2026-07-15). Bump when /player
    // starts returning that message again.
    private const string TvPlayerClientVersion = "7.20260715.15.00";
    private const string TvUserAgent =
        "Mozilla/5.0 (SMART-TV; LINUX; Tizen 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Version/5.0 TV Safari/537.36";

    // signatureTimestamp ("sts") — the revision number of YouTube's player JS. TVHTML5 /player
    // REQUIRES it: without it the response is "UNPLAYABLE: The page needs to be reloaded", and
    // with it the same request returns OK plus the playbackTracking block (verified A/B).
    //
    // It only lives inside the player JS (~2.3 MB, roughly halfway in), so it is extracted by
    // streaming the file and stopping at the match — never buffering it whole, which would be a
    // real memory risk on this device — and then cached persistently. It changes only when
    // YouTube ships a new player, so refetching is rare.
    private const string StsSettingKey = "PlayerSignatureTimestamp";
    private static int _cachedSts;

    internal static async Task<int> GetSignatureTimestampAsync(bool forceRefresh)
    {
        if (!forceRefresh)
        {
            if (_cachedSts > 0)
            {
                return _cachedSts;
            }

            try
            {
                var stored = ApplicationData.Current.LocalSettings.Values[StsSettingKey];
                if (stored is int && (int)stored > 0)
                {
                    _cachedSts = (int)stored;
                    return _cachedSts;
                }
            }
            catch
            {
            }
        }

        try
        {
            // The TV page references the player JS we need.
            string tvPage;
            using (var request = new HttpRequestMessage(HttpMethod.Get, "https://www.youtube.com/tv"))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return _cachedSts;
                }

                tvPage = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }

            var match = System.Text.RegularExpressions.Regex.Match(
                tvPage, "/s/player/[0-9a-fA-F]+/[\\w\\-]+\\.vflset/[\\w\\-]+\\.js");
            if (!match.Success)
            {
                System.Diagnostics.Debug.WriteLine("[History] Player JS URL not found on /tv");
                return _cachedSts;
            }

            var jsUrl = "https://www.youtube.com" + match.Value;
            var sts = await ScanPlayerJsForStsAsync(jsUrl).ConfigureAwait(false);
            if (sts > 0)
            {
                _cachedSts = sts;
                try { ApplicationData.Current.LocalSettings.Values[StsSettingKey] = sts; } catch { }
                System.Diagnostics.Debug.WriteLine("[History] signatureTimestamp = " + sts);
            }

            return sts > 0 ? sts : _cachedSts;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[History] sts lookup failed: " + ex.Message);
            return _cachedSts;
        }
    }

    // Streams the player JS in small chunks and stops as soon as the value is found.
    private static async Task<int> ScanPlayerJsForStsAsync(string jsUrl)
    {
        using (var request = new HttpRequestMessage(HttpMethod.Get, jsUrl))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
            using (var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode)
                {
                    return 0;
                }

                using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    var buffer = new byte[64 * 1024];
                    var carry = string.Empty;   // overlap so a split match is not missed
                    int read;

                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        var chunk = carry + Encoding.ASCII.GetString(buffer, 0, read);
                        var m = System.Text.RegularExpressions.Regex.Match(chunk, "signatureTimestamp[:=](\\d+)");
                        if (m.Success)
                        {
                            int value;
                            if (int.TryParse(m.Groups[1].Value, out value) && value > 0)
                            {
                                return value;
                            }
                        }

                        carry = chunk.Length > 64 ? chunk.Substring(chunk.Length - 64) : chunk;
                    }
                }
            }
        }

        return 0;
    }

    private const string CpnAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
    private static readonly Random CpnRandom = new Random();

    // 16-character client playback nonce, same shape the TV client uses (e.g. "Y6M_d7ev1m6YQEkp").
    public static string GenerateCpn()
    {
        var chars = new char[16];
        lock (CpnRandom)
        {
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = CpnAlphabet[CpnRandom.Next(CpnAlphabet.Length)];
            }
        }

        return new string(chars);
    }

    // Records the video in the signed-in account's watch history.
    //   positionSeconds - how far playback has got (0 right after start)
    //   lengthSeconds   - total video length, if known
    // Returns false when not signed in or YouTube rejected the pings.
    public static async Task<bool> ReportWatchHistoryAsync(
        string videoId, string refreshToken, double positionSeconds, double lengthSeconds)
    {
        if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(refreshToken))
        {
            return false;
        }

        try
        {
            var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                System.Diagnostics.Debug.WriteLine("[History] No access token; skipping");
                return false;
            }

            // Authenticated player request purely to obtain this account's tracking URLs.
            // A stale sts yields "The page needs to be reloaded", so retry once with a fresh one.
            JsonObject root = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var sts = await GetSignatureTimestampAsync(attempt > 0).ConfigureAwait(false);
                var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + TvPlayerClientVersion + "\""
                    + ",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"TV\""
                    + ",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"osName\":\"Tizen\",\"osVersion\":\"5.0\"}}"
                    + ",\"videoId\":\"" + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true"
                    + ",\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\""
                    + (sts > 0 ? ",\"signatureTimestamp\":" + sts : string.Empty)
                    + "}}}";

                var json = await PostInnertubeJsonAsync("player", payload, accessToken, "TVHTML5", TvPlayerClientVersion).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(json))
                {
                    System.Diagnostics.Debug.WriteLine("[History] Authenticated player request failed");
                    return false;
                }

                root = JsonObject.Parse(json);
                if (root.ContainsKey("playbackTracking"))
                {
                    break;
                }

                var status = root.ContainsKey("playabilityStatus")
                    ? GetJsonString(root.GetNamedObject("playabilityStatus"), "status") : "?";
                var reason = root.ContainsKey("playabilityStatus")
                    ? GetJsonString(root.GetNamedObject("playabilityStatus"), "reason") : string.Empty;

                var stale = reason != null && reason.IndexOf("reload", StringComparison.OrdinalIgnoreCase) >= 0;
                if (attempt == 0 && stale)
                {
                    System.Diagnostics.Debug.WriteLine("[History] sts looks stale; refreshing and retrying");
                    continue;
                }

                System.Diagnostics.Debug.WriteLine("[History] No playbackTracking (playability=" + status + " " + reason + ")");
                return false;
            }

            if (root == null || !root.ContainsKey("playbackTracking"))
            {
                return false;
            }

            var tracking = root.GetNamedObject("playbackTracking");
            var playbackUrl = GetTrackingBaseUrl(tracking, "videostatsPlaybackUrl");
            var watchtimeUrl = GetTrackingBaseUrl(tracking, "videostatsWatchtimeUrl");
            if (string.IsNullOrWhiteSpace(playbackUrl) && string.IsNullOrWhiteSpace(watchtimeUrl))
            {
                return false;
            }

            if (lengthSeconds <= 0 && root.ContainsKey("videoDetails"))
            {
                double parsed;
                if (double.TryParse(GetJsonString(root.GetNamedObject("videoDetails"), "lengthSeconds"),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out parsed))
                {
                    lengthSeconds = parsed;
                }
            }

            var cpn = GenerateCpn();
            var position = positionSeconds > 0 ? positionSeconds : 0;
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // Shared client identity, mirroring what the TV client reports.
            var common = "&cpn=" + Uri.EscapeDataString(cpn)
                + "&ver=2&fs=0&volume=100&muted=0&state=playing"
                + "&c=TVHTML5&cver=" + TvPlayerClientVersion + "&cplayer=UNIPLAYER&cmodel=SmartTV"
                + "&cos=Tizen&cosver=5.0&cplatform=TV&ctheme=CLASSIC"
                + "&hl=" + Uri.EscapeDataString(Hl) + "&cr=" + Uri.EscapeDataString(Gl);

            var ok = false;

            // 1) Playback ping — registers the view.
            if (!string.IsNullOrWhiteSpace(playbackUrl))
            {
                ok |= await SendStatsPingAsync(playbackUrl + common + "&cmt=" + position.ToString("0.###", inv), accessToken).ConfigureAwait(false);
            }

            // 2) Watchtime ping — reports the watched segment, which is what makes the entry
            //    show up (and stores the resume position).
            if (!string.IsNullOrWhiteSpace(watchtimeUrl))
            {
                var end = position > 0 ? position : 1;
                var watchtime = watchtimeUrl + common
                    + "&cmt=" + position.ToString("0.###", inv)
                    + "&st=0&et=" + end.ToString("0.###", inv)
                    + "&rt=" + end.ToString("0.###", inv);
                if (lengthSeconds > 0)
                {
                    watchtime += "&len=" + lengthSeconds.ToString("0.###", inv);
                }

                ok |= await SendStatsPingAsync(watchtime, accessToken).ConfigureAwait(false);
            }

            System.Diagnostics.Debug.WriteLine("[History] " + videoId + " reported at "
                + position.ToString("0.#", inv) + "s, ok=" + ok);
            return ok;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[History] Failed: " + ex.Message);
            return false;
        }
    }

    private static string GetTrackingBaseUrl(JsonObject tracking, string key)
    {
        try
        {
            if (tracking == null || !tracking.ContainsKey(key))
            {
                return string.Empty;
            }

            return GetJsonString(tracking.GetNamedObject(key), "baseUrl");
        }
        catch
        {
            return string.Empty;
        }
    }

    private static async Task<bool> SendStatsPingAsync(string url, string accessToken)
    {
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                request.Headers.TryAddWithoutValidation("User-Agent",
                    TvUserAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/tv");

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[History] Ping " + (int)response.StatusCode
                        + " for " + new Uri(url).AbsolutePath);
                    return false;
                }

                return true;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[History] Ping failed: " + ex.Message);
            return false;
        }
    }

    // A YouTube "jam"/mix: an auto-generated, endlessly-continuing playlist. These live in the
    // watch (/next) context rather than as a stored playlist.
    public static bool IsMixPlaylistId(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return false;
        }

        var id = playlistId.Trim();
        if (id.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
        {
            id = id.Substring(2);
        }

        return id.StartsWith("RD", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPlaylistIdFromText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        value = value.Trim();
        var listIndex = value.IndexOf("list=", StringComparison.OrdinalIgnoreCase);
        if (listIndex >= 0)
        {
            value = value.Substring(listIndex + 5);
            var amp = value.IndexOf('&');
            if (amp >= 0)
            {
                value = value.Substring(0, amp);
            }
        }

        return value.Trim();
    }

    private static PlaylistDetails ParsePlaylistDetails(string json, string requestedPlaylistId, int maxVideos)
    {
        var result = new PlaylistDetails
        {
            PlaylistId = ExtractPlaylistIdFromText(requestedPlaylistId),
            Title = "Playlist",
            OwnerName = string.Empty,
            OwnerThumbnailUrl = string.Empty,
            Description = string.Empty,
            ThumbnailUrl = string.Empty,
            MetadataText = string.Empty,
            ContinuationToken = string.Empty,
            Videos = new List<VideoCardItem>()
        };

        try
        {
            var root = JsonValue.Parse(json);
            result.ContinuationToken = ExtractHistoryContinuationToken(root);
            var metadataParts = new List<string>();

            foreach (var obj in EnumerateObjects(root, 24000))
            {
                if (obj.ContainsKey("playlistMetadataRenderer"))
                {
                    ApplyPlaylistMetadataRenderer(result, obj.GetNamedObject("playlistMetadataRenderer"));
                }
                else if (obj.ContainsKey("playlistHeaderRenderer"))
                {
                    ApplyPlaylistHeaderRenderer(result, obj.GetNamedObject("playlistHeaderRenderer"), metadataParts);
                }
                else if (obj.ContainsKey("playlistSidebarPrimaryInfoRenderer"))
                {
                    ApplyPlaylistSidebarPrimaryInfoRenderer(result, obj.GetNamedObject("playlistSidebarPrimaryInfoRenderer"), metadataParts);
                }
                else if (obj.ContainsKey("playlistSidebarSecondaryInfoRenderer"))
                {
                    ApplyPlaylistSidebarSecondaryInfoRenderer(result, obj.GetNamedObject("playlistSidebarSecondaryInfoRenderer"));
                }
                else if (obj.ContainsKey("videoOwnerRenderer"))
                {
                    ApplyVideoOwnerRenderer(result, obj.GetNamedObject("videoOwnerRenderer"));
                }
                else if (obj.ContainsKey("pageHeaderViewModel"))
                {
                    ApplyPlaylistPageHeaderViewModel(result, obj.GetNamedObject("pageHeaderViewModel"), metadataParts);
                }
                else if (obj.ContainsKey("pageHeaderRenderer"))
                {
                    ApplyPlaylistPageHeaderRenderer(result, obj.GetNamedObject("pageHeaderRenderer"), metadataParts);
                }
            }

            result.Videos = ParsePlaylistVideoCards(json, maxVideos);

            if (string.IsNullOrWhiteSpace(result.ThumbnailUrl) && result.Videos != null && result.Videos.Count > 0)
            {
                result.ThumbnailUrl = result.Videos[0].ThumbnailUrl;
            }

            if (metadataParts.Count == 0 && result.Videos != null && result.Videos.Count > 0)
            {
                metadataParts.Add(result.Videos.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " videos");
            }

            result.MetadataText = JoinDistinctMetadata(metadataParts);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Playlist] Parse error: " + ex.Message);
        }

        return result;
    }


    private static void ApplyPlaylistMetadataRenderer(PlaylistDetails result, JsonObject renderer)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.Title = PreferExisting(result.Title, FirstNonEmpty(
            GetJsonString(renderer, "title"),
            ExtractTextFromNamedValue(renderer, "title"),
            string.Empty));
        result.Description = PreferExisting(result.Description, FirstNonEmpty(
            GetJsonString(renderer, "description"),
            ExtractTextFromNamedValue(renderer, "description"),
            string.Empty));
    }

    private static void ApplyPlaylistSidebarPrimaryInfoRenderer(PlaylistDetails result, JsonObject renderer, List<string> metadataParts)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.Title = PreferExisting(result.Title, FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "title"),
            GetJsonString(renderer, "title"),
            string.Empty));
        result.Description = PreferExisting(result.Description, FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "description"),
            GetJsonString(renderer, "description"),
            string.Empty));
        result.ThumbnailUrl = PreferExisting(result.ThumbnailUrl, FirstNonEmpty(
            ExtractFirstUrlFromNamedValue(renderer, "thumbnailRenderer"),
            ExtractBestThumbnailUrl(renderer, "thumbnail"),
            ExtractFirstUrlFromAnyValue(renderer)));

        if (renderer.ContainsKey("stats") && renderer.GetNamedValue("stats").ValueType == JsonValueType.Array)
        {
            try
            {
                var stats = renderer.GetNamedArray("stats");
                for (var i = 0; i < stats.Count; i++)
                {
                    AddMetadata(metadataParts, ExtractTextFromAnyValue(stats[i]));
                }
            }
            catch
            {
            }
        }
    }

    private static void ApplyPlaylistSidebarSecondaryInfoRenderer(PlaylistDetails result, JsonObject renderer)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        if (renderer.ContainsKey("videoOwner"))
        {
            var ownerValue = renderer.GetNamedValue("videoOwner");
            foreach (var obj in EnumerateObjects(ownerValue, 80))
            {
                if (obj.ContainsKey("videoOwnerRenderer"))
                {
                    ApplyVideoOwnerRenderer(result, obj.GetNamedObject("videoOwnerRenderer"));
                    return;
                }
            }
        }

        var ownerText = ExtractFirstOwnerNameFromAnyValue(renderer);
        if (!string.IsNullOrWhiteSpace(ownerText))
        {
            result.OwnerName = PreferExisting(result.OwnerName, ownerText);
        }
    }

    private static void ApplyVideoOwnerRenderer(PlaylistDetails result, JsonObject renderer)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.OwnerName = PreferExisting(result.OwnerName, FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "title"),
            ExtractTextFromField(renderer, "title", string.Empty),
            string.Empty));
        result.OwnerThumbnailUrl = PreferExisting(result.OwnerThumbnailUrl, FirstNonEmpty(
            ExtractFirstUrlFromNamedValue(renderer, "thumbnail"),
            ExtractFirstUrlFromAnyValue(renderer)));
    }

    private static void ApplyPlaylistHeaderRenderer(PlaylistDetails result, JsonObject renderer, List<string> metadataParts)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.Title = PreferExisting(result.Title, FirstNonEmpty(
            ExtractTextFromField(renderer, "title", string.Empty),
            ExtractTextFromNamedValue(renderer, "title")));
        result.Description = PreferExisting(result.Description, FirstNonEmpty(
            ExtractTextFromField(renderer, "descriptionText", string.Empty),
            ExtractTextFromNamedValue(renderer, "descriptionText"),
            ExtractTextFromField(renderer, "description", string.Empty),
            ExtractTextFromNamedValue(renderer, "description")));
        result.OwnerName = PreferExisting(result.OwnerName, FirstNonEmpty(
            ExtractTextFromField(renderer, "ownerText", string.Empty),
            ExtractTextFromNamedValue(renderer, "ownerText"),
            ExtractTextFromField(renderer, "longBylineText", string.Empty),
            ExtractTextFromNamedValue(renderer, "longBylineText"),
            ExtractTextFromField(renderer, "byline", string.Empty),
            ExtractTextFromNamedValue(renderer, "byline")));
        result.ThumbnailUrl = PreferExisting(result.ThumbnailUrl, FirstNonEmpty(
            ExtractBestThumbnailUrl(renderer, "thumbnail"),
            ExtractBestThumbnailUrl(renderer, "playlistHeaderBanner"),
            ExtractFirstUrlFromAnyValue(renderer)));
        result.OwnerThumbnailUrl = PreferExisting(result.OwnerThumbnailUrl, ExtractBestThumbnailUrl(renderer, "ownerThumbnail"));

        AddMetadata(metadataParts, ExtractTextFromField(renderer, "privacy", string.Empty));
        AddMetadata(metadataParts, ExtractTextFromField(renderer, "numVideosText", string.Empty));
        AddMetadata(metadataParts, ExtractTextFromField(renderer, "viewCountText", string.Empty));
        AddMetadata(metadataParts, ExtractTextFromField(renderer, "updatedText", string.Empty));
        AddPlaylistStatsFromAnyText(metadataParts, ExtractTextFromAnyValue(renderer));
    }

    private static void ApplyPlaylistPageHeaderRenderer(PlaylistDetails result, JsonObject renderer, List<string> metadataParts)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.ThumbnailUrl = PreferExisting(result.ThumbnailUrl, FirstNonEmpty(
            ExtractBestThumbnailUrl(renderer, "thumbnail"),
            ExtractFirstUrlFromAnyValue(renderer)));

        if (renderer.ContainsKey("content"))
        {
            var contentValue = renderer.GetNamedValue("content");
            if (contentValue.ValueType == JsonValueType.Object)
            {
                var content = contentValue.GetObject();
                if (content.ContainsKey("pageHeaderViewModel"))
                {
                    ApplyPlaylistPageHeaderViewModel(result, content.GetNamedObject("pageHeaderViewModel"), metadataParts);
                }
            }
        }
    }

    private static void ApplyPlaylistPageHeaderViewModel(PlaylistDetails result, JsonObject renderer, List<string> metadataParts)
    {
        if (result == null || renderer == null)
        {
            return;
        }

        result.Title = PreferExisting(result.Title, FirstNonEmpty(
            ExtractPlaylistTitleFromPageHeader(renderer),
            ExtractTextFromField(renderer, "title", string.Empty),
            ExtractTextFromNamedValue(renderer, "title"),
            ExtractTextFromField(renderer, "pageTitle", string.Empty),
            ExtractTextFromNamedValue(renderer, "pageTitle")));
        result.Description = PreferExisting(result.Description, FirstNonEmpty(
            ExtractTextFromField(renderer, "description", string.Empty),
            ExtractTextFromNamedValue(renderer, "description"),
            ExtractTextFromField(renderer, "descriptionText", string.Empty),
            ExtractTextFromNamedValue(renderer, "descriptionText")));
        result.ThumbnailUrl = PreferExisting(result.ThumbnailUrl, FirstNonEmpty(
            ExtractBestThumbnailUrl(renderer, "image"),
            ExtractBestThumbnailUrl(renderer, "thumbnail"),
            ExtractFirstUrlFromNamedValue(renderer, "image"),
            ExtractFirstUrlFromNamedValue(renderer, "thumbnail"),
            ExtractFirstUrlFromAnyValue(renderer)));
        result.OwnerName = PreferExisting(result.OwnerName, ExtractPlaylistOwnerFromPageHeader(renderer));

        var allText = ExtractTextFromAnyValue(renderer);
        if (result.Title == "Playlist")
        {
            var first = ExtractFirstUsefulLine(allText);
            if (!IsBadPlaylistTitleCandidate(first))
            {
                result.Title = first;
            }
        }
        AddPlaylistStatsFromAnyText(metadataParts, allText);
    }


    private static string ExtractPlaylistTitleFromPageHeader(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        var direct = FirstNonEmpty(
            ExtractTextFromNamedValue(renderer, "pageTitle"),
            ExtractTextFromNamedValue(renderer, "title"),
            GetJsonString(renderer, "pageTitle"),
            GetJsonString(renderer, "title"));
        if (!IsBadPlaylistTitleCandidate(direct))
        {
            return direct;
        }

        foreach (var obj in EnumerateObjects(renderer, 500))
        {
            var candidate = string.Empty;
            if (obj.ContainsKey("dynamicTextViewModel"))
            {
                candidate = ExtractTextFromAnyValue(obj.GetNamedValue("dynamicTextViewModel"));
            }
            else if (obj.ContainsKey("pageTitle"))
            {
                candidate = ExtractTextFromNamedValue(obj, "pageTitle");
            }
            else if (obj.ContainsKey("title"))
            {
                candidate = ExtractTextFromNamedValue(obj, "title");
            }

            if (!IsBadPlaylistTitleCandidate(candidate))
            {
                return NormalizeWhitespace(candidate);
            }
        }

        return string.Empty;
    }

    private static string ExtractPlaylistOwnerFromPageHeader(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(renderer, 700))
        {
            foreach (var key in new[] { "ownerText", "owner", "byline", "subtitle" })
            {
                if (!obj.ContainsKey(key))
                {
                    continue;
                }

                var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                text = NormalizeOwnerCandidate(text);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractFirstOwnerNameFromAnyValue(IJsonValue value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(value, 400))
        {
            foreach (var key in new[] { "title", "ownerText", "author", "byline" })
            {
                if (!obj.ContainsKey(key))
                {
                    continue;
                }

                var text = NormalizeOwnerCandidate(ExtractTextFromAnyValue(obj.GetNamedValue(key)));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeOwnerCandidate(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var lower = value.ToLowerInvariant();
        if (lower == "playlist" || lower == "playlists" || lower == "videos" || lower == "video" ||
            lower == "share" || lower == "save" || lower == "download" || lower == "more" || lower == "play all")
        {
            return string.Empty;
        }

        if (lower.IndexOf(" video", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower.IndexOf(" view", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower.IndexOf("updated", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower == "public" || lower == "private" || lower == "unlisted")
        {
            return string.Empty;
        }

        return value;
    }

    private static bool IsBadPlaylistTitleCandidate(string value)
    {
        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var lower = value.ToLowerInvariant();
        if (lower == "playlist" || lower == "playlists" || lower == "play all" || lower == "shuffle" ||
            lower == "save" || lower == "share" || lower == "download" || lower == "more" || lower == "videos")
        {
            return true;
        }

        if (lower.IndexOf(" video", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower.IndexOf(" view", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower.IndexOf("updated", StringComparison.OrdinalIgnoreCase) >= 0 ||
            lower == "public" || lower == "private" || lower == "unlisted")
        {
            return true;
        }

        return false;
    }

    private static string PreferExisting(string target, string value)
    {
        if ((string.IsNullOrWhiteSpace(target) || target == "Playlist") && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        return target;
    }

    private static void AddMetadata(List<string> list, string value)
    {
        if (list == null || string.IsNullOrWhiteSpace(value) || list.Count >= 4)
        {
            return;
        }

        value = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(value);
    }

    private static string JoinDistinctMetadata(List<string> parts)
    {
        if (parts == null || parts.Count == 0)
        {
            return string.Empty;
        }

        var clean = new List<string>();
        for (var i = 0; i < parts.Count; i++)
        {
            var part = NormalizeWhitespace(parts[i]);
            if (string.IsNullOrWhiteSpace(part))
            {
                continue;
            }

            var exists = false;
            for (var j = 0; j < clean.Count; j++)
            {
                if (string.Equals(clean[j], part, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (!exists)
            {
                clean.Add(part);
            }
        }

        return string.Join(" • ", clean.ToArray());
    }

    private static void AddPlaylistStatsFromAnyText(List<string> metadataParts, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = NormalizeWhitespace(lines[i]);
            var lower = line.ToLowerInvariant();
            if (lower.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0 ||
                lower == "public" || lower == "private" || lower == "unlisted")
            {
                AddMetadata(metadataParts, line);
            }
        }
    }

    private static string ExtractFirstUsefulLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < lines.Length; i++)
        {
            var line = NormalizeWhitespace(lines[i]);
            if (!string.IsNullOrWhiteSpace(line) && line.Length > 1)
            {
                return line;
            }
        }

        return string.Empty;
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var lastWasSpace = false;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                lastWasSpace = false;
            }
        }

        return sb.ToString().Trim();
    }

    public static async Task<VideoDetails> GetVideoDetailsAsync(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[VideoDetails] Fetching details for video: {videoId}");

        var payload = "{\"context\":{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20250101\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}},\"videoId\":\"" + videoId + "\"}";
        var url = "https://www.youtube.com/youtubei/v1/next?key=" + InnertubeApiKey;

        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20250101");
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[VideoDetails] Error: {response.StatusCode} - {errorContent}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            System.Diagnostics.Debug.WriteLine($"[VideoDetails] Got response, length: {json.Length}");
            
            return VideoParser.ParseVideoDetails(json, videoId);
        }
    }

    public static Task<List<CommentItem>> GetCommentsAsync(
        string videoId,
        string continuationToken = null)
    {
        return GetCommentsCoreAsync(videoId, continuationToken, true);
    }

    // A reply continuation is tied to the client context that produced the parent thread.
    // The Video page currently obtains comment threads from WEB, so it must be able to submit
    // that exact token without GetCommentsAsync silently switching it to MWEB authentication.
    public static Task<List<CommentItem>> GetCommentRepliesAsync(
        string videoId,
        string continuationToken,
        bool authenticatedMwebContext)
    {
        if (string.IsNullOrWhiteSpace(continuationToken))
            return Task.FromResult(new List<CommentItem>());

        return GetCommentsCoreAsync(
            videoId, continuationToken, authenticatedMwebContext);
    }

    // Shorts can be returned by a different Innertube client than the watch page. Let that page
    // explicitly keep the initial comment continuation and all reply continuations in one client
    // context; mixing WEB tokens with authenticated MWEB requests produces an empty response.
    public static Task<List<CommentItem>> GetCommentsForClientContextAsync(
        string videoId,
        string continuationToken,
        bool authenticatedMwebContext)
    {
        return GetCommentsCoreAsync(
            videoId, continuationToken, authenticatedMwebContext);
    }

    private static async Task<List<CommentItem>> GetCommentsCoreAsync(
        string videoId,
        string continuationToken,
        bool preferAuthenticatedMweb)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return new List<CommentItem>();
        }

        try
        {
            LoadUserToken();
            var commentsAccessToken = !preferAuthenticatedMweb || string.IsNullOrWhiteSpace(UserToken)
                ? string.Empty
                : await RefreshAccessTokenAsync(UserToken).ConfigureAwait(false);
            var authenticatedComments = !string.IsNullOrWhiteSpace(commentsAccessToken);
            var commentsClientName = authenticatedComments ? "MWEB" : "WEB";
            var commentsClientVersion = authenticatedComments
                ? "2.20251222.01.00"
                : "2.20250101";
            var commentsClientHeader = authenticatedComments ? "2" : "1";
            var commentsUserAgent = authenticatedComments
                ? "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1"
                : "Mozilla/5.0 (Windows NT 10.0; Win64; x64)";
            var commentsPlatform = authenticatedComments
                ? ",\"osName\":\"iOS\",\"osVersion\":\"18\",\"platform\":\"MOBILE\""
                : string.Empty;
            var commentsContext = "{\"client\":{\"clientName\":\""
                + commentsClientName + "\",\"clientVersion\":\""
                + commentsClientVersion + "\",\"hl\":\"" + Hl
                + "\",\"gl\":\"" + Gl + "\"" + commentsPlatform + "}}";

            // Если continuation token не предоставлен, сначала получаем его через /next endpoint
            if (string.IsNullOrEmpty(continuationToken))
            {
                System.Diagnostics.Debug.WriteLine($"[Comments] Fetching continuation token for video: {videoId}");
                
                // Загружаем /next endpoint для получения continuation token
                var nextPayload = "{\"context\":" + commentsContext
                    + ",\"videoId\":\"" + videoId + "\"}";
                nextPayload = ApplySelectedAccountContext(nextPayload, authenticatedComments);
                var nextUrl = "https://www.youtube.com/youtubei/v1/next?key=" + InnertubeApiKey;

                using (var nextRequest = new HttpRequestMessage(HttpMethod.Post, nextUrl))
                {
                    nextRequest.Headers.TryAddWithoutValidation("User-Agent", commentsUserAgent);
                    nextRequest.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                    nextRequest.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", commentsClientHeader);
                    nextRequest.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", commentsClientVersion);
                    if (authenticatedComments)
                    {
                        nextRequest.Headers.TryAddWithoutValidation("Authorization", "Bearer " + commentsAccessToken);
                        nextRequest.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                        nextRequest.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                        nextRequest.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                        ApplySelectedAccountHeader(nextRequest, true);
                    }
                    nextRequest.Content = new StringContent(nextPayload, Encoding.UTF8, "application/json");

                    var nextResponse = await httpClient.SendAsync(nextRequest, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    
                    if (!nextResponse.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine($"[Comments] Error fetching /next: {nextResponse.StatusCode}");
                        return new List<CommentItem>();
                    }

                    var nextJson = await nextResponse.Content.ReadAsStringAsync();
                    
                    // Извлекаем continuation token из ответа
                    continuationToken = ExtractCommentsContinuationToken(nextJson);
                    
                    if (string.IsNullOrEmpty(continuationToken))
                    {
                        System.Diagnostics.Debug.WriteLine("[Comments] No continuation token found");
                        return new List<CommentItem>();
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[Comments] Got continuation token: {continuationToken.Substring(0, Math.Min(50, continuationToken.Length))}...");
                }
            }

            // Теперь загружаем комментарии через continuation endpoint
            System.Diagnostics.Debug.WriteLine("[Comments] Fetching comments with continuation token...");
            
            var payload = "{\"context\":" + commentsContext
                + ",\"continuation\":\"" + continuationToken + "\"}";
            payload = ApplySelectedAccountContext(payload, authenticatedComments);
            var url = "https://www.youtube.com/youtubei/v1/next?key=" + InnertubeApiKey;

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", commentsUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", commentsClientHeader);
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", commentsClientVersion);
                if (authenticatedComments)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + commentsAccessToken);
                    request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                    ApplySelectedAccountHeader(request, true);
                }
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                
                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine($"[Comments] Error: {response.StatusCode} - {errorContent}");
                    return new List<CommentItem>();
                }

                var json = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[Comments] Got response, length: {json.Length}");
                
                return VideoParser.ParseComments(json);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comments] Exception: {ex.Message}");
            return new List<CommentItem>();
        }
    }

    // The Video page already owns the initial /next response. Reuse its parsed root to obtain
    // the comment continuation instead of downloading and parsing the same large response again.
    public static Task<List<CommentItem>> GetCommentsFromNextAsync(
        string videoId,
        JsonObject alreadyLoadedNextRoot)
    {
        return GetCommentsFromNextAsync(videoId, alreadyLoadedNextRoot, true);
    }

    public static Task<List<CommentItem>> GetCommentsFromNextAsync(
        string videoId,
        JsonObject alreadyLoadedNextRoot,
        bool authenticatedMwebContext)
    {
        var continuationToken = ExtractCommentsContinuationToken(alreadyLoadedNextRoot);
        if (string.IsNullOrWhiteSpace(continuationToken))
        {
            System.Diagnostics.Debug.WriteLine(
                "[Comments] Shared /next has no continuation token; refetching compatible context");
            return GetCommentsCoreAsync(videoId, null, authenticatedMwebContext);
        }

        return GetCommentsCoreAsync(
            videoId, continuationToken, authenticatedMwebContext);
    }

    private static string ExtractCommentsContinuationToken(string json)
    {
        try
        {
            JsonObject root;
            if (!FastJson.TryParseObject(json, out root))
            {
                return null;
            }
            return ExtractCommentsContinuationToken(root);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comments] Error extracting continuation token: {ex.Message}");
            return null;
        }
    }

    private static string ExtractCommentsContinuationToken(JsonObject root)
    {
        try
        {
            if (root == null)
            {
                return null;
            }

            // Path: contents.twoColumnWatchNextResults.results.results.contents
            var contents = root.GetNamedObject("contents")
                .GetNamedObject("twoColumnWatchNextResults")
                .GetNamedObject("results")
                .GetNamedObject("results")
                .GetNamedArray("contents");

            // Ищем itemSectionRenderer с sectionIdentifier = "comment-item-section"
            foreach (var itemToken in contents)
            {
                var item = itemToken.GetObject();
                if (item.ContainsKey("itemSectionRenderer"))
                {
                    var renderer = item.GetNamedObject("itemSectionRenderer");
                    var sectionId = renderer.GetNamedString("sectionIdentifier", "");
                    
                    if (sectionId == "comment-item-section")
                    {
                        // Ищем continuationItemRenderer в contents
                        var rendererContents = renderer.GetNamedArray("contents");
                        foreach (var contentToken in rendererContents)
                        {
                            var content = contentToken.GetObject();
                            if (content.ContainsKey("continuationItemRenderer"))
                            {
                                var continuationItem = content.GetNamedObject("continuationItemRenderer");
                                var endpoint = continuationItem.GetNamedObject("continuationEndpoint");
                                var command = endpoint.GetNamedObject("continuationCommand");
                                return command.GetNamedString("token");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comments] Fixed continuation path unavailable: {ex.Message}");
        }

        // YouTube moves the comment section between watch-next layouts. Fall back to one
        // bounded logical walk of the already parsed tree and only accept continuations inside
        // a renderer explicitly identified as comments.
        try
        {
            foreach (var container in EnumerateObjects(root))
            {
                if (!container.ContainsKey("itemSectionRenderer")) continue;
                var section = container.GetNamedObject("itemSectionRenderer");
                var sectionId = FirstNonEmpty(
                    GetJsonString(section, "sectionIdentifier"),
                    GetJsonString(section, "targetId"));
                if (sectionId.IndexOf("comment", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var candidate in EnumerateObjects(section))
                {
                    if (!candidate.ContainsKey("continuationCommand")) continue;
                    var command = candidate.GetNamedObject("continuationCommand");
                    var token = GetJsonString(command, "token");
                    if (!string.IsNullOrWhiteSpace(token)) return token;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comments] Fallback continuation parser failed: {ex.Message}");
        }

        // Authenticated MWEB uses engagementPanelSectionListRenderer rather than the desktop
        // itemSectionRenderer. Accept a continuation only below a container explicitly marked
        // as comments, so unrelated shelves cannot be mistaken for the comment feed.
        try
        {
            foreach (var container in EnumerateObjects(root))
            {
                var marker = FirstNonEmpty(
                    GetJsonString(container, "targetId"),
                    GetJsonString(container, "identifier"),
                    GetJsonString(container, "panelIdentifier"),
                    GetJsonString(container, "sectionIdentifier"));
                if (marker.IndexOf("comment", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                foreach (var candidate in EnumerateObjects(container))
                {
                    if (!candidate.ContainsKey("continuationCommand")) continue;
                    var command = candidate.GetNamedObject("continuationCommand");
                    var token = GetJsonString(command, "token");
                    if (!string.IsNullOrWhiteSpace(token)) return token;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Comments] MWEB continuation parser failed: " + ex.Message);
        }

        return null;
    }


    public static async Task<ShortsFeedResult> GetShortsAsync(string sequenceToken = null)
    {
        var result = new ShortsFeedResult
        {
            Items = new List<ShortsVideoItem>(),
            SequenceToken = string.Empty
        };

        LoadUserToken();
        if (string.IsNullOrWhiteSpace(UserToken))
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] No refresh token available");
            return result;
        }

        var accessToken = await RefreshAccessTokenAsync(UserToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Failed to get access token");
            return result;
        }

        var endpoint = string.IsNullOrWhiteSpace(sequenceToken)
            ? "reel/reel_item_watch"
            : "reel/reel_watch_sequence";

        // Request variants, described rather than pre-rendered: each has to be re-rendered per
        // client, because the body carries the client identity too.
        var seedVariants = new List<KeyValuePair<string, bool>>();
        if (string.IsNullOrWhiteSpace(sequenceToken))
        {
            // Same seedless Shorts request shape used by SymTube / yt-api-legacy.
            // Some InnerTube deployments are picky about params encoding, so keep a tiny fallback list.
            seedVariants.Add(new KeyValuePair<string, bool>("CA8%3D", true));
            seedVariants.Add(new KeyValuePair<string, bool>("CA8=", true));
            seedVariants.Add(new KeyValuePair<string, bool>("CA8%3D", false));
        }
        else
        {
            seedVariants.Add(new KeyValuePair<string, bool>(null, true));
        }

        for (var i = 0; i < seedVariants.Count; i++)
        {
            var variant = seedVariants[i];
            var quiet = endpoint == "reel/reel_watch_sequence" || endpoint == "reel/reel_item_watch";

            // Whether the Shorts feed is personalized hinges on one unknown: which client, if any,
            // these endpoints accept our OAuth bearer from. The bearer is minted for the TV client,
            // and a body/header client mismatch is what makes them answer 400 — so each client is
            // tried with a matching body, best candidate first, and every outcome is logged.
            string json = null;
            var authorizedClient = string.Empty;

            for (var c = 0; c < ShortsAuthClients.Length; c++)
            {
                var client = ShortsAuthClients[c];
                var attemptPayload = string.IsNullOrWhiteSpace(sequenceToken)
                    ? BuildSeedlessShortsPayload(client, variant.Key, variant.Value)
                    : BuildShortsSequencePayload(client, sequenceToken);

                var attempt = await PostInnertubeJsonAsync(
                    endpoint, attemptPayload, accessToken, client, ShortsClientVersion(client), quiet
                );

                if (!string.IsNullOrWhiteSpace(attempt))
                {
                    json = attempt;
                    authorizedClient = client;
                    System.Diagnostics.Debug.WriteLine(
                        "[Shorts][Auth] ACCEPTED: " + endpoint + " answered the AUTHORIZED request as "
                        + client + " (payload #" + (i + 1) + ")"
                    );
                    break;
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Shorts][Auth] rejected as " + client + " (" + endpoint + ", payload #" + (i + 1) + ")"
                );
            }

            var authorized = !string.IsNullOrWhiteSpace(json);
            if (!authorized)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Shorts][Auth] REJECTED by every client; retrying anonymously — this feed will NOT be personalized"
                );

                var anonPayload = string.IsNullOrWhiteSpace(sequenceToken)
                    ? BuildSeedlessShortsPayload("WEB", variant.Key, variant.Value)
                    : BuildShortsSequencePayload("WEB", sequenceToken);

                json = await PostInnertubeJsonAsync(
                    endpoint, anonPayload, string.Empty, "WEB", ShortsWebClientVersion, quiet
                );

                System.Diagnostics.Debug.WriteLine(
                    "[Shorts][Auth] anonymous retry " + (string.IsNullOrWhiteSpace(json) ? "also failed" : "succeeded")
                );
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                continue;
            }

            result.SequenceToken = ExtractShortsSequenceToken(json);
            result.Items = await ParseShortsResponseAsync(json, accessToken);

            System.Diagnostics.Debug.WriteLine(
                "[Shorts] Parsed " + result.Items.Count + " items. Continuation: "
                + (!string.IsNullOrWhiteSpace(result.SequenceToken)) + ", payload #" + (i + 1)
                + ", source=" + (authorized ? "AUTHORIZED as " + authorizedClient : "ANONYMOUS")
            );
            if (result.Items.Count > 0 || !string.IsNullOrWhiteSpace(result.SequenceToken))
            {
                return result;
            }
        }

        if (!string.IsNullOrWhiteSpace(sequenceToken))
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] reel_watch_sequence was rejected; retrying seedless Shorts feed.");
            return await GetShortsAsync(null);
        }

        System.Diagnostics.Debug.WriteLine("[Shorts] No shorts were parsed after all request variants.");
        return result;
    }

    public static async Task<string> GetShortPlaybackUrlAsync(string videoId)
    {
        LoadUserToken();
        var accessToken = string.IsNullOrWhiteSpace(UserToken)
            ? string.Empty
            : await RefreshAccessTokenAsync(UserToken);

        return await GetShortPlaybackUrlAsync(videoId, accessToken);
    }

    public static async Task<bool> RateVideoAsync(string videoId, string rating)
    {
        return await SetVideoRatingAsync(videoId, rating);
    }

    public static async Task<bool> SetVideoRatingAsync(string videoId, string rating)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return false;
        }

        LoadUserToken();
        if (string.IsNullOrWhiteSpace(UserToken))
        {
            return false;
        }

        var accessToken = await RefreshAccessTokenAsync(UserToken);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var normalized = string.IsNullOrWhiteSpace(rating) ? "none" : rating.Trim().ToLowerInvariant();
        string endpoint;
        if (normalized == "like")
        {
            endpoint = "like/like";
        }
        else if (normalized == "dislike")
        {
            endpoint = "like/dislike";
        }
        else
        {
            endpoint = "like/removelike";
        }

        // TubeReplacer builds likes with the mobile web client and sends the same
        // target.videoId body to like/like or like/removelike. We keep that shape
        // and add like/dislike for the Shorts dislike button.
        var payload = "{\"context\":{" + BuildWebMobileClientJson() + "},\"target\":{\"videoId\":\"" + JsonEscape(videoId) + "\"}}";
        var json = await PostInnertubeJsonAsync(endpoint, payload, accessToken, "MWEB", "2.20251222.01.00");

        if (string.IsNullOrWhiteSpace(json) && normalized == "dislike")
        {
            // Some account states reject dislike from MWEB; WEB is a safe fallback.
            payload = "{\"context\":{" + BuildWebClientJson() + "},\"target\":{\"videoId\":\"" + JsonEscape(videoId) + "\"}}";
            json = await PostInnertubeJsonAsync(endpoint, payload, accessToken, "WEB", "2.20250101");
        }

        return !string.IsNullOrWhiteSpace(json);
    }

    private static async Task<List<ShortsVideoItem>> ParseShortsResponseAsync(string json, string accessToken)
    {
        var result = new List<ShortsVideoItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var root = JsonValue.Parse(json);
            var entries = ExtractShortsEntries(root);

            // Last-resort fallback for unusual responses: keep the old wide search,
            // but prefer entry-based parsing because it keeps metadata attached to
            // the correct reelWatchEndpoint.
            if (entries.Count == 0)
            {
                foreach (var obj in EnumerateObjects(root))
                {
                    if (obj.ContainsKey("reelWatchEndpoint"))
                    {
                        entries.Add(obj);
                    }
                }
            }

            foreach (var entry in entries)
            {
                var endpoint = ExtractReelWatchEndpoint(entry);
                if (endpoint == null)
                {
                    continue;
                }

                var videoId = GetJsonString(endpoint, "videoId");
                if (string.IsNullOrWhiteSpace(videoId) || seen.Contains(videoId))
                {
                    continue;
                }

                var item = new ShortsVideoItem
                {
                    VideoId = videoId,
                    Title = "Shorts",
                    ChannelName = string.Empty,
                    ChannelThumbnailUrl = string.Empty,
                    ThumbnailUrl = BuildHqThumbnailUrl(videoId),
                    VideoUrl = string.Empty,
                    LikeCount = string.Empty,
                    CommentCount = string.Empty,
                    IsLiked = false,
                    IsDisliked = false
                };

                ApplyEndpointDataToShort(item, endpoint);

                var playerResponse = ExtractPlayerResponseObject(endpoint) ?? ExtractPlayerResponseObject(entry);
                if (playerResponse != null)
                {
                    ApplyPlayerResponseToShort(item, playerResponse);
                }

                ApplyShortsUiMetadata(item, entry, endpoint, playerResponse);

                seen.Add(videoId);
                result.Add(item);
            }

            // The per-item player request is a network round trip, and the TV feed omits enough
            // that nearly every item needs one. Running them one after another meant the user
            // waited out eight sequential requests before the feed could be swiped at all. They
            // are independent of each other, so run them together — capped, so a phone is not
            // firing an unbounded number of requests at once.
            var pending = new List<ShortsVideoItem>();
            for (var i = 0; i < result.Count; i++)
            {
                var candidate = result[i];
                if (string.IsNullOrWhiteSpace(candidate.VideoUrl)
                    || candidate.Title == "Shorts"
                    || string.IsNullOrWhiteSpace(candidate.ChannelName))
                {
                    pending.Add(candidate);
                }
            }

            // Only the first Short is visible on a phone. Resolving every item before returning
            // made initial navigation wait for several player requests; the Shorts page already
            // resolves an item's URL lazily when the user swipes to it.
            if (global::YouTube.ResponsiveLayout.IsPhoneDevice && pending.Count > 1)
                pending.RemoveRange(1, pending.Count - 1);

            const int MaxParallelShortLookups = 4;
            for (var start = 0; start < pending.Count; start += MaxParallelShortLookups)
            {
                var batch = new List<Task>();
                for (var i = start; i < pending.Count && i < start + MaxParallelShortLookups; i++)
                {
                    batch.Add(PopulateShortPlaybackInfoAsync(pending[i], accessToken));
                }

                await Task.WhenAll(batch);
            }

            for (var i = 0; i < result.Count; i++)
            {
                var parsed = result[i];
                System.Diagnostics.Debug.WriteLine("[Shorts] Item parsed: id=" + parsed.VideoId +
                    ", title=" + parsed.Title +
                    ", channel=" + parsed.ChannelName +
                    ", likes=" + parsed.LikeCount +
                    ", comments=" + parsed.CommentCount);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Parse response error: " + ex.Message);
        }

        return result;
    }

    private static List<JsonObject> ExtractShortsEntries(IJsonValue root)
    {
        var entries = new List<JsonObject>();

        try
        {
            if (root == null || root.ValueType != JsonValueType.Object)
            {
                return entries;
            }

            var rootObj = root.GetObject();
            if (rootObj.ContainsKey("entries"))
            {
                var entriesValue = rootObj.GetNamedValue("entries");
                if (entriesValue.ValueType == JsonValueType.Array)
                {
                    var arr = entriesValue.GetArray();
                    for (var i = 0; i < arr.Count; i++)
                    {
                        if (arr[i].ValueType == JsonValueType.Object)
                        {
                            entries.Add(arr[i].GetObject());
                        }
                    }
                }
            }

            if (entries.Count == 0 && rootObj.ContainsKey("replacementEndpoint"))
            {
                entries.Add(rootObj);
            }
        }
        catch
        {
        }

        return entries;
    }

    private static JsonObject ExtractReelWatchEndpoint(JsonObject entry)
    {
        if (entry == null)
        {
            return null;
        }

        try
        {
            if (entry.ContainsKey("reelWatchEndpoint"))
            {
                var value = entry.GetNamedValue("reelWatchEndpoint");
                if (value.ValueType == JsonValueType.Object)
                {
                    return value.GetObject();
                }
            }

            if (entry.ContainsKey("command"))
            {
                var commandValue = entry.GetNamedValue("command");
                if (commandValue.ValueType == JsonValueType.Object)
                {
                    var command = commandValue.GetObject();
                    if (command.ContainsKey("reelWatchEndpoint"))
                    {
                        var endpointValue = command.GetNamedValue("reelWatchEndpoint");
                        if (endpointValue.ValueType == JsonValueType.Object)
                        {
                            return endpointValue.GetObject();
                        }
                    }
                }
            }

            if (entry.ContainsKey("replacementEndpoint"))
            {
                var replacementValue = entry.GetNamedValue("replacementEndpoint");
                if (replacementValue.ValueType == JsonValueType.Object)
                {
                    var replacement = replacementValue.GetObject();
                    if (replacement.ContainsKey("reelWatchEndpoint"))
                    {
                        var endpointValue = replacement.GetNamedValue("reelWatchEndpoint");
                        if (endpointValue.ValueType == JsonValueType.Object)
                        {
                            return endpointValue.GetObject();
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void ApplyShortsUiMetadata(ShortsVideoItem item, JsonObject entry, JsonObject endpoint, JsonObject playerResponse)
    {
        if (item == null)
        {
            return;
        }

        var overlay = FindFirstObjectWithKey(entry, "reelPlayerOverlayRenderer");
        if (overlay != null && overlay.ContainsKey("reelPlayerOverlayRenderer"))
        {
            var overlayValue = overlay.GetNamedValue("reelPlayerOverlayRenderer");
            if (overlayValue.ValueType == JsonValueType.Object)
            {
                overlay = overlayValue.GetObject();
            }
        }

        var title = FirstNonEmpty(
            ExtractTitleFromShortsOverlay(overlay),
            ExtractTitleFromShortsOverlay(entry),
            ExtractTitleFromShortsOverlay(endpoint));
        if (!string.IsNullOrWhiteSpace(title) && !IsBadShortsMetadataText(title) &&
            (string.IsNullOrWhiteSpace(item.Title) || item.Title == "Shorts" || item.Title == "YouTube Short"))
        {
            item.Title = title;
        }

        var channelName = FirstNonEmpty(
            ExtractChannelNameFromShortsOverlay(overlay),
            ExtractChannelNameFromShortsOverlay(entry),
            ExtractChannelNameFromShortsOverlay(endpoint));
        if (!string.IsNullOrWhiteSpace(channelName) && !IsBadShortsMetadataText(channelName) &&
            string.IsNullOrWhiteSpace(item.ChannelName))
        {
            item.ChannelName = channelName;
        }

        var channelThumb = FirstNonEmpty(
            ExtractThumbnailFromFirstObjectWithKey(overlay, "reelPlayerBylineRenderer"),
            ExtractThumbnailFromFirstObjectWithKey(entry, "reelPlayerBylineRenderer"),
            ExtractThumbnailFromFirstObjectWithKey(entry, "channelThumbnail"),
            ExtractThumbnailFromFirstObjectWithKey(entry, "avatar"),
            ExtractThumbnailFromFirstObjectWithKey(entry, "authorThumbnail"));
        if (!string.IsNullOrWhiteSpace(channelThumb))
        {
            item.ChannelThumbnailUrl = channelThumb;
        }

        var likeCount = FirstNonEmpty(
            ExtractEngagementCount(entry, "like"),
            ExtractEngagementCount(endpoint, "like"));
        if (!string.IsNullOrWhiteSpace(likeCount))
        {
            item.LikeCount = likeCount;
        }

        var commentCount = FirstNonEmpty(
            ExtractEngagementCount(entry, "comment"),
            ExtractEngagementCount(endpoint, "comment"));
        if (!string.IsNullOrWhiteSpace(commentCount))
        {
            item.CommentCount = commentCount;
        }

        var ratingState = ExtractShortsRatingState(overlay, entry, endpoint);
        if (ratingState == "like")
        {
            item.IsLiked = true;
            item.IsDisliked = false;
        }
        else if (ratingState == "dislike")
        {
            item.IsLiked = false;
            item.IsDisliked = true;
        }

        // If the overlay does not include the public title, fall back to the
        // prefetched playerResponse, then to the video id thumbnail only.
        if ((string.IsNullOrWhiteSpace(item.Title) || item.Title == "Shorts") && playerResponse != null)
        {
            if (playerResponse.ContainsKey("videoDetails"))
            {
                var details = playerResponse.GetNamedObject("videoDetails");
                var playerTitle = GetJsonString(details, "title");
                if (!string.IsNullOrWhiteSpace(playerTitle))
                {
                    item.Title = playerTitle;
                }
            }
        }
    }

    private static string ExtractTitleFromShortsOverlay(JsonObject root)
    {
        if (root == null)
        {
            return string.Empty;
        }

        var titleRendererWrapper = FindFirstObjectWithKey(root, "reelTitleRenderer");
        if (titleRendererWrapper != null && titleRendererWrapper.ContainsKey("reelTitleRenderer"))
        {
            var rendererValue = titleRendererWrapper.GetNamedValue("reelTitleRenderer");
            if (rendererValue.ValueType == JsonValueType.Object)
            {
                var renderer = rendererValue.GetObject();
                var text = FirstNonEmpty(
                    ExtractTextFromField(renderer, "text", string.Empty),
                    ExtractTextFromField(renderer, "title", string.Empty),
                    ExtractTextFromAnyValue(renderer));
                if (!string.IsNullOrWhiteSpace(text) && !IsBadShortsMetadataText(text))
                {
                    return text;
                }
            }
        }

        foreach (var obj in EnumerateObjects(root))
        {
            var text = FirstNonEmpty(
                ExtractTextFromField(obj, "headline", string.Empty),
                ExtractTextFromField(obj, "videoTitle", string.Empty),
                ExtractTextFromField(obj, "title", string.Empty));
            if (!string.IsNullOrWhiteSpace(text) && !IsBadShortsMetadataText(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ExtractChannelNameFromShortsOverlay(JsonObject root)
    {
        if (root == null)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "reelPlayerBylineRenderer", "shortBylineText", "ownerText", "longBylineText", "author", "channelName" })
        {
            var wrapper = FindFirstObjectWithKey(root, key);
            if (wrapper == null)
            {
                continue;
            }

            if (wrapper.ContainsKey(key))
            {
                var value = wrapper.GetNamedValue(key);
                var text = ExtractTextFromAnyValue(value);
                if (!string.IsNullOrWhiteSpace(text) && !IsBadShortsMetadataText(text))
                {
                    return text;
                }
            }
        }

        foreach (var obj in EnumerateObjects(root))
        {
            var text = FirstNonEmpty(
                GetJsonString(obj, "ownerChannelName"),
                GetJsonString(obj, "author"),
                ExtractTextFromField(obj, "shortBylineText", string.Empty),
                ExtractTextFromField(obj, "ownerText", string.Empty),
                ExtractTextFromField(obj, "channelName", string.Empty));
            if (!string.IsNullOrWhiteSpace(text) && !IsBadShortsMetadataText(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ExtractShortsRatingState(params JsonObject[] roots)
    {
        if (roots == null)
        {
            return string.Empty;
        }

        for (var i = 0; i < roots.Length; i++)
        {
            var root = roots[i];
            if (root == null)
            {
                continue;
            }

            foreach (var obj in EnumerateObjects(root))
            {
                var direct = ExtractDirectRatingStatus(obj);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct;
                }

                foreach (var key in new[] { "dislikeButton", "dislikeButtonRenderer", "dislikeButtonViewModel" })
                {
                    if (obj.ContainsKey(key) && ValueContainsToggledState(obj.GetNamedValue(key)))
                    {
                        return "dislike";
                    }
                }

                foreach (var key in new[] { "likeButton", "likeButtonRenderer", "likeButtonViewModel" })
                {
                    if (obj.ContainsKey(key) && ValueContainsToggledState(obj.GetNamedValue(key)))
                    {
                        return "like";
                    }
                }

                if (ValueContainsToggledState(obj))
                {
                    if (ObjectLooksLikeReaction(obj, "dislike"))
                    {
                        return "dislike";
                    }

                    if (ObjectLooksLikeReaction(obj, "like"))
                    {
                        return "like";
                    }
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractDirectRatingStatus(JsonObject obj)
    {
        if (obj == null)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "likeStatus", "ratingStatus", "status", "feedbackState" })
        {
            if (!obj.ContainsKey(key))
            {
                continue;
            }

            var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var lower = text.ToLowerInvariant();
            if (lower.Contains("dislike"))
            {
                return "dislike";
            }

            if (lower == "like" || lower.Contains("liked"))
            {
                return "like";
            }
        }

        return string.Empty;
    }

    private static bool ValueContainsToggledState(IJsonValue value)
    {
        if (value == null)
        {
            return false;
        }

        try
        {
            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                foreach (var key in new[] { "isToggled", "toggled", "isSelected", "selected", "isActive", "hasSelectedState" })
                {
                    if (obj.ContainsKey(key))
                    {
                        var flag = obj.GetNamedValue(key);
                        if (flag.ValueType == JsonValueType.Boolean && flag.GetBoolean())
                        {
                            return true;
                        }
                    }
                }

                foreach (var key in new[] { "state", "buttonState", "toggleState" })
                {
                    if (obj.ContainsKey(key))
                    {
                        var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            var lower = text.ToLowerInvariant();
                            if (lower.Contains("selected") || lower.Contains("toggled") || lower.Contains("active") || lower.Contains("on"))
                            {
                                return true;
                            }
                        }
                    }
                }

                foreach (var item in obj)
                {
                    if (ValueContainsToggledState(item.Value))
                    {
                        return true;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (var i = 0; i < arr.Count; i++)
                {
                    if (ValueContainsToggledState(arr[i]))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static bool ObjectLooksLikeReaction(JsonObject obj, string kind)
    {
        if (obj == null)
        {
            return false;
        }

        var text = ExtractTextFromAnyValue(obj);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        if (kind == "dislike")
        {
            return lower.Contains("dislike") || lower.Contains("не нравится");
        }

        return (lower.Contains("like") || lower.Contains("нравится")) && !lower.Contains("dislike") && !lower.Contains("не нравится");
    }

    // /next reports the like count as a bare number ("341683") while the Shorts feed reports it
    // already localized and shortened ("202 т"). Shorten the bare ones so the counter does not
    // change shape from one short to the next. Anything containing letters is left alone —
    // it is already a formatted, localized string.
    public static string FormatCompactCount(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var value = raw.Trim();
        var digits = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (char.IsLetter(ch))
            {
                return value;
            }

            if (ch >= '0' && ch <= '9')
            {
                digits.Append(ch);
            }
        }

        ulong number;
        if (digits.Length == 0
            || !ulong.TryParse(digits.ToString(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out number))
        {
            return value;
        }

        // Suffixes follow the device language, so the counter reads the same way as everywhere
        // else on the phone. Only the languages the UI actually ships are special-cased; anything
        // else falls back to the international K/M/B rather than showing a wrong word.
        var russian = !string.IsNullOrWhiteSpace(Hl)
            && Hl.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

        if (number >= 1000000000UL)
        {
            return FormatCountUnit(number / 1000000000.0) + (russian ? " млрд" : "B");
        }
        if (number >= 1000000UL)
        {
            return FormatCountUnit(number / 1000000.0) + (russian ? " млн" : "M");
        }
        if (number >= 1000UL)
        {
            return FormatCountUnit(number / 1000.0) + (russian ? " тыс." : "K");
        }

        return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatCountUnit(double value)
    {
        var rounded = Math.Round(value, 1);
        if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
        {
            return Math.Round(rounded).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return rounded.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    }

    // The channel avatar is missing from the TV Shorts feed for the same reason the like count
    // is, and it lives in the /next response the page already fetches — so no extra request.
    // Largest variant wins: the circle is small but on a high-DPI phone the 48px one is mushy.
    public static string ExtractChannelAvatarFromNextJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            var root = JsonValue.Parse(json);
            if (root.ValueType != JsonValueType.Object)
            {
                return string.Empty;
            }

            var owner = FindFirstObjectWithKey(root.GetObject(), "videoOwnerRenderer");
            if (owner == null)
            {
                return string.Empty;
            }

            var renderer = owner.GetNamedObject("videoOwnerRenderer");
            if (!renderer.ContainsKey("thumbnail"))
            {
                return string.Empty;
            }

            var thumbnail = renderer.GetNamedObject("thumbnail");
            if (!thumbnail.ContainsKey("thumbnails"))
            {
                return string.Empty;
            }

            var thumbnails = thumbnail.GetNamedArray("thumbnails");
            var best = string.Empty;
            double bestWidth = -1;

            for (uint i = 0; i < thumbnails.Count; i++)
            {
                var entry = thumbnails.GetObjectAt(i);
                var url = GetJsonString(entry, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                double width = 0;
                if (entry.ContainsKey("width"))
                {
                    try { width = entry.GetNamedNumber("width"); }
                    catch { width = 0; }
                }

                if (width > bestWidth)
                {
                    bestWidth = width;
                    best = url;
                }
            }

            return best;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Channel avatar parse failed: " + ex.Message);
            return string.Empty;
        }
    }

    // The TV Shorts sequence returns a stripped reelPlayerOverlayRenderer with no engagement
    // counts at all, so the like count has to come from the /next response the Shorts page
    // already fetches for the rating state — no extra request.
    public static string ExtractLikeCountFromNextJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            var root = JsonValue.Parse(json);
            if (root.ValueType != JsonValueType.Object)
            {
                return string.Empty;
            }

            return ExtractEngagementCount(root.GetObject(), "like");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Like count parse failed: " + ex.Message);
            return string.Empty;
        }
    }

    private static string ExtractEngagementCount(JsonObject root, string kind)
    {
        if (root == null)
        {
            return string.Empty;
        }

        var keys = kind == "comment"
            ? new[] { "viewCommentsButton", "commentsButton", "commentButton", "commentCount", "commentCountText", "commentsCountText", "commentsCount" }
            : new[] { "likeButton", "likeButtonRenderer", "likeButtonViewModel", "segmentedLikeDislikeButtonRenderer", "likeCount", "likeCountText" };

        foreach (var key in keys)
        {
            var wrapper = FindFirstObjectWithKey(root, key);
            if (wrapper == null)
            {
                continue;
            }

            var text = ExtractBestCountText(wrapper, kind);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string ExtractBestCountText(JsonObject root, string kind)
    {
        if (root == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(root))
        {
            foreach (var key in new[] { "likeCount", "likeCountText", "commentCount", "commentCountText", "commentsCount", "commentsCountText", "title", "text", "label", "accessibilityText", "defaultText", "toggledText" })
            {
                if (!obj.ContainsKey(key))
                {
                    continue;
                }

                var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                var count = NormalizeEngagementCount(text, kind);
                if (!string.IsNullOrWhiteSpace(count))
                {
                    return count;
                }
            }

            if (obj.ContainsKey("accessibilityData"))
            {
                var count = NormalizeEngagementCount(ExtractTextFromAnyValue(obj.GetNamedValue("accessibilityData")), kind);
                if (!string.IsNullOrWhiteSpace(count))
                {
                    return count;
                }
            }
        }

        return string.Empty;
    }

    private static string NormalizeEngagementCount(string text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Trim();
        value = value.Replace("\r", " ").Replace("\n", " ").Replace("  ", " ").Trim();

        var lower = value.ToLowerInvariant();
        if (kind == "like")
        {
            if (lower == "like" || lower == "likes" || lower.Contains("dislike") || lower.Contains("i like this"))
            {
                return string.Empty;
            }
        }
        else
        {
            if (lower == "comment" || lower == "comments" || lower.Contains("add a comment"))
            {
                return string.Empty;
            }
        }

        // Accessibility labels are often "12,345 likes" / "1.2M comments".
        var keywords = kind == "comment"
            ? new[] { " comments", " comment", " коммент", " комментар" }
            : new[] { " likes", " like", " отмет", " нравится" };

        foreach (var keyword in keywords)
        {
            var index = lower.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var prefix = value.Substring(0, index).Trim();
                var compact = CleanCompactCount(prefix);
                if (!string.IsNullOrWhiteSpace(compact))
                {
                    return compact;
                }
            }
        }

        return CleanCompactCount(value);
    }

    private static string CleanCompactCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var value = text.Trim();
        var lower = value.ToLowerInvariant();
        if (lower.Contains("http") || lower.Contains("short") || lower.Contains("subscribe") || lower.Contains("share") || lower.Contains("reply"))
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsDigit(c) || c == '.' || c == ',' || c == ' ' || c == '\u00A0' || c == 'K' || c == 'k' || c == 'M' || c == 'm' || c == 'B' || c == 'b' || c == '万' || c == '億' || c == 'т' || c == 'м')
            {
                sb.Append(c);
            }
            else if (sb.Length > 0)
            {
                break;
            }
        }

        var count = sb.ToString().Trim();
        count = count.Replace("\u00A0", " ").Trim();
        if (string.IsNullOrWhiteSpace(count))
        {
            return string.Empty;
        }

        var hasDigit = false;
        for (var i = 0; i < count.Length; i++)
        {
            if (char.IsDigit(count[i]))
            {
                hasDigit = true;
                break;
            }
        }

        if (!hasDigit)
        {
            return string.Empty;
        }

        return count;
    }

    private static string ExtractThumbnailFromFirstObjectWithKey(JsonObject root, string key)
    {
        var wrapper = FindFirstObjectWithKey(root, key);
        if (wrapper == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(wrapper))
        {
            foreach (var field in new[] { "thumbnail", "avatar", "image", "authorThumbnail", "channelThumbnail" })
            {
                var url = ExtractThumbnailUrl(obj, field);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }
        }

        return string.Empty;
    }

    private static JsonObject FindFirstObjectWithKey(JsonObject root, string key)
    {
        if (root == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        foreach (var obj in EnumerateObjects(root))
        {
            if (obj.ContainsKey(key))
            {
                return obj;
            }
        }

        return null;
    }

    private static string ExtractTextFromAnyValue(IJsonValue value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        try
        {
            if (value.ValueType == JsonValueType.String)
            {
                return value.GetString().Trim();
            }

            if (value.ValueType == JsonValueType.Number)
            {
                return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey("simpleText"))
                {
                    return GetJsonString(obj, "simpleText");
                }

                if (obj.ContainsKey("content"))
                {
                    var contentText = ExtractTextFromAnyValue(obj.GetNamedValue("content"));
                    if (!string.IsNullOrWhiteSpace(contentText))
                    {
                        return contentText;
                    }
                }

                if (obj.ContainsKey("label"))
                {
                    var labelText = ExtractTextFromAnyValue(obj.GetNamedValue("label"));
                    if (!string.IsNullOrWhiteSpace(labelText))
                    {
                        return labelText;
                    }
                }

                if (obj.ContainsKey("accessibilityData"))
                {
                    var accessibilityText = ExtractTextFromAnyValue(obj.GetNamedValue("accessibilityData"));
                    if (!string.IsNullOrWhiteSpace(accessibilityText))
                    {
                        return accessibilityText;
                    }
                }

                if (obj.ContainsKey("runs"))
                {
                    var runsValue = obj.GetNamedValue("runs");
                    if (runsValue.ValueType == JsonValueType.Array)
                    {
                        var runs = runsValue.GetArray();
                        var sb = new StringBuilder();
                        for (var i = 0; i < runs.Count; i++)
                        {
                            if (runs[i].ValueType == JsonValueType.Object)
                            {
                                var run = runs[i].GetObject();
                                var text = GetJsonString(run, "text");
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    sb.Append(text);
                                }
                            }
                        }

                        var valueText = sb.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(valueText))
                        {
                            return valueText;
                        }
                    }
                }

                foreach (var key in new[] { "text", "title", "subtitle", "name", "value", "accessibilityText" })
                {
                    if (obj.ContainsKey(key))
                    {
                        var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            return text;
                        }
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                var sb = new StringBuilder();
                for (var i = 0; i < arr.Count; i++)
                {
                    var text = ExtractTextFromAnyValue(arr[i]);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                    }
                }

                return sb.ToString().Trim();
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsBadShortsMetadataText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var value = text.Trim();
        var lower = value.ToLowerInvariant();
        if (lower == "shorts" || lower == "like" || lower == "dislike" || lower == "comments" || lower == "comment" || lower == "share" || lower == "send" || lower == "subscribe")
        {
            return true;
        }

        if (lower.Contains("sign in") || lower.Contains("add a comment") || lower.Contains("reply"))
        {
            return true;
        }

        var compact = CleanCompactCount(value);
        if (!string.IsNullOrWhiteSpace(compact) && string.Equals(compact, value, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static void ApplyEndpointDataToShort(ShortsVideoItem item, JsonObject endpoint)
    {
        if (item == null || endpoint == null)
        {
            return;
        }

        if (endpoint.ContainsKey("title"))
        {
            var title = ExtractTextFromField(endpoint, "title", string.Empty);
            if (!string.IsNullOrWhiteSpace(title))
            {
                item.Title = title;
            }
        }

        if (endpoint.ContainsKey("thumbnail"))
        {
            var thumbnail = ExtractThumbnailUrl(endpoint, "thumbnail");
            if (!string.IsNullOrWhiteSpace(thumbnail))
            {
                item.ThumbnailUrl = thumbnail;
            }
        }
    }

    private static async Task<string> GetShortPlaybackUrlAsync(string videoId, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return string.Empty;
        }

        // IMPORTANT: /player is intentionally requested without OAuth first.
        // The reel endpoints require the signed-in gate in our UI, but YouTube's
        // ANDROID player endpoint often rejects Bearer tokens with 400 INVALID_ARGUMENT.
        var androidPayload = "{\"context\":{" + BuildAndroidClientJson() + "},\"videoId\":\"" + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}";
        var json = await PostInnertubeJsonAsync("player", androidPayload, string.Empty, "ANDROID", "20.10.38");
        var videoUrl = ExtractPlaybackUrlFromPlayerJson(videoId, json, "ANDROID anonymous");
        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            return videoUrl;
        }

        // Web fallback. This commonly returns an HLS manifest, which CustomShortsPlayer can play.
        var webPayload = "{\"context\":{" + BuildShortsWebClientJson() + "},\"videoId\":\"" + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true,\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\"}}}";
        json = await PostInnertubeJsonAsync("player", webPayload, string.Empty, "WEB", ShortsWebClientVersion);
        videoUrl = ExtractPlaybackUrlFromPlayerJson(videoId, json, "WEB anonymous");
        if (!string.IsNullOrWhiteSpace(videoUrl))
        {
            return videoUrl;
        }

        // Last resort: try the signed WEB player. Some account/region states need it.
        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            json = await PostInnertubeJsonAsync("player", webPayload, accessToken, "WEB", ShortsWebClientVersion);
            videoUrl = ExtractPlaybackUrlFromPlayerJson(videoId, json, "WEB authorized");
            if (!string.IsNullOrWhiteSpace(videoUrl))
            {
                return videoUrl;
            }
        }

        return string.Empty;
    }

    private static async Task PopulateShortPlaybackInfoAsync(ShortsVideoItem item, string accessToken)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
        {
            return;
        }

        var androidPayload = "{\"context\":{" + BuildAndroidClientJson() + "},\"videoId\":\"" + JsonEscape(item.VideoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}";
        var json = await PostInnertubeJsonAsync("player", androidPayload, string.Empty, "ANDROID", "20.10.38");
        if (ApplyPlayerJsonToShort(item, json, "ANDROID anonymous"))
        {
            return;
        }

        var webPayload = "{\"context\":{" + BuildShortsWebClientJson() + "},\"videoId\":\"" + JsonEscape(item.VideoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true,\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\"}}}";
        json = await PostInnertubeJsonAsync("player", webPayload, string.Empty, "WEB", ShortsWebClientVersion);
        if (ApplyPlayerJsonToShort(item, json, "WEB anonymous"))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            json = await PostInnertubeJsonAsync("player", webPayload, accessToken, "WEB", ShortsWebClientVersion);
            ApplyPlayerJsonToShort(item, json, "WEB authorized");
        }
    }

    private static bool ApplyPlayerJsonToShort(ShortsVideoItem item, string json, string sourceName)
    {
        if (item == null || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            var root = JsonObject.Parse(json);
            ApplyPlayerResponseToShort(item, root);
            if (!string.IsNullOrWhiteSpace(item.VideoUrl))
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Player info applied for " + item.VideoId + " via " + sourceName);
                return true;
            }

            if (root.ContainsKey("playabilityStatus"))
            {
                var status = root.GetNamedObject("playabilityStatus");
                System.Diagnostics.Debug.WriteLine("[Shorts] Player has no playable URL for " + item.VideoId + " via " + sourceName + ": " + GetJsonString(status, "status") + " " + GetJsonString(status, "reason"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Player apply error for " + item.VideoId + " via " + sourceName + ": " + ex.Message);
        }

        return false;
    }

    private static string ExtractPlaybackUrlFromPlayerJson(string videoId, string json, string sourceName)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            var root = JsonObject.Parse(json);
            var temp = new ShortsVideoItem { VideoId = videoId };
            ApplyPlayerResponseToShort(temp, root);

            if (!string.IsNullOrWhiteSpace(temp.VideoUrl))
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Player URL selected for " + videoId + " via " + sourceName);
                return temp.VideoUrl;
            }

            if (root.ContainsKey("playabilityStatus"))
            {
                var status = root.GetNamedObject("playabilityStatus");
                System.Diagnostics.Debug.WriteLine("[Shorts] Player has no playable URL for " + videoId + " via " + sourceName + ": " + GetJsonString(status, "status") + " " + GetJsonString(status, "reason"));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Player parse error for " + videoId + " via " + sourceName + ": " + ex.Message);
        }

        return string.Empty;
    }

    private static async Task<string> PostInnertubeJsonAsync(string endpoint, string payload, string accessToken, string clientName, string clientVersion, bool quietFailure = false)
    {
        var url = "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
        using (var request = new HttpRequestMessage(HttpMethod.Post, url))
        {
            request.Content = new StringContent(
                ApplySelectedAccountContext(payload, !string.IsNullOrWhiteSpace(accessToken)),
                Encoding.UTF8,
                "application/json");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                ApplySelectedAccountHeader(request, true);
            }

            if (clientName == "ANDROID")
            {
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "3");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                request.Headers.TryAddWithoutValidation("User-Agent", "com.google.android.youtube/20.10.38 (Linux; U; Android 11) gzip");
            }
            else if (clientName == "TVHTML5")
            {
                // The TV client must be identified consistently: client-name 7 and a TV user
                // agent. Falling through to the WEB branch below (client-name 1, browser UA,
                // a /shorts/ referer and the Shorts visitor id) contradicts the TVHTML5 body
                // and makes /player answer "The page needs to be reloaded".
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "7");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", Hl + "," + Hl + ";q=0.9");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/tv");
            }
            else if (clientName == "MWEB" || clientName == "WEB_MOBILE")
            {
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "2");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", ShortsVisitorId);
                request.Headers.TryAddWithoutValidation("Origin", "https://m.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://m.youtube.com/shorts/");
            }
            else
            {
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", ShortsVisitorId);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/shorts/");
            }

            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                if (!quietFailure)
                {
                    System.Diagnostics.Debug.WriteLine("[Innertube] " + endpoint + " failed: " + response.StatusCode + " " + json);
                }
                else
                {
                    // Even a "quiet" failure has to say what happened, otherwise an authorized
                    // request that gets rejected is indistinguishable from one that was never
                    // attempted. Status only — never the body, which can echo request data.
                    System.Diagnostics.Debug.WriteLine(
                        "[Innertube] " + endpoint + " failed: " + (int)response.StatusCode
                        + " (" + clientName + (string.IsNullOrWhiteSpace(accessToken) ? ", anonymous)" : ", authorized)")
                    );
                }
                return string.Empty;
            }

            return json;
        }
    }

    private static string ExtractShortsSequenceToken(string json)
    {
        try
        {
            var root = JsonValue.Parse(json);
            if (root.ValueType == JsonValueType.Object)
            {
                var rootObj = root.GetObject();
                if (rootObj.ContainsKey("sequenceContinuation"))
                {
                    var token = rootObj.GetNamedString("sequenceContinuation", string.Empty);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }

            foreach (var obj in EnumerateObjects(root))
            {
                if (obj.ContainsKey("continuationCommand"))
                {
                    var continuationCommand = obj.GetNamedObject("continuationCommand");
                    var token = GetJsonString(continuationCommand, "token");
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }

                if (obj.ContainsKey("nextContinuationData"))
                {
                    var nextContinuation = obj.GetNamedObject("nextContinuationData");
                    var token = GetJsonString(nextContinuation, "continuation");
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Sequence token parse error: " + ex.Message);
        }

        return string.Empty;
    }

    private static JsonObject ExtractPlayerResponseObject(JsonObject container)
    {
        if (container == null)
        {
            return null;
        }

        var direct = ExtractPlayerResponseValue(container, "playerResponse");
        if (direct != null)
        {
            return direct;
        }

        if (container.ContainsKey("unserializedPrefetchData"))
        {
            var prefetchValue = container.GetNamedValue("unserializedPrefetchData");
            if (prefetchValue.ValueType == JsonValueType.Object)
            {
                var prefetch = prefetchValue.GetObject();
                var nested = ExtractPlayerResponseValue(prefetch, "playerResponse");
                if (nested != null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static JsonObject ExtractPlayerResponseValue(JsonObject container, string key)
    {
        if (container == null || !container.ContainsKey(key))
        {
            return null;
        }

        try
        {
            var value = container.GetNamedValue(key);
            if (value.ValueType == JsonValueType.Object)
            {
                return value.GetObject();
            }

            if (value.ValueType == JsonValueType.String)
            {
                var json = value.GetString();
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return JsonObject.Parse(json);
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static void ApplyPlayerResponseToShort(ShortsVideoItem item, JsonObject playerResponse)
    {
        if (item == null || playerResponse == null)
        {
            return;
        }

        if (playerResponse.ContainsKey("videoDetails"))
        {
            var videoDetails = playerResponse.GetNamedObject("videoDetails");
            var title = GetJsonString(videoDetails, "title");
            if (!string.IsNullOrWhiteSpace(title))
            {
                item.Title = title;
            }

            var author = GetJsonString(videoDetails, "author");
            if (!string.IsNullOrWhiteSpace(author))
            {
                item.ChannelName = author;
            }

            if (videoDetails.ContainsKey("thumbnail"))
            {
                var thumbnail = ExtractThumbnailUrl(videoDetails, "thumbnail");
                if (!string.IsNullOrWhiteSpace(thumbnail))
                {
                    item.ThumbnailUrl = thumbnail;
                }
            }
        }

        if (playerResponse.ContainsKey("microformat"))
        {
            var microformat = playerResponse.GetNamedObject("microformat");
            if (microformat.ContainsKey("playerMicroformatRenderer"))
            {
                var renderer = microformat.GetNamedObject("playerMicroformatRenderer");
                var owner = GetJsonString(renderer, "ownerChannelName");
                if (!string.IsNullOrWhiteSpace(owner))
                {
                    item.ChannelName = owner;
                }

                if (renderer.ContainsKey("thumbnail"))
                {
                    var thumbnail = ExtractThumbnailUrl(renderer, "thumbnail");
                    if (!string.IsNullOrWhiteSpace(thumbnail))
                    {
                        item.ThumbnailUrl = thumbnail;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(item.VideoUrl))
        {
            item.VideoUrl = SelectPlayableShortUrl(playerResponse);
        }
    }

    // Applies the "Preferred Shorts quality" setting to the muxed candidates. Auto (or nothing
    // set) takes the highest. Otherwise the highest that does not exceed the preferred height; if
    // the video has nothing at or below it, the maximum available is used — matching the video
    // rule that an unavailable preferred quality falls back to the best on offer.
    private static string ChooseShortByPreferredQuality(List<KeyValuePair<int, string>> candidates)
    {
        if (candidates == null || candidates.Count == 0)
        {
            return string.Empty;
        }

        var maxUrl = string.Empty;
        var maxHeight = -1;
        foreach (var c in candidates)
        {
            if (c.Key > maxHeight)
            {
                maxHeight = c.Key;
                maxUrl = c.Value;
            }
        }

        var preferred = GetPreferredShortsQualityHeight();
        if (preferred <= 0)
        {
            return maxUrl;
        }

        var bestUrl = string.Empty;
        var bestHeight = -1;
        foreach (var c in candidates)
        {
            if (c.Key <= preferred && c.Key > bestHeight)
            {
                bestHeight = c.Key;
                bestUrl = c.Value;
            }
        }

        // Nothing at or below the preferred height — fall back to the maximum available.
        return string.IsNullOrWhiteSpace(bestUrl) ? maxUrl : bestUrl;
    }

    public sealed class ShortDemuxFormats
    {
        public PlayerFormatModel Video;
        public PlayerFormatModel Audio;
        // Distinct language tracks the short offers, for the audio-track picker.
        public List<AudioTrackInfo> AudioTracks = new List<AudioTrackInfo>();
    }

    public sealed class AudioTrackInfo
    {
        public string Id;
        public string Name;
        public bool IsDefault;
    }

    // Language part of the account locale, e.g. "ru" from "ru" or "ru-RU". Used to pick the audio
    // track that matches the account when several languages exist.
    public static string LocaleLanguagePrefix
    {
        get
        {
            var hl = Hl ?? string.Empty;
            var dash = hl.IndexOf('-');
            return (dash > 0 ? hl.Substring(0, dash) : hl).ToLowerInvariant();
        }
    }

    // Match an audioTrack id ("ru.4", "en-US.4", …) against Windows' system language.
    // Exact regional matches outrank primary-language matches. The first system language wins.
    public static int SystemAudioTrackMatchScore(string audioTrackId)
    {
        if (string.IsNullOrWhiteSpace(audioTrackId))
        {
            return 0;
        }

        var dot = audioTrackId.IndexOf('.');
        var trackLanguage = (dot > 0 ? audioTrackId.Substring(0, dot) : audioTrackId)
            .Replace('_', '-')
            .ToLowerInvariant();
        var trackDash = trackLanguage.IndexOf('-');
        var trackPrimary = trackDash > 0 ? trackLanguage.Substring(0, trackDash) : trackLanguage;

        try
        {
            var languages = Windows.System.UserProfile.GlobalizationPreferences.Languages;
            for (int i = 0; i < languages.Count; i++)
            {
                var systemLanguage = (languages[i] ?? string.Empty).Replace('_', '-').ToLowerInvariant();
                if (string.IsNullOrEmpty(systemLanguage))
                {
                    continue;
                }
                if (string.Equals(trackLanguage, systemLanguage, StringComparison.OrdinalIgnoreCase))
                {
                    return 100010 - (i * 100);
                }

                var systemDash = systemLanguage.IndexOf('-');
                var systemPrimary = systemDash > 0
                    ? systemLanguage.Substring(0, systemDash)
                    : systemLanguage;
                if (string.Equals(trackPrimary, systemPrimary, StringComparison.OrdinalIgnoreCase))
                {
                    return 100000 - (i * 100);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Audio] System language query failed: " + ex.Message);
        }

        return 0;
    }

    // True when an audioTrack id belongs to the account/app locale.
    public static bool AudioTrackMatchesLocale(string audioTrackId)
    {
        if (string.IsNullOrEmpty(audioTrackId))
        {
            return false;
        }
        var lang = LocaleLanguagePrefix;
        if (string.IsNullOrEmpty(lang))
        {
            return false;
        }
        var id = audioTrackId.ToLowerInvariant();
        return id.StartsWith(lang + ".") || id.StartsWith(lang + "-") || id == lang;
    }

    // Distinct audio-language tracks in a format list, default first.
    public static List<AudioTrackInfo> EnumerateAudioTracks(IEnumerable<PlayerFormatModel> formats)
    {
        var tracks = new List<AudioTrackInfo>();
        if (formats == null)
        {
            return tracks;
        }

        foreach (var f in formats)
        {
            if (f == null || !f.HasAudio || f.HasVideo || string.IsNullOrEmpty(f.AudioTrackId))
            {
                continue;
            }
            if (tracks.Exists(t => string.Equals(t.Id, f.AudioTrackId, StringComparison.Ordinal)))
            {
                continue;
            }
            tracks.Add(new AudioTrackInfo
            {
                Id = f.AudioTrackId,
                Name = string.IsNullOrEmpty(f.AudioTrackName) ? f.AudioTrackId : f.AudioTrackName,
                IsDefault = f.AudioIsDefault
            });
        }

        tracks.Sort((a, b) => (b.IsDefault ? 1 : 0) - (a.IsDefault ? 1 : 0));
        return tracks;
    }

    // Best AAC audio-only format for the chosen (or default) language track.
    private static PlayerFormatModel SelectShortAudioTrack(
        IEnumerable<PlayerFormatModel> formats, string preferredTrackId)
    {
        var audio = new List<PlayerFormatModel>();
        foreach (var f in formats)
        {
            if (f == null || string.IsNullOrWhiteSpace(f.Url) || !f.HasAudio || f.HasVideo)
            {
                continue;
            }
            var mime = string.IsNullOrEmpty(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
            if (mime.IndexOf("mp4a", StringComparison.Ordinal) < 0)
            {
                continue;
            }
            audio.Add(f);
        }

        if (audio.Count == 0)
        {
            return null;
        }

        var pool = ChooseAudioTrackPool(audio, preferredTrackId);

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

    // Narrows a set of audio-only formats to a single language track: an explicit choice first,
    // then the Windows system language, then whatever YouTube marks default, then everything.
    private static List<PlayerFormatModel> ChooseAudioTrackPool(
        List<PlayerFormatModel> audio, string preferredTrackId)
    {
        if (!string.IsNullOrEmpty(preferredTrackId))
        {
            var chosen = audio.FindAll(f => string.Equals(f.AudioTrackId, preferredTrackId, StringComparison.Ordinal));
            if (chosen.Count > 0)
            {
                return chosen;
            }
        }

        var systemScore = 0;
        foreach (var f in audio)
        {
            systemScore = Math.Max(systemScore, SystemAudioTrackMatchScore(f.AudioTrackId));
        }
        if (systemScore > 0)
        {
            return audio.FindAll(f => SystemAudioTrackMatchScore(f.AudioTrackId) == systemScore);
        }

        var defaults = audio.FindAll(f => f.AudioIsDefault);
        return defaults.Count > 0 ? defaults : audio;
    }

    // True when the user has pinned a specific Shorts height (i.e. not "Auto"); that is what turns
    // the demuxer on for Shorts, mirroring the regular video rule.
    public static bool IsShortsMuxerPreferred()
    {
        return GetPreferredShortsQualityHeight() > 0;
    }

    // Adaptive H.264 video-only + AAC audio-only for a short, chosen against the preferred height,
    // so the DASH demuxer can play it up to 1080p. Returns null when adaptive streams are
    // unavailable, in which case the caller keeps the muxed-progressive URL path.
    public static async Task<ShortDemuxFormats> GetShortDemuxFormatsAsync(
        string videoId, string preferredAudioTrackId = null, int overrideHeight = 0)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        try
        {
            // ANDROID hands back plain (unciphered) URLs for adaptiveFormats, same as the Shorts
            // muxed path already relies on.
            var androidPayload = "{\"context\":{" + BuildAndroidClientJson() + "},\"videoId\":\""
                + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}";
            var json = await PostInnertubeJsonAsync("player", androidPayload, string.Empty, "ANDROID", "20.10.38");

            var formats = ParseAdaptiveFormatsForDemux(json);
            if (formats.Count == 0)
            {
                return null;
            }

            // A per-short menu choice overrides the Settings default.
            var preferred = overrideHeight > 0 ? overrideHeight : GetPreferredShortsQualityHeight();

            PlayerFormatModel video = null;
            PlayerFormatModel audio = null;

            foreach (var f in formats)
            {
                var mime = string.IsNullOrEmpty(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();

                if (f.HasVideo && !f.HasAudio && mime.IndexOf("avc1", StringComparison.Ordinal) >= 0)
                {
                    var tier = QualityTierOf(f);
                    // Respect the cap: never above the chosen tier (the short side, e.g. 1080).
                    if (preferred > 0 && tier > preferred)
                    {
                        continue;
                    }
                    if (video == null || tier > QualityTierOf(video))
                    {
                        video = f;
                    }
                }
            }

            audio = SelectShortAudioTrack(formats, preferredAudioTrackId);

            // The cap can exclude everything if the short only offers heights above it — then take
            // the smallest available so playback still upgrades past the muxed ceiling.
            if (video == null)
            {
                foreach (var f in formats)
                {
                    var mime = string.IsNullOrEmpty(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                    if (f.HasVideo && !f.HasAudio && mime.IndexOf("avc1", StringComparison.Ordinal) >= 0
                        && (video == null || QualityTierOf(f) < QualityTierOf(video)))
                    {
                        video = f;
                    }
                }
            }

            if (video != null && audio != null
                && !string.IsNullOrWhiteSpace(video.Url) && !string.IsNullOrWhiteSpace(audio.Url))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Shorts] Demux formats: video " + video.Height + "p itag " + video.Itag
                    + ", audio itag " + audio.Itag + " track '" + audio.AudioTrackId + "'");
                return new ShortDemuxFormats
                {
                    Video = video,
                    Audio = audio,
                    AudioTracks = EnumerateAudioTracks(formats)
                };
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Demux format fetch failed: " + ex.Message);
        }

        return null;
    }

    // The resolution tier ("1080p") of a format. For a vertical short (e.g. 1080x1920) that is
    // the SHORTER side, not the height — otherwise the label reads "1920p".
    private static int QualityTierOf(PlayerFormatModel f)
    {
        if (f == null)
        {
            return 0;
        }
        if (f.Width > 0 && f.Height > 0)
        {
            return Math.Min(f.Width, f.Height);
        }
        return f.Height > 0 ? f.Height : f.Width;
    }

    // Distinct H.264 resolution tiers a short offers (for its quality menu), ascending.
    public static async Task<List<int>> GetShortAvailableHeightsAsync(string videoId)
    {
        var result = new List<int>();
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return result;
        }
        try
        {
            var androidPayload = "{\"context\":{" + BuildAndroidClientJson() + "},\"videoId\":\""
                + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true}";
            var json = await PostInnertubeJsonAsync("player", androidPayload, string.Empty, "ANDROID", "20.10.38");
            var formats = ParseAdaptiveFormatsForDemux(json);
            var tiers = new SortedSet<int>();
            foreach (var f in formats)
            {
                var mime = string.IsNullOrEmpty(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                if (f.HasVideo && !f.HasAudio && mime.IndexOf("avc1", StringComparison.Ordinal) >= 0)
                {
                    var tier = QualityTierOf(f);
                    if (tier > 0)
                    {
                        tiers.Add(tier);
                    }
                }
            }
            foreach (var t in tiers)
            {
                result.Add(t);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Height list failed: " + ex.Message);
        }
        return result;
    }

    // Caption tracks (+ translation languages) a short offers, for its subtitles menu.
    public static async Task<Subtitles.TrackList> GetShortSubtitleTracksAsync(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return new Subtitles.TrackList();
        }
        try
        {
            // The WEB player response carries the caption tracklist; ANDROID often omits it.
            var webPayload = "{\"context\":{" + BuildShortsWebClientJson() + "},\"videoId\":\""
                + JsonEscape(videoId) + "\",\"contentCheckOk\":true,\"racyCheckOk\":true,\"playbackContext\":{\"contentPlaybackContext\":{\"html5Preference\":\"HTML5_PREF_WANTS\"}}}";
            var json = await PostInnertubeJsonAsync("player", webPayload, string.Empty, "WEB", ShortsWebClientVersion);
            if (!string.IsNullOrWhiteSpace(json))
            {
                return Subtitles.ParseTracks(JsonObject.Parse(json));
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] Subtitle track fetch failed: " + ex.Message);
        }
        return new Subtitles.TrackList();
    }

    private static List<PlayerFormatModel> ParseAdaptiveFormatsForDemux(string json)
    {
        var result = new List<PlayerFormatModel>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var root = JsonObject.Parse(json);
            if (!root.ContainsKey("streamingData"))
            {
                return result;
            }

            var streamingData = root.GetNamedObject("streamingData");
            if (!streamingData.ContainsKey("adaptiveFormats"))
            {
                return result;
            }

            foreach (var value in streamingData.GetNamedArray("adaptiveFormats"))
            {
                if (value.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var format = value.GetObject();
                var url = GetJsonString(format, "url");
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                var mime = GetJsonString(format, "mimeType");
                var mimeLower = mime.ToLowerInvariant();

                result.Add(new PlayerFormatModel
                {
                    Url = url,
                    Width = (int)format.GetNamedNumber("width", 0),
                    Height = (int)format.GetNamedNumber("height", 0),
                    MimeType = mime,
                    Itag = (int)format.GetNamedNumber("itag", 0),
                    Fps = (int)format.GetNamedNumber("fps", 0),
                    Bitrate = (int)format.GetNamedNumber("bitrate", 0),
                    AverageBitrate = (int)format.GetNamedNumber("averageBitrate", 0),
                    InitRangeStart = GetRangeField(format, "initRange", "start"),
                    InitRangeEnd = GetRangeField(format, "initRange", "end"),
                    IndexRangeStart = GetRangeField(format, "indexRange", "start"),
                    IndexRangeEnd = GetRangeField(format, "indexRange", "end"),
                    HasAudio = mimeLower.IndexOf("audio", StringComparison.Ordinal) >= 0
                        || format.ContainsKey("audioChannels"),
                    HasVideo = mimeLower.IndexOf("video", StringComparison.Ordinal) >= 0
                        || format.ContainsKey("width"),
                    IsAdaptive = true,
                    AudioTrackId = GetAudioTrackField(format, "id"),
                    AudioTrackName = GetAudioTrackField(format, "displayName"),
                    AudioIsDefault = GetAudioTrackIsDefault(format)
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Shorts] adaptiveFormats parse failed: " + ex.Message);
        }

        return result;
    }

    private static string GetAudioTrackField(JsonObject format, string key)
    {
        try
        {
            if (format != null && format.ContainsKey("audioTrack"))
            {
                var track = format.GetNamedObject("audioTrack");
                if (track.ContainsKey(key))
                {
                    return track.GetNamedString(key);
                }
            }
        }
        catch { }
        return string.Empty;
    }

    private static bool GetAudioTrackIsDefault(JsonObject format)
    {
        try
        {
            if (format != null && format.ContainsKey("audioTrack"))
            {
                var track = format.GetNamedObject("audioTrack");
                if (track.ContainsKey("audioIsDefault"))
                {
                    return track.GetNamedBoolean("audioIsDefault");
                }
            }
        }
        catch { }
        return false;
    }

    private static string GetRangeField(JsonObject format, string rangeName, string key)
    {
        try
        {
            if (format.ContainsKey(rangeName))
            {
                var range = format.GetNamedObject(rangeName);
                if (range.ContainsKey(key))
                {
                    return range.GetNamedString(key);
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static int GetPreferredShortsQualityHeight()
    {
        try
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            if (values.ContainsKey("PreferredShortsQuality"))
            {
                var raw = values["PreferredShortsQuality"] as string;
                if (!string.IsNullOrWhiteSpace(raw)
                    && !string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    var trimmed = raw.Trim();
                    if (trimmed.EndsWith("p", StringComparison.OrdinalIgnoreCase))
                    {
                        trimmed = trimmed.Substring(0, trimmed.Length - 1);
                    }

                    int height;
                    if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out height))
                    {
                        return height;
                    }
                }
            }
        }
        catch
        {
        }

        return 0;
    }

    private static string SelectPlayableShortUrl(JsonObject playerResponse)
    {
        if (playerResponse == null || !playerResponse.ContainsKey("streamingData"))
        {
            return string.Empty;
        }

        var streamingData = playerResponse.GetNamedObject("streamingData");

        // Every playable muxed candidate, so the preferred-quality cap can choose among them.
        var candidates = new List<KeyValuePair<int, string>>();

        if (streamingData.ContainsKey("formats"))
        {
            var formats = streamingData.GetNamedArray("formats");
            for (var i = 0; i < formats.Count; i++)
            {
                try
                {
                    var format = formats[i].GetObject();
                    var url = GetJsonString(format, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    var mimeType = GetJsonString(format, "mimeType");
                    if (mimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var hasAudio = mimeType.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0 || format.ContainsKey("audioChannels");
                    if (!hasAudio)
                    {
                        continue;
                    }

                    var height = 0;
                    if (format.ContainsKey("height"))
                    {
                        height = (int)format.GetNamedNumber("height", 0);
                    }

                    var width = 0;
                    if (format.ContainsKey("width"))
                    {
                        width = (int)format.GetNamedNumber("width", 0);
                    }

                    var score = Math.Min(height, width > 0 ? width : height);
                    candidates.Add(new KeyValuePair<int, string>(score, url));
                }
                catch
                {
                }
            }
        }

        var chosen = ChooseShortByPreferredQuality(candidates);
        if (!string.IsNullOrWhiteSpace(chosen))
        {
            return chosen;
        }

        if (streamingData.ContainsKey("hlsManifestUrl"))
        {
            return GetJsonString(streamingData, "hlsManifestUrl");
        }

        return string.Empty;
    }

    private static string BuildWebClientJson()
    {
        return "\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20250101\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}";
    }

    private static string BuildWebMobileClientJson()
    {
        return "\"client\":{\"clientName\":\"MWEB\",\"clientVersion\":\"2.20251222.01.00\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"osName\":\"iOS\",\"osVersion\":\"18\",\"platform\":\"MOBILE\"}";
    }

    private static string BuildShortsWebClientJson()
    {
        return "\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"" + ShortsWebClientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}";
    }

    private static string BuildSeedlessShortsPayload(string seedParams, bool disablePlayerResponse)
    {
        return BuildSeedlessShortsPayload("WEB", seedParams, disablePlayerResponse);
    }

    // The client in the BODY has to match the client in the HEADERS. Sending a TV-minted bearer
    // with a WEB body is what makes the reel endpoints answer 400 — the same mismatch that made
    // /player answer "the page needs to be reloaded".
    private static string BuildShortsClientJson(string clientName)
    {
        if (clientName == "TVHTML5")
        {
            return "\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"" + TvPlayerClientVersion
                + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl
                + "\",\"platform\":\"TV\",\"clientFormFactor\":\"UNKNOWN_FORM_FACTOR\"}";
        }

        if (clientName == "MWEB")
        {
            return "\"client\":{\"clientName\":\"MWEB\",\"clientVersion\":\"" + ShortsMwebClientVersion
                + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\",\"platform\":\"MOBILE\"}";
        }

        return BuildShortsWebClientJson();
    }

    private static string ShortsClientVersion(string clientName)
    {
        if (clientName == "TVHTML5") return TvPlayerClientVersion;
        if (clientName == "MWEB") return ShortsMwebClientVersion;
        return ShortsWebClientVersion;
    }

    private static string BuildSeedlessShortsPayload(string clientName, string seedParams, bool disablePlayerResponse)
    {
        return "{\"context\":{" + BuildShortsClientJson(clientName)
            + "},\"inputType\":\"REEL_WATCH_INPUT_TYPE_SEEDLESS\",\"params\":\"" + JsonEscape(seedParams)
            + "\",\"disablePlayerResponse\":" + (disablePlayerResponse ? "true" : "false") + "}";
    }

    private static string BuildShortsSequencePayload(string clientName, string sequenceToken)
    {
        return "{\"context\":{" + BuildShortsClientJson(clientName)
            + "},\"sequenceParams\":\"" + JsonEscape(sequenceToken) + "\"}";
    }

    private static string BuildAndroidClientJson()
    {
        return "\"client\":{\"clientName\":\"ANDROID\",\"clientVersion\":\"20.10.38\",\"androidSdkVersion\":30,\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl + "\"}";
    }

    private static string BuildHqThumbnailUrl(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return string.Empty;
        }

        return "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg";
    }

    private static string JsonEscape(string value)
    {
        return FastJson.Escape(value);
    }

    // Helper methods

    private static string GetJsonString(JsonObject obj, string key)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
        {
            return string.Empty;
        }

        try
        {
            var value = obj.GetNamedValue(key);
            if (value.ValueType == JsonValueType.String)
            {
                return value.GetString();
            }

            if (value.ValueType == JsonValueType.Number)
            {
                return value.GetNumber().ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (value.ValueType == JsonValueType.Boolean)
            {
                return value.GetBoolean() ? "true" : "false";
            }
        }
        catch
        {
        }

        return string.Empty;
    }


    private static List<PlaylistItem> ParseDataApiPlaylists(string json, int maxCount)
    {
        var result = new List<PlaylistItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var root = JsonObject.Parse(json);
            if (!root.ContainsKey("items"))
            {
                return result;
            }

            var items = root.GetNamedArray("items");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (uint i = 0; i < items.Count; i++)
            {
                if (maxCount > 0 && result.Count >= maxCount)
                {
                    break;
                }

                var itemValue = items[(int)i];
                if (itemValue.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var item = itemValue.GetObject();
                var playlistId = GetJsonString(item, "id");
                if (string.IsNullOrWhiteSpace(playlistId) || seen.Contains(playlistId))
                {
                    continue;
                }

                var title = "Playlist";
                var authorName = string.Empty;
                var thumbnail = string.Empty;
                var privacy = string.Empty;
                var countText = string.Empty;

                if (item.ContainsKey("snippet"))
                {
                    var snippet = item.GetNamedObject("snippet");
                    title = GetJsonString(snippet, "title");
                    if (string.IsNullOrWhiteSpace(title))
                    {
                        title = "Playlist";
                    }
                    authorName = FirstNonEmpty(
                        GetJsonString(snippet, "channelTitle"),
                        GetJsonString(snippet, "localizedChannelTitle"));
                    thumbnail = ExtractDataApiPlaylistThumbnail(snippet);
                }

                if (item.ContainsKey("status"))
                {
                    var status = item.GetNamedObject("status");
                    privacy = FormatPrivacyText(GetJsonString(status, "privacyStatus"));
                }

                if (item.ContainsKey("contentDetails"))
                {
                    var contentDetails = item.GetNamedObject("contentDetails");
                    if (contentDetails.ContainsKey("itemCount"))
                    {
                        try
                        {
                            var count = (int)contentDetails.GetNamedNumber("itemCount", 0);
                            countText = FormatPlaylistCountText(count);
                        }
                        catch
                        {
                        }
                    }
                }

                if (string.IsNullOrWhiteSpace(privacy))
                {
                    privacy = "Playlist";
                }

                seen.Add(playlistId);
                result.Add(new PlaylistItem
                {
                    PlaylistId = playlistId,
                    Title = title,
                    AuthorName = authorName,
                    ThumbnailUrl = thumbnail,
                    VideoCountText = countText,
                    PrivacyText = privacy
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Playlists] Data API parse error: " + ex.Message);
        }

        return result;
    }

    private static List<PlaylistItem> ParsePlaylistCards(string json, int maxCount)
    {
        var result = new List<PlaylistItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var root = JsonValue.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in EnumerateObjects(root, 16000))
            {
                if (maxCount > 0 && result.Count >= maxCount)
                {
                    break;
                }

                PlaylistItem item = null;

                if (obj.ContainsKey("playlistRenderer"))
                {
                    item = ParsePlaylistRenderer(obj.GetNamedObject("playlistRenderer"));
                }
                else if (obj.ContainsKey("gridPlaylistRenderer"))
                {
                    item = ParsePlaylistRenderer(obj.GetNamedObject("gridPlaylistRenderer"));
                }
                else if (obj.ContainsKey("tileRenderer"))
                {
                    item = ParsePlaylistTileRenderer(obj.GetNamedObject("tileRenderer"));
                }
                else if (obj.ContainsKey("lockupViewModel"))
                {
                    item = ParsePlaylistLockupViewModel(obj.GetNamedObject("lockupViewModel"));
                }

                if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId) || seen.Contains(item.PlaylistId))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Title))
                {
                    item.Title = Localization.GetString("Playlist");
                }
                if (string.IsNullOrWhiteSpace(item.VideoCountText))
                {
                    item.VideoCountText = "";
                }
                if (string.IsNullOrWhiteSpace(item.PrivacyText))
                {
                    item.PrivacyText = Localization.GetString("Playlist");
                }

                seen.Add(item.PlaylistId);
                result.Add(item);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Playlists] InnerTube parse error: " + ex.Message);
        }

        return result;
    }

    private static PlaylistItem ParsePlaylistRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var playlistId = GetJsonString(renderer, "playlistId");
        if (string.IsNullOrWhiteSpace(playlistId) && renderer.ContainsKey("navigationEndpoint"))
        {
            playlistId = ExtractPlaylistIdFromEndpoint(renderer.GetNamedObject("navigationEndpoint"));
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return null;
        }

        return new PlaylistItem
        {
            PlaylistId = playlistId,
            Title = ExtractTextFromField(renderer, "title", "Playlist"),
            AuthorName = ExtractPlaylistCardAuthor(renderer),
            ThumbnailUrl = ExtractBestThumbnailUrl(renderer, "thumbnail"),
            VideoCountText = FirstNonEmpty(
                ExtractPlaylistBadgeText(renderer),
                ExtractTextFromField(renderer, "videoCountShortText", string.Empty),
                ExtractTextFromField(renderer, "videoCountText", string.Empty)),
            PrivacyText = ExtractTextFromField(renderer, "privacy", string.Empty)
        };
    }

    private static PlaylistItem ParsePlaylistTileRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var contentType = GetJsonString(renderer, "contentType");
        var playlistId = GetJsonString(renderer, "contentId");
        if (string.IsNullOrWhiteSpace(playlistId) || (!playlistId.StartsWith("PL") && !playlistId.StartsWith("VL") && !contentType.ToUpperInvariant().Contains("PLAYLIST")))
        {
            return null;
        }

        var title = "Playlist";
        var thumbnail = string.Empty;
        var subtitle = string.Empty;

        if (renderer.ContainsKey("metadata"))
        {
            var metadata = renderer.GetNamedObject("metadata");
            if (metadata.ContainsKey("tileMetadataRenderer"))
            {
                var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                title = ExtractTextFromField(tileMetadata, "title", "Playlist");
                subtitle = ExtractTextFromAnyValue(tileMetadata);
            }
        }

        if (renderer.ContainsKey("header"))
        {
            var header = renderer.GetNamedObject("header");
            if (header.ContainsKey("tileHeaderRenderer"))
            {
                thumbnail = ExtractBestThumbnailUrl(header.GetNamedObject("tileHeaderRenderer"), "thumbnail");
            }
        }

        return new PlaylistItem
        {
            PlaylistId = playlistId,
            Title = title,
            AuthorName = ExtractPlaylistCardAuthor(renderer),
            ThumbnailUrl = thumbnail,
            VideoCountText = FirstNonEmpty(
                ExtractPlaylistBadgeText(renderer),
                ExtractPlaylistCountFromText(subtitle)),
            PrivacyText = "Playlist"
        };
    }

    private static PlaylistItem ParsePlaylistLockupViewModel(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var playlistId = GetJsonString(renderer, "contentId");
        if (string.IsNullOrWhiteSpace(playlistId) || (!playlistId.StartsWith("PL") && !playlistId.StartsWith("VL") && !playlistId.StartsWith("LL") && !playlistId.StartsWith("WL")))
        {
            return null;
        }

        var allText = ExtractTextFromAnyValue(renderer);
        return new PlaylistItem
        {
            PlaylistId = playlistId,
            Title = ExtractPlaylistLockupTitle(renderer),
            AuthorName = ExtractPlaylistCardAuthor(renderer),
            ThumbnailUrl = ExtractFirstUrlFromAnyValue(renderer),
            VideoCountText = FirstNonEmpty(
                ExtractPlaylistBadgeText(renderer),
                ExtractPlaylistCountFromText(allText)),
            PrivacyText = "Playlist"
        };
    }

    private static string ExtractPlaylistLockupTitle(JsonObject renderer)
    {
        if (renderer != null && renderer.ContainsKey("metadata"))
        {
            var text = ExtractTextFromAnyValue(renderer.GetNamedValue("metadata"));
            if (!string.IsNullOrWhiteSpace(text))
            {
                var pieces = text.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length > 0)
                {
                    return pieces[0].Trim();
                }
            }
        }

        return "Playlist";
    }

    private static string ExtractPlaylistIdFromEndpoint(JsonObject endpoint)
    {
        if (endpoint == null)
        {
            return string.Empty;
        }

        if (endpoint.ContainsKey("watchEndpoint"))
        {
            var watch = endpoint.GetNamedObject("watchEndpoint");
            var playlistId = GetJsonString(watch, "playlistId");
            if (!string.IsNullOrWhiteSpace(playlistId) && !IsReservedPersonalPlaylistId(playlistId))
            {
                return playlistId;
            }
        }

        if (endpoint.ContainsKey("browseEndpoint"))
        {
            var browse = endpoint.GetNamedObject("browseEndpoint");
            var browseId = GetJsonString(browse, "browseId");
            if (browseId.StartsWith("VL"))
            {
                return browseId.Substring(2);
            }
            return browseId;
        }

        return string.Empty;
    }

    private static string ExtractDataApiPlaylistThumbnail(JsonObject snippet)
    {
        if (snippet == null || !snippet.ContainsKey("thumbnails"))
        {
            return string.Empty;
        }

        try
        {
            var thumbnails = snippet.GetNamedObject("thumbnails");
            var keys = new[] { "maxres", "standard", "high", "medium", "default" };
            for (var i = 0; i < keys.Length; i++)
            {
                if (thumbnails.ContainsKey(keys[i]))
                {
                    var thumb = thumbnails.GetNamedObject(keys[i]);
                    var url = GetJsonString(thumb, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractBestThumbnailUrl(JsonObject obj, string fieldName)
    {
        if (obj == null || string.IsNullOrWhiteSpace(fieldName) || !obj.ContainsKey(fieldName))
        {
            return string.Empty;
        }

        try
        {
            var thumbObj = obj.GetNamedObject(fieldName);
            if (!thumbObj.ContainsKey("thumbnails"))
            {
                return string.Empty;
            }

            var thumbnails = thumbObj.GetNamedArray("thumbnails");
            for (var i = (int)thumbnails.Count - 1; i >= 0; i--)
            {
                var thumbValue = thumbnails[i];
                if (thumbValue.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var url = GetJsonString(thumbValue.GetObject(), "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url.StartsWith("//") ? "https:" + url : url;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }


    private static string ExtractFirstUrlFromNamedValue(JsonObject obj, string key)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
        {
            return string.Empty;
        }

        try
        {
            return ExtractFirstUrlFromAnyValue(obj.GetNamedValue(key));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractFirstUrlFromAnyValue(IJsonValue value)
    {
        try
        {
            foreach (var obj in EnumerateObjects(value, 800))
            {
                var url = GetJsonString(obj, "url");
                if (!string.IsNullOrWhiteSpace(url) && (url.StartsWith("http://") || url.StartsWith("https://") || url.StartsWith("//")))
                {
                    return url.StartsWith("//") ? "https:" + url : url;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractPlaylistCountFromText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lower = text.ToLowerInvariant();
        var keywords = new[] { "video", "видео", "ролик" };
        for (var i = 0; i < keywords.Length; i++)
        {
            var index = lower.IndexOf(keywords[i], StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var start = index;
                while (start > 0 && !char.IsDigit(lower[start - 1]))
                {
                    start--;
                }
                while (start > 0 && (char.IsDigit(lower[start - 1]) || lower[start - 1] == ' ' || lower[start - 1] == ',' || lower[start - 1] == '.'))
                {
                    start--;
                }

                var countPart = text.Substring(start, index - start).Trim();
                if (!string.IsNullOrWhiteSpace(countPart))
                {
                    return countPart;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractPlaylistBadgeText(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var key in new[]
        {
            "thumbnailBadgeViewModel",
            "thumbnailOverlayTimeStatusRenderer",
            "thumbnailOverlayBottomPanelRenderer"
        })
        {
            var wrapper = FindFirstObjectWithKey(renderer, key);
            if (wrapper == null || !wrapper.ContainsKey(key)
                || wrapper[key].ValueType != JsonValueType.Object)
            {
                continue;
            }

            var text = ExtractTextFromField(wrapper[key].GetObject(), "text", string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return FirstNonEmpty(
            ExtractTextFromField(renderer, "videoCountShortText", string.Empty),
            ExtractTextFromField(renderer, "videoCountText", string.Empty));
    }

    private static string FormatPlaylistCountText(int count)
    {
        if (count <= 0)
        {
            return string.Empty;
        }

        return count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatPrivacyText(string privacy)
    {
        if (string.IsNullOrWhiteSpace(privacy))
        {
            return string.Empty;
        }

        privacy = privacy.Trim().ToLowerInvariant();
        if (privacy == "private")
            return "Private";
        if (privacy == "unlisted")
            return "Unlisted";
        if (privacy == "public")
            return "Public";

        return privacy;
    }

    private static List<SubscriptionChannel> ParseSubscribedChannels(string json)
    {
        var result = new List<SubscriptionChannel>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            if (json.IndexOf("\"TILE_CONTENT_TYPE_CHANNEL\"", StringComparison.Ordinal) < 0 &&
                json.IndexOf("TILE_CONTENT_TYPE_CHANNEL", StringComparison.Ordinal) < 0)
            {
                return result;
            }

            var root = JsonObject.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var obj in EnumerateObjects(root, MaxObjectsToScanForSubscriptions))
            {
                if (obj.ContainsKey("tileRenderer"))
                {
                    var tileRenderer = obj.GetNamedObject("tileRenderer");
                    var contentType = GetJsonString(tileRenderer, "contentType");
                    if (contentType != "TILE_CONTENT_TYPE_CHANNEL")
                    {
                        continue;
                    }

                    var channelId = GetJsonString(tileRenderer, "contentId");
                    if (string.IsNullOrWhiteSpace(channelId) || seen.Contains(channelId))
                    {
                        continue;
                    }

                    var channelName = Localization.GetString("Unknown");
                    if (tileRenderer.ContainsKey("metadata"))
                    {
                        var metadata = tileRenderer.GetNamedObject("metadata");
                        if (metadata.ContainsKey("tileMetadataRenderer"))
                        {
                            var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                            channelName = ExtractTextFromField(tileMetadata, "title", Localization.GetString("Unknown"));
                        }
                    }

                    var thumbnailUrl = "";
                    if (tileRenderer.ContainsKey("header"))
                    {
                        var header = tileRenderer.GetNamedObject("header");
                        if (header.ContainsKey("tileHeaderRenderer"))
                        {
                            var tileHeader = header.GetNamedObject("tileHeaderRenderer");
                            if (tileHeader.ContainsKey("thumbnail"))
                            {
                                thumbnailUrl = ExtractThumbnailUrl(tileHeader, "thumbnail");
                            }
                        }
                    }

                    DebugParserLog("[ParseSubscribedChannels] Found channel: " + channelId + " - " + channelName);

                    if (!string.IsNullOrWhiteSpace(channelId))
                    {
                        seen.Add(channelId);
                        result.Add(new SubscriptionChannel
                        {
                            ChannelId = channelId,
                            ChannelName = channelName,
                            ThumbnailUrl = thumbnailUrl
                        });
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[ParseSubscribedChannels] Total channels found: {result.Count}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ParseSubscribedChannels] Error: {ex.Message}");
        }

        return result;
    }

    private static List<VideoCardItem> ParsePlaylistVideoCards(string json, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<VideoCardItem>();
        }

        JsonObject root;
        if (!FastJson.TryParseObject(json, out root))
        {
            return new List<VideoCardItem>();
        }

        return ParsePlaylistVideoCards(root, maxCount);
    }

    private static List<VideoCardItem> ParsePlaylistVideoCards(IJsonValue root, int maxCount)
    {
        var result = ParseVideoCards(root, maxCount);
        if (result.Count > 0 || root == null)
        {
            return result;
        }

        // Current YouTube playlist pages can use newer view-model nodes. YouTube.js treats
        // PlaylistVideo, ReelItem and ShortsLockupView as playlist items, so scan for the same
        // families with a much larger limit than Home/Search parsing.
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in EnumerateObjects(root, 120000))
            {
                VideoCardItem item = null;
                IJsonValue rendererValue;

                if (obj.TryGetValue("playlistVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParsePlaylistVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("playlistPanelVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParsePlaylistPanelVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("videoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("lockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseLockupViewModel(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("reelItemRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseReelItemRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("shortsLockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseShortsLockupViewModel(rendererValue.GetObject());
                }

                if (item == null || string.IsNullOrWhiteSpace(item.VideoId) || !seen.Add(item.VideoId))
                {
                    continue;
                }

                result.Add(item);
                if (maxCount > 0 && result.Count >= maxCount)
                {
                    break;
                }
            }

            System.Diagnostics.Debug.WriteLine("[Playlist] YouTube.js-style videos parsed: " + result.Count);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Playlist] YouTube.js-style parse error: " + ex.Message);
        }

        return result;
    }

    private static HistoryFeedPage ParseHistoryFeedPage(string json, int count)
    {
        var page = new HistoryFeedPage();

        if (string.IsNullOrWhiteSpace(json))
        {
            return page;
        }

        try
        {
            var root = JsonValue.Parse(json);
            page.ContinuationToken = ExtractHistoryContinuationToken(root);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = 0;
            CollectHistorySections(root, page.Groups, seen, count, ref added);

            if (page.Groups.Count == 0)
            {
                var fallback = new HistoryDateGroup { DateTitle = Localization.GetString("Older") };
                ExtractHistoryItemsFromValue(root, seen, fallback.Videos, fallback.Shorts, ref added, count);
                if (fallback.Videos.Count > 0 || fallback.Shorts.Count > 0)
                {
                    page.Groups.Add(fallback);
                }
            }

            for (var i = page.Groups.Count - 1; i >= 0; i--)
            {
                var group = page.Groups[i];
                if ((group.Videos == null || group.Videos.Count == 0) &&
                    (group.Shorts == null || group.Shorts.Count == 0))
                {
                    page.Groups.RemoveAt(i);
                }
            }

            System.Diagnostics.Debug.WriteLine("[History] Parsed groups: " + page.Groups.Count + ", continuation: " + (!string.IsNullOrWhiteSpace(page.ContinuationToken)).ToString());
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[History] Parse error: " + ex.Message);
        }

        return page;
    }

    private static void CollectHistorySections(IJsonValue value, List<HistoryDateGroup> groups, HashSet<string> seen, int limit, ref int added)
    {
        if (value == null)
        {
            return;
        }

        if (limit > 0 && added >= limit)
        {
            return;
        }

        try
        {
            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                JsonObject sectionRenderer;
                string sectionTitle;

                if (TryGetHistorySectionRenderer(obj, out sectionRenderer, out sectionTitle))
                {
                    var group = new HistoryDateGroup { DateTitle = NormalizeHistoryDateTitle(sectionTitle) };
                    ExtractHistoryItemsFromValue(sectionRenderer, seen, group.Videos, group.Shorts, ref added, limit);
                    if (group.Videos.Count > 0 || group.Shorts.Count > 0)
                    {
                        groups.Add(group);
                    }
                    return;
                }

                foreach (var pair in obj)
                {
                    CollectHistorySections(pair.Value, groups, seen, limit, ref added);
                    if (limit > 0 && added >= limit)
                    {
                        return;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (var i = 0; i < arr.Count; i++)
                {
                    CollectHistorySections(arr[i], groups, seen, limit, ref added);
                    if (limit > 0 && added >= limit)
                    {
                        return;
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static bool TryGetHistorySectionRenderer(JsonObject obj, out JsonObject renderer, out string title)
    {
        renderer = null;
        title = string.Empty;

        if (obj == null)
        {
            return false;
        }

        foreach (var key in new[] { "richShelfRenderer", "shelfRenderer" })
        {
            var candidate = GetObjectFromJsonObject(obj, key);
            if (candidate == null)
            {
                continue;
            }

            var candidateTitle = ExtractHistorySectionTitle(candidate);
            if (IsMeaningfulHistorySectionTitle(candidateTitle))
            {
                renderer = candidate;
                title = candidateTitle;
                return true;
            }
        }

        var itemSection = GetObjectFromJsonObject(obj, "itemSectionRenderer");
        if (itemSection != null)
        {
            var itemTitle = ExtractHistorySectionTitle(itemSection);
            if (IsMeaningfulHistorySectionTitle(itemTitle))
            {
                renderer = itemSection;
                title = itemTitle;
                return true;
            }
        }

        return false;
    }

    private static string ExtractHistorySectionTitle(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var key in new[] { "title", "header", "subtitle", "label" })
        {
            if (!renderer.ContainsKey(key))
            {
                continue;
            }

            var text = ExtractTextFromAnyValue(renderer.GetNamedValue(key));
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        foreach (var obj in EnumerateObjects(renderer, 250))
        {
            foreach (var key in new[] { "shelfHeaderRenderer", "richShelfHeaderRenderer", "feedFilterChipBarRenderer" })
            {
                var header = GetObjectFromJsonObject(obj, key);
                if (header == null)
                {
                    continue;
                }

                var text = FirstNonEmpty(
                    ExtractTextFromField(header, "title", string.Empty),
                    ExtractTextFromField(header, "text", string.Empty),
                    ExtractTextFromAnyValue(header));

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static bool IsMeaningfulHistorySectionTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var normalized = NormalizeHistoryDateTitle(title);
        var lower = normalized.ToLowerInvariant();

        if (lower == "history" || lower == "watch history" || lower == "videos" || lower == "shorts" || lower == "music")
        {
            return false;
        }

        if (lower.Contains("search") || lower.Contains("clear all") || lower.Contains("manage all"))
        {
            return false;
        }

        return true;
    }

    private static string NormalizeHistoryDateTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return Localization.GetString("Older");
        }

        var value = title.Replace("\r", " ").Replace("\n", " ").Trim();
        while (value.Contains("  "))
        {
            value = value.Replace("  ", " ");
        }

        var lower = value.ToLowerInvariant();
        if (lower == "сегодня" || lower == "today")
        {
            return Localization.GetString("Today");
        }
        if (lower == "вчера" || lower == "yesterday")
        {
            return Localization.GetString("Yesterday");
        }
        if (lower == "this week" || lower.Contains("на этой неделе") || lower.Contains("эта неделя"))
        {
            return Localization.GetString("ThisWeek");
        }
        if (lower == "older" || lower.Contains("ранее") || lower.Contains("старые"))
        {
            return Localization.GetString("Older");
        }

        return value;
    }

    private static void ExtractHistoryItemsFromValue(IJsonValue value, HashSet<string> seen,
        List<VideoCardItem> videos, List<VideoCardItem> shorts, ref int added, int limit)
    {
        if (value == null || videos == null || shorts == null)
        {
            return;
        }

        if (limit > 0 && added >= limit)
        {
            return;
        }

        try
        {
            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                VideoCardItem item = null;
                var isShort = false;
                IJsonValue rendererValue;

                if (obj.TryGetValue("videoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParseVideoRenderer(renderer);
                }
                else if (obj.TryGetValue("gridVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParseVideoRenderer(renderer);
                }
                else if (obj.TryGetValue("compactVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParseVideoRenderer(renderer);
                }
                else if (obj.TryGetValue("playlistVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParsePlaylistVideoRenderer(renderer);
                }
                else if (obj.TryGetValue("playlistPanelVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParsePlaylistPanelVideoRenderer(renderer);
                }
                else if (obj.TryGetValue("tileRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParseTileRenderer(renderer);
                }
                else if (obj.TryGetValue("lockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    var renderer = rendererValue.GetObject();
                    isShort = IsShortsLikeRenderer(renderer);
                    item = ParseLockupViewModel(renderer);
                }
                else if (obj.TryGetValue("reelItemRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    isShort = true;
                    item = ParseReelItemRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("shortsLockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    isShort = true;
                    item = ParseShortsLockupViewModel(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("shortsLockupView", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    isShort = true;
                    item = ParseShortsLockupViewModel(rendererValue.GetObject());
                }

                if (item != null && !string.IsNullOrWhiteSpace(item.VideoId))
                {
                    if (seen == null || !seen.Contains(item.VideoId))
                    {
                        if (seen != null)
                        {
                            seen.Add(item.VideoId);
                        }

                        if (string.IsNullOrWhiteSpace(item.Title) || item.Title == "Без названия")
                        {
                            item.Title = Localization.GetString("Untitled");
                        }

                        if (isShort)
                        {
                            shorts.Add(item);
                        }
                        else
                        {
                            videos.Add(item);
                        }
                        added++;
                    }
                    return;
                }

                foreach (var pair in obj)
                {
                    ExtractHistoryItemsFromValue(pair.Value, seen, videos, shorts, ref added, limit);
                    if (limit > 0 && added >= limit)
                    {
                        return;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (var i = 0; i < arr.Count; i++)
                {
                    ExtractHistoryItemsFromValue(arr[i], seen, videos, shorts, ref added, limit);
                    if (limit > 0 && added >= limit)
                    {
                        return;
                    }
                }
            }
        }
        catch
        {
        }
    }

    private static bool IsShortsLikeRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return false;
        }

        foreach (var obj in EnumerateObjects(renderer, 300))
        {
            if (obj.ContainsKey("reelWatchEndpoint") || obj.ContainsKey("shortsLockupViewModel") || obj.ContainsKey("shortsLockupView"))
            {
                return true;
            }

            if (obj.ContainsKey("url"))
            {
                var url = GetJsonString(obj, "url");
                if (!string.IsNullOrWhiteSpace(url) && url.IndexOf("/shorts/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            var metadata = GetObjectFromJsonObject(obj, "webCommandMetadata");
            if (metadata != null)
            {
                var url = GetJsonString(metadata, "url");
                if (!string.IsNullOrWhiteSpace(url) && url.IndexOf("/shorts/", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string ExtractHistoryContinuationToken(IJsonValue root)
    {
        if (root == null)
        {
            return string.Empty;
        }

        try
        {
            foreach (var obj in EnumerateObjects(root, 7000))
            {
                var continuationItem = GetObjectFromJsonObject(obj, "continuationItemRenderer");
                if (continuationItem != null)
                {
                    var token = ExtractContinuationTokenFromObject(continuationItem);
                    if (!string.IsNullOrWhiteSpace(token))
                    {
                        return token;
                    }
                }
            }

            foreach (var obj in EnumerateObjects(root, 7000))
            {
                var token = ExtractContinuationTokenFromObject(obj);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractContinuationTokenFromObject(JsonObject obj)
    {
        if (obj == null)
        {
            return string.Empty;
        }

        var continuationCommand = GetObjectFromJsonObject(obj, "continuationCommand");
        if (continuationCommand != null)
        {
            var token = GetJsonString(continuationCommand, "token");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        var nextContinuationData = GetObjectFromJsonObject(obj, "nextContinuationData");
        if (nextContinuationData != null)
        {
            var token = GetJsonString(nextContinuationData, "continuation");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        var reloadContinuationData = GetObjectFromJsonObject(obj, "reloadContinuationData");
        if (reloadContinuationData != null)
        {
            var token = GetJsonString(reloadContinuationData, "continuation");
            if (!string.IsNullOrWhiteSpace(token))
            {
                return token;
            }
        }

        var endpoint = GetObjectFromJsonObject(obj, "continuationEndpoint");
        if (endpoint != null)
        {
            return ExtractContinuationTokenFromObject(endpoint);
        }

        return string.Empty;
    }

    private static JsonObject GetObjectFromJsonObject(JsonObject obj, string key)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        IJsonValue value;
        if (!obj.TryGetValue(key, out value) || value == null || value.ValueType != JsonValueType.Object)
        {
            return null;
        }

        return value.GetObject();
    }

    internal static List<VideoCardItem> ParseVideoCards(string json)
    {
        return ParseVideoCards(json, 0);
    }

    internal static List<VideoCardItem> ParseVideoCards(string json, int maxCount)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            System.Diagnostics.Debug.WriteLine("[ParseVideoCards] JSON is empty");
            return new List<VideoCardItem>();
        }

        try
        {
            if (!ContainsAnyOrdinal(json, VideoRendererMarkers))
            {
                return new List<VideoCardItem>();
            }

            JsonObject root;
            if (!FastJson.TryParseObject(json, out root))
            {
                return new List<VideoCardItem>();
            }

            return ParseVideoCards(root, maxCount);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ParseVideoCards] Error parsing videos: " + ex.Message);
            return new List<VideoCardItem>();
        }
    }

    // Use this overload whenever the request coordinator has already built the WinRT JSON tree.
    // Re-parsing the same 0.5-2 MB response was a major ARM/Windows 10 Mobile hot path.
    internal static List<VideoCardItem> ParseVideoCards(IJsonValue root, int maxCount)
    {
        var result = new List<VideoCardItem>(maxCount > 0 ? Math.Min(maxCount, 64) : 16);
        if (root == null)
        {
            return result;
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var maxObjects = maxCount > 0
                ? Math.Min(MaxObjectsToScanForVideoCards, Math.Max(1200, maxCount * 160))
                : MaxObjectsToScanForVideoCards;

            foreach (var obj in EnumerateObjects(root, maxObjects))
            {
                VideoCardItem item = null;
                IJsonValue rendererValue;

                if (obj.TryGetValue("videoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("gridVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("compactVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("playlistVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParsePlaylistVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("playlistPanelVideoRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParsePlaylistPanelVideoRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("lockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseLockupViewModel(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("reelItemRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseReelItemRenderer(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("shortsLockupViewModel", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseShortsLockupViewModel(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("tileRenderer", out rendererValue) && rendererValue.ValueType == JsonValueType.Object)
                {
                    item = ParseTileRenderer(rendererValue.GetObject());
                }

                if (item == null || string.IsNullOrWhiteSpace(item.VideoId) || !seen.Add(item.VideoId))
                {
                    continue;
                }

                result.Add(item);
                DebugParserLog("[ParseVideoCards] Added: " + item.VideoId + " - " + item.Title);

                if (maxCount > 0 && result.Count >= maxCount)
                {
                    break;
                }
            }
            
            DebugParserLog("[ParseVideoCards] Total videos parsed: " + result.Count);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ParseVideoCards] Error parsing videos: " + ex.Message);
        }

        return result;
    }

    internal static string ExtractVideoCardChannelId(JsonObject renderer)
    {
        if (renderer == null)
            return string.Empty;

        // The author/channel endpoint is normally attached to one of the byline text fields.
        // Read those first so menu endpoints from unrelated actions cannot win.
        var bylineKeys = new[]
        {
            "ownerText",
            "shortBylineText",
            "longBylineText",
            "bylineText",
            "shortBylineTextViewModel",
            // TVHTML5 tileRenderer stores "Go to channel" under the long-press menu rather
            // than next to the visible channel text. Keep it ahead of the broad fallback.
            "onLongPressCommand",
            "navigationEndpoint",
            "metadata"
        };

        for (var i = 0; i < bylineKeys.Length; i++)
        {
            var key = bylineKeys[i];
            if (!renderer.ContainsKey(key))
                continue;

            try
            {
                var id = ExtractChannelIdFromAnyValue(renderer.GetNamedValue(key), 240);
                if (!string.IsNullOrWhiteSpace(id))
                    return id;
            }
            catch
            {
            }
        }

        // New lockup view-models can place the author endpoint deeper in metadata. Keep the
        // fallback bounded and only accept real UC channel ids.
        return ExtractChannelIdFromAnyValue(renderer, 700);
    }

    private static string ExtractChannelIdFromAnyValue(IJsonValue value, int maxObjects)
    {
        if (value == null)
            return string.Empty;

        try
        {
            foreach (var obj in EnumerateObjects(value, maxObjects))
            {
                var id = GetJsonString(obj, "channelId");
                if (IsYouTubeChannelId(id))
                    return id;

                id = GetJsonString(obj, "browseId");
                if (IsYouTubeChannelId(id))
                    return id;

                if (obj.ContainsKey("browseEndpoint") && obj.GetNamedValue("browseEndpoint").ValueType == JsonValueType.Object)
                {
                    id = GetJsonString(obj.GetNamedObject("browseEndpoint"), "browseId");
                    if (IsYouTubeChannelId(id))
                        return id;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static bool IsYouTubeChannelId(string value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Length >= 20
            && value.StartsWith("UC", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractChannelThumbnailSupportedRenderer(JsonObject renderer)
    {
        if (renderer == null || !renderer.ContainsKey("channelThumbnailSupportedRenderers"))
            return string.Empty;

        try
        {
            var supported = renderer.GetNamedObject("channelThumbnailSupportedRenderers");
            if (supported.ContainsKey("channelThumbnailWithLinkRenderer"))
            {
                var linked = supported.GetNamedObject("channelThumbnailWithLinkRenderer");
                var url = ExtractBestThumbnailUrl(linked, "thumbnail");
                if (!string.IsNullOrWhiteSpace(url))
                    return url;
            }

            return ExtractFirstUrlFromAnyValue(supported);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string ExtractVideoCardChannelThumbnail(JsonObject renderer)
    {
        if (renderer == null)
            return string.Empty;

        // Fast paths used by the renderer families seen in current WEB responses:
        // videoRenderer -> channelThumbnailSupportedRenderers.channelThumbnailWithLinkRenderer.thumbnail
        // compactVideoRenderer -> channelThumbnail
        var url = FirstNonEmpty(
            ExtractBestThumbnailUrl(renderer, "channelThumbnail"),
            ExtractBestThumbnailUrl(renderer, "authorThumbnail"),
            ExtractBestThumbnailUrl(renderer, "ownerThumbnail"),
            ExtractChannelThumbnailSupportedRenderer(renderer));
        if (!string.IsNullOrWhiteSpace(url))
            return url;

        // Newer WEB responses move the avatar into nested view-model objects. Walk the card
        // once and only inspect channel/avatar-specific branches; never fall back to the main
        // video thumbnail and never issue a separate channel request.
        var keys = new[]
        {
            "channelThumbnail",
            "authorThumbnail",
            "channelThumbnailSupportedRenderers",
            "decoratedAvatarViewModel",
            "avatarViewModel",
            "channelAvatar",
            "ownerThumbnail",
            "avatar"
        };

        foreach (var obj in EnumerateObjects(renderer, 320))
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var key = keys[i];
                if (!obj.ContainsKey(key))
                    continue;

                try
                {
                    url = ExtractFirstUrlFromAnyValue(obj.GetNamedValue(key));
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
                catch
                {
                }
            }
        }

        return string.Empty;
    }

    internal static VideoCardItem ParseVideoRenderer(JsonObject renderer)
    {
        if (renderer == null) return null;

        var videoId = GetJsonString(renderer, "videoId");
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        var channelTitle = FirstNonEmpty(
            ExtractVideoCardAuthor(renderer),
            Localization.GetString("Unknown"));

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = ExtractTextFromField(renderer, "title", Localization.GetString("Untitled")),
            ChannelTitle = channelTitle,
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = FirstNonEmpty(
                ExtractTextFromField(renderer, "lengthText", string.Empty),
                ExtractDurationFromOverlays(renderer),
                ExtractDurationFromAnyValue(renderer),
                string.Empty),
            ThumbnailUrl = BuildMqThumbnailUrl(videoId),
            ViewCount = FirstNonEmpty(
                ExtractTextFromField(renderer, "viewCountText", string.Empty),
                ExtractTextFromField(renderer, "shortViewCountText", string.Empty),
                string.Empty),
            PublishedText = ExtractTextFromField(renderer, "publishedTimeText", string.Empty),
            PlaylistId = ExtractPlaylistIdFromWatchEndpoints(renderer),
            WatchedPercent = ExtractWatchedPercent(renderer)
        };
    }

    // Personalized TVHTML5 browse/search responses expose resume progress either in the
    // classic thumbnailOverlayResumePlaybackRenderer.percentDurationWatched shape or in
    // the current thumbnail bottom overlay progressBar.startPercent view-model shape.
    // Renderer shapes vary between classic, lockup and tile cards, so keep this bounded
    // recursive lookup in one place and let every card parser use it.
    internal static double ExtractWatchedPercent(JsonObject renderer)
    {
        if (renderer == null)
        {
            return 0;
        }

        foreach (var obj in EnumerateObjects(renderer, 800))
        {
            IJsonValue value;
            if ((!obj.TryGetValue("percentDurationWatched", out value) || value == null)
                && (!obj.TryGetValue("startPercent", out value) || value == null))
            {
                continue;
            }

            double percent;
            if (value.ValueType == JsonValueType.Number)
            {
                percent = value.GetNumber();
            }
            else if (value.ValueType == JsonValueType.String &&
                     double.TryParse(value.GetString(), System.Globalization.NumberStyles.Float,
                         System.Globalization.CultureInfo.InvariantCulture, out percent))
            {
            }
            else
            {
                continue;
            }

            return Math.Max(0, Math.Min(100, percent));
        }

        return 0;
    }

    // WEB and TVHTML5 put the author in different places. In TV search it is commonly
    // metadataRows[0].metadataParts[0] (lockup) or the first tile metadata line, while
    // classic WEB renderers use ownerText/shortBylineText.
    internal static string ExtractVideoCardAuthor(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        var author = FirstNonEmpty(
            ExtractTextFromField(renderer, "shortBylineText", string.Empty),
            ExtractTextFromField(renderer, "longBylineText", string.Empty),
            ExtractTextFromField(renderer, "ownerText", string.Empty),
            ExtractLockupMetadataPart(renderer, 0, 0),
            ExtractLockupChannelTitle(renderer));
        if (!string.IsNullOrWhiteSpace(author))
        {
            return author;
        }

        try
        {
            if (renderer.ContainsKey("metadata"))
            {
                var metadata = renderer.GetNamedObject("metadata");
                if (metadata.ContainsKey("tileMetadataRenderer"))
                {
                    var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                    if (tileMetadata.ContainsKey("lines"))
                    {
                        var lines = tileMetadata.GetNamedArray("lines");
                        if (lines.Count > 0 && lines[0].ValueType == JsonValueType.Object)
                        {
                            return ExtractTileLineItemText(lines[0].GetObject(), 0);
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    // Playlist lockups use the same author fields as videos, but their first generic
    // metadata part can be the video count. Do not accept that unfiltered value as the owner.
    internal static string ExtractPlaylistCardAuthor(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        var author = FirstNonEmpty(
            ExtractTextFromField(renderer, "shortBylineText", string.Empty),
            ExtractTextFromField(renderer, "longBylineText", string.Empty),
            ExtractTextFromField(renderer, "ownerText", string.Empty),
            ExtractLockupChannelTitle(renderer));
        if (!string.IsNullOrWhiteSpace(author) && !LooksLikePlaylistMetadata(author))
        {
            return author;
        }

        try
        {
            if (renderer.ContainsKey("metadata"))
            {
                var metadata = renderer.GetNamedObject("metadata");
                if (metadata.ContainsKey("tileMetadataRenderer"))
                {
                    var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                    if (tileMetadata.ContainsKey("lines"))
                    {
                        var lines = tileMetadata.GetNamedArray("lines");
                        if (lines.Count > 0 && lines[0].ValueType == JsonValueType.Object)
                        {
                            author = ExtractTileLineItemText(lines[0].GetObject(), 0);
                            if (!LooksLikePlaylistMetadata(author))
                            {
                                return author;
                            }
                        }
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }


    private static VideoCardItem ParsePlaylistPanelVideoRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var videoId = FirstNonEmpty(
            GetJsonString(renderer, "videoId"),
            ExtractVideoIdFromAnyValue(renderer, 500));
        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = ExtractTextFromField(renderer, "title", "Untitled"),
            ChannelTitle = FirstNonEmpty(
                ExtractTextFromField(renderer, "shortBylineText", string.Empty),
                ExtractTextFromField(renderer, "longBylineText", string.Empty),
                ExtractTextFromField(renderer, "ownerText", string.Empty),
                "Unknown"),
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = FirstNonEmpty(
                ExtractTextFromField(renderer, "lengthText", string.Empty),
                ExtractDurationFromOverlays(renderer),
                ExtractDurationFromAnyValue(renderer),
                string.Empty),
            ThumbnailUrl = FirstNonEmpty(
                ExtractBestThumbnailUrl(renderer, "thumbnail"),
                BuildMqThumbnailUrl(videoId)),
            ViewCount = FirstNonEmpty(
                ExtractTextFromField(renderer, "viewCountText", string.Empty),
                ExtractTextFromField(renderer, "shortViewCountText", string.Empty),
                string.Empty),
            WatchedPercent = ExtractWatchedPercent(renderer)
        };
    }

    private static VideoCardItem ParseLockupViewModel(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var contentType = GetJsonString(renderer, "contentType");
        if (!string.IsNullOrWhiteSpace(contentType) &&
            contentType.IndexOf("VIDEO", StringComparison.OrdinalIgnoreCase) < 0 &&
            contentType.IndexOf("SHORT", StringComparison.OrdinalIgnoreCase) < 0)
        {
            return null;
        }

        var videoId = FirstNonEmpty(
            GetJsonString(renderer, "contentId"),
            ExtractVideoIdFromAnyValue(renderer, 600));
        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        var title = FirstNonEmpty(
            ExtractLockupTitle(renderer),
            ExtractTextFromField(renderer, "title", string.Empty),
            "Untitled");

        // Lockup metadata layout (as SymTube reads it):
        //   row 0, part 0 -> channel, row 1, part 0 -> views, row 1, part 1 -> published.
        return new VideoCardItem
        {
            VideoId = videoId,
            Title = title,
            ChannelTitle = FirstNonEmpty(
                ExtractLockupMetadataPart(renderer, 0, 0),
                ExtractLockupChannelTitle(renderer),
                "Unknown"),
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = FirstNonEmpty(ExtractDurationFromAnyValue(renderer), string.Empty),
            ThumbnailUrl = FirstNonEmpty(
                ExtractBestThumbnailUrl(renderer, "thumbnail"),
                ExtractBestThumbnailUrl(renderer, "image"),
                ExtractFirstUrlFromAnyValue(renderer),
                BuildMqThumbnailUrl(videoId)),
            ViewCount = ExtractLockupMetadataPart(renderer, 1, 0),
            PublishedText = ExtractLockupMetadataPart(renderer, 1, 1),
            PlaylistId = ExtractPlaylistIdFromWatchEndpoints(renderer),
            WatchedPercent = ExtractWatchedPercent(renderer)
        };
    }

    // playlistId from any watchEndpoint inside the card (mix / "jam" cards carry one, both in
    // the classic navigationEndpoint shape and the lockup onTap.innertubeCommand shape).
    // "WL" (Watch Later), "LL" (Liked), "LM" (Liked Music), "HL" (History) are the account's
    // personal system lists. They appear on ordinary cards inside "Save"/menu endpoints, and a
    // deep scan for any playlistId would grab one and make a normal video open as, e.g., the
    // user's Watch Later with itself at the top. A real jam/mix is "RD...", a real playlist
    // "PL/OL/UU/...", never these two-letter ids — so reject them.
    // Upgrades a standard i.ytimg.com/vi/<id>/<name>.jpg thumbnail to maxresdefault, dropping any
    // query. Anything that is not that exact shape — a mix's stacked cover, a ggpht avatar, an
    // already-max URL — is returned untouched, so we never rewrite it into a wrong image.
    public static string UpgradeThumbnailToMaxRes(string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
        {
            return thumbnailUrl;
        }

        try
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                thumbnailUrl,
                @"^(https?://i\.ytimg\.com/vi/[^/]+/)[^/?]+\.jpg",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (match.Success)
            {
                return match.Groups[1].Value + "maxresdefault.jpg";
            }
        }
        catch
        {
        }

        return thumbnailUrl;
    }

    private static bool IsReservedPersonalPlaylistId(string playlistId)
    {
        if (string.IsNullOrWhiteSpace(playlistId))
        {
            return true;
        }

        var id = playlistId.Trim();
        return string.Equals(id, "WL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "LL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "LM", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "HL", StringComparison.OrdinalIgnoreCase)
            || string.Equals(id, "FL", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractPlaylistIdFromWatchEndpoints(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        // Mix / radio cards expose the playlist in several shapes:
        //   * watchEndpoint.playlistId          (a video opened inside a playlist)
        //   * watchPlaylistEndpoint.playlistId  (a mix card — SymTube handles this one too)
        //   * playlistId directly on the renderer (radioRenderer / compactRadioRenderer)
        foreach (var obj in EnumerateObjects(renderer, 600))
        {
            try
            {
                if (obj.ContainsKey("watchEndpoint"))
                {
                    var playlistId = GetJsonString(obj.GetNamedObject("watchEndpoint"), "playlistId");
                    if (!string.IsNullOrWhiteSpace(playlistId) && !IsReservedPersonalPlaylistId(playlistId))
                    {
                        return playlistId;
                    }
                }

                if (obj.ContainsKey("watchPlaylistEndpoint"))
                {
                    var playlistId = GetJsonString(obj.GetNamedObject("watchPlaylistEndpoint"), "playlistId");
                    if (!string.IsNullOrWhiteSpace(playlistId) && !IsReservedPersonalPlaylistId(playlistId))
                    {
                        return playlistId;
                    }
                }

                // NOTE: no blanket `obj.ContainsKey("playlistId")` scan here. That matched menu /
                // "Save" endpoints too, which is exactly how "WL" leaked onto ordinary cards. Only
                // the two real watch endpoints above are trusted.
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    // metadataRows[row].metadataParts[part].text.content inside a lockupViewModel.
    private static string ExtractLockupMetadataPart(JsonObject renderer, int rowIndex, int partIndex)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(renderer, 420))
        {
            if (!obj.ContainsKey("metadataRows"))
            {
                continue;
            }

            try
            {
                var rows = obj.GetNamedArray("metadataRows");
                if (rowIndex < 0 || rowIndex >= rows.Count || rows[rowIndex].ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var row = rows[rowIndex].GetObject();
                if (!row.ContainsKey("metadataParts"))
                {
                    continue;
                }

                var parts = row.GetNamedArray("metadataParts");
                if (partIndex < 0 || partIndex >= parts.Count)
                {
                    continue;
                }

                var text = NormalizeWhitespace(ExtractTextFromAnyValue(parts[partIndex]));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static VideoCardItem ParseReelItemRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var videoId = FirstNonEmpty(
            GetJsonString(renderer, "videoId"),
            ExtractVideoIdFromAnyValue(renderer, 400));
        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = FirstNonEmpty(
                ExtractTextFromField(renderer, "headline", string.Empty),
                ExtractTextFromField(renderer, "title", string.Empty),
                "Shorts"),
            ChannelTitle = FirstNonEmpty(
                ExtractTextFromField(renderer, "shortBylineText", string.Empty),
                ExtractLockupChannelTitle(renderer),
                "Unknown"),
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = string.Empty,
            ThumbnailUrl = FirstNonEmpty(
                ExtractBestThumbnailUrl(renderer, "thumbnail"),
                ExtractFirstUrlFromAnyValue(renderer),
                BuildMqThumbnailUrl(videoId)),
            ViewCount = FirstNonEmpty(
                ExtractTextFromField(renderer, "viewCountText", string.Empty),
                ExtractTextFromField(renderer, "shortViewCountText", string.Empty),
                ExtractTextFromField(renderer, "viewCount", string.Empty),
                string.Empty)
        };
    }

    private static VideoCardItem ParseShortsLockupViewModel(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var videoId = ExtractVideoIdFromAnyValue(renderer, 500);
        if (!IsValidYouTubeVideoId(videoId))
        {
            var entityId = GetJsonString(renderer, "entityId");
            videoId = ExtractVideoIdFromText(entityId);
        }

        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = FirstNonEmpty(
                ExtractShortsLockupText(renderer, "primaryText"),
                ExtractLockupTitle(renderer),
                "Shorts"),
            ChannelTitle = FirstNonEmpty(
                ExtractShortsLockupText(renderer, "secondaryText"),
                ExtractLockupChannelTitle(renderer),
                "Unknown"),
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = string.Empty,
            ThumbnailUrl = FirstNonEmpty(
                ExtractBestThumbnailUrl(renderer, "thumbnail"),
                ExtractFirstUrlFromAnyValue(renderer),
                BuildMqThumbnailUrl(videoId)),
            ViewCount = FirstNonEmpty(
                ExtractShortsLockupText(renderer, "secondaryText"),
                ExtractTextFromField(renderer, "viewCountText", string.Empty),
                ExtractTextFromField(renderer, "shortViewCountText", string.Empty),
                string.Empty)
        };
    }

    private static string ExtractLockupTitle(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(renderer, 260))
        {
            if (obj.ContainsKey("lockupMetadataViewModel"))
            {
                var modelValue = obj.GetNamedValue("lockupMetadataViewModel");
                if (modelValue.ValueType == JsonValueType.Object)
                {
                    var model = modelValue.GetObject();
                    var title = FirstNonEmpty(
                        ExtractTextFromNamedValue(model, "title"),
                        ExtractTextFromNamedValue(model, "heading"),
                        ExtractTextFromNamedValue(model, "primaryText"));
                    if (!string.IsNullOrWhiteSpace(title) && !LooksLikePlaylistMetadata(title))
                    {
                        return title;
                    }
                }
            }

            var direct = FirstNonEmpty(
                ExtractTextFromNamedValue(obj, "title"),
                ExtractTextFromNamedValue(obj, "headline"));
            if (!string.IsNullOrWhiteSpace(direct) && !LooksLikePlaylistMetadata(direct))
            {
                return direct;
            }
        }

        return string.Empty;
    }

    private static string ExtractShortsLockupText(JsonObject renderer, string key)
    {
        if (renderer == null || string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(renderer, 220))
        {
            if (obj.ContainsKey(key))
            {
                var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractLockupChannelTitle(JsonObject renderer)
    {
        if (renderer == null)
        {
            return string.Empty;
        }

        foreach (var obj in EnumerateObjects(renderer, 420))
        {
            if (!obj.ContainsKey("metadataRows"))
            {
                continue;
            }

            try
            {
                var rows = obj.GetNamedArray("metadataRows");
                for (var rowIndex = 0; rowIndex < rows.Count; rowIndex++)
                {
                    if (rows[rowIndex].ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var row = rows[rowIndex].GetObject();
                    if (!row.ContainsKey("metadataParts"))
                    {
                        continue;
                    }

                    var parts = row.GetNamedArray("metadataParts");
                    for (var partIndex = 0; partIndex < parts.Count; partIndex++)
                    {
                        var text = ExtractTextFromAnyValue(parts[partIndex]);
                        if (!string.IsNullOrWhiteSpace(text) && !LooksLikePlaylistMetadata(text) && !LooksLikeDuration(text))
                        {
                            return NormalizeWhitespace(text);
                        }
                    }
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static string ExtractTextFromNamedValue(JsonObject obj, string key)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
        {
            return string.Empty;
        }

        return NormalizeWhitespace(ExtractTextFromAnyValue(obj.GetNamedValue(key)));
    }

    private static string ExtractVideoIdFromAnyValue(IJsonValue value, int maxObjects)
    {
        if (value == null)
        {
            return string.Empty;
        }

        try
        {
            foreach (var obj in EnumerateObjects(value, maxObjects))
            {
                if (obj.ContainsKey("watchEndpoint"))
                {
                    var endpointValue = obj.GetNamedValue("watchEndpoint");
                    if (endpointValue.ValueType == JsonValueType.Object)
                    {
                        var id = GetJsonString(endpointValue.GetObject(), "videoId");
                        if (IsValidYouTubeVideoId(id))
                        {
                            return id;
                        }
                    }
                }

                if (obj.ContainsKey("reelWatchEndpoint"))
                {
                    var endpointValue = obj.GetNamedValue("reelWatchEndpoint");
                    if (endpointValue.ValueType == JsonValueType.Object)
                    {
                        var id = GetJsonString(endpointValue.GetObject(), "videoId");
                        if (IsValidYouTubeVideoId(id))
                        {
                            return id;
                        }
                    }
                }

                var direct = GetJsonString(obj, "videoId");
                if (IsValidYouTubeVideoId(direct))
                {
                    return direct;
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractVideoIdFromText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        for (var i = 0; i <= value.Length - 11; i++)
        {
            var candidate = value.Substring(i, 11);
            if (IsValidYouTubeVideoId(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }

    private static bool IsValidYouTubeVideoId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length != 11)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string ExtractDurationFromAnyValue(IJsonValue value)
    {
        if (value == null)
        {
            return string.Empty;
        }

        try
        {
            foreach (var obj in EnumerateObjects(value, 650))
            {
                foreach (var key in new[] { "lengthText", "duration", "text", "content", "label", "accessibilityText" })
                {
                    if (!obj.ContainsKey(key))
                    {
                        continue;
                    }

                    var text = ExtractTextFromAnyValue(obj.GetNamedValue(key));
                    var duration = ExtractDurationToken(text);
                    if (!string.IsNullOrWhiteSpace(duration))
                    {
                        return duration;
                    }
                }
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static string ExtractDurationToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var parts = text.Split(new[] { ' ', '\r', '\n', '\t', '•', '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = parts[i].Trim();
            if (LooksLikeDuration(candidate))
            {
                return candidate;
            }
        }

        return LooksLikeDuration(text.Trim()) ? text.Trim() : string.Empty;
    }

    private static bool LooksLikeDuration(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var value = text.Trim();
        var colonCount = 0;
        var digitCount = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsDigit(c))
            {
                digitCount++;
                continue;
            }
            if (c == ':')
            {
                colonCount++;
                continue;
            }
            return false;
        }

        return colonCount >= 1 && colonCount <= 2 && digitCount >= 2;
    }

    private static bool LooksLikePlaylistMetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return true;
        }

        var lower = text.Trim().ToLowerInvariant();
        return lower == "playlist" ||
               lower == "плейлист" ||
               lower == "play all" ||
               lower == "view full playlist" ||
               lower == "public" ||
               lower == "private" ||
               lower == "unlisted" ||
               lower == "общедоступный" ||
               lower == "ограниченный доступ" ||
               lower.IndexOf("видео", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf("просмотр", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf("обновлен", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf(" video", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf(" videos", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf(" view", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf(" views", StringComparison.OrdinalIgnoreCase) >= 0 ||
               lower.IndexOf("updated", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static VideoCardItem ParsePlaylistVideoRenderer(JsonObject renderer)
    {
        if (renderer == null)
        {
            return null;
        }

        var videoId = FirstNonEmpty(
            GetJsonString(renderer, "videoId"),
            ExtractVideoIdFromAnyValue(renderer, 500));
        if (!IsValidYouTubeVideoId(videoId))
        {
            return null;
        }

        var title = ExtractTextFromField(renderer, "title", "Untitled");
        var channelTitle = FirstNonEmpty(
            ExtractTextFromField(renderer, "shortBylineText", string.Empty),
            ExtractTextFromField(renderer, "longBylineText", string.Empty),
            ExtractTextFromField(renderer, "ownerText", string.Empty),
            "Unknown");

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = title,
            ChannelTitle = channelTitle,
            ChannelId = ExtractVideoCardChannelId(renderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(renderer),
            Duration = FirstNonEmpty(
                ExtractTextFromField(renderer, "lengthText", string.Empty),
                ExtractDurationFromOverlays(renderer),
                ExtractDurationFromAnyValue(renderer),
                string.Empty),
            ThumbnailUrl = FirstNonEmpty(
                ExtractBestThumbnailUrl(renderer, "thumbnail"),
                BuildMqThumbnailUrl(videoId)),
            WatchedPercent = ExtractWatchedPercent(renderer)
        };
    }

    internal static VideoCardItem ParseTileRenderer(JsonObject tileRenderer)
    {
        if (tileRenderer == null) return null;

        // A tile points either at a plain video (watchEndpoint) or at a mix / "jam"
        // (watchPlaylistEndpoint). Both carry a playlistId that has to survive the tap,
        // otherwise the mix opens as a single video with no queue. Same shapes SymTube reads.
        var videoId = string.Empty;
        var playlistId = string.Empty;
        if (tileRenderer.ContainsKey("onSelectCommand"))
        {
            var onSelect = tileRenderer.GetNamedObject("onSelectCommand");
            if (onSelect.ContainsKey("watchEndpoint"))
            {
                var watchEndpoint = onSelect.GetNamedObject("watchEndpoint");
                videoId = GetJsonString(watchEndpoint, "videoId");
                playlistId = GetJsonString(watchEndpoint, "playlistId");
            }
            else if (onSelect.ContainsKey("watchPlaylistEndpoint"))
            {
                var watchPlaylistEndpoint = onSelect.GetNamedObject("watchPlaylistEndpoint");
                videoId = GetJsonString(watchPlaylistEndpoint, "videoId");
                playlistId = GetJsonString(watchPlaylistEndpoint, "playlistId");
            }
        }

        if (IsReservedPersonalPlaylistId(playlistId))
        {
            playlistId = string.Empty;
        }

        if (string.IsNullOrWhiteSpace(playlistId))
        {
            playlistId = ExtractPlaylistIdFromWatchEndpoints(tileRenderer);
        }

        if (string.IsNullOrWhiteSpace(videoId)) return null;

        var title = "Untitled";
        var channelTitle = "Unknown";
        
        if (tileRenderer.ContainsKey("metadata"))
        {
            var metadata = tileRenderer.GetNamedObject("metadata");
            if (metadata.ContainsKey("tileMetadataRenderer"))
            {
                var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                
                // Получаем заголовок
                title = ExtractTextFromField(tileMetadata, "title", title);
                
                // Получаем имя канала из lines[0]
                if (tileMetadata.ContainsKey("lines"))
                {
                    var lines = tileMetadata.GetNamedArray("lines");
                    if (lines.Count > 0)
                    {
                        // Берем первую строку (индекс 0) - это имя канала
                        var channelLine = lines[0].GetObject();
                        if (channelLine.ContainsKey("lineRenderer"))
                        {
                            var lineRenderer = channelLine.GetNamedObject("lineRenderer");
                            
                            // В lineRenderer текст находится в items[0].lineItemRenderer.text
                            if (lineRenderer.ContainsKey("items"))
                            {
                                var items = lineRenderer.GetNamedArray("items");
                                if (items.Count > 0)
                                {
                                    var item = items[0].GetObject();
                                    if (item.ContainsKey("lineItemRenderer"))
                                    {
                                        var lineItemRenderer = item.GetNamedObject("lineItemRenderer");
                                        channelTitle = ExtractTextFromField(lineItemRenderer, "text", "Unknown");
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        var duration = string.Empty;
        if (tileRenderer.ContainsKey("header"))
        {
            var header = tileRenderer.GetNamedObject("header");
            if (header.ContainsKey("tileHeaderRenderer"))
            {
                duration = ExtractDurationFromOverlays(header.GetNamedObject("tileHeaderRenderer"));
            }
        }

        // lines[1] holds the second metadata line. SymTube reads it from the end, because the
        // number of items varies: last = published time, last-2 = view count.
        var viewCount = string.Empty;
        var publishedText = string.Empty;
        if (tileRenderer.ContainsKey("metadata"))
        {
            var metadataObj = tileRenderer.GetNamedObject("metadata");
            if (metadataObj.ContainsKey("tileMetadataRenderer"))
            {
                var tileMetadata = metadataObj.GetNamedObject("tileMetadataRenderer");
                if (tileMetadata.ContainsKey("lines"))
                {
                    var lines = tileMetadata.GetNamedArray("lines");
                    if (lines.Count > 1)
                    {
                        publishedText = ExtractTileLineItemText(lines[1].GetObject(), -1);
                        viewCount = ExtractTileLineItemText(lines[1].GetObject(), -3);
                    }
                }
            }
        }

        return new VideoCardItem
        {
            VideoId = videoId,
            Title = title,
            ChannelTitle = FirstNonEmpty(ExtractVideoCardAuthor(tileRenderer), channelTitle),
            ChannelId = ExtractVideoCardChannelId(tileRenderer),
            ChannelThumbnailUrl = ExtractVideoCardChannelThumbnail(tileRenderer),
            Duration = duration,
            ThumbnailUrl = BuildMqThumbnailUrl(videoId),
            ViewCount = viewCount,
            PublishedText = publishedText,
            PlaylistId = playlistId,
            WatchedPercent = ExtractWatchedPercent(tileRenderer)
        };
    }

    // Text of one item of a tileMetadataRenderer line. A negative index counts from the end
    // (-1 = last item), matching how SymTube addresses these variable-length lines.
    private static string ExtractTileLineItemText(JsonObject line, int index)
    {
        try
        {
            if (line == null || !line.ContainsKey("lineRenderer"))
            {
                return string.Empty;
            }

            var lineRenderer = line.GetNamedObject("lineRenderer");
            if (!lineRenderer.ContainsKey("items"))
            {
                return string.Empty;
            }

            var items = lineRenderer.GetNamedArray("items");
            var resolved = index < 0 ? items.Count + index : index;
            if (resolved < 0 || resolved >= items.Count)
            {
                return string.Empty;
            }

            var item = items[resolved].GetObject();
            if (!item.ContainsKey("lineItemRenderer"))
            {
                return string.Empty;
            }

            return ExtractTextFromField(item.GetNamedObject("lineItemRenderer"), "text", string.Empty);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractTextFromField(JsonObject obj, string fieldName, string fallback)
    {
        if (obj == null || !obj.ContainsKey(fieldName))
        {
            return fallback;
        }

        var field = obj.GetNamedValue(fieldName);
        if (field.ValueType == JsonValueType.String)
        {
            var direct = field.GetString();
            return string.IsNullOrWhiteSpace(direct) ? fallback : direct.Trim();
        }

        if (field.ValueType != JsonValueType.Object)
        {
            return fallback;
        }

        var fieldObject = field.GetObject();
        if (fieldObject.ContainsKey("simpleText"))
        {
            return fieldObject.GetNamedString("simpleText", fallback);
        }

        if (fieldObject.ContainsKey("content")
            && fieldObject["content"].ValueType == JsonValueType.String)
        {
            var content = fieldObject["content"].GetString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content.Trim();
            }
        }

        if (fieldObject.ContainsKey("runs"))
        {
            var runs = fieldObject.GetNamedArray("runs");
            var sb = new StringBuilder();
            for (var i = 0; i < runs.Count; i++)
            {
                var runObj = runs[i].GetObject();
                var piece = runObj.GetNamedString("text", string.Empty);
                if (!string.IsNullOrWhiteSpace(piece))
                {
                    sb.Append(piece);
                }
            }

            var value = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        return fallback;
    }

    // On Windows 10 Mobile prefer the native 16:9 mq image. Besides transferring fewer bytes,
    // it bypasses the SoftwareBitmap download/decode/crop path required by 4:3 hqdefault; that
    // removes a burst of CPU, memory and parallel thumbnail requests during first paint.
    // Desktop retains the sharper hq source.
    private static string BuildMqThumbnailUrl(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return string.Empty;
        }

        var fileName = UseMobileCardThumbnails ? "mqdefault.jpg" : "hqdefault.jpg";
        return "https://i.ytimg.com/vi/" + videoId + "/" + fileName;
    }

    private static bool DetectWindowsMobileDevice()
    {
        try
        {
            return string.Equals(
                Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily,
                "Windows.Mobile",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static string ExtractThumbnailUrl(JsonObject obj, string fieldName)
    {
        if (obj == null || !obj.ContainsKey(fieldName))
        {
            return string.Empty;
        }

        var photoObj = obj.GetNamedObject(fieldName);
        if (photoObj.ContainsKey("thumbnails"))
        {
            var thumbnails = photoObj.GetNamedArray("thumbnails");
            if (thumbnails.Count > 0)
            {
                var thumb0 = thumbnails[0].GetObject();
                if (thumb0.ContainsKey("url"))
                {
                    var url = thumb0.GetNamedString("url");
                    return url.StartsWith("//") ? "https:" + url : url;
                }
            }
        }

        return string.Empty;
    }

    private static string ExtractDurationFromOverlays(JsonObject renderer)
    {
        if (renderer == null || !renderer.ContainsKey("thumbnailOverlays"))
        {
            return string.Empty;
        }

        var overlays = renderer.GetNamedArray("thumbnailOverlays");
        for (var i = 0; i < overlays.Count; i++)
        {
            var overlayObj = overlays[i].GetObject();
            if (!overlayObj.ContainsKey("thumbnailOverlayTimeStatusRenderer"))
            {
                continue;
            }

            var status = overlayObj.GetNamedObject("thumbnailOverlayTimeStatusRenderer");
            var text = ExtractTextFromField(status, "text", string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    internal static IEnumerable<JsonObject> EnumerateObjects(IJsonValue value)
    {
        return EnumerateObjects(value, 0);
    }

    internal static IEnumerable<JsonObject> EnumerateObjects(IJsonValue value, int maxObjects)
    {
        if (value == null)
        {
            yield break;
        }

        var stack = new Stack<IJsonValue>();
        stack.Push(value);
        var visitedObjects = 0;

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current == null)
            {
                continue;
            }

            var currentType = current.ValueType;
            if (currentType == JsonValueType.Object)
            {
                if (maxObjects > 0 && visitedObjects >= maxObjects)
                {
                    yield break;
                }

                visitedObjects++;
                var obj = current.GetObject();
                yield return obj;

                foreach (var pair in obj)
                {
                    var child = pair.Value;
                    if (child == null)
                    {
                        continue;
                    }

                    var childType = child.ValueType;
                    if (childType == JsonValueType.Object || childType == JsonValueType.Array)
                    {
                        stack.Push(child);
                    }
                }
            }
            else if (currentType == JsonValueType.Array)
            {
                var arr = current.GetArray();
                // Push in reverse so enumeration keeps approximately the original JSON order.
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    var child = arr[i];
                    if (child == null)
                    {
                        continue;
                    }

                    var childType = child.ValueType;
                    if (childType == JsonValueType.Object || childType == JsonValueType.Array)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }

    private static bool ContainsAnyOrdinal(string text, string[] needles)
    {
        if (string.IsNullOrEmpty(text) || needles == null)
        {
            return false;
        }

        for (var i = 0; i < needles.Length; i++)
        {
            if (!string.IsNullOrEmpty(needles[i]) && text.IndexOf(needles[i], StringComparison.Ordinal) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static void DebugParserLog(string message)
    {
        if (VerboseParserLogs)
        {
            System.Diagnostics.Debug.WriteLine(message);
        }
    }

    private static string BuildTokenKey(string refreshToken)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return string.Empty;
        }

        // Do not store/log the whole token as a cache key. Length + tail is enough to
        // separate accounts in memory and avoids keeping another full token copy.
        var tailLength = Math.Min(16, refreshToken.Length);
        return refreshToken.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ":" + refreshToken.Substring(refreshToken.Length - tailLength, tailLength)
            + ":" + SelectedYouTubeAccountBrandId;
    }

    private static void RememberSubscriptionChannelAvatars(List<SubscriptionChannel> channels)
    {
        if (channels == null || channels.Count == 0)
            return;

        for (var i = 0; i < channels.Count; i++)
        {
            var channel = channels[i];
            if (channel == null)
                continue;

            RememberChannelAvatar(channel.ChannelId, channel.ChannelName, channel.ThumbnailUrl);
        }
    }

    private static void RememberChannelAvatar(string channelId, string channelTitle, string thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(thumbnailUrl))
            return;

        var cleanUrl = thumbnailUrl.Trim();
        var persistById = false;
        var normalizedChannelId = string.IsNullOrWhiteSpace(channelId)
            ? string.Empty
            : channelId.Trim();
        lock (_cacheGate)
        {
            if (!string.IsNullOrWhiteSpace(normalizedChannelId))
            {
                string previous;
                persistById = !_channelAvatarById.TryGetValue(normalizedChannelId, out previous)
                    || !string.Equals(previous, cleanUrl, StringComparison.Ordinal);
                _channelAvatarById[normalizedChannelId] = cleanUrl;
            }

            if (!string.IsNullOrWhiteSpace(channelTitle))
                _channelAvatarByTitle[channelTitle.Trim()] = cleanUrl;
        }

        // Channel avatars are public and stable. Persist them by UC id so returning to a feed (or
        // restarting the app) does not repeat a large metadata request for every visible card.
        if (persistById)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    ChannelAvatarCacheSettingPrefix + normalizedChannelId] = cleanUrl;
            }
            catch
            {
            }
        }
    }

    private static void ApplyKnownChannelIdToVideos(List<VideoCardItem> videos, string channelId)
    {
        if (videos == null || videos.Count == 0 || !IsYouTubeChannelId(channelId))
            return;

        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item != null && string.IsNullOrWhiteSpace(item.ChannelId))
                item.ChannelId = channelId;
        }
    }

    private static void RememberChannelAvatar(VideoCardItem item)
    {
        if (item == null)
            return;

        RememberChannelAvatar(item.ChannelId, item.ChannelTitle, item.ChannelThumbnailUrl);
    }

    private static string GetRememberedChannelAvatar(VideoCardItem item)
    {
        if (item == null)
            return string.Empty;

        lock (_cacheGate)
        {
            string value;
            if (!string.IsNullOrWhiteSpace(item.ChannelId)
                && _channelAvatarById.TryGetValue(item.ChannelId.Trim(), out value))
                return value;

            if (!string.IsNullOrWhiteSpace(item.ChannelTitle)
                && _channelAvatarByTitle.TryGetValue(item.ChannelTitle.Trim(), out value))
                return value;
        }

        if (!string.IsNullOrWhiteSpace(item.ChannelId))
        {
            try
            {
                var normalizedChannelId = item.ChannelId.Trim();
                object stored;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    ChannelAvatarCacheSettingPrefix + normalizedChannelId,
                    out stored))
                {
                    var storedUrl = stored as string;
                    if (!string.IsNullOrWhiteSpace(storedUrl))
                    {
                        lock (_cacheGate)
                        {
                            _channelAvatarById[normalizedChannelId] = storedUrl;
                            if (!string.IsNullOrWhiteSpace(item.ChannelTitle))
                                _channelAvatarByTitle[item.ChannelTitle.Trim()] = storedUrl;
                        }
                        return storedUrl;
                    }
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static async Task HydrateChannelThumbnailsWithRefreshTokenAsync(List<VideoCardItem> videos, string refreshToken)
    {
        if (videos == null || videos.Count == 0 || !ChannelIconController.IsEnabled())
            return;

        // First use direct renderer URLs and the in-memory cache. Only ask the Data API when
        // the renderer omitted an avatar but did provide a real UC channel id.
        await HydrateMissingChannelThumbnailsAsync(videos, string.Empty).ConfigureAwait(false);
        if (!HasMissingChannelThumbnails(videos) || string.IsNullOrWhiteSpace(refreshToken))
            return;

        var accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(accessToken))
            await HydrateMissingChannelThumbnailsAsync(videos, accessToken).ConfigureAwait(false);
    }

    private static bool HasMissingChannelThumbnails(List<VideoCardItem> videos)
    {
        if (videos == null || videos.Count == 0 || !ChannelIconController.IsEnabled())
            return false;

        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item != null
                && string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                && IsYouTubeChannelId(item.ChannelId))
                return true;
        }

        return false;
    }

    internal static async Task HydrateMissingChannelThumbnailsAsync(List<VideoCardItem> videos, string accessToken)
    {
        if (videos == null || videos.Count == 0 || !ChannelIconController.IsEnabled())
            return;

        // 1) Renderer URL / already-known channel cache. This is always the fastest path.
        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item == null)
                continue;

            if (!string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl))
            {
                RememberChannelAvatar(item);
                continue;
            }

            var remembered = GetRememberedChannelAvatar(item);
            if (!string.IsNullOrWhiteSpace(remembered))
                item.ChannelThumbnailUrl = remembered;
        }

        // On phones network hydration is started by ChannelIconController only after a card Image
        // has been loaded. Starting it here makes parsing compete with the first layout pass and
        // also wastes requests on cards which have not been realized yet.
        if (global::YouTube.ResponsiveLayout.IsPhoneDevice)
            return;

        await HydrateMissingChannelThumbnailsNetworkAsync(videos, accessToken).ConfigureAwait(false);
    }

    // Called for one realized mobile card after its first layout pass. Keeping this entry point
    // separate prevents the normal feed parser from starting avatar traffic prematurely.
    internal static async Task HydrateVisibleChannelThumbnailAsync(VideoCardItem video)
    {
        if (video == null || !ChannelIconController.IsEnabled())
            return;

        var videos = new List<VideoCardItem> { video };
        await HydrateMissingChannelThumbnailsAsync(videos, string.Empty).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(video.ChannelThumbnailUrl))
            await HydrateMissingChannelThumbnailsNetworkSafeAsync(videos, string.Empty).ConfigureAwait(false);
    }

    private static async Task HydrateMissingChannelThumbnailsNetworkSafeAsync(
        List<VideoCardItem> videos,
        string accessToken)
    {
        try
        {
            await HydrateMissingChannelThumbnailsNetworkAsync(videos, accessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[ChannelIcons] Background mobile hydration failed: " + ex.Message);
        }
    }

    private static async Task HydrateMissingChannelThumbnailsNetworkAsync(
        List<VideoCardItem> videos,
        string accessToken)
    {
        if (videos == null || videos.Count == 0 || !ChannelIconController.IsEnabled())
            return;

        var missingIds = CollectMissingChannelIds(videos);

        // 2) Keep the single-request Data API batch as an optional fast path when the current
        // account token allows it. The TV device-flow token used by older installs commonly lacks
        // a YouTube Data API v3 scope; after the first 401/403 we disable this path for the process
        // instead of paying for the same rejected request on every feed.
        var canTryDataApi = missingIds.Count > 0 && !string.IsNullOrWhiteSpace(accessToken);
        lock (_cacheGate)
        {
            if (_channelDataApiUnavailable)
                canTryDataApi = false;
        }

        if (canTryDataApi)
        {
            await TryHydrateChannelThumbnailsViaDataApiAsync(missingIds, accessToken).ConfigureAwait(false);
            ApplyRememberedChannelAvatars(videos);
            missingIds = CollectMissingChannelIds(videos);
        }

        // 3) Resolve remaining cards progressively. Do not wait for every channel before assigning
        // the first successful image: one slow request used to hold the whole feed blank.
        var tasks = new List<Task>();
        for (var i = 0; i < missingIds.Count; i++)
            tasks.Add(ResolveAndApplyChannelAvatarAsync(videos, missingIds[i]));

        // A few lockup/tile renderers expose an author name but no UC id. Use their video /next as
        // a last resort so those cards are not permanently missing an icon.
        var missingVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item != null
                && string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                && !IsYouTubeChannelId(item.ChannelId)
                && !string.IsNullOrWhiteSpace(item.VideoId)
                && missingVideoIds.Add(item.VideoId))
            {
                tasks.Add(ResolveAndApplyVideoAvatarAsync(videos, item.VideoId));
            }
        }

        if (tasks.Count > 0)
            await Task.WhenAll(tasks).ConfigureAwait(false);

        ApplyRememberedChannelAvatars(videos);
    }

    private static async Task ResolveAndApplyChannelAvatarAsync(
        List<VideoCardItem> videos,
        string channelId)
    {
        var avatarUrl = await GetChannelAvatarViaInnertubeAsync(channelId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(avatarUrl))
        {
            // If the channel header shape changed, its video watch response provides an independent
            // route to the owner avatar. One fallback per channel is enough.
            var fallbackVideoId = string.Empty;
            for (var i = 0; i < videos.Count; i++)
            {
                var candidate = videos[i];
                if (candidate != null
                    && string.Equals(candidate.ChannelId, channelId, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(candidate.VideoId))
                {
                    fallbackVideoId = candidate.VideoId;
                    break;
                }
            }

            if (!string.IsNullOrWhiteSpace(fallbackVideoId))
                avatarUrl = await ResolveChannelAvatarViaVideoAsync(fallbackVideoId).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(avatarUrl))
            return;

        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item != null
                && string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                && string.Equals(item.ChannelId, channelId, StringComparison.OrdinalIgnoreCase))
            {
                item.ChannelThumbnailUrl = avatarUrl;
                RememberChannelAvatar(item);
            }
        }
    }

    private static async Task ResolveAndApplyVideoAvatarAsync(
        List<VideoCardItem> videos,
        string videoId)
    {
        var avatarUrl = await ResolveChannelAvatarViaVideoAsync(videoId).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(avatarUrl))
            return;

        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item != null
                && string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                && string.Equals(item.VideoId, videoId, StringComparison.OrdinalIgnoreCase))
            {
                item.ChannelThumbnailUrl = avatarUrl;
                RememberChannelAvatar(item);
            }
        }
    }

    private static List<string> CollectMissingChannelIds(List<VideoCardItem> videos)
    {
        var result = new List<string>();
        if (videos == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item == null
                || !string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                || !IsYouTubeChannelId(item.ChannelId))
                continue;

            var id = item.ChannelId.Trim();
            if (seen.Add(id))
                result.Add(id);
        }

        return result;
    }

    private static void ApplyRememberedChannelAvatars(List<VideoCardItem> videos)
    {
        if (videos == null)
            return;

        for (var i = 0; i < videos.Count; i++)
        {
            var item = videos[i];
            if (item == null || !string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl))
                continue;

            var remembered = GetRememberedChannelAvatar(item);
            if (!string.IsNullOrWhiteSpace(remembered))
                item.ChannelThumbnailUrl = remembered;
        }
    }

    private static async Task TryHydrateChannelThumbnailsViaDataApiAsync(List<string> channelIds, string accessToken)
    {
        if (channelIds == null || channelIds.Count == 0 || string.IsNullOrWhiteSpace(accessToken))
            return;

        for (var offset = 0; offset < channelIds.Count; offset += 50)
        {
            var take = Math.Min(50, channelIds.Count - offset);
            var batch = new List<string>();
            for (var i = 0; i < take; i++)
                batch.Add(channelIds[offset + i]);

            var url = "https://www.googleapis.com/youtube/v3/channels?part=snippet&id="
                + Uri.EscapeDataString(string.Join(",", batch))
                + "&maxResults=50&prettyPrint=false";

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                    request.Headers.TryAddWithoutValidation("User-Agent", WebUserAgent);
                    var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        if ((int)response.StatusCode == 401 || (int)response.StatusCode == 403)
                        {
                            lock (_cacheGate)
                                _channelDataApiUnavailable = true;
                        }

                        System.Diagnostics.Debug.WriteLine("[ChannelIcons] Data API batch lookup failed: " + response.StatusCode);
                        return;
                    }

                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JsonObject.Parse(json);
                    if (!root.ContainsKey("items") || root.GetNamedValue("items").ValueType != JsonValueType.Array)
                        continue;

                    var items = root.GetNamedArray("items");
                    for (var i = 0; i < items.Count; i++)
                    {
                        if (items[i].ValueType != JsonValueType.Object)
                            continue;

                        var channel = items[i].GetObject();
                        var channelId = GetJsonString(channel, "id");
                        if (!IsYouTubeChannelId(channelId)
                            || !channel.ContainsKey("snippet")
                            || channel.GetNamedValue("snippet").ValueType != JsonValueType.Object)
                            continue;

                        var snippet = channel.GetNamedObject("snippet");
                        var title = GetJsonString(snippet, "title");
                        var avatarUrl = string.Empty;
                        if (snippet.ContainsKey("thumbnails") && snippet.GetNamedValue("thumbnails").ValueType == JsonValueType.Object)
                            avatarUrl = ExtractDataApiThumbnailUrl(snippet.GetNamedObject("thumbnails"));

                        RememberChannelAvatar(channelId, title, avatarUrl);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChannelIcons] Data API batch lookup exception: " + ex.Message);
                return;
            }
        }
    }

    private static Task<string> GetChannelAvatarViaInnertubeAsync(string channelId)
    {
        if (!IsYouTubeChannelId(channelId))
            return Task.FromResult(string.Empty);

        channelId = channelId.Trim();
        lock (_cacheGate)
        {
            string cached;
            if (_channelAvatarById.TryGetValue(channelId, out cached) && !string.IsNullOrWhiteSpace(cached))
                return Task.FromResult(cached);

            Task<string> pending;
            if (_channelAvatarLookupTasks.TryGetValue(channelId, out pending))
                return pending;

            pending = ResolveChannelAvatarViaInnertubeCoreAsync(channelId);
            _channelAvatarLookupTasks[channelId] = pending;
            return pending;
        }
    }

    private static async Task<string> ResolveChannelAvatarViaInnertubeCoreAsync(string channelId)
    {
        // Force an asynchronous boundary before this method can re-enter _cacheGate in finally.
        // GetChannelAvatarViaInnertubeAsync creates the shared task while holding that lock.
        await Task.Yield();
        await _channelAvatarNetworkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            // MWEB channel metadata is considerably smaller than the full desktop WEB channel
            // response (roughly one third in current responses), which matters on W10M.
            var payload = "{\"context\":{\"client\":{\"clientName\":\"MWEB\",\"clientVersion\":\""
                + ShortsMwebClientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl
                + "\",\"platform\":\"MOBILE\"}},\"browseId\":\"" + JsonEscape(channelId) + "\"}";
            var json = await PostInnertubeJsonAsync(
                "browse",
                payload,
                string.Empty,
                "MWEB",
                ShortsMwebClientVersion,
                true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            var root = JsonValue.Parse(json);
            var title = string.Empty;
            var avatarUrl = string.Empty;

            foreach (var obj in EnumerateObjects(root, 1800))
            {
                try
                {
                    if (obj.ContainsKey("channelMetadataRenderer")
                        && obj.GetNamedValue("channelMetadataRenderer").ValueType == JsonValueType.Object)
                    {
                        var metadata = obj.GetNamedObject("channelMetadataRenderer");
                        title = FirstNonEmpty(title, GetJsonString(metadata, "title"));
                        avatarUrl = FirstNonEmpty(
                            avatarUrl,
                            ExtractBestThumbnailUrl(metadata, "avatar"),
                            ExtractFirstUrlFromNamedValue(metadata, "avatar"));
                    }

                    if (string.IsNullOrWhiteSpace(avatarUrl) && obj.ContainsKey("avatar"))
                    {
                        var candidate = ExtractFirstUrlFromAnyValue(obj.GetNamedValue("avatar"));
                        if (!string.IsNullOrWhiteSpace(candidate))
                            avatarUrl = candidate;
                    }

                    if (!string.IsNullOrWhiteSpace(avatarUrl) && !string.IsNullOrWhiteSpace(title))
                        break;
                }
                catch
                {
                }
            }

            if (!string.IsNullOrWhiteSpace(avatarUrl))
            {
                RememberChannelAvatar(channelId, title, avatarUrl);
                System.Diagnostics.Debug.WriteLine("[ChannelIcons] Innertube avatar resolved for " + channelId);
                return avatarUrl;
            }

            System.Diagnostics.Debug.WriteLine("[ChannelIcons] Innertube avatar missing for " + channelId);
            return string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[ChannelIcons] Innertube avatar lookup exception: " + ex.Message);
            return string.Empty;
        }
        finally
        {
            _channelAvatarNetworkGate.Release();
            lock (_cacheGate)
                _channelAvatarLookupTasks.Remove(channelId);
        }
    }

    private static async Task<string> ResolveChannelAvatarViaVideoAsync(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId))
            return string.Empty;

        await _channelAvatarNetworkGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var payload = "{\"context\":{\"client\":{\"clientName\":\"MWEB\",\"clientVersion\":\""
                + ShortsMwebClientVersion + "\",\"hl\":\"" + Hl + "\",\"gl\":\"" + Gl
                + "\",\"platform\":\"MOBILE\"}},\"videoId\":\"" + JsonEscape(videoId) + "\"}";
            var json = await PostInnertubeJsonAsync(
                "next",
                payload,
                string.Empty,
                "MWEB",
                ShortsMwebClientVersion,
                true).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
                return string.Empty;

            var root = JsonValue.Parse(json);
            var avatarUrl = ExtractVideoCardChannelThumbnail(root.GetObject());
            if (!string.IsNullOrWhiteSpace(avatarUrl))
                return avatarUrl;

            // The owner block can be deeper than the card-oriented fast scan on some MWEB builds.
            // Inspect only avatar-specific keys to avoid ever returning the video's own thumbnail.
            var avatarKeys = new[]
            {
                "channelThumbnail",
                "authorThumbnail",
                "ownerThumbnail",
                "decoratedAvatarViewModel",
                "avatarViewModel",
                "channelAvatar",
                "avatar"
            };
            foreach (var obj in EnumerateObjects(root, 3200))
            {
                for (var i = 0; i < avatarKeys.Length; i++)
                {
                    var key = avatarKeys[i];
                    if (!obj.ContainsKey(key))
                        continue;

                    try
                    {
                        avatarUrl = ExtractFirstUrlFromAnyValue(obj.GetNamedValue(key));
                        if (!string.IsNullOrWhiteSpace(avatarUrl))
                            return avatarUrl;
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[ChannelIcons] Video owner avatar fallback failed: " + ex.Message);
        }
        finally
        {
            _channelAvatarNetworkGate.Release();
        }

        return string.Empty;
    }

    private static string BuildVideoListCacheKey(string kind, string browseId, string continuationToken, int count)
    {
        return (kind ?? string.Empty) + "|" + (browseId ?? string.Empty) + "|" + (continuationToken ?? string.Empty) + "|" + count.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static bool TryGetCachedVideoList(string refreshToken, string cacheKey, out List<VideoCardItem> items)
    {
        items = null;
        lock (_cacheGate)
        {
            VideoListCacheEntry entry;
            if (_videoListCache.TryGetValue(cacheKey, out entry) &&
                entry != null &&
                entry.Items != null &&
                entry.ExpiresUtc > DateTime.UtcNow &&
                string.Equals(entry.TokenKey, BuildTokenKey(refreshToken), StringComparison.Ordinal))
            {
                items = new List<VideoCardItem>(entry.Items);
                return true;
            }
        }

        return false;
    }

    private static void SaveCachedVideoList(string refreshToken, string cacheKey, List<VideoCardItem> items)
    {
        if (items == null)
        {
            return;
        }

        lock (_cacheGate)
        {
            if (_videoListCache.Count >= MaxVideoListCacheEntries)
            {
                PruneExpiredVideoListCacheUnsafe();
            }

            if (_videoListCache.Count >= MaxVideoListCacheEntries)
            {
                _videoListCache.Clear();
            }

            _videoListCache[cacheKey] = new VideoListCacheEntry
            {
                TokenKey = BuildTokenKey(refreshToken),
                ExpiresUtc = DateTime.UtcNow.AddSeconds(BrowseCacheSeconds),
                Items = new List<VideoCardItem>(items)
            };
        }
    }

    private static void PruneExpiredVideoListCacheUnsafe()
    {
        var now = DateTime.UtcNow;
        var expired = new List<string>();
        foreach (var pair in _videoListCache)
        {
            if (pair.Value == null || pair.Value.ExpiresUtc <= now)
            {
                expired.Add(pair.Key);
            }
        }

        for (var i = 0; i < expired.Count; i++)
        {
            _videoListCache.Remove(expired[i]);
        }
    }

    private static bool TryGetCachedSubscriptions(string refreshToken, out List<SubscriptionChannel> items)
    {
        items = null;
        lock (_cacheGate)
        {
            if (_subscriptionsCache != null &&
                _subscriptionsCache.Items != null &&
                _subscriptionsCache.ExpiresUtc > DateTime.UtcNow &&
                string.Equals(_subscriptionsCache.TokenKey, BuildTokenKey(refreshToken), StringComparison.Ordinal))
            {
                items = new List<SubscriptionChannel>(_subscriptionsCache.Items);
                return true;
            }
        }

        return false;
    }

    private static void SaveCachedSubscriptions(string refreshToken, List<SubscriptionChannel> items)
    {
        if (items == null)
        {
            return;
        }

        lock (_cacheGate)
        {
            _subscriptionsCache = new SubscriptionsCacheEntry
            {
                TokenKey = BuildTokenKey(refreshToken),
                ExpiresUtc = DateTime.UtcNow.AddSeconds(SubscriptionsCacheSeconds),
                Items = new List<SubscriptionChannel>(items)
            };
        }
    }

    private static bool TryGetCachedAccount(string refreshToken, out AccountInfo account)
    {
        account = null;
        lock (_cacheGate)
        {
            if (_accountCache != null &&
                _accountCache.Account != null &&
                _accountCache.ExpiresUtc > DateTime.UtcNow &&
                string.Equals(_accountCache.TokenKey, BuildTokenKey(refreshToken), StringComparison.Ordinal))
            {
                account = _accountCache.Account;
                return true;
            }
        }

        return false;
    }

    private static void SaveCachedAccount(string refreshToken, AccountInfo account)
    {
        if (account == null)
        {
            return;
        }

        lock (_cacheGate)
        {
            _accountCache = new AccountCacheEntry
            {
                TokenKey = BuildTokenKey(refreshToken),
                ExpiresUtc = DateTime.UtcNow.AddSeconds(AccountCacheSeconds),
                Account = account
            };
        }
    }

    private static List<YouTubeAccountItem> ParseYouTubeAccountsList(string json)
    {
        var result = new List<YouTubeAccountItem>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var root = JsonValue.Parse(json);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in EnumerateObjects(root, 12000))
            {
                IJsonValue accountValue;
                if (!obj.TryGetValue("accountItem", out accountValue)
                    || accountValue.ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var account = accountValue.GetObject();
                var displayName = ExtractTextFromField(account, "accountName", string.Empty);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    continue;
                }

                var handle = ExtractTextFromField(account, "channelHandle", string.Empty);
                var brandId = string.Empty;
                foreach (var nested in EnumerateObjects(account, 160))
                {
                    IJsonValue pageIdTokenValue;
                    if (nested.TryGetValue("pageIdToken", out pageIdTokenValue)
                        && pageIdTokenValue.ValueType == JsonValueType.Object)
                    {
                        brandId = GetJsonString(pageIdTokenValue.GetObject(), "pageId");
                        if (!string.IsNullOrWhiteSpace(brandId))
                        {
                            break;
                        }
                    }
                }

                var uniqueKey = string.IsNullOrWhiteSpace(brandId)
                    ? "primary:" + displayName + ":" + handle
                    : "brand:" + brandId;
                if (!seen.Add(uniqueKey))
                {
                    continue;
                }

                var selected = false;
                IJsonValue selectedValue;
                if (account.TryGetValue("isSelected", out selectedValue)
                    && selectedValue.ValueType == JsonValueType.Boolean)
                {
                    selected = selectedValue.GetBoolean();
                }

                result.Add(new YouTubeAccountItem
                {
                    AccountKey = uniqueKey,
                    DisplayName = displayName,
                    ChannelHandle = handle,
                    ThumbnailUrl = ExtractBestThumbnailUrl(account, "accountPhoto"),
                    BrandId = brandId,
                    IsPrimaryOwner = account.ContainsKey("accountByline"),
                    IsSelected = selected
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Accounts] Parse failed: " + ex.Message);
        }

        System.Diagnostics.Debug.WriteLine("[Accounts] Parsed " + result.Count + " account(s)");
        return result;
    }

    private static List<YouTubeAccountItem> ApplyPersistedAccountSelection(List<YouTubeAccountItem> accounts)
    {
        accounts = accounts ?? new List<YouTubeAccountItem>();
        if (accounts.Count == 0)
        {
            return accounts;
        }

        var values = ApplicationData.Current.LocalSettings.Values;
        var initialized = values.ContainsKey(SelectedYouTubeAccountInitializedSetting)
            && values[SelectedYouTubeAccountInitializedSetting] is bool
            && (bool)values[SelectedYouTubeAccountInitializedSetting];
        var selectedAccountKey = SelectedYouTubeAccountKey;

        // Older builds persisted only BrandId. Several owner/persona/Kids rows legitimately
        // have an empty BrandId, so comparing that value made every one of them look selected.
        // Migrate those settings to the exact account row and prefer the same primary-account
        // marker (accountByline) that the original profile parser used.
        if (!initialized || string.IsNullOrWhiteSpace(selectedAccountKey))
        {
            var persistedBrandId = SelectedYouTubeAccountBrandId;
            var persistedBrandAccount = !string.IsNullOrWhiteSpace(persistedBrandId)
                ? accounts.FirstOrDefault(item => item != null && string.Equals(
                    item.BrandId ?? string.Empty,
                    persistedBrandId,
                    StringComparison.Ordinal))
                : null;
            var serverSelectedItems = accounts.Where(item => item != null && item.IsSelected).ToList();
            var serverSelected = serverSelectedItems.Count == 1 ? serverSelectedItems[0] : null;
            var selected = persistedBrandAccount
                ?? accounts.FirstOrDefault(item => item != null && item.IsPrimaryOwner)
                ?? serverSelected
                ?? accounts.FirstOrDefault(item => item != null && string.IsNullOrWhiteSpace(item.BrandId))
                ?? accounts[0];
            values[SelectedYouTubeAccountBrandIdSetting] = selected.BrandId ?? string.Empty;
            values[SelectedYouTubeAccountKeySetting] = selected.AccountKey ?? string.Empty;
            values[SelectedYouTubeAccountInitializedSetting] = true;
            selectedAccountKey = selected.AccountKey ?? string.Empty;
        }

        var matched = false;
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            if (account == null)
            {
                continue;
            }

            account.IsSelected = !matched && string.Equals(
                account.AccountKey ?? string.Empty,
                selectedAccountKey ?? string.Empty,
                StringComparison.Ordinal);
            matched = matched || account.IsSelected;
        }

        if (!matched)
        {
            var fallback = accounts.FirstOrDefault(item => item != null && item.IsPrimaryOwner)
                ?? accounts.FirstOrDefault(item => item != null && string.IsNullOrWhiteSpace(item.BrandId))
                ?? accounts[0];
            values[SelectedYouTubeAccountBrandIdSetting] = fallback.BrandId ?? string.Empty;
            values[SelectedYouTubeAccountKeySetting] = fallback.AccountKey ?? string.Empty;
            for (var i = 0; i < accounts.Count; i++)
            {
                if (accounts[i] != null)
                {
                    accounts[i].IsSelected = object.ReferenceEquals(accounts[i], fallback);
                }
            }
        }

        return accounts;
    }

    private static AccountInfo ParseAccountInfoFromAccountsList(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: Empty JSON response");
                return null;
            }

            System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: JSON length = " + json.Length);
            
            var root = JsonObject.Parse(json);
            
            // Parse response like yt-api-legacy-main does:
            // contents[0].accountSectionListRenderer.contents[0].accountItemSectionRenderer.contents
            if (!root.ContainsKey("contents"))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: No 'contents' key");
                return null;
            }

            var contents = root.GetNamedArray("contents");
            if (contents.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: Empty contents array");
                return null;
            }

            var item0 = contents[0].GetObject();
            if (!item0.ContainsKey("accountSectionListRenderer"))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: No 'accountSectionListRenderer' key");
                return null;
            }

            var sectionListRenderer = item0.GetNamedObject("accountSectionListRenderer");
            if (!sectionListRenderer.ContainsKey("contents"))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: No 'contents' in sectionListRenderer");
                return null;
            }

            var sectionContents = sectionListRenderer.GetNamedArray("contents");
            if (sectionContents.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: Empty sectionContents array");
                return null;
            }

            var sectionItem0 = sectionContents[0].GetObject();
            if (!sectionItem0.ContainsKey("accountItemSectionRenderer"))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: No 'accountItemSectionRenderer' key");
                return null;
            }

            var itemSectionRenderer = sectionItem0.GetNamedObject("accountItemSectionRenderer");
            if (!itemSectionRenderer.ContainsKey("contents"))
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: No 'contents' in itemSectionRenderer");
                return null;
            }

            var accounts = itemSectionRenderer.GetNamedArray("contents");
            System.Diagnostics.Debug.WriteLine($"ParseAccountInfoFromAccountsList: Found {accounts.Count} accounts");

            // Find primary account (the one with accountByline)
            JsonObject primaryAccount = null;
            foreach (var accountToken in accounts)
            {
                if (accountToken.ValueType == Windows.Data.Json.JsonValueType.Object)
                {
                    var accountObj = accountToken.GetObject();
                    if (accountObj.ContainsKey("accountItem"))
                    {
                        var accountItem = accountObj.GetNamedObject("accountItem");
                        // Primary account has accountByline
                        if (accountItem.ContainsKey("accountByline"))
                        {
                            primaryAccount = accountItem;
                            break;
                        }
                    }
                }
            }

            if (primaryAccount == null)
            {
                System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: Primary account not found");
                return null;
            }

            System.Diagnostics.Debug.WriteLine("ParseAccountInfoFromAccountsList: Found primary account");

            // Extract data
            var accountName = ExtractSimpleText(primaryAccount, "accountName") ?? "Unknown";
            var accountByline = ExtractSimpleText(primaryAccount, "accountByline") ?? "";
            var channelHandle = ExtractSimpleText(primaryAccount, "channelHandle") ?? "";
            var thumbnailUrl = ExtractThumbnailUrl(primaryAccount, "accountPhoto");

            // Remove @ from channelHandle if present
            if (!string.IsNullOrEmpty(channelHandle) && channelHandle.StartsWith("@"))
            {
                channelHandle = channelHandle.Substring(1);
            }

            // Check if has channel
            var hasChannel = false;
            if (primaryAccount.ContainsKey("hasChannel"))
            {
                var hasChannelValue = primaryAccount.GetNamedValue("hasChannel");
                if (hasChannelValue.ValueType == Windows.Data.Json.JsonValueType.Boolean)
                {
                    hasChannel = primaryAccount.GetNamedBoolean("hasChannel");
                }
            }

            System.Diagnostics.Debug.WriteLine($"ParseAccountInfoFromAccountsList: Name={accountName}, Handle={channelHandle}, HasChannel={hasChannel}");

            return new AccountInfo
            {
                DisplayName = accountName,
                ChannelHandle = channelHandle,
                SubscribersCount = 0, // accounts_list doesn't provide subscriber count
                ThumbnailUrl = thumbnailUrl ?? ""
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"ParseAccountInfoFromAccountsList ERROR: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"Stack: {ex.StackTrace}");
            return null;
        }
    }

    private static string ExtractSimpleText(JsonObject obj, string fieldName)
    {
        if (obj == null || !obj.ContainsKey(fieldName))
        {
            return null;
        }

        var fieldObj = obj.GetNamedObject(fieldName);
        if (fieldObj.ContainsKey("simpleText"))
        {
            return fieldObj.GetNamedString("simpleText");
        }

        return null;
    }
}

// Model classes

public sealed class HistoryFeedPage
{
    public List<HistoryDateGroup> Groups { get; set; }
    public string ContinuationToken { get; set; }

    public HistoryFeedPage()
    {
        Groups = new List<HistoryDateGroup>();
        ContinuationToken = string.Empty;
    }
}

public sealed class HistoryDateGroup
{
    public string DateTitle { get; set; }
    public List<VideoCardItem> Videos { get; set; }
    public List<VideoCardItem> Shorts { get; set; }

    public HistoryDateGroup()
    {
        DateTitle = string.Empty;
        Videos = new List<VideoCardItem>();
        Shorts = new List<VideoCardItem>();
    }
}

public sealed class HomeCategoryItem
{
    public string Title { get; set; }
    public string CategoryId { get; set; }
    public bool IsAll { get; set; }
    public string BrowseId { get; set; }
    public string Params { get; set; }
    public string ContinuationToken { get; set; }
    public bool UseWebClient { get; set; }
    public string ClientName { get; set; }
    public string SearchQuery { get; set; }

    public string Key
    {
        get
        {
            if (IsAll)
            {
                return "all";
            }

            if (!string.IsNullOrWhiteSpace(CategoryId))
            {
                return "category:" + CategoryId;
            }

            if (!string.IsNullOrWhiteSpace(ContinuationToken))
            {
                return "continuation:" + (ClientName ?? string.Empty) + ":" + ContinuationToken;
            }

            if (!string.IsNullOrWhiteSpace(BrowseId))
            {
                return "browse:" + (ClientName ?? string.Empty) + ":" + BrowseId + ":" + (Params ?? string.Empty);
            }

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                return "search:" + SearchQuery;
            }

            return "title:" + (Title ?? string.Empty);
        }
    }
}

public sealed class VideoCardItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _channelThumbnailUrl;
    private double _watchedPercent;

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

    public string VideoId { get; set; }
    public string Title { get; set; }
    public string ChannelTitle { get; set; }
    public string ChannelId { get; set; }
    public string ChannelThumbnailUrl
    {
        get { return _channelThumbnailUrl; }
        set
        {
            if (string.Equals(_channelThumbnailUrl, value, StringComparison.Ordinal))
                return;
            _channelThumbnailUrl = value;
            RaisePropertyChanged("ChannelThumbnailUrl");
        }
    }
    public string Duration { get; set; }
    public string ThumbnailUrl { get; set; }

    // Full-resolution frame for the large cards. Built by upgrading the thumbnail the API gave
    // us — NOT by deriving a URL from VideoId, which discarded a mix's stacked cover and could
    // point at the wrong frame. Missing maxresdefault falls back to ThumbnailUrl on ImageFailed.
    public string LargeThumbnailUrl
    {
        get { return Config.UpgradeThumbnailToMaxRes(ThumbnailUrl); }
    }
    public string ViewCount { get; set; }
    public string PublishedText { get; set; }
    // Set when the card's watch endpoint points at a playlist — a mix / "jam" card does. It
    // must travel to the video page, otherwise the mix plays as a single video with no queue.
    public string PlaylistId { get; set; }
    public double WatchedPercent
    {
        get { return _watchedPercent; }
        set
        {
            if (Math.Abs(_watchedPercent - value) < 0.001)
                return;
            _watchedPercent = value;
            RaisePropertyChanged("WatchedPercent");
            RaisePropertyChanged("WatchedProgressVisibility");
        }
    }

    private void RaisePropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        if (handler != null)
            handler(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }

    public Windows.UI.Xaml.Visibility WatchedProgressVisibility
    {
        get
        {
            return WatchedPercent > 0
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
        }
    }

    // "Channel • 1.2M views • 3 days ago" — the line under the thumbnail, official-app style.
    // Empty pieces are dropped so a card never shows a dangling separator.
    public string MetadataLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ChannelTitle)) parts.Add(ChannelTitle.Trim());
            if (!string.IsNullOrWhiteSpace(ViewCount)) parts.Add(ViewCount.Trim());
            if (!string.IsNullOrWhiteSpace(PublishedText)) parts.Add(PublishedText.Trim());
            return string.Join(" • ", parts);
        }
    }

    // Same line without the channel name, for lists that are already scoped to one channel
    // (the channel page) — repeating the channel on every card there is just noise.
    public string StatsLine
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(ViewCount)) parts.Add(ViewCount.Trim());
            if (!string.IsNullOrWhiteSpace(PublishedText)) parts.Add(PublishedText.Trim());
            return string.Join(" • ", parts);
        }
    }
}


public sealed class ShortsFeedResult
{
    public List<ShortsVideoItem> Items { get; set; }
    public string SequenceToken { get; set; }
}

public sealed class ShortsVideoItem
{
    public string VideoId { get; set; }
    public string Title { get; set; }
    public string ChannelName { get; set; }
    public string ChannelThumbnailUrl { get; set; }
    public string ThumbnailUrl { get; set; }
    public string ViewCount { get; set; }
    public string VideoUrl { get; set; }
    public string LikeCount { get; set; }
    public string CommentCount { get; set; }
    public bool IsLiked { get; set; }
    public bool IsDisliked { get; set; }
    public string RatingState { get; set; }
}


public sealed class PlaylistItem
{
    private static readonly Windows.UI.Color[][] AccentPalettes =
    {
        new[] { Windows.UI.Color.FromArgb(255, 140, 83, 102), Windows.UI.Color.FromArgb(255, 208, 136, 156) },
        new[] { Windows.UI.Color.FromArgb(255, 72, 95, 135), Windows.UI.Color.FromArgb(255, 118, 153, 204) },
        new[] { Windows.UI.Color.FromArgb(255, 109, 85, 144), Windows.UI.Color.FromArgb(255, 167, 133, 201) },
        new[] { Windows.UI.Color.FromArgb(255, 52, 122, 120), Windows.UI.Color.FromArgb(255, 99, 170, 163) },
        new[] { Windows.UI.Color.FromArgb(255, 154, 106, 56), Windows.UI.Color.FromArgb(255, 212, 164, 103) },
        new[] { Windows.UI.Color.FromArgb(255, 82, 117, 73), Windows.UI.Color.FromArgb(255, 130, 173, 117) },
        new[] { Windows.UI.Color.FromArgb(255, 147, 78, 72), Windows.UI.Color.FromArgb(255, 207, 126, 115) },
        new[] { Windows.UI.Color.FromArgb(255, 98, 104, 116), Windows.UI.Color.FromArgb(255, 149, 157, 170) }
    };

    public string PlaylistId { get; set; }
    public string Title { get; set; }
    public string AuthorName { get; set; }
    public string ThumbnailUrl { get; set; }
    public string VideoCountText { get; set; }
    public string PrivacyText { get; set; }

    public Windows.UI.Xaml.Visibility VideoCountVisibility
    {
        get
        {
            return string.IsNullOrWhiteSpace(VideoCountText)
                ? Windows.UI.Xaml.Visibility.Collapsed
                : Windows.UI.Xaml.Visibility.Visible;
        }
    }

    public string OverlayIconSource
    {
        get
        {
            return GetPersonalPlaylistCode() == "LL"
                ? "ms-appx:///Assets/Dark/player/like.png"
                : string.Empty;
        }
    }

    public double OverlayIconImageOpacity
    {
        get { return GetPersonalPlaylistCode() == "LL" ? 1.0 : 0.0; }
    }

    public double OverlayIconGlyphOpacity
    {
        get { return GetPersonalPlaylistCode() == "LL" ? 0.0 : 1.0; }
    }

    public string OverlayIconGlyph
    {
        get
        {
            var code = GetPersonalPlaylistCode();
            if (code == "WL" || code == "HL")
                return "\uE121";
            if (code == "LM")
                return "\uE142";
            return "\uE133";
        }
    }

    public Windows.UI.Xaml.Media.SolidColorBrush AccentBackColor
    {
        get { return new Windows.UI.Xaml.Media.SolidColorBrush(AccentPalettes[GetAccentPaletteIndex()][0]); }
    }

    public Windows.UI.Xaml.Media.SolidColorBrush AccentFrontColor
    {
        get { return new Windows.UI.Xaml.Media.SolidColorBrush(AccentPalettes[GetAccentPaletteIndex()][1]); }
    }

    public string MetadataText
    {
        get
        {
            var privacy = (PrivacyText ?? string.Empty).Trim();
            var count = (VideoCountText ?? string.Empty).Trim();
            if (privacy.Length == 0)
                return count;
            if (count.Length == 0)
                return privacy;
            return privacy + " · " + count;
        }
    }

    private string GetPersonalPlaylistCode()
    {
        var id = (PlaylistId ?? string.Empty).Trim().ToUpperInvariant();
        if (id.StartsWith("VL", StringComparison.Ordinal) && id.Length > 2)
            id = id.Substring(2);
        return id;
    }

    private int GetAccentPaletteIndex()
    {
        var source = string.IsNullOrWhiteSpace(PlaylistId) ? Title : PlaylistId;
        source = source ?? string.Empty;

        unchecked
        {
            uint hash = 2166136261;
            for (var index = 0; index < source.Length; index++)
            {
                hash ^= char.ToUpperInvariant(source[index]);
                hash *= 16777619;
            }
            return (int)(hash % (uint)AccentPalettes.Length);
        }
    }
}

public sealed class PlaylistSaveState
{
    public PlaylistItem Playlist { get; set; }
    public bool ContainsVideo { get; set; }
}

// Navigation payload for the video page when a video is opened as part of a playlist or an
// auto-generated mix ("jam"). Carrying the playlist id keeps the watch queue alive, which is
// what makes mixes continue endlessly instead of stopping after one video.
public sealed class VideoNavigationArgs
{
    public string VideoId { get; set; }
    public string PlaylistId { get; set; }
    public string PlaylistTitle { get; set; }
}

public sealed class PlaylistDetails
{
    public string PlaylistId { get; set; }
    public string Title { get; set; }
    public string OwnerName { get; set; }
    public string OwnerThumbnailUrl { get; set; }
    public string Description { get; set; }
    public string ThumbnailUrl { get; set; }
    public string MetadataText { get; set; }
    public string ContinuationToken { get; set; }
    public List<VideoCardItem> Videos { get; set; }
    public Task DeferredEnrichment { get; set; }
}

public sealed class SubscriptionChannel
{
    public string ChannelId { get; set; }
    public string ChannelName { get; set; }
    public string ThumbnailUrl { get; set; }
}

public sealed class NotificationItem
{
    public string Title { get; set; }
    public string Message { get; set; }
    public string TimeText { get; set; }
    public string AvatarUrl { get; set; }
    public string ThumbnailUrl { get; set; }
    public string VideoId { get; set; }
    public bool IsRead { get; set; }
    public string VideoTitle { get; set; }
    public string Author { get; set; }
}

public sealed class AccountInfo
{
    public string DisplayName { get; set; }
    public string ChannelHandle { get; set; }
    public long SubscribersCount { get; set; }
    public string ThumbnailUrl { get; set; }
}

public sealed class YouTubeAccountItem
{
    public string AccountKey { get; set; }
    public string DisplayName { get; set; }
    public string ChannelHandle { get; set; }
    public string ThumbnailUrl { get; set; }
    public string BrandId { get; set; }
    public bool IsPrimaryOwner { get; set; }
    public bool IsSelected { get; set; }

    public Windows.UI.Xaml.Visibility SelectedVisibility
    {
        get { return IsSelected ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed; }
    }
}

public sealed class VideoDetails
{
    public string VideoId { get; set; }
    public string Title { get; set; }
    public string Description { get; set; }
    public string ChannelId { get; set; }
    public string ChannelName { get; set; }
    public string ChannelThumbnail { get; set; }
    public string ViewCount { get; set; }
    public string LikeCount { get; set; }
    public string PublishedAt { get; set; }
    public string Duration { get; set; }
    public string ThumbnailUrl { get; set; }
    public string CommentsContinuationToken { get; set; }
    public List<CommentItem> Comments { get; set; }
    public List<VideoCardItem> RelatedVideos { get; set; }
}

public sealed class CommentItem : System.ComponentModel.INotifyPropertyChanged
{
    private string _replyContinuationToken;
    private string _repliesText;
    private bool _repliesLoading;
    private bool _repliesExpanded;

    public string Author { get; set; }
    public string AuthorThumbnail { get; set; }
    public string Text { get; set; }
    public string PublishedAt { get; set; }
    public string LikeCount { get; set; }
    public string ContinuationToken { get; set; }
    public string ToolbarStateKey { get; set; }
    public string ReplyCount { get; set; }
    public bool IsReply { get; set; }

    public System.Collections.ObjectModel.ObservableCollection<CommentItem> Replies { get; private set; }

    public string ReplyContinuationToken
    {
        get { return _replyContinuationToken; }
        set
        {
            _replyContinuationToken = value ?? string.Empty;
            RaiseReplyStateChanged();
        }
    }

    public string RepliesText
    {
        get { return string.IsNullOrWhiteSpace(_repliesText) ? Localization.GetString("Replies") : _repliesText; }
        set
        {
            _repliesText = value ?? string.Empty;
            OnPropertyChanged("RepliesText");
        }
    }

    public Windows.UI.Xaml.Visibility RepliesButtonVisibility
    {
        get
        {
            return !string.IsNullOrWhiteSpace(_replyContinuationToken)
                && !_repliesExpanded && !_repliesLoading
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
        }
    }

    public Windows.UI.Xaml.Visibility RepliesLoadingVisibility
    {
        get
        {
            return _repliesLoading
                ? Windows.UI.Xaml.Visibility.Visible
                : Windows.UI.Xaml.Visibility.Collapsed;
        }
    }

    public bool RepliesLoadingActive
    {
        get { return _repliesLoading; }
    }

    public CommentItem()
    {
        _replyContinuationToken = string.Empty;
        _repliesText = string.Empty;
        Replies = new System.Collections.ObjectModel.ObservableCollection<CommentItem>();
    }

    public bool TryBeginLoadingReplies()
    {
        if (_repliesLoading || _repliesExpanded || string.IsNullOrWhiteSpace(_replyContinuationToken))
        {
            return false;
        }
        _repliesLoading = true;
        RaiseReplyStateChanged();
        return true;
    }

    public void FinishLoadingReplies(IEnumerable<CommentItem> replies, bool succeeded)
    {
        Replies.Clear();
        if (succeeded && replies != null)
        {
            foreach (var reply in replies)
            {
                if (reply == null) continue;
                reply.IsReply = true;
                reply.ReplyContinuationToken = string.Empty;
                Replies.Add(reply);
            }
        }

        _repliesLoading = false;
        _repliesExpanded = succeeded;
        RaiseReplyStateChanged();
    }

    private void RaiseReplyStateChanged()
    {
        OnPropertyChanged("RepliesButtonVisibility");
        OnPropertyChanged("RepliesLoadingVisibility");
        OnPropertyChanged("RepliesLoadingActive");
    }

    public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        var handler = PropertyChanged;
        if (handler != null)
        {
            handler(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }
}

public static class VideoParser
{
    public static VideoDetails ParseVideoDetails(string json, string videoId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            var root = JsonObject.Parse(json);
            var details = new VideoDetails
            {
                VideoId = videoId,
                Comments = new List<CommentItem>(),
                RelatedVideos = new List<VideoCardItem>()
            };

            // Try to extract from twoColumnWatchNextResults
            if (!root.ContainsKey("contents"))
            {
                System.Diagnostics.Debug.WriteLine("[VideoDetails] No 'contents' in JSON");
                return details;
            }

            var contents = root.GetNamedObject("contents");
            if (!contents.ContainsKey("twoColumnWatchNextResults"))
            {
                System.Diagnostics.Debug.WriteLine("[VideoDetails] No 'twoColumnWatchNextResults' in JSON");
                return details;
            }

            var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
            System.Diagnostics.Debug.WriteLine("[VideoDetails] Found twoColumnWatchNextResults");

            // Extract video info by walking through the JSON structure (like Python script)
            if (twoColumn.ContainsKey("results"))
            {
                var results = twoColumn.GetNamedObject("results");
                if (results.ContainsKey("results"))
                {
                    var resultsContent = results.GetNamedObject("results");
                    System.Diagnostics.Debug.WriteLine("[VideoDetails] Walking through results to extract video info...");
                    WalkForVideoInfo(resultsContent, details);
                }
            }

            // Extract comments continuation token from engagement panels
            if (twoColumn.ContainsKey("engagementPanels"))
            {
                var panels = twoColumn.GetNamedArray("engagementPanels");
                details.CommentsContinuationToken = FindCommentsContinuation(panels);
            }

            // Extract related videos from secondaryResults
            if (twoColumn.ContainsKey("secondaryResults"))
            {
                var secondaryResults = twoColumn.GetNamedObject("secondaryResults");
                ExtractRelatedVideos(secondaryResults, details.RelatedVideos);
            }

            System.Diagnostics.Debug.WriteLine($"[VideoDetails] Parsing complete: Title={details.Title}, Channel={details.ChannelName}");
            return details;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoDetails] ERROR in ParseVideoDetails: {ex.Message}");
            System.Diagnostics.Debug.WriteLine($"[VideoDetails] Stack trace: {ex.StackTrace}");
            return null;
        }
    }

    private static void WalkForVideoInfo(IJsonValue value, VideoDetails details)
    {
        if (value.ValueType != JsonValueType.Object)
            return;

        var obj = value.GetObject();

        // Look for videoView or other renderers that contain video information
        if (obj.ContainsKey("videoView"))
        {
            try
            {
                var videoView = obj.GetNamedObject("videoView");
                System.Diagnostics.Debug.WriteLine("[VideoDetails] Found videoView, extracting fields...");

                // Title
                if (videoView.ContainsKey("title"))
                {
                    var titleObj = videoView.GetNamedObject("title");
                    if (titleObj.ContainsKey("content"))
                    {
                        details.Title = titleObj.GetNamedString("content");
                        System.Diagnostics.Debug.WriteLine($"[VideoDetails] Title: {details.Title}");
                    }
                }

                // Owner (channel info)
                if (videoView.ContainsKey("owner"))
                {
                    var owner = videoView.GetNamedObject("owner");
                    System.Diagnostics.Debug.WriteLine("[VideoDetails] Found owner");

                    // Try to extract channel name and ID
                    if (owner.ContainsKey("ownerButton"))
                    {
                        var ownerButton = owner.GetNamedObject("ownerButton");
                        if (ownerButton.ContainsKey("commandRuns"))
                        {
                            var commands = ownerButton.GetNamedArray("commandRuns");
                            if (commands.Count > 0)
                            {
                                var cmd = commands[0].GetObject();
                                if (cmd.ContainsKey("onTap"))
                                {
                                    var onTap = cmd.GetNamedObject("onTap");
                                    if (onTap.ContainsKey("innertubeCommand"))
                                    {
                                        var innertubeCmd = onTap.GetNamedObject("innertubeCommand");
                                        if (innertubeCmd.ContainsKey("browseEndpoint"))
                                        {
                                            var browseEndpoint = innertubeCmd.GetNamedObject("browseEndpoint");
                                            details.ChannelId = browseEndpoint.GetNamedString("browseId");
                                            System.Diagnostics.Debug.WriteLine($"[VideoDetails] ChannelId: {details.ChannelId}");
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // View count
                if (videoView.ContainsKey("viewCount"))
                {
                    var viewCountObj = videoView.GetNamedObject("viewCount");
                    if (viewCountObj.ContainsKey("content"))
                    {
                        details.ViewCount = viewCountObj.GetNamedString("content");
                        System.Diagnostics.Debug.WriteLine($"[VideoDetails] ViewCount: {details.ViewCount}");
                    }
                }

                // Description
                if (videoView.ContainsKey("description"))
                {
                    var descObj = videoView.GetNamedObject("description");
                    if (descObj.ContainsKey("content"))
                    {
                        details.Description = descObj.GetNamedString("content");
                        System.Diagnostics.Debug.WriteLine($"[VideoDetails] Description length: {details.Description.Length}");
                    }
                    else if (descObj.ContainsKey("runs"))
                    {
                        var runs = descObj.GetNamedArray("runs");
                        var sb = new StringBuilder();
                        foreach (var run in runs)
                        {
                            var runObj = run.GetObject();
                            if (runObj.ContainsKey("text"))
                            {
                                sb.Append(runObj.GetNamedString("text"));
                            }
                        }
                        details.Description = sb.ToString();
                        System.Diagnostics.Debug.WriteLine($"[VideoDetails] Description length (from runs): {details.Description.Length}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VideoDetails] Error extracting videoView: {ex.Message}");
            }
        }

        // Also look for videoDetails (from player response fallback)
        if (obj.ContainsKey("videoDetails"))
        {
            try
            {
                var videoDetails = obj.GetNamedObject("videoDetails");
                
                if (string.IsNullOrEmpty(details.Title) && videoDetails.ContainsKey("title"))
                {
                    details.Title = videoDetails.GetNamedString("title");
                    System.Diagnostics.Debug.WriteLine($"[VideoDetails] Title (from videoDetails): {details.Title}");
                }

                if (string.IsNullOrEmpty(details.ChannelName) && videoDetails.ContainsKey("author"))
                {
                    details.ChannelName = videoDetails.GetNamedString("author");
                    System.Diagnostics.Debug.WriteLine($"[VideoDetails] Channel: {details.ChannelName}");
                }

                if (string.IsNullOrEmpty(details.ViewCount) && videoDetails.ContainsKey("viewCount"))
                {
                    details.ViewCount = videoDetails.GetNamedString("viewCount");
                    System.Diagnostics.Debug.WriteLine($"[VideoDetails] Views: {details.ViewCount}");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VideoDetails] Error extracting videoDetails: {ex.Message}");
            }
        }

        // Recurse into children
        foreach (var item in obj)
        {
            try
            {
                WalkForVideoInfo(item.Value, details);
            }
            catch { }
        }
    }

    private static string FindCommentsContinuation(JsonArray panels)
    {
        foreach (var panelToken in panels)
        {
            var panel = panelToken.GetObject();
            if (panel.ContainsKey("engagementPanelSectionRenderer"))
            {
                var sectionRenderer = panel.GetNamedObject("engagementPanelSectionRenderer");
                if (sectionRenderer.ContainsKey("content"))
                {
                    var content = sectionRenderer.GetNamedObject("content");
                    if (content.ContainsKey("structuredDescriptionContentRenderer"))
                    {
                        var descContent = content.GetNamedObject("structuredDescriptionContentRenderer");
                        if (descContent.ContainsKey("items"))
                        {
                            var items = descContent.GetNamedArray("items");
                            foreach (var item in items)
                            {
                                var itemObj = item.GetObject();
                                if (itemObj.ContainsKey("videoDescriptionHeaderRenderer"))
                                {
                                    var header = itemObj.GetNamedObject("videoDescriptionHeaderRenderer");
                                    if (header.ContainsKey("commands"))
                                    {
                                        // Found comments section
                                    }
                                }
                                
                                if (itemObj.ContainsKey("expandableVideoDescriptionBodyRenderer"))
                                {
                                    var body = itemObj.GetNamedObject("expandableVideoDescriptionBodyRenderer");
                                    if (body.ContainsKey("content"))
                                    {
                                        var bodyContent = body.GetNamedObject("content");
                                        if (bodyContent.ContainsKey("expandedContents"))
                                        {
                                            // This might have continuation
                                        }
                                    }
                                }
                            }
                        }
                    }
                    
                    // Look for comments section
                    if (sectionRenderer.ContainsKey("panelIdentifier"))
                    {
                        var identifier = sectionRenderer.GetNamedString("panelIdentifier");
                        if (identifier.Contains("comments"))
                        {
                            // Extract continuation from content
                            if (content.ContainsKey("sectionListRenderer"))
                            {
                                var sectionList = content.GetNamedObject("sectionListRenderer");
                                if (sectionList.ContainsKey("contents"))
                                {
                                    var sectionContents = sectionList.GetNamedArray("contents");
                                    if (sectionContents.Count > 0)
                                    {
                                        var firstSection = sectionContents[0].GetObject();
                                        if (firstSection.ContainsKey("itemSectionRenderer"))
                                        {
                                            var itemSection = firstSection.GetNamedObject("itemSectionRenderer");
                                            if (itemSection.ContainsKey("contents"))
                                            {
                                                var itemContents = itemSection.GetNamedArray("contents");
                                                if (itemContents.Count > 0)
                                                {
                                                    var firstItem = itemContents[0].GetObject();
                                                    if (firstItem.ContainsKey("commentsEntryPointHeaderRenderer"))
                                                    {
                                                        var commentsHeader = firstItem.GetNamedObject("commentsEntryPointHeaderHeaderRenderer");
                                                        if (commentsHeader.ContainsKey("content"))
                                                        {
                                                            var commentsContent = commentsHeader.GetNamedObject("content");
                                                            if (commentsContent.ContainsKey("continuationCommand"))
                                                            {
                                                                return commentsContent.GetNamedObject("continuationCommand").GetNamedString("token");
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
        return null;
    }

    private static void ExtractRelatedVideos(JsonObject secondaryResults, List<VideoCardItem> relatedVideos)
    {
        try
        {
            if (!secondaryResults.ContainsKey("secondaryResults")) return;
            
            var secondaryResultsValue = secondaryResults.GetNamedValue("secondaryResults");
            if (secondaryResultsValue.ValueType != JsonValueType.Object) return;
            
            var results = secondaryResultsValue.GetObject();
            if (!results.ContainsKey("results")) return;
            
            var resultsValue = results.GetNamedValue("results");
            if (resultsValue.ValueType != JsonValueType.Object) return;
            
            var resultsContent = resultsValue.GetObject();
            
            // JsonObject already implements IJsonValue. Avoid Stringify()+Parse() round-trip.
            foreach (var obj in Config.EnumerateObjects(resultsContent, 5000))
            {
                VideoCardItem video = null;
                
                if (obj.ContainsKey("videoRenderer"))
                {
                    var videoRendererValue = obj.GetNamedValue("videoRenderer");
                    if (videoRendererValue.ValueType == JsonValueType.Object)
                        video = Config.ParseVideoRenderer(videoRendererValue.GetObject());
                }
                else if (obj.ContainsKey("compactVideoRenderer"))
                {
                    var compactRendererValue = obj.GetNamedValue("compactVideoRenderer");
                    if (compactRendererValue.ValueType == JsonValueType.Object)
                        video = Config.ParseVideoRenderer(compactRendererValue.GetObject());
                }
                else if (obj.ContainsKey("gridVideoRenderer"))
                {
                    var gridRendererValue = obj.GetNamedValue("gridVideoRenderer");
                    if (gridRendererValue.ValueType == JsonValueType.Object)
                        video = Config.ParseVideoRenderer(gridRendererValue.GetObject());
                }
                
                if (video != null && !string.IsNullOrWhiteSpace(video.VideoId))
                {
                    relatedVideos.Add(video);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[VideoDetails] Error extracting related videos: {ex.Message}");
        }
    }

    public static List<CommentItem> ParseComments(string json)
    {
        var comments = new List<CommentItem>();
        try
        {
            if (string.IsNullOrWhiteSpace(json)) return comments;
            if (json.IndexOf("commentEntityPayload", StringComparison.Ordinal) < 0 &&
                json.IndexOf("commentRenderer", StringComparison.Ordinal) < 0)
            {
                return comments;
            }

            var root = JsonValue.Parse(json);
            WalkForComments(root, comments);

            // youtube-ios links commentThreadRenderer.replies to commentEntityPayload by
            // toolbarStateKey. The reply bodies are not part of this page; only their
            // continuation is, so keep that token on the parent and load the branch on tap.
            var replyThreads = ExtractCommentReplyThreads(root);
            var unusedThreads = new List<CommentReplyThreadInfo>(replyThreads);
            for (var i = 0; i < comments.Count; i++)
            {
                var comment = comments[i];
                if (comment == null)
                {
                    continue;
                }

                CommentReplyThreadInfo matched = null;
                if (!string.IsNullOrWhiteSpace(comment.ToolbarStateKey))
                {
                    for (var j = 0; j < unusedThreads.Count; j++)
                    {
                        if (string.Equals(unusedThreads[j].ToolbarStateKey,
                            comment.ToolbarStateKey, StringComparison.Ordinal))
                        {
                            matched = unusedThreads[j];
                            unusedThreads.RemoveAt(j);
                            break;
                        }
                    }
                }

                if (matched != null && !string.IsNullOrWhiteSpace(matched.ContinuationToken))
                {
                    comment.ReplyContinuationToken = matched.ContinuationToken;
                    comment.RepliesText = FirstNonEmptyText(
                        matched.DisplayText,
                        BuildRepliesFallbackText(comment.ReplyCount));
                }
            }

            // Same positional fallback as youtube-ios, but only after every exact key match
            // had a chance to claim its thread. Restrict it to comments whose toolbar says
            // they have replies so a missing key cannot attach a branch to a plain comment.
            for (var i = 0; i < comments.Count && unusedThreads.Count > 0; i++)
            {
                var comment = comments[i];
                if (comment == null
                    || !string.IsNullOrWhiteSpace(comment.ReplyContinuationToken)
                    || string.IsNullOrWhiteSpace(comment.ReplyCount))
                {
                    continue;
                }

                var matched = unusedThreads[0];
                unusedThreads.RemoveAt(0);
                comment.ReplyContinuationToken = matched.ContinuationToken;
                comment.RepliesText = FirstNonEmptyText(
                    matched.DisplayText,
                    BuildRepliesFallbackText(comment.ReplyCount));
            }
            
            System.Diagnostics.Debug.WriteLine($"[Comments] Parsed {comments.Count} comments from JSON");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comments] Error parsing comments: {ex.Message}");
        }
        return comments;
    }

    private static void WalkForComments(Windows.Data.Json.IJsonValue value, List<CommentItem> comments)
    {
        if (value == null || comments == null)
        {
            return;
        }

        var stack = new Stack<Windows.Data.Json.IJsonValue>();
        stack.Push(value);
        var visitedObjects = 0;

        // Current comment pages can contain far more than 7000 objects. Reply threads are
        // commonly located after that old ceiling, which made them disappear completely.
        while (stack.Count > 0 && comments.Count < 80 && visitedObjects < 200000)
        {
            var current = stack.Pop();
            if (current == null)
            {
                continue;
            }

            var currentType = current.ValueType;
            if (currentType == Windows.Data.Json.JsonValueType.Object)
            {
                visitedObjects++;
                var obj = current.GetObject();
                Windows.Data.Json.IJsonValue rendererValue;
                CommentItem comment = null;

                if (obj.TryGetValue("commentEntityPayload", out rendererValue) &&
                    rendererValue != null &&
                    rendererValue.ValueType == Windows.Data.Json.JsonValueType.Object)
                {
                    comment = ParseCommentEntityPayload(rendererValue.GetObject());
                }
                else if (obj.TryGetValue("commentRenderer", out rendererValue) &&
                    rendererValue != null &&
                    rendererValue.ValueType == Windows.Data.Json.JsonValueType.Object)
                {
                    comment = ParseCommentRenderer(rendererValue.GetObject());
                }

                if (comment != null)
                {
                    comments.Add(comment);
                    continue;
                }

                foreach (var item in obj)
                {
                    var child = item.Value;
                    if (child == null)
                    {
                        continue;
                    }

                    var childType = child.ValueType;
                    if (childType == Windows.Data.Json.JsonValueType.Object || childType == Windows.Data.Json.JsonValueType.Array)
                    {
                        stack.Push(child);
                    }
                }
            }
            else if (currentType == Windows.Data.Json.JsonValueType.Array)
            {
                var arr = current.GetArray();
                for (var i = arr.Count - 1; i >= 0; i--)
                {
                    var child = arr[i];
                    if (child == null)
                    {
                        continue;
                    }

                    var childType = child.ValueType;
                    if (childType == Windows.Data.Json.JsonValueType.Object || childType == Windows.Data.Json.JsonValueType.Array)
                    {
                        stack.Push(child);
                    }
                }
            }
        }
    }

    private static CommentItem ParseCommentEntityPayload(JsonObject payload)
    {
        try
        {
            var authorData = payload.GetNamedObject("author");
            var props = payload.GetNamedObject("properties");

            // Extract author name with @ symbol
            var displayName = authorData.GetNamedString("displayName", "").Trim();
            var author = displayName.StartsWith("@") ? displayName : $"@{displayName}";
            
            // Debug log disabled for speed

            // Extract comment text
            var content = props.GetNamedObject("content");
            string text = "";
            if (content.ContainsKey("content"))
            {
                text = content.GetNamedString("content");
            }
            else if (content.ContainsKey("runs"))
            {
                text = ExtractFromRuns(content.GetNamedArray("runs"));
            }
            
            if (string.IsNullOrWhiteSpace(text))
            {
                // Debug log disabled for speed
                return null;
            }

            // Extract published time
            var publishedTime = props.GetNamedString("publishedTime", "unknown");
            
            // Newer commentEntityPayload variants often place the avatar URL directly on the
            // author object. Prefer it because it is already the renderer's canonical 88px image.
            // Older/current variants still expose avatar.image.sources[], so keep that as fallback.
            var thumbnail = authorData.GetNamedString("avatarThumbnailUrl", "");
            var bestAvatarArea = -1.0;
            if (string.IsNullOrWhiteSpace(thumbnail) && payload.ContainsKey("avatar"))
            {
                var avatar = payload.GetNamedObject("avatar");
                if (avatar.ContainsKey("image"))
                {
                    var image = avatar.GetNamedObject("image");
                    if (image.ContainsKey("sources"))
                    {
                        var sources = image.GetNamedArray("sources");
                        for (uint i = 0; i < sources.Count; i++)
                        {
                            try
                            {
                                var source = sources[(int)i].GetObject();
                                var url = source.GetNamedString("url", "");
                                if (string.IsNullOrWhiteSpace(url))
                                {
                                    continue;
                                }

                                var width = source.GetNamedNumber("width", 0);
                                var height = source.GetNamedNumber("height", 0);
                                var area = width * height;
                                if (string.IsNullOrWhiteSpace(thumbnail) || area >= bestAvatarArea)
                                {
                                    thumbnail = url;
                                    bestAvatarArea = area;
                                }
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }

            // Debug log disabled for speed
            
            return new CommentItem
            {
                Author = author,
                Text = text.Trim(),
                PublishedAt = publishedTime,
                AuthorThumbnail = thumbnail,
                ToolbarStateKey = props.GetNamedString("toolbarStateKey", string.Empty),
                LikeCount = ExtractCommentLikeCount(payload),
                ReplyCount = ExtractCommentReplyCount(payload)
            };
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Comment] Error parsing comment: {ex.Message}");
            return null;
        }
    }

    private static CommentItem ParseCommentRenderer(JsonObject renderer)
    {
        try
        {
            var author = "";
            if (renderer.ContainsKey("authorText"))
            {
                var authorValue = renderer.GetNamedValue("authorText");
                author = ExtractTextFromJsonValue(authorValue);
            }

            var text = "";
            if (renderer.ContainsKey("contentText"))
            {
                var contentText = renderer.GetNamedObject("contentText");
                if (contentText.ContainsKey("runs"))
                {
                    text = ExtractFromRuns(contentText.GetNamedArray("runs"));
                }
            }

            var publishedTime = "";
            if (renderer.ContainsKey("publishedTimeText"))
            {
                var publishedText = renderer.GetNamedObject("publishedTimeText");
                if (publishedText.ContainsKey("runs"))
                {
                    publishedTime = ExtractFromRuns(publishedText.GetNamedArray("runs"));
                }
            }

            var thumbnail = "";
            if (renderer.ContainsKey("authorThumbnail"))
            {
                thumbnail = Config.ExtractThumbnailUrl(renderer, "authorThumbnail");
            }

            var token = "";
            if (renderer.ContainsKey("commentId"))
            {
                token = renderer.GetNamedString("commentId");
            }

            return new CommentItem
            {
                Author = author,
                Text = text,
                PublishedAt = publishedTime,
                AuthorThumbnail = thumbnail,
                ContinuationToken = token,
                ToolbarStateKey = token,
                LikeCount = renderer.ContainsKey("voteCount")
                    ? ExtractTextFromJsonValue(renderer.GetNamedValue("voteCount"))
                    : string.Empty,
                ReplyCount = FirstNonEmptyText(
                    renderer.ContainsKey("replyCount")
                        ? ExtractTextFromJsonValue(renderer.GetNamedValue("replyCount"))
                        : string.Empty,
                    renderer.ContainsKey("replyCountText")
                        ? ExtractTextFromJsonValue(renderer.GetNamedValue("replyCountText"))
                        : string.Empty)
            };
        }
        catch
        {
            return null;
        }
    }

    private sealed class CommentReplyThreadInfo
    {
        public string ContinuationToken;
        public string ToolbarStateKey;
        public string DisplayText;
    }

    private static List<CommentReplyThreadInfo> ExtractCommentReplyThreads(IJsonValue root)
    {
        var result = new List<CommentReplyThreadInfo>();
        foreach (var thread in FindCommentObjects(root, "commentThreadRenderer", 200000))
        {
            try
            {
                var replies = thread.ContainsKey("replies")
                    && thread.GetNamedValue("replies").ValueType == JsonValueType.Object
                    ? thread.GetNamedObject("replies")
                    : null;
                if (replies == null)
                {
                    continue;
                }

                var token = FindCommentContinuationToken(replies);
                if (string.IsNullOrWhiteSpace(token))
                {
                    continue;
                }

                var toolbarKey = string.Empty;
                foreach (var view in FindCommentObjects(thread, "commentViewModel", 700))
                {
                    toolbarKey = view.GetNamedString("toolbarStateKey", string.Empty);
                    if (!string.IsNullOrWhiteSpace(toolbarKey))
                    {
                        break;
                    }
                }
                if (string.IsNullOrWhiteSpace(toolbarKey))
                {
                    foreach (var classic in FindCommentObjects(thread, "commentRenderer", 700))
                    {
                        toolbarKey = classic.GetNamedString("commentId", string.Empty);
                        if (!string.IsNullOrWhiteSpace(toolbarKey))
                        {
                            break;
                        }
                    }
                }

                result.Add(new CommentReplyThreadInfo
                {
                    ContinuationToken = token,
                    ToolbarStateKey = toolbarKey,
                    DisplayText = FindRepliesDisplayText(replies)
                });
            }
            catch
            {
            }
        }
        return result;
    }

    private static List<JsonObject> FindCommentObjects(IJsonValue root, string key, int maxObjects)
    {
        var found = new List<JsonObject>();
        if (root == null || string.IsNullOrWhiteSpace(key))
        {
            return found;
        }

        var stack = new Stack<IJsonValue>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited < maxObjects)
        {
            var value = stack.Pop();
            if (value == null)
            {
                continue;
            }

            if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    stack.Push(array[i]);
                }
                continue;
            }

            if (value.ValueType != JsonValueType.Object)
            {
                continue;
            }

            visited++;
            var obj = value.GetObject();
            IJsonValue match;
            if (obj.TryGetValue(key, out match) && match != null
                && match.ValueType == JsonValueType.Object)
            {
                found.Add(match.GetObject());
            }

            var children = new List<IJsonValue>();
            foreach (var pair in obj)
            {
                if (pair.Value != null
                    && (pair.Value.ValueType == JsonValueType.Object
                        || pair.Value.ValueType == JsonValueType.Array))
                {
                    children.Add(pair.Value);
                }
            }
            for (var i = children.Count - 1; i >= 0; i--)
            {
                stack.Push(children[i]);
            }
        }
        return found;
    }

    private static string FindCommentContinuationToken(IJsonValue root)
    {
        var stack = new Stack<IJsonValue>();
        stack.Push(root);
        var visited = 0;
        while (stack.Count > 0 && visited < 2000)
        {
            var value = stack.Pop();
            if (value == null)
            {
                continue;
            }

            if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (var i = array.Count - 1; i >= 0; i--)
                {
                    stack.Push(array[i]);
                }
                continue;
            }
            if (value.ValueType != JsonValueType.Object)
            {
                continue;
            }

            visited++;
            var obj = value.GetObject();
            if (obj.ContainsKey("continuationCommand"))
            {
                var command = obj.GetNamedObject("continuationCommand");
                var token = command.GetNamedString("token", string.Empty);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }
            if (obj.ContainsKey("nextContinuationData"))
            {
                var next = obj.GetNamedObject("nextContinuationData");
                var token = next.GetNamedString("continuation", string.Empty);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            foreach (var pair in obj)
            {
                if (pair.Value != null
                    && (pair.Value.ValueType == JsonValueType.Object
                        || pair.Value.ValueType == JsonValueType.Array))
                {
                    stack.Push(pair.Value);
                }
            }
        }
        return string.Empty;
    }

    private static string FindRepliesDisplayText(IJsonValue root)
    {
        var preferredKeys = new[] { "moreText", "buttonText", "text", "title" };
        foreach (var obj in FindAllCommentObjects(root, 2400))
        {
            for (var i = 0; i < preferredKeys.Length; i++)
            {
                IJsonValue value;
                if (!obj.TryGetValue(preferredKeys[i], out value))
                {
                    continue;
                }

                var text = ExtractTextFromJsonValue(value);
                var lower = (text ?? string.Empty).ToLowerInvariant();
                if (lower.Contains("repl") || lower.Contains("ответ")
                    || lower.Contains("respuesta") || lower.Contains("réponse")
                    || lower.Contains("antwort"))
                {
                    return text.Trim();
                }
            }
        }
        return string.Empty;
    }

    private static List<JsonObject> FindAllCommentObjects(IJsonValue root, int maxObjects)
    {
        var result = new List<JsonObject>();
        var stack = new Stack<IJsonValue>();
        stack.Push(root);
        while (stack.Count > 0 && result.Count < maxObjects)
        {
            var value = stack.Pop();
            if (value == null)
            {
                continue;
            }
            if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (var i = array.Count - 1; i >= 0; i--) stack.Push(array[i]);
                continue;
            }
            if (value.ValueType != JsonValueType.Object)
            {
                continue;
            }

            var obj = value.GetObject();
            result.Add(obj);
            foreach (var pair in obj)
            {
                if (pair.Value != null
                    && (pair.Value.ValueType == JsonValueType.Object
                        || pair.Value.ValueType == JsonValueType.Array))
                {
                    stack.Push(pair.Value);
                }
            }
        }
        return result;
    }

    private static string ExtractCommentReplyCount(JsonObject payload)
    {
        try
        {
            if (payload != null && payload.ContainsKey("toolbar"))
            {
                var toolbar = payload.GetNamedObject("toolbar");
                if (toolbar.ContainsKey("replyCount"))
                {
                    return ExtractTextFromJsonValue(toolbar.GetNamedValue("replyCount"));
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private static string ExtractCommentLikeCount(JsonObject payload)
    {
        try
        {
            if (payload != null && payload.ContainsKey("toolbar"))
            {
                var toolbar = payload.GetNamedObject("toolbar");
                var keys = new[] { "likeCountLiked", "likeCountNotliked", "likeCount" };
                for (var i = 0; i < keys.Length; i++)
                {
                    if (!toolbar.ContainsKey(keys[i]))
                        continue;

                    var value = ExtractTextFromJsonValue(toolbar.GetNamedValue(keys[i]));
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }
        }
        catch
        {
        }
        return string.Empty;
    }

    private static string BuildRepliesFallbackText(string count)
    {
        var value = (count ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? Localization.GetString("Replies")
            : Localization.Format("RepliesFormat", value);
    }

    private static string FirstNonEmptyText(params string[] values)
    {
        if (values != null)
        {
            for (var i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i])) return values[i];
            }
        }
        return string.Empty;
    }

    private static string ExtractTextFromJsonValue(Windows.Data.Json.IJsonValue value)
    {
        try
        {
            if (value == null) return string.Empty;
            if (value.ValueType == JsonValueType.String) return value.GetString();
            if (value.ValueType != JsonValueType.Object) return string.Empty;

            var obj = value.GetObject();
            if (obj.ContainsKey("simpleText")) return obj.GetNamedString("simpleText", string.Empty);
            if (obj.ContainsKey("content")) return obj.GetNamedString("content", string.Empty);
            if (obj.ContainsKey("runs") && obj.GetNamedValue("runs").ValueType == JsonValueType.Array)
                return ExtractFromRuns(obj.GetNamedArray("runs"));
        }
        catch
        {
        }
        return string.Empty;
    }

    private static string ExtractFromRuns(Windows.Data.Json.JsonArray runs)
    {
        var sb = new StringBuilder();
        foreach (var run in runs)
        {
            var runObj = run.GetObject();
            if (runObj.ContainsKey("text"))
            {
                sb.Append(runObj.GetNamedString("text"));
            }
        }
        return sb.ToString();
    }
}
