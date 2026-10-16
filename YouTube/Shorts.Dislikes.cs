using System;
using System.Globalization;
using Windows.UI.Xaml;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class Shorts
    {
        private int _shortDislikeCountGeneration;
        private bool _shortDislikeCountPageLoaded;

        private void ShortDislikeCountSetting_Changed(object sender, EventArgs e) { RefreshShortDislikeCount(); }

        private async void RefreshShortDislikeCount()
        {
            var generation = ++_shortDislikeCountGeneration;
            if (DislikeCountText == null) return;
            DislikeCountText.Text = string.Empty;
            DislikeCountText.Visibility = Visibility.Collapsed;
            var item = CurrentShort;
            if (!_shortDislikeCountPageLoaded || item == null || !DislikeCountService.IsEnabled()
                || string.IsNullOrWhiteSpace(item.VideoId)) return;
            var videoId = item.VideoId;
            var showGeneration = _showGeneration;
            try
            {
                var count = await DislikeCountService.GetCountAsync(videoId);
                if (generation != _shortDislikeCountGeneration || showGeneration != _showGeneration
                    || !_shortDislikeCountPageLoaded || !ReferenceEquals(item, CurrentShort)
                    || !DislikeCountService.IsEnabled() || !count.HasValue) return;
                DislikeCountText.Text = Config.FormatCompactCount(count.Value.ToString(CultureInfo.InvariantCulture));
                DislikeCountText.Visibility = Visibility.Visible;
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Dislikes] Shorts count failed: " + ex.GetType().Name); }
        }
    }
}
