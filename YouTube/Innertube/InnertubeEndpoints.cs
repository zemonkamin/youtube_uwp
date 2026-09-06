using System;

namespace YouTube.Innertube
{
    internal static class InnertubeEndpoints
    {
        internal static string Build(string endpoint)
        {
            return Build(endpoint, YouTubeApiConfig.ApiKey, false);
        }

        internal static string Build(string endpoint, bool prettyPrintFalse)
        {
            return Build(endpoint, YouTubeApiConfig.ApiKey, prettyPrintFalse);
        }

        internal static string Build(string endpoint, string apiKey, bool prettyPrintFalse)
        {
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                throw new ArgumentException("Innertube endpoint is required.", "endpoint");
            }

            var cleanEndpoint = endpoint.Trim().TrimStart('/');
            var separator = cleanEndpoint.IndexOf("?", StringComparison.Ordinal) >= 0 ? "&" : "?";
            var url = YouTubeApiConfig.InnertubeBaseUrl + cleanEndpoint
                + separator + "key=" + Uri.EscapeDataString(apiKey ?? string.Empty);
            return prettyPrintFalse ? url + "&prettyPrint=false" : url;
        }

        internal static string BuildDataApi(string relativeUrl)
        {
            if (string.IsNullOrWhiteSpace(relativeUrl))
            {
                throw new ArgumentException("Data API path is required.", "relativeUrl");
            }

            return YouTubeApiConfig.DataApiBaseUrl + relativeUrl.TrimStart('/');
        }

        internal static string BuildWithoutKey(string relativeUrl)
        {
            if (string.IsNullOrWhiteSpace(relativeUrl))
            {
                throw new ArgumentException("Innertube path is required.", "relativeUrl");
            }

            return YouTubeApiConfig.InnertubeBaseUrl + relativeUrl.TrimStart('/');
        }
    }
}
