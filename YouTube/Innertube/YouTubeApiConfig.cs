namespace YouTube.Innertube
{
    // Server constants live here instead of being copied across XAML pages.
    // Keep this class C# 6 compatible: the project is built by VS2015/MSBuild 14.
    internal static class YouTubeApiConfig
    {
        internal const string ApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        internal const string OAuthClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
        internal const string OAuthClientSecret = "SboVhoG9s0rNafixCSGGKXAT";

        internal const string YouTubeOrigin = "https://www.youtube.com";
        internal const string InnertubeBaseUrl = "https://www.youtube.com/youtubei/v1/";
        internal const string DataApiBaseUrl = "https://www.googleapis.com/youtube/v3/";
        internal const string OAuthRefreshUrl = "https://oauth2.googleapis.com/token";
        internal const string DeviceCodeUrl = "https://www.youtube.com/o/oauth2/device/code";
        internal const string DeviceTokenUrl = "https://www.youtube.com/o/oauth2/token";
        internal const string SuggestionsBaseUrl = "https://clients1.google.com/complete/search";

        internal const string TvUserAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0)";
        internal const string WebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
        internal const string MobileWebUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
    }
}
