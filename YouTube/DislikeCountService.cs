using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace YouTube
{
    // Troubadour's YTDislikes: optional fetch, 15-second timeout, 64 KiB limit,
    // ten-minute cache. This client carries no YouTube credentials or cookies.
    internal static class DislikeCountService
    {
        private const string SettingKey = "ShowDislikeCount";
        private const int BodyLimit = 64 * 1024;
        private const int CacheLimit = 128;
        private static readonly object Sync = new object();
        private static readonly HttpClient Client = new HttpClient(new HttpClientHandler { UseCookies = false });
        private static readonly Dictionary<string, CacheEntry> Cache = new Dictionary<string, CacheEntry>();
        private static readonly Dictionary<string, PendingRequest> Pending = new Dictionary<string, PendingRequest>();

        private sealed class CacheEntry
        {
            public long Count;
            public DateTime ExpiresUtc;
        }

        private sealed class PendingRequest
        {
            public readonly TaskCompletionSource<long?> Completion = new TaskCompletionSource<long?>();
            public readonly CancellationTokenSource Cancellation = new CancellationTokenSource();
        }

        public static event EventHandler EnabledChanged;

        public static bool IsEnabled()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out value) && value is bool)
                    return (bool)value;
            }
            catch { }
            return false;
        }

        public static void SetEnabled(bool enabled)
        {
            try { ApplicationData.Current.LocalSettings.Values[SettingKey] = enabled; }
            catch { }
            if (!enabled)
            {
                List<PendingRequest> requests;
                lock (Sync) { requests = new List<PendingRequest>(Pending.Values); }
                foreach (var request in requests)
                {
                    try { request.Cancellation.Cancel(); }
                    catch (ObjectDisposedException) { }
                }
            }
            var handler = EnabledChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }

        public static Task<long?> GetCountAsync(string videoId)
        {
            if (!IsEnabled() || string.IsNullOrWhiteSpace(videoId)) return Task.FromResult<long?>(null);
            videoId = videoId.Trim();
            PendingRequest request;
            lock (Sync)
            {
                CacheEntry entry;
                if (Cache.TryGetValue(videoId, out entry) && entry.ExpiresUtc > DateTime.UtcNow)
                    return Task.FromResult<long?>(entry.Count);
                if (Pending.TryGetValue(videoId, out request))
                {
                    if (!request.Cancellation.IsCancellationRequested) return request.Completion.Task;
                    Pending.Remove(videoId);
                }
                request = new PendingRequest();
                Pending.Add(videoId, request);
            }
            var ignored = FetchAsync(videoId, request);
            return request.Completion.Task;
        }

        private static async Task FetchAsync(string videoId, PendingRequest request)
        {
            long? count = null;
            try
            {
                if (IsEnabled())
                {
                    request.Cancellation.CancelAfter(TimeSpan.FromSeconds(15));
                    var token = request.Cancellation.Token;
                    var url = "https://returnyoutubedislikeapi.com/votes?videoId=" + Uri.EscapeDataString(videoId);
                    using (var message = new HttpRequestMessage(HttpMethod.Get, url))
                    using (var response = await Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                    {
                        if (response.IsSuccessStatusCode
                            && (!response.Content.Headers.ContentLength.HasValue || response.Content.Headers.ContentLength.Value <= BodyLimit))
                        {
                            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            using (var body = new MemoryStream())
                            {
                                var buffer = new byte[4096];
                                while (body.Length <= BodyLimit)
                                {
                                    var length = Math.Min(buffer.Length, BodyLimit + 1 - (int)body.Length);
                                    var read = await stream.ReadAsync(buffer, 0, length, token).ConfigureAwait(false);
                                    if (read == 0) break;
                                    body.Write(buffer, 0, read);
                                }
                                if (body.Length <= BodyLimit)
                                    count = ParseCount(Encoding.UTF8.GetString(body.ToArray(), 0, (int)body.Length));
                            }
                        }
                    }
                }
                if (count.HasValue)
                {
                    lock (Sync)
                    {
                        if (Cache.Count >= CacheLimit)
                        {
                            string oldestKey = null;
                            var oldestExpiry = DateTime.MaxValue;
                            foreach (var entry in Cache)
                            {
                                if (entry.Value.ExpiresUtc < oldestExpiry)
                                {
                                    oldestKey = entry.Key;
                                    oldestExpiry = entry.Value.ExpiresUtc;
                                }
                            }
                            if (oldestKey != null) Cache.Remove(oldestKey);
                        }
                        Cache[videoId] = new CacheEntry { Count = count.Value, ExpiresUtc = DateTime.UtcNow.AddMinutes(10) };
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Dislikes] Fetch failed: " + ex.GetType().Name); }
            finally
            {
                lock (Sync)
                {
                    PendingRequest active;
                    if (Pending.TryGetValue(videoId, out active) && ReferenceEquals(active, request))
                        Pending.Remove(videoId);
                }
                request.Completion.TrySetResult(count);
                request.Cancellation.Dispose();
            }
        }

        private static long? ParseCount(string json)
        {
            JsonObject root;
            IJsonValue value;
            if (!JsonObject.TryParse(json, out root) || !root.TryGetValue("dislikes", out value)
                || value.ValueType != JsonValueType.Number) return null;
            var number = value.GetNumber();
            if (double.IsNaN(number) || double.IsInfinity(number) || number < 0
                || number >= 9223372036854775808.0 || Math.Truncate(number) != number) return null;
            return (long)number;
        }
    }
}
