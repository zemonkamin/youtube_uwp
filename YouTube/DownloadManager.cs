using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Data.Json;
using Windows.Graphics.Imaging;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.AccessCache;
using Windows.Storage.Streams;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Windows.UI.Xaml;

namespace YouTube
{
    public sealed class DownloadedVideoItem
    {
        public string VideoId { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string ChannelId { get; set; }
        public string Description { get; set; }
        public string ThumbnailUrl { get; set; }
        public string ChannelThumbnailUrl { get; set; }
        public string LocalThumbnailUri { get; set; }
        public string LocalChannelThumbnailUri { get; set; }
        public string ThumbnailFileToken { get; set; }
        public string FileToken { get; set; }
        public string OperationId { get; set; }
        public string VideoOperationId { get; set; }
        public string AudioOperationId { get; set; }
        public string VideoPartName { get; set; }
        public string AudioPartName { get; set; }
        public string CompletionTaskName { get; set; }
        public string TransferGroupName { get; set; }
        public string Quality { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public ulong BytesReceived { get; set; }
        public ulong TotalBytes { get; set; }
        public bool IsComplete { get; set; }
        public bool IsDownloading { get; set; }
        public bool IsAdaptive { get; set; }
        public bool IsFinalizing { get; set; }
        public long AddedTicks { get; set; }

        public int ProgressPercent
        {
            get
            {
                if (IsComplete) return 100;
                if (TotalBytes == 0) return 0;
                return (int)Math.Max(0, Math.Min(100,
                    Math.Round((double)BytesReceived * 100.0 / TotalBytes)));
            }
        }

        public string QualityText { get { return Quality ?? string.Empty; } }
        public string ProgressText { get { return ProgressPercent.ToString() + "%"; } }
        public Visibility DownloadingVisibility
        {
            get { return IsDownloading || IsFinalizing ? Visibility.Visible : Visibility.Collapsed; }
        }
    }

    internal static class DownloadManager
    {
        public const string FolderAccessToken = "YouTubeDownloadFolder";
        public const string FolderNameSettingKey = "DownloadFolderName";

        private const string IndexFileName = "downloads_index.json";
        private const string ThumbnailFolderName = "DownloadThumbnails";
        private const string PartsFolderName = "DownloadParts";
        private const string CompletionTaskPrefix = "YouTubeDownloadCompletion_";
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim ThumbnailGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim LegacyImageGate = new SemaphoreSlim(1, 1);
        private static readonly SemaphoreSlim FinalizeGate = new SemaphoreSlim(1, 1);
        private static readonly HttpClient Http = new HttpClient();
        private static readonly List<DownloadedVideoItem> Items = new List<DownloadedVideoItem>();
        private static readonly Dictionary<string, CancellationTokenSource> Active =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static volatile bool _clearing;
        private static int _clearGeneration;
        private static DateTime _lastExternalMergeUtc = DateTime.MinValue;

        // Do not use System.Progress<T> here. The VS2015 .NET Native toolchain emits invalid-IL
        // warnings for Progress<T>.InvokeHandlers on ARM, while BackgroundDownloader only needs
        // the tiny IProgress<T> contract and does not require UI-context marshalling.
        private sealed class TransferProgress : IProgress<DownloadOperation>
        {
            private readonly Action<DownloadOperation> _report;

            public TransferProgress(Action<DownloadOperation> report)
            {
                _report = report;
            }

            public void Report(DownloadOperation value)
            {
                if (_report != null) _report(value);
            }
        }

        private sealed class TranscodeProgress : IProgress<double>
        {
            private readonly Action<double> _report;

            public TranscodeProgress(Action<double> report)
            {
                _report = report;
            }

            public void Report(double value)
            {
                if (_report != null) _report(value);
            }
        }

        private sealed class AdaptiveDownloadProgress
        {
            private readonly object _gate = new object();
            private readonly DownloadedVideoItem _item;
            private ulong _videoReceived;
            private ulong _videoTotal;
            private ulong _audioReceived;
            private ulong _audioTotal;
            private double _lastPercent;
            private DateTime _lastSaved = DateTime.UtcNow;

            public AdaptiveDownloadProgress(DownloadedVideoItem item)
            {
                _item = item;
            }

            public void Report(bool video, DownloadOperation operation)
            {
                if (operation == null) return;
                var state = operation.Progress;
                lock (_gate)
                {
                    if (video)
                    {
                        _videoReceived = state.BytesReceived;
                        _videoTotal = state.TotalBytesToReceive;
                    }
                    else
                    {
                        _audioReceived = state.BytesReceived;
                        _audioTotal = state.TotalBytesToReceive;
                    }

                    var total = _videoTotal + _audioTotal;
                    var received = _videoReceived + _audioReceived;
                    var percent = total == 0 ? _lastPercent
                        : Math.Max(_lastPercent, Math.Min(90.0, (double)received * 90.0 / total));
                    _lastPercent = percent;
                    ReportAdaptiveProgress(_item, percent, ref _lastSaved);
                }
            }

            public DateTime LastSaved
            {
                get { lock (_gate) return _lastSaved; }
            }
        }

        public static event EventHandler Changed;

        public static async Task<IList<DownloadedVideoItem>> GetItemsAsync()
        {
            await EnsureLoadedAsync();
            await MergeExternalCompletionAsync();

            if (_clearing) return null;
            lock (Items)
            {
                return Items
                    .Where(i => i != null && (i.IsComplete || i.IsDownloading || i.IsFinalizing))
                    .OrderByDescending(i => i.AddedTicks)
                    .ToList();
            }
        }

        public static async Task<DownloadedVideoItem> FindAsync(string videoId)
        {
            await EnsureLoadedAsync();
            await MergeExternalCompletionAsync();
            lock (Items)
            {
                return Items
                    .Where(i => i != null
                        && string.Equals(i.VideoId, videoId, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(i => i.IsDownloading)
                    .ThenByDescending(i => i.IsComplete)
                    .ThenByDescending(i => i.AddedTicks)
                    .FirstOrDefault();
            }
        }

        public static async Task<DownloadedVideoItem> FindAsync(string videoId, string quality)
        {
            await EnsureLoadedAsync();
            await MergeExternalCompletionAsync();
            lock (Items)
            {
                return Items.FirstOrDefault(i => i != null
                    && string.Equals(i.VideoId, videoId, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(i.Quality, quality, StringComparison.OrdinalIgnoreCase));
            }
        }

        public static async Task<DownloadedVideoItem> StartAsync(
            string videoId,
            string title,
            string author,
            string channelId,
            string description,
            string thumbnailUrl,
            string channelThumbnailUrl,
            PlayerFormatModel format,
            PlayerFormatModel audioFormat,
            string mediaUserAgent)
        {
            if (format == null || string.IsNullOrWhiteSpace(format.Url)
                || string.IsNullOrWhiteSpace(videoId))
                return null;

            await EnsureLoadedAsync();

            if (_clearing) return null;

            var quality = Math.Max(1, format.QualityTier).ToString() + "p";
            var key = MakeKey(videoId, quality);
            lock (Items)
            {
                var existing = Items.FirstOrDefault(i => MakeKey(i.VideoId, i.Quality) == key);
                if (existing != null && (existing.IsComplete || existing.IsDownloading))
                    return existing;
            }

            var file = await CreateDestinationFileAsync(
                SanitizeFileName(videoId + "_" + quality + ".mp4"));
            if (file == null) return null;
            var fileToken = "YouTubeDownload_" + videoId + "_" + quality;
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(fileToken, file);

            var item = new DownloadedVideoItem
            {
                VideoId = videoId,
                Title = title ?? string.Empty,
                Author = author ?? string.Empty,
                ChannelId = channelId ?? string.Empty,
                Description = description ?? string.Empty,
                ThumbnailUrl = thumbnailUrl ?? string.Empty,
                ChannelThumbnailUrl = channelThumbnailUrl ?? string.Empty,
                FileToken = fileToken,
                Quality = quality,
                Width = format.Width,
                Height = format.Height,
                IsDownloading = true,
                AddedTicks = DateTimeOffset.UtcNow.Ticks
            };
            ClearCancellationMarker(item);

            if (audioFormat != null)
            {
                var partsFolder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    PartsFolderName, CreationCollisionOption.OpenIfExists);
                var partPrefix = SanitizeFileName(
                    videoId + "_" + quality + "_" + Guid.NewGuid().ToString("N"));
                var videoPart = await partsFolder.CreateFileAsync(
                    partPrefix + "_video.mp4", CreationCollisionOption.FailIfExists);
                var audioPart = await partsFolder.CreateFileAsync(
                    partPrefix + "_audio.m4a", CreationCollisionOption.FailIfExists);

                var completionGroup = await TryCreateCompletionGroupAsync(item, partPrefix);
                var transferGroup = BackgroundTransferGroup.CreateGroup("YouTubeAdaptiveDownloads");
                transferGroup.TransferBehavior = BackgroundTransferBehavior.Parallel;

                var videoDownloader = completionGroup == null
                    ? new BackgroundDownloader()
                    : new BackgroundDownloader(completionGroup);
                var audioDownloader = completionGroup == null
                    ? new BackgroundDownloader()
                    : new BackgroundDownloader(completionGroup);
                ConfigureAdaptiveDownloader(videoDownloader, transferGroup,
                    FirstNonEmpty(mediaUserAgent, format.MediaUserAgent));
                ConfigureAdaptiveDownloader(audioDownloader, transferGroup,
                    FirstNonEmpty(mediaUserAgent, audioFormat.MediaUserAgent));

                var videoOperation = videoDownloader.CreateDownload(
                    new Uri(format.Url), videoPart);
                var audioOperation = audioDownloader.CreateDownload(
                    new Uri(audioFormat.Url), audioPart);
                videoOperation.Priority = BackgroundTransferPriority.High;
                audioOperation.Priority = BackgroundTransferPriority.High;

                item.IsAdaptive = true;
                item.VideoOperationId = videoOperation.Guid.ToString();
                item.AudioOperationId = audioOperation.Guid.ToString();
                item.VideoPartName = videoPart.Name;
                item.AudioPartName = audioPart.Name;
                item.TransferGroupName = transferGroup.Name;
                lock (Items)
                {
                    Items.RemoveAll(i => MakeKey(i.VideoId, i.Quality) == key);
                    Items.Add(item);
                }
                await SaveAsync();
                RaiseChanged();

                var ignoredAdaptiveThumb = KeepImagesAsync(item, file);
                var ignoredAdaptiveRun = MonitorAdaptiveOperationsAsync(
                    item, videoOperation, audioOperation, completionGroup, false);
                return item;
            }

            var downloader = new BackgroundDownloader();
            var effectiveUserAgent = FirstNonEmpty(mediaUserAgent, format.MediaUserAgent);
            if (!string.IsNullOrWhiteSpace(effectiveUserAgent))
            {
                try { downloader.SetRequestHeader("User-Agent", effectiveUserAgent); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Downloads] Could not set media User-Agent: " + ex.Message);
                }
            }
            downloader.SuccessToastNotification = CreateCompletedToast(item);
            var operation = downloader.CreateDownload(new Uri(format.Url), file);
            item.OperationId = operation.Guid.ToString();

            lock (Items)
            {
                Items.RemoveAll(i => MakeKey(i.VideoId, i.Quality) == key);
                Items.Add(item);
            }
            await SaveAsync();
            RaiseChanged();

            var ignoredThumb = KeepImagesAsync(item, file);
            var ignoredRun = RunAsync(item, operation, false);
            return item;
        }

        private static void ConfigureAdaptiveDownloader(
            BackgroundDownloader downloader,
            BackgroundTransferGroup transferGroup,
            string userAgent)
        {
            downloader.TransferGroup = transferGroup;
            downloader.CostPolicy = BackgroundTransferCostPolicy.Always;
            if (!string.IsNullOrWhiteSpace(userAgent))
            {
                try { downloader.SetRequestHeader("User-Agent", userAgent); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Downloads] Could not set adaptive media User-Agent: " + ex.Message);
                }
            }
        }

        private static async Task<BackgroundTransferCompletionGroup> TryCreateCompletionGroupAsync(
            DownloadedVideoItem item,
            string partPrefix)
        {
            try
            {
                var access = await BackgroundExecutionManager.RequestAccessAsync();
                var accessText = access.ToString();
                if (accessText.IndexOf("Denied", StringComparison.OrdinalIgnoreCase) >= 0
                    || accessText.IndexOf("Unspecified", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Downloads] Background completion access unavailable: " + accessText);
                    return null;
                }

                var completionGroup = new BackgroundTransferCompletionGroup();
                var taskName = CompletionTaskPrefix + partPrefix;
                var builder = new BackgroundTaskBuilder
                {
                    Name = taskName
                };
                // No TaskEntryPoint: App.OnBackgroundActivated handles this as an in-process
                // task. An out-of-process C# task must live in a Windows Runtime Component;
                // placing it in this executable makes backgroundTaskHost exit with code 1.
                builder.SetTrigger(completionGroup.Trigger);
                builder.Register();
                item.CompletionTaskName = taskName;
                return completionGroup;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Completion group registration failed: " + ex.Message);
                return null;
            }
        }

        private static async Task MonitorAdaptiveOperationsAsync(
            DownloadedVideoItem item,
            DownloadOperation videoOperation,
            DownloadOperation audioOperation,
            BackgroundTransferCompletionGroup completionGroup,
            bool attach)
        {
            if (item == null || videoOperation == null || audioOperation == null) return;
            var key = MakeKey(item.VideoId, item.Quality);
            var cts = new CancellationTokenSource();
            lock (Active) Active[key] = cts;

            try
            {
                var downloadProgress = new AdaptiveDownloadProgress(item);
                var videoProgress = new TransferProgress(value => downloadProgress.Report(true, value));
                var audioProgress = new TransferProgress(value => downloadProgress.Report(false, value));
                Task<DownloadOperation> videoTask;
                Task<DownloadOperation> audioTask;
                if (attach)
                {
                    videoTask = videoOperation.AttachAsync().AsTask(cts.Token, videoProgress);
                    audioTask = audioOperation.AttachAsync().AsTask(cts.Token, audioProgress);
                }
                else
                {
                    videoTask = videoOperation.StartAsync().AsTask(cts.Token, videoProgress);
                    audioTask = audioOperation.StartAsync().AsTask(cts.Token, audioProgress);
                    if (completionGroup != null) completionGroup.Enable();
                }

                await Task.WhenAll(videoTask, audioTask);
                if (videoOperation.Progress.Status != BackgroundTransferStatus.Completed
                    || audioOperation.Progress.Status != BackgroundTransferStatus.Completed)
                    throw new InvalidOperationException("Adaptive media transfer did not complete");

                var lastSaved = downloadProgress.LastSaved;
                ReportAdaptiveProgress(item, 90.0, ref lastSaved);
                // If the foreground is still alive, finish immediately. The completion trigger
                // remains the fallback for suspension/termination; finalization is idempotent.
                await FinalizeAdaptiveItemAsync(item, true);
            }
            catch (Exception ex)
            {
                if (!(ex is OperationCanceledException))
                    System.Diagnostics.Debug.WriteLine(
                        "[Downloads] Adaptive transfer monitor failed: " + ex.Message);
            }
            finally
            {
                lock (Active)
                {
                    CancellationTokenSource current;
                    if (Active.TryGetValue(key, out current) && ReferenceEquals(current, cts))
                        Active.Remove(key);
                }
                RaiseChanged();
            }
        }

        private static void ReportAdaptiveProgress(
            DownloadedVideoItem item,
            double percent,
            ref DateTime lastSaved)
        {
            percent = Math.Max(0.0, Math.Min(100.0, percent));
            item.BytesReceived = (ulong)Math.Round(percent * 1000.0);
            item.TotalBytes = 100000;
            item.IsDownloading = true;
            RaiseChanged();
            if ((DateTime.UtcNow - lastSaved).TotalMilliseconds >= 750)
            {
                lastSaved = DateTime.UtcNow;
                var ignoredSave = SaveAsync();
            }
        }

        public static async Task ProcessCompletionGroupAsync(
            BackgroundTransferCompletionGroupTriggerDetails details)
        {
            if (details == null || details.Downloads == null) return;
            await EnsureLoadedAsync(false);

            var operationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = true;
            foreach (var operation in details.Downloads)
            {
                if (operation == null) continue;
                operationIds.Add(operation.Guid.ToString());
                if (operation.Progress.Status != BackgroundTransferStatus.Completed)
                    completed = false;
            }

            DownloadedVideoItem item;
            lock (Items)
            {
                item = Items.FirstOrDefault(i => i != null && i.IsAdaptive
                    && (operationIds.Contains(i.VideoOperationId)
                        || operationIds.Contains(i.AudioOperationId)));
            }
            if (item == null) return;

            if (!completed)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Completion group reported a failed transfer for " + item.VideoId);
                await CancelAsync(item);
                return;
            }

            await FinalizeAdaptiveItemAsync(item, true);
        }

        public static bool IsCompletionTaskName(string taskName)
        {
            return !string.IsNullOrWhiteSpace(taskName)
                && taskName.StartsWith(CompletionTaskPrefix, StringComparison.Ordinal);
        }

        private static async Task FinalizeAdaptiveItemAsync(
            DownloadedVideoItem item,
            bool showToast)
        {
            if (item == null || item.IsComplete || !item.IsAdaptive) return;
            await FinalizeGate.WaitAsync();
            StorageFile muxLock = null;
            var key = MakeKey(item.VideoId, item.Quality);
            var cts = new CancellationTokenSource();
            try
            {
                // A second completion path may have been waiting on FinalizeGate while the
                // first one finished and removed the part files.
                if (item.IsComplete) return;
                if (IsCancellationRequested(item))
                {
                    await DeleteItemAsync(item);
                    return;
                }

                muxLock = await TryAcquireMuxLockAsync(item);
                if (muxLock == null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Downloads] Another process is already muxing " + item.VideoId);
                    return;
                }

                var output = await GetFileAsync(item);
                var parts = await ApplicationData.Current.LocalFolder.GetFolderAsync(PartsFolderName);
                var videoPart = await parts.GetFileAsync(item.VideoPartName);
                var audioPart = await parts.GetFileAsync(item.AudioPartName);
                if (output == null || videoPart == null || audioPart == null)
                    throw new InvalidOperationException("Adaptive download files are missing");

                item.IsDownloading = true;
                item.IsFinalizing = true;
                item.BytesReceived = 90000;
                item.TotalBytes = 100000;
                await SaveAsync();
                RaiseChanged();

                lock (Active) Active[key] = cts;
                var lastSaved = DateTime.UtcNow;
                var progress = new TranscodeProgress(value =>
                {
                    if (IsCancellationRequested(item)) cts.Cancel();
                    ReportAdaptiveProgress(item,
                        90.0 + Math.Max(0.0, Math.Min(100.0, value)) * 0.10,
                        ref lastSaved);
                });

                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Both background parts completed; direct-muxing "
                    + item.Width + "x" + item.Height + " MP4");
                var muxed = await Mp4DownloadMuxer.WriteAsync(
                    output, videoPart, audioPart, cts.Token, progress);
                if (!muxed) throw new InvalidOperationException("Direct MP4 mux failed");

                item.BytesReceived = 100000;
                item.TotalBytes = 100000;
                item.IsDownloading = false;
                item.IsFinalizing = false;
                item.IsComplete = true;
                item.VideoOperationId = string.Empty;
                item.AudioOperationId = string.Empty;
                await DeletePartFilesAsync(item);
                UnregisterCompletionTask(item.CompletionTaskName);
                item.CompletionTaskName = string.Empty;
                ClearCancellationMarker(item);
                await SaveAsync();
                RaiseChanged();
                if (showToast) ShowCompletedToast(item);
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Adaptive MP4 completed in background: " + output.Path);
            }
            catch (OperationCanceledException)
            {
                await DeleteItemAsync(item);
            }
            catch (Exception ex)
            {
                item.IsFinalizing = false;
                item.IsDownloading = true;
                item.BytesReceived = 90000;
                item.TotalBytes = 100000;
                await SaveAsync();
                RaiseChanged();
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Background MP4 mux failed and will be retried: " + ex.Message);
            }
            finally
            {
                lock (Active)
                {
                    CancellationTokenSource current;
                    if (Active.TryGetValue(key, out current) && ReferenceEquals(current, cts))
                        Active.Remove(key);
                }
                if (muxLock != null)
                {
                    try { await muxLock.DeleteAsync(StorageDeleteOption.PermanentDelete); }
                    catch { }
                }
                FinalizeGate.Release();
            }
        }

        public static async Task CancelAsync(DownloadedVideoItem item)
        {
            if (item == null) return;
            await EnsureLoadedAsync();
            MarkCancellationRequested(item);

            var key = MakeKey(item.VideoId, item.Quality);
            CancellationTokenSource active;
            lock (Active) Active.TryGetValue(key, out active);
            if (active != null)
            {
                try { active.Cancel(); }
                catch { }
            }

            try
            {
                var operations = await GetAllDownloadOperationsAsync();
                foreach (var operation in operations)
                {
                    if (operation == null || !OperationBelongsTo(item, operation.Guid.ToString()))
                        continue;
                    try
                    {
                        var cancel = operation.AttachAsync();
                        cancel.Cancel();
                        try { await cancel; }
                        catch { }
                    }
                    catch { }
                }
            }
            catch { }

            for (var attempt = 0; attempt < 30; attempt++)
            {
                bool released;
                lock (Active) released = !Active.ContainsKey(key);
                if (released) break;
                await Task.Delay(50);
            }
            await DeleteItemAsync(item);
        }

        private static bool OperationBelongsTo(DownloadedVideoItem item, string operationId)
        {
            return item != null && !string.IsNullOrWhiteSpace(operationId)
                && (string.Equals(item.OperationId, operationId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.VideoOperationId, operationId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.AudioOperationId, operationId, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<IList<DownloadOperation>> GetAllDownloadOperationsAsync()
        {
            var result = new List<DownloadOperation>();
            try
            {
                var ungrouped = await BackgroundDownloader.GetCurrentDownloadsAsync();
                if (ungrouped != null) result.AddRange(ungrouped);
            }
            catch { }
            try
            {
                var group = BackgroundTransferGroup.CreateGroup("YouTubeAdaptiveDownloads");
                group.TransferBehavior = BackgroundTransferBehavior.Parallel;
                var grouped = await BackgroundDownloader
                    .GetCurrentDownloadsForTransferGroupAsync(group);
                if (grouped != null) result.AddRange(grouped);
            }
            catch { }
            return result;
        }

        private static async Task DeleteItemAsync(DownloadedVideoItem item)
        {
            if (item == null) return;
            UnregisterCompletionTask(item.CompletionTaskName);
            await DeletePartFilesAsync(item);
            await DeleteThumbnailBesideVideoAsync(item);
            await DeleteAccessListFileAsync(item.ThumbnailFileToken);
            await DeleteAccessListFileAsync(item.FileToken);
            await DeleteLegacyImageAsync(item.LocalThumbnailUri);
            await DeleteLegacyImageAsync(item.LocalChannelThumbnailUri);
            lock (Items) Items.RemoveAll(i => i != null
                && MakeKey(i.VideoId, i.Quality) == MakeKey(item.VideoId, item.Quality));
            await SaveAsync();
            RaiseChanged();
        }

        private static async Task DeletePartFilesAsync(DownloadedVideoItem item)
        {
            if (item == null) return;
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync(PartsFolderName);
                var names = new[] { item.VideoPartName, item.AudioPartName };
                for (var i = 0; i < names.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(names[i])) continue;
                    try
                    {
                        var file = await folder.GetFileAsync(names[i]);
                        await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private static async Task DeleteLegacyImageAsync(string uri)
        {
            const string prefix = "ms-appdata:///local/";
            if (string.IsNullOrWhiteSpace(uri)
                || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return;
            try
            {
                var relative = Uri.UnescapeDataString(uri.Substring(prefix.Length))
                    .Replace('/', System.IO.Path.DirectorySeparatorChar);
                var path = System.IO.Path.Combine(ApplicationData.Current.LocalFolder.Path, relative);
                var file = await StorageFile.GetFileFromPathAsync(path);
                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch { }
        }

        private static async Task<StorageFile> TryAcquireMuxLockAsync(DownloadedVideoItem item)
        {
            var lockName = SanitizeFileName(item.VideoId + "_" + item.Quality + ".muxlock");
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    PartsFolderName, CreationCollisionOption.OpenIfExists);
                try
                {
                    return await folder.CreateFileAsync(
                        lockName, CreationCollisionOption.FailIfExists);
                }
                catch
                {
                    // A killed background process may leave this marker behind. Recover it
                    // after a short safety window instead of pinning the item at 90% forever.
                    try
                    {
                        var existing = await folder.GetFileAsync(lockName);
                        var properties = await existing.GetBasicPropertiesAsync();
                        if ((DateTimeOffset.Now - properties.DateModified).TotalSeconds >= 20)
                        {
                            await existing.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            return await folder.CreateFileAsync(
                                lockName, CreationCollisionOption.FailIfExists);
                        }
                    }
                    catch { }
                    return null;
                }
            }
            catch { return null; }
        }

        private static string CancellationSettingKey(DownloadedVideoItem item)
        {
            return "DownloadCancel_" + SanitizeFileName(MakeKey(
                item == null ? string.Empty : item.VideoId,
                item == null ? string.Empty : item.Quality));
        }

        private static void MarkCancellationRequested(DownloadedVideoItem item)
        {
            try { ApplicationData.Current.LocalSettings.Values[CancellationSettingKey(item)] = true; }
            catch { }
        }

        private static bool IsCancellationRequested(DownloadedVideoItem item)
        {
            try
            {
                object value;
                return ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    CancellationSettingKey(item), out value) && value is bool && (bool)value;
            }
            catch { return false; }
        }

        private static void ClearCancellationMarker(DownloadedVideoItem item)
        {
            try { ApplicationData.Current.LocalSettings.Values.Remove(CancellationSettingKey(item)); }
            catch { }
        }

        private static void UnregisterCompletionTask(string taskName)
        {
            if (string.IsNullOrWhiteSpace(taskName)) return;
            try
            {
                foreach (var task in BackgroundTaskRegistration.AllTasks)
                {
                    if (task.Value != null && string.Equals(
                        task.Value.Name, taskName, StringComparison.Ordinal))
                    {
                        task.Value.Unregister(false);
                    }
                }
            }
            catch { }
        }

        private static ToastNotification CreateCompletedToast(DownloadedVideoItem item)
        {
            var title = XmlEscape(Localization.GetString("Downloaded"));
            var videoTitle = XmlEscape(item == null ? string.Empty : item.Title);
            var launch = XmlEscape("openVideo=" + (item == null ? string.Empty : item.VideoId));
            var xml = "<toast launch=\"" + launch + "\" activationType=\"foreground\">"
                + "<visual><binding template=\"ToastGeneric\">"
                + "<text>" + title + "</text><text>" + videoTitle + "</text>"
                + "</binding></visual></toast>";
            var document = new XmlDocument();
            document.LoadXml(xml);
            var toast = new ToastNotification(document);
            if (item != null && !string.IsNullOrWhiteSpace(item.VideoId))
                toast.Tag = item.VideoId.Length > 16 ? item.VideoId.Substring(0, 16) : item.VideoId;
            toast.Group = "downloads";
            toast.ExpirationTime = DateTimeOffset.Now.AddDays(1);
            return toast;
        }

        private static void ShowCompletedToast(DownloadedVideoItem item)
        {
            try { ToastNotificationManager.CreateToastNotifier().Show(CreateCompletedToast(item)); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Completion toast failed: " + ex.Message);
            }
        }

        private static string XmlEscape(string value)
        {
            return (value ?? string.Empty).Replace("&", "&amp;")
                .Replace("<", "&lt;").Replace(">", "&gt;")
                .Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static string FirstNonEmpty(string first, string second)
        {
            return !string.IsNullOrWhiteSpace(first) ? first : (second ?? string.Empty);
        }

        public static async Task<StorageFile> GetFileAsync(DownloadedVideoItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.FileToken)) return null;
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(item.FileToken))
                    return await StorageApplicationPermissions.FutureAccessList.GetFileAsync(item.FileToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Cannot open file: " + ex.Message);
            }
            return null;
        }

        public static async Task<StorageFile> GetThumbnailFileAsync(DownloadedVideoItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.ThumbnailFileToken)) return null;
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(item.ThumbnailFileToken))
                    return await StorageApplicationPermissions.FutureAccessList
                        .GetFileAsync(item.ThumbnailFileToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Cannot open thumbnail: " + ex.Message);
            }
            return null;
        }

        public static async Task EnsureThumbnailBesideVideoAsync(DownloadedVideoItem item)
        {
            if (item == null || !item.IsComplete) return;
            if (await GetThumbnailFileAsync(item) != null) return;

            var videoFile = await GetFileAsync(item);
            if (videoFile == null) return;
            if (await KeepVideoThumbnailAsync(item, videoFile))
            {
                await SaveAsync();
                RaiseChanged();
            }
        }

        private static async Task<StorageFile> CreateDestinationFileAsync(string fileName)
        {
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(FolderAccessToken))
                {
                    var selectedFolder = await StorageApplicationPermissions.FutureAccessList
                        .GetFolderAsync(FolderAccessToken);
                    return await selectedFolder.CreateFileAsync(
                        fileName, CreationCollisionOption.GenerateUniqueName);
                }
            }
            catch
            {
                try { StorageApplicationPermissions.FutureAccessList.Remove(FolderAccessToken); } catch { }
                ApplicationData.Current.LocalSettings.Values.Remove(FolderNameSettingKey);
            }

            try
            {
                return await Windows.Storage.DownloadsFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.GenerateUniqueName);
            }
            catch
            {
                var fallback = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "Downloads", CreationCollisionOption.OpenIfExists);
                return await fallback.CreateFileAsync(
                    fileName, CreationCollisionOption.GenerateUniqueName);
            }
        }

        public static void SetDestinationFolder(StorageFolder folder)
        {
            if (folder == null) return;
            StorageApplicationPermissions.FutureAccessList.AddOrReplace(FolderAccessToken, folder);
            ApplicationData.Current.LocalSettings.Values[FolderNameSettingKey] = folder.DisplayName;
        }

        public static string GetDestinationDisplayName()
        {
            object raw;
            if (ApplicationData.Current.LocalSettings.Values.TryGetValue(FolderNameSettingKey, out raw)
                && raw != null && !string.IsNullOrWhiteSpace(raw.ToString()))
                return raw.ToString();
            return Localization.GetString("DownloadsFolderDefault");
        }

        public static async Task InitializeAsync()
        {
            await EnsureLoadedAsync();
        }

        private static async Task MergeExternalCompletionAsync(bool force = false)
        {
            if (!force && (DateTime.UtcNow - _lastExternalMergeUtc).TotalMilliseconds < 900)
                return;
            _lastExternalMergeUtc = DateTime.UtcNow;
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(IndexFileName);
                var text = await FileIO.ReadTextAsync(file);
                JsonArray rows;
                if (!JsonArray.TryParse(text, out rows)) return;
                var diskItems = new List<DownloadedVideoItem>();
                foreach (var row in rows)
                {
                    var diskItem = FromJson(row.GetObject());
                    if (diskItem != null) diskItems.Add(diskItem);
                }

                lock (Items)
                {
                    foreach (var local in Items)
                    {
                        if (local == null) continue;
                        var disk = diskItems.FirstOrDefault(i => i != null
                            && MakeKey(i.VideoId, i.Quality) == MakeKey(local.VideoId, local.Quality));
                        if (disk == null) continue;
                        if (!disk.IsComplete && !disk.IsFinalizing
                            && disk.ProgressPercent < local.ProgressPercent) continue;

                        local.BytesReceived = disk.BytesReceived;
                        local.TotalBytes = disk.TotalBytes;
                        local.IsComplete = disk.IsComplete;
                        local.IsDownloading = disk.IsDownloading;
                        local.IsFinalizing = disk.IsFinalizing;
                        local.ThumbnailFileToken = disk.ThumbnailFileToken;
                        local.LocalThumbnailUri = disk.LocalThumbnailUri;
                        local.LocalChannelThumbnailUri = disk.LocalChannelThumbnailUri;
                        local.VideoOperationId = disk.VideoOperationId;
                        local.AudioOperationId = disk.AudioOperationId;
                        local.CompletionTaskName = disk.CompletionTaskName;
                    }
                }
            }
            catch { }
        }

        private static async Task EnsureLoadedAsync(bool reattach = true)
        {
            if (_loaded) return;
            await Gate.WaitAsync();
            try
            {
                if (_loaded) return;
                try
                {
                    var file = await ApplicationData.Current.LocalFolder.GetFileAsync(IndexFileName);
                    var text = await FileIO.ReadTextAsync(file);
                    JsonArray rows;
                    if (JsonArray.TryParse(text, out rows))
                    {
                        foreach (var row in rows)
                        {
                            var item = FromJson(row.GetObject());
                            if (item != null) Items.Add(item);
                        }
                    }
                }
                catch { }
                lock (Items)
                {
                    // Remove only rows created by the old in-process implementation. New
                    // adaptive rows persist both operation ids and part names and are recoverable.
                    Items.RemoveAll(i => i != null && !i.IsComplete
                        && string.IsNullOrWhiteSpace(i.OperationId)
                        && string.IsNullOrWhiteSpace(i.VideoOperationId)
                        && string.IsNullOrWhiteSpace(i.AudioOperationId)
                        && string.IsNullOrWhiteSpace(i.VideoPartName)
                        && string.IsNullOrWhiteSpace(i.AudioPartName));
                }
                _loaded = true;
            }
            finally
            {
                Gate.Release();
            }

            if (reattach)
            {
                var ignored = ReattachAsync();
            }
        }

        private static async Task ReattachAsync()
        {
            try
            {
                var operations = await GetAllDownloadOperationsAsync();
                List<DownloadedVideoItem> snapshot;
                lock (Items) snapshot = Items.Where(i => i != null && !i.IsComplete).ToList();
                foreach (var item in snapshot)
                {
                    if (item.IsAdaptive)
                    {
                        var videoOperation = operations.FirstOrDefault(o => o != null
                            && string.Equals(o.Guid.ToString(), item.VideoOperationId,
                                StringComparison.OrdinalIgnoreCase));
                        var audioOperation = operations.FirstOrDefault(o => o != null
                            && string.Equals(o.Guid.ToString(), item.AudioOperationId,
                                StringComparison.OrdinalIgnoreCase));
                        if (videoOperation != null && audioOperation != null)
                        {
                            item.IsDownloading = true;
                            var ignoredAdaptive = MonitorAdaptiveOperationsAsync(
                                item, videoOperation, audioOperation, null, true);
                        }
                        else
                        {
                            // If no operations remain, the completion task may already be muxing.
                            // The lock makes this safe if foreground recovery races it.
                            var ignoredFinalize = RecoverAdaptiveFinalizationAsync(item);
                        }
                    }
                    else
                    {
                        var operation = operations.FirstOrDefault(o => o != null
                            && string.Equals(o.Guid.ToString(), item.OperationId,
                                StringComparison.OrdinalIgnoreCase));
                        if (operation != null)
                        {
                            item.IsDownloading = true;
                            var ignored = RunAsync(item, operation, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Resume scan failed: " + ex.Message);
            }
        }

        private static async Task RecoverAdaptiveFinalizationAsync(DownloadedVideoItem item)
        {
            try
            {
                for (var attempt = 0; attempt < 16; attempt++)
                {
                    await Task.Delay(attempt == 0 ? 1200 : 2000);
                    await MergeExternalCompletionAsync(true);
                    if (item == null || item.IsComplete || IsCancellationRequested(item))
                        return;
                    await FinalizeAdaptiveItemAsync(item, true);
                    if (item.IsComplete) return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Adaptive recovery failed: " + ex.Message);
            }
        }

        private static async Task RunAsync(
            DownloadedVideoItem item,
            DownloadOperation operation,
            bool attach)
        {
            var key = MakeKey(item.VideoId, item.Quality);
            var cts = new CancellationTokenSource();
            lock (Active) Active[key] = cts;
            var lastSaved = DateTime.UtcNow;
            try
            {
                var progress = new TransferProgress(value =>
                {
                    var state = value.Progress;
                    item.BytesReceived = state.BytesReceived;
                    item.TotalBytes = state.TotalBytesToReceive;
                    item.IsDownloading = true;
                    RaiseChanged();
                    if ((DateTime.UtcNow - lastSaved).TotalMilliseconds >= 750)
                    {
                        lastSaved = DateTime.UtcNow;
                        var ignoredSave = SaveAsync();
                    }
                });

                if (attach)
                    await operation.AttachAsync().AsTask(cts.Token, progress);
                else
                    await operation.StartAsync().AsTask(cts.Token, progress);

                item.BytesReceived = operation.Progress.BytesReceived;
                item.TotalBytes = operation.Progress.TotalBytesToReceive;
                item.IsDownloading = false;
                item.IsComplete = operation.Progress.Status == BackgroundTransferStatus.Completed;
            }
            catch (Exception ex)
            {
                item.IsDownloading = false;
                System.Diagnostics.Debug.WriteLine("[Downloads] Transfer failed: " + ex.Message);
            }
            finally
            {
                lock (Active) Active.Remove(key);
                await SaveAsync();
                RaiseChanged();
            }
        }

        private static async Task KeepImagesAsync(DownloadedVideoItem item, StorageFile videoFile)
        {
            var generation = _clearGeneration;
            await KeepVideoThumbnailAsync(item, videoFile);
            if (generation != _clearGeneration) return;

            await LegacyImageGate.WaitAsync();
            try
            {
                if (generation != _clearGeneration) return;
                item.LocalChannelThumbnailUri = await KeepImageAsync(
                    item.ChannelThumbnailUrl, item.VideoId + "_channel.jpg");
                if (generation != _clearGeneration) return;
                await SaveAsync();
                RaiseChanged();
            }
            finally
            {
                LegacyImageGate.Release();
            }
        }

        private static async Task<bool> KeepVideoThumbnailAsync(
            DownloadedVideoItem item,
            StorageFile videoFile)
        {
            if (item == null || videoFile == null || string.IsNullOrWhiteSpace(item.VideoId))
                return false;

            StorageFile thumbnailFile = null;
            var generation = _clearGeneration;
            await ThumbnailGate.WaitAsync();
            try
            {
                if (generation != _clearGeneration) return false;
                if (await GetThumbnailFileAsync(item) != null)
                    return true;

                var legacyBytes = await ReadLegacyThumbnailAsync(item.LocalThumbnailUri);
                var bytes = legacyBytes;
                var needsWideCrop = legacyBytes != null && legacyBytes.Length > 0;
                if (bytes == null || bytes.Length == 0)
                {
                    var urls = VideoThumbnailController.GetCandidateUrls(
                        item.VideoId, item.ThumbnailUrl);
                    for (var i = 0; i < urls.Count; i++)
                    {
                        try
                        {
                            bytes = await Http.GetByteArrayAsync(new Uri(urls[i]));
                            if (bytes != null && bytes.Length > 0)
                            {
                                needsWideCrop = ThumbnailImageLoader
                                    .RequiresPhysicalWideCrop(urls[i]);
                                break;
                            }
                        }
                        catch
                        {
                        }
                    }
                }

                if (bytes == null || bytes.Length == 0)
                    return false;

                thumbnailFile = await CreateThumbnailBesideVideoAsync(
                    videoFile, SanitizeFileName(item.VideoId + ".jpg"));
                if (thumbnailFile == null)
                    return false;

                if (needsWideCrop)
                    await WriteWideThumbnailAsync(thumbnailFile, bytes);
                else
                    await FileIO.WriteBytesAsync(thumbnailFile, bytes);

                var token = "YouTubeDownloadThumb_" + item.VideoId;
                StorageApplicationPermissions.FutureAccessList.AddOrReplace(token, thumbnailFile);
                item.ThumbnailFileToken = token;
                item.LocalThumbnailUri = string.Empty;
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Thumbnail saved beside MP4: " + thumbnailFile.Path);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] Could not save thumbnail beside MP4: " + ex.Message);
                return false;
            }
            finally
            {
                ThumbnailGate.Release();
            }
        }

        private static async Task<byte[]> ReadLegacyThumbnailAsync(string localUri)
        {
            const string prefix = "ms-appdata:///local/";
            if (string.IsNullOrWhiteSpace(localUri)
                || !localUri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var relative = Uri.UnescapeDataString(localUri.Substring(prefix.Length))
                    .Replace('/', System.IO.Path.DirectorySeparatorChar);
                var path = System.IO.Path.Combine(
                    ApplicationData.Current.LocalFolder.Path, relative);
                var file = await StorageFile.GetFileFromPathAsync(path);
                var buffer = await FileIO.ReadBufferAsync(file);
                return buffer.ToArray();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<StorageFile> CreateThumbnailBesideVideoAsync(
            StorageFile videoFile,
            string fileName)
        {
            try
            {
                var parent = await videoFile.GetParentAsync();
                if (parent != null)
                    return await CreateOrOpenFileAsync(parent, fileName);
            }
            catch
            {
            }

            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(FolderAccessToken))
                {
                    var selectedFolder = await StorageApplicationPermissions.FutureAccessList
                        .GetFolderAsync(FolderAccessToken);
                    return await CreateOrOpenFileAsync(selectedFolder, fileName);
                }
            }
            catch
            {
            }

            try
            {
                return await Windows.Storage.DownloadsFolder.CreateFileAsync(
                    fileName, CreationCollisionOption.FailIfExists);
            }
            catch
            {
                var token = "YouTubeDownloadThumb_"
                    + System.IO.Path.GetFileNameWithoutExtension(fileName);
                try
                {
                    if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                        return await StorageApplicationPermissions.FutureAccessList.GetFileAsync(token);
                }
                catch
                {
                }
            }

            try
            {
                var fallback = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    "Downloads", CreationCollisionOption.OpenIfExists);
                return await CreateOrOpenFileAsync(fallback, fileName);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<StorageFile> CreateOrOpenFileAsync(
            StorageFolder folder,
            string fileName)
        {
            if (folder == null) return null;
            try
            {
                return await folder.GetFileAsync(fileName);
            }
            catch
            {
                return await folder.CreateFileAsync(fileName, CreationCollisionOption.FailIfExists);
            }
        }

        private static async Task WriteWideThumbnailAsync(StorageFile file, byte[] bytes)
        {
            using (var input = new InMemoryRandomAccessStream())
            {
                await input.WriteAsync(bytes.AsBuffer());
                input.Seek(0);
                var decoder = await BitmapDecoder.CreateAsync(input);
                var width = decoder.PixelWidth;
                var height = decoder.PixelHeight;
                if (width == 0 || height == 0)
                    throw new InvalidOperationException("Thumbnail has invalid dimensions");

                var cropHeight = (uint)Math.Max(1.0, Math.Round(width * 9.0 / 16.0));
                if (cropHeight > height) cropHeight = height;
                var transform = new BitmapTransform
                {
                    Bounds = new BitmapBounds
                    {
                        X = 0,
                        Y = (height - cropHeight) / 2,
                        Width = width,
                        Height = cropHeight
                    }
                };
                var bitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    transform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);
                try
                {
                    using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
                    {
                        output.Size = 0;
                        var encoder = await BitmapEncoder.CreateAsync(
                            BitmapEncoder.JpegEncoderId, output);
                        encoder.SetSoftwareBitmap(bitmap);
                        await encoder.FlushAsync();
                    }
                }
                finally
                {
                    bitmap.Dispose();
                }
            }
        }

        private static async Task<string> KeepImageAsync(string url, string name)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;
            try
            {
                var bytes = await Http.GetByteArrayAsync(new Uri(url));
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(
                    ThumbnailFolderName, CreationCollisionOption.OpenIfExists);
                var file = await folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(file, bytes);
                return "ms-appdata:///local/" + ThumbnailFolderName + "/" + name;
            }
            catch { return string.Empty; }
        }

        private static async Task SaveAsync()
        {
            await Gate.WaitAsync();
            try
            {
                var rows = new JsonArray();
                lock (Items)
                {
                    foreach (var item in Items) rows.Add(ToJson(item));
                }
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    IndexFileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, rows.Stringify());
            }
            finally { Gate.Release(); }
        }

        public static async Task<int> ClearAllAsync()
        {
            await EnsureLoadedAsync();
            _clearing = true;
            System.Threading.Interlocked.Increment(ref _clearGeneration);

            try
            {
                List<DownloadedVideoItem> snapshot;
                lock (Items) snapshot = Items.Where(i => i != null).ToList();
                for (var i = 0; i < snapshot.Count; i++)
                    MarkCancellationRequested(snapshot[i]);

                CancellationTokenSource[] cancellations;
                lock (Active)
                    cancellations = Active.Values.ToArray();
                for (var i = 0; i < cancellations.Length; i++)
                {
                    try { cancellations[i].Cancel(); }
                    catch { }
                }

                try
                {
                    var operations = await GetAllDownloadOperationsAsync();
                    foreach (var operation in operations)
                    {
                        if (operation == null || !snapshot.Any(i => OperationBelongsTo(
                            i, operation.Guid.ToString()))) continue;
                        try
                        {
                            var cancel = operation.AttachAsync();
                            cancel.Cancel();
                        }
                        catch { }
                    }
                }
                catch { }

                // Let active BackgroundDownloader/direct-mux tasks release their files before
                // deleting them. The timeout keeps Settings responsive if WinRT never reports
                // cancellation on a broken connection.
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    bool finished;
                    lock (Active) finished = Active.Count == 0;
                    if (finished) break;
                    await Task.Delay(50);
                }

                await ThumbnailGate.WaitAsync();
                await LegacyImageGate.WaitAsync();
                try
                {
                    lock (Items)
                    {
                        Items.Clear();
                    }

                    for (var i = 0; i < snapshot.Count; i++)
                    {
                        UnregisterCompletionTask(snapshot[i].CompletionTaskName);
                        await DeleteThumbnailBesideVideoAsync(snapshot[i]);
                        await DeleteAccessListFileAsync(snapshot[i].ThumbnailFileToken);
                        await DeleteAccessListFileAsync(snapshot[i].FileToken);
                    }

                    await DeleteLocalFolderAsync(ThumbnailFolderName);
                    await DeleteLocalFolderAsync(PartsFolderName);
                    await DeleteLocalFolderAsync("Downloads");
                    try
                    {
                        var index = await ApplicationData.Current.LocalFolder
                            .GetFileAsync(IndexFileName);
                        await index.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                    catch
                    {
                    }

                    _loaded = true;
                    RaiseChanged();
                    return snapshot.Count;
                }
                finally
                {
                    LegacyImageGate.Release();
                    ThumbnailGate.Release();
                }
            }
            finally
            {
                _clearing = false;
            }
        }

        private static async Task DeleteAccessListFileAsync(string token)
        {
            if (string.IsNullOrWhiteSpace(token)) return;
            try
            {
                if (StorageApplicationPermissions.FutureAccessList.ContainsItem(token))
                {
                    var removeToken = false;
                    StorageFile file = null;
                    try
                    {
                        file = await StorageApplicationPermissions.FutureAccessList
                            .GetFileAsync(token);
                    }
                    catch
                    {
                        // The token is stale and no longer resolves to a file.
                        removeToken = true;
                    }
                    if (file != null)
                    {
                        for (var attempt = 0; attempt < 10; attempt++)
                        {
                            try
                            {
                                await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                                removeToken = true;
                                break;
                            }
                            catch
                            {
                                if (attempt == 9) throw;
                                await Task.Delay(75);
                            }
                        }
                    }
                    if (removeToken)
                        StorageApplicationPermissions.FutureAccessList.Remove(token);
                }
            }
            catch
            {
            }
        }

        private static async Task DeleteThumbnailBesideVideoAsync(DownloadedVideoItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId)
                || string.IsNullOrWhiteSpace(item.FileToken))
            {
                return;
            }

            try
            {
                if (!StorageApplicationPermissions.FutureAccessList.ContainsItem(item.FileToken))
                    return;
                var videoFile = await StorageApplicationPermissions.FutureAccessList
                    .GetFileAsync(item.FileToken);
                var parent = await videoFile.GetParentAsync();
                if (parent == null) return;
                var thumbnail = await parent.GetFileAsync(
                    SanitizeFileName(item.VideoId + ".jpg"));
                await thumbnail.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static async Task DeleteLocalFolderAsync(string name)
        {
            try
            {
                var folder = await ApplicationData.Current.LocalFolder.GetFolderAsync(name);
                await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
            }
            catch
            {
            }
        }

        private static JsonObject ToJson(DownloadedVideoItem i)
        {
            var o = new JsonObject();
            Put(o, "videoId", i.VideoId); Put(o, "title", i.Title); Put(o, "author", i.Author);
            Put(o, "channelId", i.ChannelId); Put(o, "description", i.Description);
            Put(o, "thumbnailUrl", i.ThumbnailUrl); Put(o, "channelThumbnailUrl", i.ChannelThumbnailUrl);
            Put(o, "localThumbnailUri", i.LocalThumbnailUri); Put(o, "localChannelThumbnailUri", i.LocalChannelThumbnailUri);
            Put(o, "thumbnailFileToken", i.ThumbnailFileToken);
            Put(o, "fileToken", i.FileToken); Put(o, "operationId", i.OperationId); Put(o, "quality", i.Quality);
            Put(o, "videoOperationId", i.VideoOperationId); Put(o, "audioOperationId", i.AudioOperationId);
            Put(o, "videoPartName", i.VideoPartName); Put(o, "audioPartName", i.AudioPartName);
            Put(o, "completionTaskName", i.CompletionTaskName); Put(o, "transferGroupName", i.TransferGroupName);
            o["width"] = JsonValue.CreateNumberValue(i.Width); o["height"] = JsonValue.CreateNumberValue(i.Height);
            o["bytesReceived"] = JsonValue.CreateStringValue(i.BytesReceived.ToString());
            o["totalBytes"] = JsonValue.CreateStringValue(i.TotalBytes.ToString());
            o["complete"] = JsonValue.CreateBooleanValue(i.IsComplete);
            o["downloading"] = JsonValue.CreateBooleanValue(i.IsDownloading);
            o["adaptive"] = JsonValue.CreateBooleanValue(i.IsAdaptive);
            o["finalizing"] = JsonValue.CreateBooleanValue(i.IsFinalizing);
            o["addedTicks"] = JsonValue.CreateStringValue(i.AddedTicks.ToString());
            return o;
        }

        private static DownloadedVideoItem FromJson(JsonObject o)
        {
            if (o == null) return null;
            ulong received, total; long ticks;
            ulong.TryParse(Get(o, "bytesReceived"), out received);
            ulong.TryParse(Get(o, "totalBytes"), out total);
            long.TryParse(Get(o, "addedTicks"), out ticks);
            return new DownloadedVideoItem
            {
                VideoId = Get(o, "videoId"), Title = Get(o, "title"), Author = Get(o, "author"),
                ChannelId = Get(o, "channelId"), Description = Get(o, "description"),
                ThumbnailUrl = Get(o, "thumbnailUrl"), ChannelThumbnailUrl = Get(o, "channelThumbnailUrl"),
                LocalThumbnailUri = Get(o, "localThumbnailUri"), LocalChannelThumbnailUri = Get(o, "localChannelThumbnailUri"),
                ThumbnailFileToken = Get(o, "thumbnailFileToken"),
                FileToken = Get(o, "fileToken"), OperationId = Get(o, "operationId"), Quality = Get(o, "quality"),
                VideoOperationId = Get(o, "videoOperationId"), AudioOperationId = Get(o, "audioOperationId"),
                VideoPartName = Get(o, "videoPartName"), AudioPartName = Get(o, "audioPartName"),
                CompletionTaskName = Get(o, "completionTaskName"), TransferGroupName = Get(o, "transferGroupName"),
                Width = (int)o.GetNamedNumber("width", 0), Height = (int)o.GetNamedNumber("height", 0),
                BytesReceived = received, TotalBytes = total,
                IsComplete = o.GetNamedBoolean("complete", false),
                IsDownloading = o.GetNamedBoolean("downloading", false),
                IsAdaptive = o.GetNamedBoolean("adaptive", false),
                IsFinalizing = o.GetNamedBoolean("finalizing", false), AddedTicks = ticks
            };
        }

        private static void Put(JsonObject o, string key, string value)
        {
            o[key] = JsonValue.CreateStringValue(value ?? string.Empty);
        }

        private static string Get(JsonObject o, string key)
        {
            return o.ContainsKey(key) ? o.GetNamedString(key, string.Empty) : string.Empty;
        }

        private static string MakeKey(string id, string quality)
        {
            return (id ?? string.Empty) + "|" + (quality ?? string.Empty);
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler(null, EventArgs.Empty);
        }
    }

    public sealed class OfflineVideoNavigationArgs
    {
        public DownloadedVideoItem Item { get; set; }
    }
}
