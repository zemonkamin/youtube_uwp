using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace YouTube.Innertube
{
    // A single HttpClient/handler is shared by every YouTube screen. Creating one per page
    // fragments the connection pool and is particularly expensive on Windows 10 Mobile.
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
            return Client.SendAsync(request);
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Client.SendAsync(request, cancellationToken);
        }

        internal Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption)
        {
            return Client.SendAsync(request, completionOption);
        }

        internal Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
        {
            return Client.SendAsync(request, completionOption, cancellationToken);
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
