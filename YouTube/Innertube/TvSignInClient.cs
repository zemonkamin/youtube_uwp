using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    internal static class TvSignInClient
    {
        internal static async Task<string> GetQrBase64Async(string userCode, string language, string region, CancellationToken cancellationToken)
        {
            var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20251217.19.00\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"platform\":\"TV\",\"hl\":\""
                + FastJson.Escape(language) + "\",\"gl\":\"" + FastJson.Escape(region)
                + "\"}},\"handoffQrParams\":{\"rapidQrParams\":{\"qrPresetStyle\":\"HANDOFF_QR_LIMITED_PRESET_STYLE_MODERN_BIG_DOTS_INVERT_WITH_YT_LOGO\",\"userCode\":\""
                + FastJson.Escape(userCode)
                + "\",\"rapidQrFeature\":\"RAPID_QR_FEATURE_DEFAULT\"}}}";

            using (var request = new HttpRequestMessage(HttpMethod.Post, InnertubeEndpoints.Build("mdx/handoff")))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", YouTubeApiConfig.TvUserAgent);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                using (var response = await YouTubeHttpClient.Shared.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var root = JsonObject.Parse(json);
                    var qrUrl = root
                        .GetNamedObject("rapidQrRenderer")
                        .GetNamedObject("qrCodeRenderer")
                        .GetNamedObject("qrCodeImage")
                        .GetNamedArray("thumbnails")[0]
                        .GetObject()
                        .GetNamedString("url");

                    const string prefix = "base64,";
                    var markerIndex = qrUrl.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                    if (markerIndex >= 0)
                    {
                        return qrUrl.Substring(markerIndex + prefix.Length);
                    }

                    var bytes = await YouTubeImageClient.DownloadAsync(qrUrl).ConfigureAwait(false);
                    return Convert.ToBase64String(bytes);
                }
            }
        }
    }
}
