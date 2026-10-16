using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Web.Http.Filters;

namespace YouTube.Innertube
{
    // Anonymous/QR requests share one .NET connection pool. Browser requests use the
    // WebView's WinRT cookie store, including HttpOnly cookies and server updates.
    internal sealed class YouTubeHttpClient
    {
        private static readonly HttpClient Client = CreateClient();
        private static readonly YouTubeHttpClient SharedInstance = new YouTubeHttpClient();

        private YouTubeHttpClient()
        {
        }

        internal static YouTubeHttpClient Shared
        {
            get { return SharedInstance; }
        }

        // Media components which require HttpClient in their existing public contract can still
        // share the same connection pool without constructing a second client.
        internal HttpClient RawClient
        {
            get { return Client; }
        }

        private static HttpClient CreateClient()
        {
            // Innertube browse/search/player responses are often hundreds of kilobytes. The
            // VS2015 UWP HttpClient does not consistently advertise compression unless its
            // handler enables it explicitly. Let the platform decompress gzip/deflate while
            // preserving the single shared connection pool (including Windows 10 Mobile ARM).
            var handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false
            };
            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(45);
            return client;
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            return SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption)
        {
            return SendAsync(request, completionOption, CancellationToken.None);
        }

        internal async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
        {
            var innertube = WebAccountSession.IsYouTubeUri(request.RequestUri)
                && request.RequestUri.AbsolutePath.StartsWith("/youtubei/", StringComparison.Ordinal);
            IEnumerable<string> values;
            var authorization = request.Headers.TryGetValues("Authorization", out values) ? values.FirstOrDefault() : string.Empty;
            var bearer = innertube && !string.IsNullOrEmpty(authorization)
                && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
            var browser = innertube && !string.IsNullOrEmpty(authorization)
                && authorization.StartsWith("SAPISIDHASH ", StringComparison.Ordinal);
            var accessToken = bearer ? authorization.Substring(7).Trim() : string.Empty;
            // Remember ownership before sending: a parallel request can refresh the cache.
            var refreshToken = bearer ? Config.GetRefreshTokenForAccessToken(accessToken) : string.Empty;
            var browserSession = browser ? WebAccountSession.CacheKey : string.Empty;
            // Apply the selected browser account before the first request as well as a
            // 401 retry. The client version in the body and header must stay in sync.
            if (browser) await ApplyBrowserRequestContextAsync(request).ConfigureAwait(false);
            var response = browser
                ? await SendBrowserRequestAsync(request, cancellationToken).ConfigureAwait(false)
                : await Client.SendAsync(request, completionOption, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.Unauthorized
                || (!browser && string.IsNullOrEmpty(refreshToken))) return response;

            // A 401 is a definite rejection; replay once after refreshing credentials.
            // Never replay a timeout/5xx/success, especially for comment creation.
            try
            {
                if (bearer)
                {
                    accessToken = await Config.RefreshRejectedAccessTokenAsync(accessToken, refreshToken).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(accessToken)) return response;
                }
                else
                {
                    await WebAccountSession.RefreshBrowserContextAsync(string.Empty).ConfigureAwait(false);
                    if (browserSession != WebAccountSession.CacheKey || !WebAccountSession.IsSignedIn) return response;
                }
                cancellationToken.ThrowIfCancellationRequested();
                using (var retry = await CopyRequestAsync(request).ConfigureAwait(false))
                {
                    retry.Headers.Remove("Authorization");
                    if (bearer) retry.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                    else
                    {
                        await ApplyBrowserRequestContextAsync(retry).ConfigureAwait(false);
                        if (!WebAccountSession.ApplyHeaders(retry)) return response;
                    }
                    if (bearer && !string.Equals(Config.UserToken, refreshToken, StringComparison.Ordinal)) return response;
                    if (browser && browserSession != WebAccountSession.CacheKey) return response;
                    var retriedResponse = browser
                        ? await SendBrowserRequestAsync(retry, cancellationToken).ConfigureAwait(false)
                        : await Client.SendAsync(retry, completionOption, cancellationToken).ConfigureAwait(false);
                    // The retry request is temporary; expose the caller's request on the response.
                    retriedResponse.RequestMessage = request;
                    response.Dispose();
                    return retriedResponse;
                }
            }
            catch (OperationCanceledException) { response.Dispose(); throw; }
            catch (Exception)
            {
                System.Diagnostics.Debug.WriteLine("[Innertube] Authentication recovery unavailable.");
                return response;
            }
        }

        private static async Task<HttpRequestMessage> CopyRequestAsync(HttpRequestMessage original)
        {
            var copy = new HttpRequestMessage(original.Method, original.RequestUri) { Version = original.Version };
            foreach (var header in original.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            foreach (var property in original.Properties) copy.Properties[property.Key] = property.Value;
            if (original.Content != null)
            {
                copy.Content = new ByteArrayContent(await original.Content.ReadAsByteArrayAsync().ConfigureAwait(false));
                foreach (var header in original.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return copy;
        }

        private static async Task ApplyBrowserRequestContextAsync(HttpRequestMessage request)
        {
            if (request.Content == null) return;
            JsonObject body;
            if (!JsonObject.TryParse(await request.Content.ReadAsStringAsync().ConfigureAwait(false), out body)) return;
            Windows.Data.Json.IJsonValue value;
            if (!body.TryGetValue("context", out value) || value.ValueType != JsonValueType.Object) return;
            var context = value.GetObject();
            WebAccountSession.ApplyClientContext(context);
            if (context.TryGetValue("client", out value) && value.ValueType == JsonValueType.Object)
            {
                var version = value.GetObject().GetNamedString("clientVersion", string.Empty);
                request.Headers.Remove("X-YouTube-Client-Version");
                if (!string.IsNullOrEmpty(version)) request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", version);
            }
            request.Content.Dispose();
            request.Content = new StringContent(body.Stringify(), System.Text.Encoding.UTF8, "application/json");
        }

        private static async Task<HttpResponseMessage> SendBrowserRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // WebView and this transport share HttpOnly cookies and their Set-Cookie updates.
            // Anonymous and QR requests continue to use the cookie-free .NET transport.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            using (var filter = new HttpBaseProtocolFilter { AllowAutoRedirect = false })
            using (var client = new Windows.Web.Http.HttpClient(filter))
            using (var nativeRequest = new Windows.Web.Http.HttpRequestMessage(
                new Windows.Web.Http.HttpMethod(request.Method.Method), request.RequestUri))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                foreach (var header in request.Headers)
                    if (!string.Equals(header.Key, "Cookie", StringComparison.OrdinalIgnoreCase))
                        nativeRequest.Headers.TryAppendWithoutValidation(header.Key, string.Join(", ", header.Value));
                if (request.Content != null)
                {
                    var bytes = await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    nativeRequest.Content = new Windows.Web.Http.HttpBufferContent(CryptographicBuffer.CreateFromByteArray(bytes));
                    // Set typed content headers explicitly; appending Content-Type can
                    // leave the buffer's default type or combine it with application/json.
                    var contentType = request.Content.Headers.ContentType;
                    if (contentType != null)
                        nativeRequest.Content.Headers.ContentType =
                            Windows.Web.Http.Headers.HttpMediaTypeHeaderValue.Parse(contentType.ToString());
                    nativeRequest.Content.Headers.ContentLength = (ulong)bytes.Length;
                    foreach (var header in request.Content.Headers)
                    {
                        if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                        nativeRequest.Content.Headers[header.Key] = string.Join(", ", header.Value);
                    }
                }
                using (var nativeResponse = await client.SendRequestAsync(nativeRequest).AsTask(timeout.Token).ConfigureAwait(false))
                {
                    var bytes = new byte[0];
                    if (nativeResponse.Content != null)
                    {
                        var buffer = await nativeResponse.Content.ReadAsBufferAsync().AsTask(timeout.Token).ConfigureAwait(false);
                        CryptographicBuffer.CopyToByteArray(buffer, out bytes);
                    }
                    var response = new HttpResponseMessage((HttpStatusCode)(int)nativeResponse.StatusCode)
                    {
                        RequestMessage = request,
                        ReasonPhrase = nativeResponse.ReasonPhrase,
                        Content = new ByteArrayContent(bytes)
                    };
                    foreach (var header in nativeResponse.Headers) response.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    if (nativeResponse.Content != null)
                        foreach (var header in nativeResponse.Content.Headers)
                            if (!string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    return response;
                }
            }
        }

        internal Task<string> GetStringAsync(string url)
        {
            return Client.GetStringAsync(url);
        }

        internal Task<byte[]> GetByteArrayAsync(string url)
        {
            return Client.GetByteArrayAsync(url);
        }

        internal Task<byte[]> GetByteArrayAsync(Uri url)
        {
            return Client.GetByteArrayAsync(url);
        }

        internal Task<HttpResponseMessage> GetAsync(Uri url)
        {
            return Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
    }
}
