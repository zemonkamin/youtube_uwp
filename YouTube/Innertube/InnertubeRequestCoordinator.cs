using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    // Coalesces identical Innertube calls and parses each JSON body once. Video pages issue
    // several consumers for /next (metadata, comments, related, rating, subscription); sharing
    // the in-flight task is considerably cheaper than sharing only the HttpClient connection.
    internal static class InnertubeRequestCoordinator
    {
        // /next responses are large on phones. A small LRU window is enough to cover concurrent
        // consumers and quick back navigation without retaining dozens of parsed trees.
        private static int MaxEntries
        {
            get { return global::YouTube.ResponsiveLayout.IsPhoneDevice ? 4 : 12; }
        }
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, CacheEntry> Entries =
            new Dictionary<string, CacheEntry>(StringComparer.Ordinal);

        internal sealed class JsonResponse
        {
            internal string Text { get; set; }
            internal JsonObject Root { get; set; }
        }

        private sealed class CacheEntry
        {
            internal Task<JsonResponse> Request { get; set; }
            internal DateTime ExpiresUtc { get; set; }
            internal long Sequence { get; set; }
        }

        private static long _sequence;

        internal static Task<JsonResponse> GetJsonAsync(
            string key,
            Func<Task<string>> requestFactory,
            TimeSpan maxAge)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ArgumentException("Innertube request key is required", "key");
            if (requestFactory == null)
                throw new ArgumentNullException("requestFactory");

            lock (Gate)
            {
                RemoveExpiredEntries(DateTime.UtcNow);

                CacheEntry existing;
                if (Entries.TryGetValue(key, out existing))
                {
                    existing.Sequence = ++_sequence;
                    return existing.Request;
                }

                var entry = new CacheEntry
                {
                    ExpiresUtc = DateTime.UtcNow.Add(maxAge),
                    Sequence = ++_sequence
                };
                entry.Request = FetchAndParseAsync(key, requestFactory);
                Entries[key] = entry;
                TrimEntries();
                return entry.Request;
            }
        }

        // Stream URLs are signed and can become unusable while a long video is still open.
        // Playback recovery must be able to bypass the otherwise useful short player cache.
        internal static void Invalidate(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return;

            lock (Gate)
            {
                Entries.Remove(key);
            }
        }

        private static async Task<JsonResponse> FetchAndParseAsync(
            string key,
            Func<Task<string>> requestFactory)
        {
            try
            {
                var text = await requestFactory().ConfigureAwait(false);
                JsonObject root = null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    root = await Task.Run(delegate
                    {
                        JsonObject parsed;
                        return FastJson.TryParseObject(text, out parsed) ? parsed : null;
                    }).ConfigureAwait(false);
                }

                if (root == null)
                    throw new FormatException("Innertube returned invalid JSON");

                return new JsonResponse { Text = text, Root = root };
            }
            catch
            {
                lock (Gate)
                {
                    // The task is still completing while its own catch block runs, so checking
                    // IsFaulted here would leave the failed request cached until expiry.
                    Entries.Remove(key);
                }
                throw;
            }
        }

        private static void RemoveExpiredEntries(DateTime now)
        {
            var expired = new List<string>();
            foreach (var pair in Entries)
            {
                if (pair.Value.ExpiresUtc <= now && pair.Value.Request.IsCompleted)
                    expired.Add(pair.Key);
            }
            for (var i = 0; i < expired.Count; i++) Entries.Remove(expired[i]);
        }

        private static void TrimEntries()
        {
            while (Entries.Count > MaxEntries)
            {
                string oldestKey = null;
                long oldestSequence = long.MaxValue;
                foreach (var pair in Entries)
                {
                    if (pair.Value.Request.IsCompleted && pair.Value.Sequence < oldestSequence)
                    {
                        oldestKey = pair.Key;
                        oldestSequence = pair.Value.Sequence;
                    }
                }
                if (oldestKey == null) return;
                Entries.Remove(oldestKey);
            }
        }
    }
}
