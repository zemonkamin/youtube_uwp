using System;
using System.Globalization;
using Windows.UI.Xaml;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class Video
    {
        private int _dislikeCountGeneration;
        private bool _dislikeCountPageLoaded;

        private void DislikeCountSetting_Changed(object sender, EventArgs e) { RefreshDislikeCount(); }

        private void ClearDislikeCount()
        {
            _dislikeCountGeneration++;
            SetDislikeCountText(string.Empty);
        }

        private void SetDislikeCountText(string text)
        {
            if (DislikeCountText == null) return;
            DislikeCountText.Text = text;
            DislikeCountText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            _actionsWidthAllLabels = double.NaN;
            _actionsWidthWithoutDownloadLabel = double.NaN;
            _actionsWidthWithoutSaveLabel = double.NaN;
            _actionsWidthIconsOnly = double.NaN;
            ScheduleLandscapeActionLabelUpdate();
        }

        private async void RefreshDislikeCount()
        {
            ClearDislikeCount();
            if (!_dislikeCountPageLoaded || _offlineMode || !DislikeCountService.IsEnabled()
                || string.IsNullOrWhiteSpace(currentVideoId)) return;
            var videoId = currentVideoId;
            var generation = _dislikeCountGeneration;
            try
            {
                var count = await DislikeCountService.GetCountAsync(videoId);
                if (generation != _dislikeCountGeneration || !_dislikeCountPageLoaded || _offlineMode
                    || !IsStillCurrentVideo(videoId) || !DislikeCountService.IsEnabled() || !count.HasValue) return;
                SetDislikeCountText(Config.FormatCompactCount(count.Value.ToString(CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Dislikes] Video count failed: " + ex.GetType().Name); }
        }
    }
}
