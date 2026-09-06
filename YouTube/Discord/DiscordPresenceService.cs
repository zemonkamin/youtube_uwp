using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System.Profile;

namespace YouTube.Discord
{
    internal sealed class DiscordPresenceState
    {
        public string Kind;
        public string ItemId;
        public string Details;
        public string State;
        public long StartTimestamp;
        public long EndTimestamp;
        public string ImageUrl;
        public string ImageText;

        public DiscordPresenceState Clone()
        {
            return (DiscordPresenceState)MemberwiseClone();
        }
    }

    // One entry point for every page.  It owns deduplication, progress throttling and transport so
    // UI code only describes what the user is doing.
    internal static class DiscordPresenceService
    {
        private static readonly object Sync = new object();
        private static readonly DiscordRpcClient Client = new DiscordRpcClient();
        private static DiscordPresenceState _current = new DiscordPresenceState
        {
            Kind = "page",
            Details = Config.ProductName,
            State = Config.ProductName
        };
        private static CancellationTokenSource _publishCancellation;
        private static DateTime _lastProgressPublishUtc = DateTime.MinValue;

        public static void SetPage(string pageName)
        {
            SetState("page", string.Empty, Safe(pageName, Config.ProductName), Config.ProductName, null, null);
        }

        public static void SetSearch(string query)
        {
            SetState("search", query, Localization.Format("DiscordStatusSearchFormat", Safe(query, Config.ProductName)), Config.ProductName, null, null);
        }

        public static void SetChannel(string channelId, string channelName, string imageUrl = null)
        {
            var name = Safe(channelName, channelId);
            SetState("channel", channelId, Localization.Format("DiscordStatusChannelFormat", name), Config.ProductName, NormalizeChannelImageUrl(imageUrl), name);
        }

        public static void SetPlaylist(string playlistId, string playlistTitle, string imageUrl = null)
        {
            var title = Safe(playlistTitle, playlistId);
            SetState("playlist", playlistId, Localization.Format("DiscordStatusPlaylistFormat", title), Config.ProductName, imageUrl, title);
        }

        public static void SetShort(string videoId, string title, string channelName, string imageUrl = null)
        {
            var safeTitle = Safe(title, "YouTube Shorts");
            if (!string.IsNullOrWhiteSpace(videoId))
                imageUrl = BuildVideoThumbnailUrl(videoId);
            SetState("short", videoId, Localization.Format("DiscordStatusShortsFormat", safeTitle), Safe(channelName, Config.ProductName), imageUrl, safeTitle);
        }

        public static void SetVideo(string videoId, string title, string channelName, string imageUrl = null)
        {
            // hqdefault/sddefault use a 4:3 canvas and bake black bars around 16:9 videos.
            // hq720 is the full 16:9 artwork Discord should receive.
            if (!string.IsNullOrWhiteSpace(videoId))
                imageUrl = BuildVideoThumbnailUrl(videoId);
            var safeTitle = Safe(title, Config.ProductName);
            SetState("video", videoId, safeTitle, Safe(channelName, Config.ProductName), imageUrl, safeTitle);
            _lastProgressPublishUtc = DateTime.MinValue;
        }

        public static void UpdateVideoProgress(TimeSpan position, TimeSpan duration, bool isPlaying, bool force = false)
        {
            DiscordPresenceState snapshot;
            lock (Sync)
            {
                if (_current == null || _current.Kind != "video")
                    return;

                var now = DateTime.UtcNow;
                if (!force && (now - _lastProgressPublishUtc).TotalSeconds < Config.ProgressPublishIntervalSeconds)
                    return;
                _lastProgressPublishUtc = now;

                position = position < TimeSpan.Zero ? TimeSpan.Zero : position;
                duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration;
                var progress = FormatTime(position);
                if (duration > TimeSpan.Zero)
                    progress += " / " + FormatTime(duration);

                var channel = RemoveProgressSuffix(_current.State);
                _current.State = channel + " • " + (isPlaying ? progress : Localization.GetString("DiscordStatusPaused") + " • " + progress);
                _current.StartTimestamp = 0;
                _current.EndTimestamp = 0;

                if (isPlaying && duration > position && duration > TimeSpan.Zero)
                {
                    var unixNow = ToUnixSeconds(now);
                    _current.StartTimestamp = unixNow - (long)position.TotalSeconds;
                    _current.EndTimestamp = unixNow + (long)(duration - position).TotalSeconds;
                }
                snapshot = _current.Clone();
            }
            SchedulePublish(snapshot);
        }

        public static bool IsAvailable()
        {
            try
            {
                return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Desktop", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        public static void EnsureDefault()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(Config.PresenceEnabledSettingKey))
                    values[Config.PresenceEnabledSettingKey] = IsAvailable();
                if (!IsAvailable())
                    values[Config.PresenceEnabledSettingKey] = false;
            }
            catch { }
        }

        public static bool IsEnabled()
        {
            EnsureDefault();
            if (!IsAvailable())
                return false;
            try
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(Config.PresenceEnabledSettingKey, out value)
                    && value is bool && (bool)value;
            }
            catch { return false; }
        }

        public static void SetEnabled(bool enabled)
        {
            enabled = enabled && IsAvailable();
            try { ApplicationData.Current.LocalSettings.Values[Config.PresenceEnabledSettingKey] = enabled; }
            catch { }

            if (!enabled)
            {
                lock (Sync)
                {
                    if (_publishCancellation != null)
                        _publishCancellation.Cancel();
                }
                var ignoredClear = Client.ClearActivityAsync();
                return;
            }

            DiscordPresenceState snapshot;
            lock (Sync) { snapshot = _current != null ? _current.Clone() : null; }
            if (snapshot != null)
                SchedulePublish(snapshot);
        }

        private static void SetState(string kind, string itemId, string details, string state, string imageUrl, string imageText)
        {
            DiscordPresenceState snapshot;
            lock (Sync)
            {
                _current = new DiscordPresenceState
                {
                    Kind = kind,
                    ItemId = itemId ?? string.Empty,
                    Details = details,
                    State = state,
                    ImageUrl = NormalizeRemoteImageUrl(imageUrl),
                    ImageText = imageText
                };
                snapshot = _current.Clone();
            }
            SchedulePublish(snapshot);
        }

        private static void SchedulePublish(DiscordPresenceState snapshot)
        {
            if (!IsEnabled())
                return;
            CancellationTokenSource cancellation;
            lock (Sync)
            {
                if (_publishCancellation != null)
                    _publishCancellation.Cancel();
                _publishCancellation = new CancellationTokenSource();
                cancellation = _publishCancellation;
            }

            var ignored = PublishAfterDelayAsync(snapshot, cancellation.Token);
        }

        private static async Task PublishAfterDelayAsync(DiscordPresenceState snapshot, CancellationToken token)
        {
            try
            {
                await Task.Delay(Config.PublishDelayMilliseconds, token);
                token.ThrowIfCancellationRequested();
                await Client.SetActivityAsync(snapshot);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Discord] Presence update failed: " + ex.Message);
            }
        }

        private static string Safe(string primary, string fallback)
        {
            return !string.IsNullOrWhiteSpace(primary) ? primary.Trim()
                : (!string.IsNullOrWhiteSpace(fallback) ? fallback.Trim() : Config.ProductName);
        }

        private static string RemoveProgressSuffix(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Config.ProductName;
            var separator = value.IndexOf(" • ", StringComparison.Ordinal);
            return separator > 0 ? value.Substring(0, separator) : value;
        }

        private static string NormalizeRemoteImageUrl(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            value = value.Trim();
            if (value.StartsWith("//", StringComparison.Ordinal))
                value = "https:" + value;
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                value = "https://" + value.Substring("http://".Length);
            return value.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? value : string.Empty;
        }

        private static string BuildVideoThumbnailUrl(string videoId)
        {
            videoId = videoId == null ? string.Empty : videoId.Trim();
            return string.IsNullOrWhiteSpace(videoId)
                ? string.Empty
                : "https://i.ytimg.com/vi/" + videoId + "/hq720.jpg";
        }

        private static string NormalizeChannelImageUrl(string value)
        {
            value = NormalizeRemoteImageUrl(value);
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            if (value.IndexOf("yt3.", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("ggpht", StringComparison.OrdinalIgnoreCase) < 0
                && value.IndexOf("googleusercontent", StringComparison.OrdinalIgnoreCase) < 0)
                return value;

            // InnerTube commonly returns s48/s88 avatars. Discord's proxy is much more
            // reliable with a proper large square source, so only replace the transform tail.
            var query = value.IndexOf('?');
            var clean = query >= 0 ? value.Substring(0, query) : value;
            var equals = clean.LastIndexOf('=');
            if (equals > clean.LastIndexOf('/'))
                clean = clean.Substring(0, equals);
            return clean + "=s1024-c-k-c0x00ffffff-no-rj";
        }

        private static string FormatTime(TimeSpan value)
        {
            if (value.TotalHours >= 1)
                return ((int)value.TotalHours) + ":" + value.Minutes.ToString("00") + ":" + value.Seconds.ToString("00");
            return ((int)value.TotalMinutes) + ":" + value.Seconds.ToString("00");
        }

        private static long ToUnixSeconds(DateTime utc)
        {
            return (long)(utc.ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}
