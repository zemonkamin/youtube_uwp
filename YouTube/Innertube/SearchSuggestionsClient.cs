using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    internal static class SearchSuggestionsClient
    {
        private const int MaxCacheEntries = 24;
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, SuggestionCacheEntry> Cache =
            new Dictionary<string, SuggestionCacheEntry>(StringComparer.OrdinalIgnoreCase);

        private sealed class SuggestionCacheEntry
        {
            internal List<string> Items { get; set; }
            internal DateTime ExpiresUtc { get; set; }
            internal long Sequence { get; set; }
        }

        private static long _cacheSequence;

        internal static async Task<List<string>> GetAsync(
            string query,
            string language,
            string region,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<string>();
            }

            query = query.Trim();
            var cacheKey = (language ?? string.Empty) + ":" + (region ?? string.Empty) + ":" + query;
            List<string> cached;
            if (TryGetCached(cacheKey, out cached))
                return cached;

            var url = YouTubeApiConfig.SuggestionsBaseUrl
                + "?client=youtube&hl=" + Uri.EscapeDataString(language ?? string.Empty)
                + "&gl=" + Uri.EscapeDataString(region ?? string.Empty)
                + "&ds=yt&q=" + Uri.EscapeDataString(query);
            string responseText;
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            using (var response = await YouTubeHttpClient.Shared.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                // CoreCLR used by the original UWP target cannot resolve windows-1251 through
                // ReadAsStringAsync. Read raw bytes so a response/proxy charset can never break
                // suggestions on Windows 10 Mobile.
                var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var charset = response.Content.Headers.ContentType == null
                    ? string.Empty
                    : response.Content.Headers.ContentType.CharSet;
                responseText = DecodeResponse(bytes, charset);
            }
            var json = UnwrapJsonp(responseText);

            JsonArray root;
            if (!JsonArray.TryParse(json, out root) || root.Count < 2 || root[1].ValueType != JsonValueType.Array)
            {
                return new List<string>();
            }

            var source = root[1].GetArray();
            var result = new List<string>(Math.Min(10, (int)source.Count));
            var count = Math.Min(10, (int)source.Count);
            for (var i = 0; i < count; i++)
            {
                if (source[i].ValueType == JsonValueType.String)
                {
                    AddSuggestion(result, source[i].GetString());
                    continue;
                }

                if (source[i].ValueType != JsonValueType.Array)
                {
                    continue;
                }

                var item = source[i].GetArray();
                if (item.Count > 0 && item[0].ValueType == JsonValueType.String)
                {
                    AddSuggestion(result, item[0].GetString());
                }
            }
            StoreCached(cacheKey, result);
            return result;
        }

        private static bool TryGetCached(string key, out List<string> items)
        {
            items = null;
            lock (CacheGate)
            {
                SuggestionCacheEntry entry;
                if (!Cache.TryGetValue(key, out entry))
                    return false;
                if (entry.ExpiresUtc <= DateTime.UtcNow)
                {
                    Cache.Remove(key);
                    return false;
                }

                entry.Sequence = ++_cacheSequence;
                items = new List<string>(entry.Items);
                return true;
            }
        }

        private static void StoreCached(string key, List<string> items)
        {
            if (string.IsNullOrWhiteSpace(key) || items == null)
                return;

            lock (CacheGate)
            {
                Cache[key] = new SuggestionCacheEntry
                {
                    Items = new List<string>(items),
                    ExpiresUtc = DateTime.UtcNow.AddMinutes(2),
                    Sequence = ++_cacheSequence
                };

                while (Cache.Count > MaxCacheEntries)
                {
                    string oldestKey = null;
                    long oldestSequence = long.MaxValue;
                    foreach (var pair in Cache)
                    {
                        if (pair.Value.Sequence < oldestSequence)
                        {
                            oldestKey = pair.Key;
                            oldestSequence = pair.Value.Sequence;
                        }
                    }
                    if (oldestKey == null)
                        break;
                    Cache.Remove(oldestKey);
                }
            }
        }

        private static void AddSuggestion(List<string> result, string value)
        {
            if (result == null || string.IsNullOrWhiteSpace(value))
                return;

            var suggestion = value.Trim();
            for (var i = 0; i < result.Count; i++)
            {
                if (string.Equals(result[i], suggestion, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            result.Add(suggestion);
        }

        private static string DecodeResponse(byte[] bytes, string charset)
        {
            if (bytes == null || bytes.Length == 0)
                return string.Empty;

            var normalizedCharset = (charset ?? string.Empty).Trim().Trim('"').ToLowerInvariant();
            if (normalizedCharset == "windows-1251"
                || normalizedCharset == "cp1251"
                || normalizedCharset == "x-cp1251")
            {
                return DecodeWindows1251(bytes);
            }

            if (bytes.Length >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

            var offset = bytes.Length >= 3
                && bytes[0] == 0xef
                && bytes[1] == 0xbb
                && bytes[2] == 0xbf
                ? 3
                : 0;
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
            }
            catch (DecoderFallbackException)
            {
                // Some regional proxies omit charset while still returning CP1251.
                return DecodeWindows1251(bytes);
            }
        }

        private static string DecodeWindows1251(byte[] bytes)
        {
            var chars = new char[bytes.Length];
            for (var i = 0; i < bytes.Length; i++)
            {
                var value = bytes[i];
                if (value < 0x80)
                {
                    chars[i] = (char)value;
                }
                else if (value >= 0xc0)
                {
                    chars[i] = (char)('\u0410' + value - 0xc0);
                }
                else
                {
                    chars[i] = Windows1251Extended[value - 0x80];
                }
            }
            return new string(chars);
        }

        private static readonly char[] Windows1251Extended =
        {
            '\u0402', '\u0403', '\u201a', '\u0453', '\u201e', '\u2026', '\u2020', '\u2021',
            '\u20ac', '\u2030', '\u0409', '\u2039', '\u040a', '\u040c', '\u040b', '\u040f',
            '\u0452', '\u2018', '\u2019', '\u201c', '\u201d', '\u2022', '\u2013', '\u2014',
            '\ufffd', '\u2122', '\u0459', '\u203a', '\u045a', '\u045c', '\u045b', '\u045f',
            '\u00a0', '\u040e', '\u045e', '\u0408', '\u00a4', '\u0490', '\u00a6', '\u00a7',
            '\u0401', '\u00a9', '\u0404', '\u00ab', '\u00ac', '\u00ad', '\u00ae', '\u0407',
            '\u00b0', '\u00b1', '\u0406', '\u0456', '\u0491', '\u00b5', '\u00b6', '\u00b7',
            '\u0451', '\u2116', '\u0454', '\u00bb', '\u0458', '\u0405', '\u0455', '\u0457'
        };

        private static string UnwrapJsonp(string value)
        {
            var json = (value ?? string.Empty).Trim();
            const string wrapper = "window.google.ac.h(";
            if (json.StartsWith(wrapper, StringComparison.Ordinal))
            {
                json = json.Substring(wrapper.Length);
                if (json.EndsWith(";", StringComparison.Ordinal))
                    json = json.Substring(0, json.Length - 1).TrimEnd();
                if (json.EndsWith(")", StringComparison.Ordinal))
                {
                    json = json.Substring(0, json.Length - 1);
                }
            }

            if (json.StartsWith(")]}'", StringComparison.Ordinal))
            {
                json = json.Substring(4);
            }
            return json;
        }
    }
}
