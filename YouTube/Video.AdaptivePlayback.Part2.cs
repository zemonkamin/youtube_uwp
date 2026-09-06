using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Video
    {
        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            var parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var separator = part.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(part.Substring(0, separator));
                var value = Uri.UnescapeDataString(part.Substring(separator + 1));
                result[key] = value;
            }

            return result;
        }

        private static string AppendQueryParameter(string baseUrl, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl;
            }

            var separator = baseUrl.IndexOf('?') >= 0 ? "&" : "?";
            return baseUrl
                + separator
                + Uri.EscapeDataString(key)
                + "="
                + Uri.EscapeDataString(value);
        }

        private static string GetQueryParameterFromUrl(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var questionIndex = url.IndexOf('?');
            if (questionIndex < 0 || questionIndex >= url.Length - 1)
            {
                return string.Empty;
            }

            var fragmentIndex = url.IndexOf('#', questionIndex + 1);
            var query = fragmentIndex >= 0
                ? url.Substring(questionIndex + 1, fragmentIndex - questionIndex - 1)
                : url.Substring(questionIndex + 1);
            var parsed = ParseQueryString(query);
            string value;
            return parsed.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string ReplaceQueryParameter(string url, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return url;
            }

            var fragment = string.Empty;
            var fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = url.Substring(fragmentIndex);
                url = url.Substring(0, fragmentIndex);
            }

            var questionIndex = url.IndexOf('?');
            var path = questionIndex >= 0 ? url.Substring(0, questionIndex) : url;
            var query = questionIndex >= 0 && questionIndex < url.Length - 1
                ? url.Substring(questionIndex + 1)
                : string.Empty;
            var parts = string.IsNullOrWhiteSpace(query)
                ? new string[0]
                : query.Split('&');
            var result = new List<string>();
            var replaced = false;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var separator = part.IndexOf('=');
                var rawKey = separator >= 0 ? part.Substring(0, separator) : part;
                var decodedKey = Uri.UnescapeDataString(rawKey);
                if (string.Equals(decodedKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty));
                    replaced = true;
                }
                else
                {
                    result.Add(part);
                }
            }

            if (!replaced)
            {
                result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty));
            }

            return path + "?" + string.Join("&", result) + fragment;
        }

        private static string ToJavaScriptStringLiteral(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                switch (ch)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static bool IsWindowsMobileDevice()
        {
            try
            {
                return string.Equals(
                    Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Mobile",
                    StringComparison.OrdinalIgnoreCase
                );
            }
            catch
            {
                return false;
            }
        }

    }
}
