using System;
using System.Threading.Tasks;

namespace YouTube.Innertube
{
    internal static class YouTubeImageClient
    {
        internal static Task<byte[]> DownloadAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return Task.FromResult(new byte[0]);
            }

            return YouTubeHttpClient.Shared.GetByteArrayAsync(url);
        }

        internal static string GetMaxResolutionThumbnailUrl(string videoId)
        {
            return "https://img.youtube.com/vi/"
                + Uri.EscapeDataString((videoId ?? string.Empty).Trim())
                + "/maxresdefault.jpg";
        }
    }
}
