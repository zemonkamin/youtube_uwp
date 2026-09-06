using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.Web.Http;

namespace YouTube.Innertube
{
    // Bitmap decoding uses the WinRT HTTP stack because it exposes IInputStream directly.
    internal static class YouTubeThumbnailClient
    {
        private static readonly HttpClient Client = new HttpClient();

        internal static Task<IInputStream> OpenStreamAsync(string url)
        {
            return Client.GetInputStreamAsync(new Uri(url)).AsTask();
        }
    }
}
