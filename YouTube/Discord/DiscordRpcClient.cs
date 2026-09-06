using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Profile;

namespace YouTube.Discord
{
    // UWP cannot open Discord's desktop named pipe directly. This publisher atomically writes the
    // RPC request into LocalState and starts the package's tiny full-trust bridge, which owns it.
    internal sealed class DiscordRpcClient : IDisposable
    {
        private const string PresenceFileName = "discord-presence.json";
        private const string PendingPresenceFileName = "discord-presence.pending";
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _bridgeLaunchAttempted;
        private bool _disposed;

        [DllImport("api-ms-win-core-processthreads-l1-1-0.dll")]
        private static extern uint GetCurrentProcessId();

        public async Task SetActivityAsync(DiscordPresenceState activity)
        {
            if (activity == null || _disposed)
                return;

            await PublishPayloadAsync(BuildSetActivityPayload(activity));
        }

        public async Task ClearActivityAsync()
        {
            if (_disposed || !DiscordPresenceService.IsAvailable())
                return;

            uint processId;
            try { processId = GetCurrentProcessId(); }
            catch { processId = 0; }
            var payload = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + processId
                + ",\"activity\":null},\"nonce\":\"" + DateTime.UtcNow.Ticks + "\"}";
            await PublishPayloadAsync(payload);
        }

        private async Task PublishPayloadAsync(string payload)
        {
            if (_disposed)
                return;

            await _gate.WaitAsync();
            try
            {
                var folder = ApplicationData.Current.LocalFolder;
                var pending = await folder.CreateFileAsync(PendingPresenceFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(pending, payload);
                var current = await folder.CreateFileAsync(PresenceFileName, CreationCollisionOption.OpenIfExists);
                await pending.MoveAndReplaceAsync(current);
                await EnsureBridgeStartedAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Discord] Activity publish failed: " + ex.Message);
            }
            finally
            {
                _gate.Release();
            }
        }

        private async Task EnsureBridgeStartedAsync()
        {
            if (_bridgeLaunchAttempted)
                return;
            _bridgeLaunchAttempted = true;

            try
            {
                if (!string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Desktop", StringComparison.OrdinalIgnoreCase))
                {
                    System.Diagnostics.Debug.WriteLine("[Discord] Rich Presence is desktop-only");
                    return;
                }
                if (!ApiInformation.IsTypePresent("Windows.ApplicationModel.FullTrustProcessLauncher"))
                    return;

                await FullTrustProcessLauncher.LaunchFullTrustProcessForCurrentAppAsync();
                System.Diagnostics.Debug.WriteLine("[Discord] IPC bridge started");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Discord] IPC bridge could not start: " + ex.Message);
            }
        }

        private static string BuildSetActivityPayload(DiscordPresenceState activity)
        {
            uint processId;
            try { processId = GetCurrentProcessId(); }
            catch { processId = 0; }

            var details = Trim(activity.Details, 128);
            var state = Trim(activity.State, 128);
            var json = "{\"cmd\":\"SET_ACTIVITY\",\"args\":{\"pid\":" + processId
                + ",\"activity\":{\"type\":3,\"details\":\"" + Escape(details)
                + "\",\"state\":\"" + Escape(state) + "\"";

            if (activity.StartTimestamp > 0 || activity.EndTimestamp > 0)
            {
                json += ",\"timestamps\":{";
                if (activity.StartTimestamp > 0)
                    json += "\"start\":" + activity.StartTimestamp;
                if (activity.StartTimestamp > 0 && activity.EndTimestamp > 0)
                    json += ",";
                if (activity.EndTimestamp > 0)
                    json += "\"end\":" + activity.EndTimestamp;
                json += "}";
            }

            var largeImage = IsRemoteImageUrl(activity.ImageUrl)
                ? activity.ImageUrl
                : Config.LargeImageKey;
            if (!string.IsNullOrWhiteSpace(largeImage))
            {
                var largeText = string.IsNullOrWhiteSpace(activity.ImageText)
                    ? Config.LargeImageText
                    : activity.ImageText;
                json += ",\"assets\":{\"large_image\":\"" + Escape(largeImage)
                    + "\",\"large_text\":\"" + Escape(Trim(largeText, 128)) + "\"}";
            }

            json += "}},\"nonce\":\"" + DateTime.UtcNow.Ticks + "\"}";
            return json;
        }

        private static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;
            var result = new System.Text.StringBuilder(value.Length + 16);
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': result.Append("\\\\"); break;
                    case '"': result.Append("\\\""); break;
                    case '\r': result.Append("\\r"); break;
                    case '\n': result.Append("\\n"); break;
                    case '\t': result.Append("\\t"); break;
                    default:
                        if (c < 32) result.Append("\\u").Append(((int)c).ToString("x4"));
                        else result.Append(c);
                        break;
                }
            }
            return result.ToString();
        }

        private static string Trim(string value, int maximum)
        {
            value = string.IsNullOrWhiteSpace(value) ? Config.ProductName : value.Trim();
            return value.Length <= maximum ? value : value.Substring(0, maximum - 1) + "…";
        }

        private static bool IsRemoteImageUrl(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        public void Dispose()
        {
            _disposed = true;
            _gate.Dispose();
        }
    }
}
