using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    internal sealed class OAuthDeviceCodeResult
    {
        internal string DeviceCode { get; set; }
        internal string UserCode { get; set; }
        internal int IntervalSeconds { get; set; }
    }

    internal sealed class OAuthTokenResult
    {
        internal string AccessToken { get; set; }
        internal string RefreshToken { get; set; }
        internal string Error { get; set; }
        internal int ExpiresInSeconds { get; set; }
    }

    internal static class OAuthClient
    {
        internal static async Task<OAuthDeviceCodeResult> RequestDeviceCodeAsync(
            string scope,
            string deviceModel,
            CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, string>
            {
                { "client_id", YouTubeApiConfig.OAuthClientId },
                { "scope", scope ?? string.Empty },
                { "device_id", Guid.NewGuid().ToString() },
                { "device_model", deviceModel ?? string.Empty }
            };

            var json = await PostFormAsync(
                YouTubeApiConfig.DeviceCodeUrl,
                body,
                YouTubeApiConfig.TvUserAgent,
                cancellationToken,
                true).ConfigureAwait(false);

            JsonObject root;
            if (!FastJson.TryParseObject(json, out root))
            {
                throw new InvalidOperationException("YouTube returned an invalid device-code response.");
            }

            return new OAuthDeviceCodeResult
            {
                DeviceCode = FastJson.GetString(root, "device_code"),
                UserCode = FastJson.GetString(root, "user_code"),
                IntervalSeconds = FastJson.GetInt32(root, "interval", 5)
            };
        }

        internal static async Task<OAuthTokenResult> PollDeviceTokenAsync(
            string deviceCode,
            CancellationToken cancellationToken)
        {
            var body = new Dictionary<string, string>
            {
                { "client_id", YouTubeApiConfig.OAuthClientId },
                { "client_secret", YouTubeApiConfig.OAuthClientSecret },
                { "code", deviceCode ?? string.Empty },
                { "grant_type", "http://oauth.net/grant_type/device/1.0" }
            };

            var json = await PostFormAsync(
                YouTubeApiConfig.DeviceTokenUrl,
                body,
                YouTubeApiConfig.TvUserAgent,
                cancellationToken,
                false).ConfigureAwait(false);
            return ParseToken(json);
        }

        internal static async Task<OAuthTokenResult> RefreshAccessTokenAsync(string refreshToken)
        {
            var body = new Dictionary<string, string>
            {
                { "client_id", YouTubeApiConfig.OAuthClientId },
                { "client_secret", YouTubeApiConfig.OAuthClientSecret },
                { "refresh_token", refreshToken ?? string.Empty },
                { "grant_type", "refresh_token" }
            };

            var json = await PostFormAsync(
                YouTubeApiConfig.OAuthRefreshUrl,
                body,
                null,
                CancellationToken.None,
                false).ConfigureAwait(false);
            return ParseToken(json);
        }

        private static OAuthTokenResult ParseToken(string json)
        {
            JsonObject root;
            if (!FastJson.TryParseObject(json, out root))
            {
                return new OAuthTokenResult { Error = "invalid_response", ExpiresInSeconds = 3600 };
            }

            return new OAuthTokenResult
            {
                AccessToken = FastJson.GetString(root, "access_token"),
                RefreshToken = FastJson.GetString(root, "refresh_token"),
                Error = FastJson.GetString(root, "error"),
                ExpiresInSeconds = FastJson.GetInt32(root, "expires_in", 3600)
            };
        }

        private static async Task<string> PostFormAsync(
            string url,
            IDictionary<string, string> values,
            string userAgent,
            CancellationToken cancellationToken,
            bool ensureSuccess)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                }
                request.Content = new FormUrlEncodedContent(values);

                using (var response = await YouTubeHttpClient.Shared.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (ensureSuccess)
                    {
                        response.EnsureSuccessStatusCode();
                    }
                    return json;
                }
            }
        }
    }
}
