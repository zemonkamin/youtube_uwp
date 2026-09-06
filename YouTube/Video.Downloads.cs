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
        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount || _offlineMode || string.IsNullOrWhiteSpace(currentVideoId)) return;

            if (MainSettingsPanel != null) MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null) QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null) SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null) SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null) AudioTrackSettingsPanel.Visibility = Visibility.Collapsed;
            if (DownloadSettingsPanel != null) DownloadSettingsPanel.Visibility = Visibility.Visible;

            var populateTask = PopulateDownloadQualityOptionsAsync();
            UpdateSettingsBottomSheetHeight();

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                ShowSettingsBottomSheetInFullscreenPopup();
            }
            else
            {
                RestoreSettingsBottomSheetFromFullscreenPopup();
                if (OverlayGrid != null) OverlayGrid.Visibility = Visibility.Visible;
                if (SettingsBottomSheetPanel != null)
                {
                    SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                    AnimateSettingsBottomSheet(true);
                }
            }

            await populateTask;
            UpdateSettingsBottomSheetHeight();
        }

        private async Task PopulateDownloadQualityOptionsAsync()
        {
            if (DownloadQualityOptionsPanel == null) return;
            DownloadQualityTitleText.Text = Localization.GetString("DownloadQualityTitle");
            _downloadOptionRows.Clear();
            DownloadQualityOptionsPanel.Children.Clear();
            DownloadUnavailableText.Visibility = Visibility.Collapsed;
            if (DownloadQualityLoadingRing != null)
            {
                DownloadQualityLoadingRing.Visibility = Visibility.Visible;
                DownloadQualityLoadingRing.IsActive = true;
            }

            var formats = await GetDownloadFormatChoicesAsync(currentVideoId);
            if (DownloadQualityLoadingRing != null)
            {
                DownloadQualityLoadingRing.IsActive = false;
                DownloadQualityLoadingRing.Visibility = Visibility.Collapsed;
            }
            if (formats.Count == 0)
            {
                DownloadUnavailableText.Visibility = Visibility.Visible;
                return;
            }

            foreach (var format in formats)
            {
                var selected = format;
                var quality = selected.Video.QualityTier + "p";
                var videoId = currentVideoId;
                var existing = await DownloadManager.FindAsync(currentVideoId, quality);
                var label = quality;
                var isActive = existing != null && (existing.IsDownloading || existing.IsFinalizing);
                if (isActive)
                    label += "  ·  " + existing.ProgressPercent + "%";
                else if (existing != null && existing.IsComplete)
                    label += "  ·  " + Localization.GetString("Downloaded");

                var row = MakeDownloadOptionRow(
                    quality,
                    label,
                    existing != null && existing.IsComplete,
                    isActive,
                    async () => await SelectDownloadFormatAsync(selected),
                    async cancelButton =>
                    {
                        cancelButton.IsEnabled = false;
                        try
                        {
                            var current = await DownloadManager.FindAsync(videoId, quality);
                            if (current != null)
                                await DownloadManager.CancelAsync(current);
                            await UpdateDownloadButtonAsync();
                            if (string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                            {
                                await PopulateDownloadQualityOptionsAsync();
                                UpdateSettingsBottomSheetHeight();
                            }
                        }
                        catch (Exception ex)
                        {
                            cancelButton.IsEnabled = true;
                            System.Diagnostics.Debug.WriteLine(
                                "[Downloads] Cancel failed: " + ex.Message);
                        }
                    });
                DownloadQualityOptionsPanel.Children.Add(row);
            }
        }

        private sealed class DownloadFormatChoice
        {
            public PlayerFormatModel Video { get; set; }
            public PlayerFormatModel Audio { get; set; }
            public List<PlayerFormatModel> AudioOptions { get; set; }
            public string MediaUserAgent { get; set; }
        }

        private sealed class DownloadOptionRowState
        {
            public string Quality { get; set; }
            public Button OptionButton { get; set; }
            public TextBlock Label { get; set; }
            public FontIcon Check { get; set; }
            public Button CancelButton { get; set; }
        }

        private async Task<List<DownloadFormatChoice>> GetDownloadFormatChoicesAsync(string videoId)
        {
            var all = new List<PlayerFormatModel>();
            var mediaUserAgent = _playbackMediaUserAgent;
            try
            {
                // Use formats and User-Agent from the same player response. availableFormats is
                // populated from the primary IOS response while _playbackMediaUserAgent can later
                // become VISIONOS/ANDROID_VR; pairing those two produced immediate googlevideo 403.
                // PostAndroidPlayerAsync is already an in-flight/result cache, so this is normally
                // no network request and only reuses the correctly attributed response.
                var json = await PostAndroidPlayerAsync(videoId).ConfigureAwait(false);
                mediaUserAgent = _playbackMediaUserAgent;
                AddDownloadFormatsFromPlayerJson(json, mediaUserAgent, all);

                // If the cached playback response has expired or carried only HLS, refresh the
                // normal player chain once. Do not fall back to a URL merely because it appears
                // in streamingData: both tracks must be usable by the existing demuxer.
                if (!ContainsDownloadableFormatSet(all))
                {
                    _lastAndroidPlayerVideoId = string.Empty;
                    _lastAndroidPlayerJson = string.Empty;
                    var refreshedJson = await PostAndroidPlayerAsync(videoId).ConfigureAwait(false);
                    mediaUserAgent = _playbackMediaUserAgent;
                    all.Clear();
                    AddDownloadFormatsFromPlayerJson(refreshedJson, mediaUserAgent, all);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Format request failed: " + ex.Message);
            }

            if (!ContainsDownloadableFormatSet(all))
            {
                foreach (var format in availableFormats)
                {
                    if (format == null) continue;
                    if (string.IsNullOrWhiteSpace(format.MediaUserAgent))
                        format.MediaUserAgent = InnertubeIosPlayerUserAgent;
                    all.Add(format);
                }
            }

            var usableFormats = all.Where(f => f != null && IsDownloadMediaUrlFresh(f.Url)).ToList();
            var progressiveChoices = usableFormats
                .Where(f => f != null && f.HasVideo && f.HasAudio
                    && f.QualityTier > 0 && IsDownloadMediaUrlFresh(f.Url)
                    && !string.IsNullOrWhiteSpace(f.MimeType)
                    && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(f => f.QualityTier)
                .Select(g => new DownloadFormatChoice
                {
                    // A muxed progressive stream needs one background job and no expensive
                    // post-processing. Prefer it whenever YouTube exposes one (usually 360p).
                    Video = g.OrderBy(GetDownloadSizeScore).First(),
                    Audio = null,
                    AudioOptions = new List<PlayerFormatModel>(),
                    MediaUserAgent = mediaUserAgent ?? string.Empty
                })
                .ToList();

            var audioOptions = GetCompactDownloadAudioFormats(usableFormats);
            var audio = SelectCompactDownloadAudioFormat(usableFormats, _selectedAudioTrackId);
            var adaptiveChoices = audio == null
                ? new List<DownloadFormatChoice>()
                : usableFormats
                .Where(f => f != null && f.IsAdaptive && f.HasVideo && !f.HasAudio
                    && f.QualityTier > 0 && IsDownloadMediaUrlFresh(f.Url)
                    && !string.IsNullOrWhiteSpace(f.MimeType)
                    && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(f => f.QualityTier)
                .Select(g => new DownloadFormatChoice
                {
                    // Same resolution can have several H.264 encodes. Prefer 30 fps and the
                    // compact encode; the old maximum-bitrate choice downloaded the largest file.
                    Video = g.OrderBy(f => f.Fps > 30 ? 1 : 0)
                        .ThenBy(GetDownloadSizeScore).First(),
                    Audio = audio,
                    AudioOptions = audioOptions,
                    MediaUserAgent = mediaUserAgent ?? string.Empty
                })
                .ToList();

            var hasMultipleAudioTracks = audioOptions.Count > 1;
            return progressiveChoices
                .Concat(adaptiveChoices)
                .GroupBy(f => f.Video.QualityTier)
                .Select(g => hasMultipleAudioTracks
                    // A progressive MP4 has its audio baked in. When several language tracks
                    // exist, force the adaptive pair so the user's later choice is actually used.
                    ? (g.FirstOrDefault(f => f.Audio != null) ?? g.First())
                    : g.OrderBy(f => f.Audio == null ? 0 : 1).First())
                .OrderByDescending(f => f.Video.QualityTier)
                .ToList();
        }

        private void AddDownloadFormatsFromPlayerJson(
            string json,
            string mediaUserAgent,
            IList<PlayerFormatModel> target)
        {
            if (string.IsNullOrWhiteSpace(json) || target == null) return;

            try
            {
                var parsed = new List<PlayerFormatModel>();
                var root = ParsePlayerJsonObject(json);
                if (root == null) return;
                CollectFormatsFromStreamingData(root, parsed);
                foreach (var format in parsed)
                {
                    if (format == null) continue;
                    format.MediaUserAgent = mediaUserAgent ?? string.Empty;
                    target.Add(format);
                }
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] " + parsed.Count + " format(s) from "
                    + (string.IsNullOrWhiteSpace(mediaUserAgent) ? "player" : mediaUserAgent));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Player response parse failed: " + ex.Message);
            }
        }

        private static bool ContainsDownloadableAdaptivePair(
            IEnumerable<PlayerFormatModel> formats)
        {
            if (formats == null) return false;
            var hasVideo = formats.Any(f => f != null && f.IsAdaptive
                && f.HasVideo && !f.HasAudio && IsDownloadMediaUrlFresh(f.Url)
                && !string.IsNullOrWhiteSpace(f.MimeType)
                && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0);
            var hasAudio = formats.Any(f => f != null && f.IsAdaptive
                && f.HasAudio && !f.HasVideo && IsDownloadMediaUrlFresh(f.Url)
                && !string.IsNullOrWhiteSpace(f.MimeType)
                && (f.MimeType.IndexOf("audio/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.MimeType.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0));
            return hasVideo && hasAudio;
        }

        private static bool ContainsDownloadableFormatSet(IEnumerable<PlayerFormatModel> formats)
        {
            if (formats == null) return false;
            if (formats.Any(f => f != null && f.HasVideo && f.HasAudio
                && IsDownloadMediaUrlFresh(f.Url)
                && !string.IsNullOrWhiteSpace(f.MimeType)
                && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0))
                return true;
            return ContainsDownloadableAdaptivePair(formats);
        }

        private static long GetDownloadSizeScore(PlayerFormatModel format)
        {
            if (format == null) return long.MaxValue;
            if (format.ContentLength > 0) return format.ContentLength;
            var bitrate = Math.Max(format.AverageBitrate, format.Bitrate);
            return bitrate > 0 ? bitrate : long.MaxValue - 1;
        }

        private static PlayerFormatModel SelectCompactDownloadAudioFormat(
            IList<PlayerFormatModel> formats,
            string preferredTrackId)
        {
            var selected = SelectAudioOnlyAacFormat(formats, preferredTrackId);
            if (selected == null) return null;

            return formats.Where(f => f != null && f.IsAdaptive && f.HasAudio && !f.HasVideo
                    && IsDownloadMediaUrlFresh(f.Url)
                    && !string.IsNullOrWhiteSpace(f.MimeType)
                    && (f.MimeType.IndexOf("audio/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                        || f.MimeType.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0)
                    && string.Equals(f.AudioTrackId, selected.AudioTrackId, StringComparison.Ordinal))
                .OrderBy(GetDownloadSizeScore)
                .FirstOrDefault() ?? selected;
        }

        private static List<PlayerFormatModel> GetCompactDownloadAudioFormats(
            IList<PlayerFormatModel> formats)
        {
            var result = new List<PlayerFormatModel>();
            if (formats == null) return result;

            // Config already performs the important de-duplication by YouTube audioTrack id.
            // Resolve each id back to one compact AAC representation so the selected object can
            // be passed directly to DownloadManager without another player request.
            var tracks = Config.EnumerateAudioTracks(formats);
            foreach (var track in tracks
                .OrderByDescending(t => Config.SystemAudioTrackMatchScore(t.Id))
                .ThenByDescending(t => t.IsDefault))
            {
                var format = SelectCompactDownloadAudioFormat(formats, track.Id);
                if (format != null && !result.Any(f => string.Equals(
                    f.AudioTrackId, format.AudioTrackId, StringComparison.Ordinal)))
                {
                    result.Add(format);
                }
            }

            // Ordinary videos often omit audioTrack entirely. Keep their single AAC stream so
            // the existing adaptive download path remains available.
            if (result.Count == 0)
            {
                var format = SelectCompactDownloadAudioFormat(formats, null);
                if (format != null) result.Add(format);
            }
            return result;
        }

        private async Task SelectDownloadFormatAsync(DownloadFormatChoice format)
        {
            if (format == null || format.Video == null) return;
            if (format.Audio != null && format.AudioOptions != null
                && format.AudioOptions.Count > 1)
            {
                ShowDownloadAudioTrackOptions(format);
                return;
            }
            await StartDownloadAsync(format);
        }

        private void ShowDownloadAudioTrackOptions(DownloadFormatChoice format)
        {
            if (format == null || DownloadQualityOptionsPanel == null) return;

            DownloadQualityTitleText.Text = Localization.GetString("AudioTrack");
            _downloadOptionRows.Clear();
            DownloadQualityOptionsPanel.Children.Clear();
            DownloadUnavailableText.Visibility = Visibility.Collapsed;

            DownloadQualityOptionsPanel.Children.Add(MakeCheckableOptionButton(
                Localization.GetString("Back"),
                false,
                async () =>
                {
                    await PopulateDownloadQualityOptionsAsync();
                    UpdateSettingsBottomSheetHeight();
                }));

            foreach (var audio in format.AudioOptions)
            {
                var selectedAudio = audio;
                var label = !string.IsNullOrWhiteSpace(selectedAudio.AudioTrackName)
                    ? selectedAudio.AudioTrackName
                    : selectedAudio.AudioTrackId;
                if (string.IsNullOrWhiteSpace(label))
                    label = Localization.GetString("AudioTrack");

                var isCurrent = format.Audio != null && string.Equals(
                    format.Audio.AudioTrackId,
                    selectedAudio.AudioTrackId,
                    StringComparison.Ordinal);
                DownloadQualityOptionsPanel.Children.Add(MakeCheckableOptionButton(
                    label,
                    isCurrent,
                    async () => await StartDownloadAsync(new DownloadFormatChoice
                    {
                        Video = format.Video,
                        Audio = selectedAudio,
                        AudioOptions = format.AudioOptions,
                        MediaUserAgent = format.MediaUserAgent
                    })));
            }
            UpdateSettingsBottomSheetHeight();
        }

        private static bool IsDownloadMediaUrlFresh(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var match = Regex.Match(url, @"(?:[?&])expire=(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success) return true;
                long expires;
                if (!long.TryParse(match.Groups[1].Value, out expires)) return true;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                // A transfer queued by the OS must have enough lifetime to survive suspension
                // and a slow mobile connection. Force a fresh /player response otherwise.
                return expires > now + 600;
            }
            catch
            {
                return true;
            }
        }

        private async Task StartDownloadAsync(DownloadFormatChoice format)
        {
            if (format == null || format.Video == null) return;
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null) OverlayGrid.Visibility = Visibility.Collapsed;

            var videoId = currentVideoId;
            System.Diagnostics.Debug.WriteLine(
                "[Downloads] Starting " + format.Video.QualityTier + "p; itag="
                + format.Video.Itag + (format.Audio == null ? "; progressive" : "; adaptive"));

            var item = await DownloadManager.StartAsync(
                videoId,
                VideoTitleText == null ? string.Empty : VideoTitleText.Text,
                currentChannelName,
                currentChannelId,
                _currentVideoDescription,
                _currentVideoThumbnailUrl,
                _currentChannelThumbnailUrl,
                format.Video,
                format.Audio,
                format.MediaUserAgent);

            if (item == null)
                System.Diagnostics.Debug.WriteLine("[Downloads] Could not start transfer");
            await UpdateDownloadButtonAsync();
        }

        private async void DownloadManager_Changed(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                await UpdateDownloadButtonAsync();
                await RefreshDownloadOptionRowsAsync();
            });
        }

        private async Task RefreshDownloadOptionRowsAsync()
        {
            if (DownloadSettingsPanel == null
                || DownloadSettingsPanel.Visibility != Visibility.Visible
                || _downloadOptionRows.Count == 0)
                return;

            foreach (var state in _downloadOptionRows.ToList())
            {
                if (state == null || string.IsNullOrWhiteSpace(state.Quality)) continue;
                var existing = await DownloadManager.FindAsync(currentVideoId, state.Quality);
                var isActive = existing != null
                    && (existing.IsDownloading || existing.IsFinalizing);
                var isComplete = existing != null && existing.IsComplete;

                if (state.Label != null)
                {
                    state.Label.Text = isActive
                        ? state.Quality + "  ·  " + existing.ProgressPercent + "%"
                        : isComplete
                            ? state.Quality + "  ·  " + Localization.GetString("Downloaded")
                            : state.Quality;
                    state.Label.FontWeight = isComplete
                        ? Windows.UI.Text.FontWeights.SemiBold
                        : Windows.UI.Text.FontWeights.Normal;
                }
                if (state.OptionButton != null)
                {
                    state.OptionButton.FontWeight = isComplete
                        ? Windows.UI.Text.FontWeights.SemiBold
                        : Windows.UI.Text.FontWeights.Normal;
                }
                if (state.Check != null)
                    state.Check.Visibility = isComplete ? Visibility.Visible : Visibility.Collapsed;
                if (state.CancelButton != null)
                {
                    state.CancelButton.Visibility = isActive
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    state.CancelButton.IsEnabled = true;
                }
            }
        }

        private async Task UpdateDownloadButtonAsync()
        {
            if (DownloadActionText == null || DownloadActionIcon == null || DownloadProgressIcon == null)
                return;

            var item = await DownloadManager.FindAsync(currentVideoId);
            var active = item != null && item.IsDownloading;
            DownloadActionIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            DownloadProgressIcon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            DownloadActionText.Text = active
                ? item.ProgressPercent + "%"
                : Localization.GetString("Download");
            if (active) SetDownloadProgressArc(item.ProgressPercent);
        }

        private void SetDownloadProgressArc(int percent)
        {
            if (DownloadProgressArc == null) return;
            var value = Math.Max(0, Math.Min(100, percent));
            if (value >= 100)
            {
                DownloadProgressArc.Data = new EllipseGeometry
                {
                    Center = new Windows.Foundation.Point(10, 10),
                    RadiusX = 8,
                    RadiusY = 8
                };
                return;
            }

            var angle = value * Math.PI * 2.0 / 100.0;
            var end = new Windows.Foundation.Point(
                10 + 8 * Math.Sin(angle),
                10 - 8 * Math.Cos(angle));
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(10, 2),
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Windows.Foundation.Size(8, 8),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = value > 50
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            DownloadProgressArc.Data = geometry;
        }

    }
}
