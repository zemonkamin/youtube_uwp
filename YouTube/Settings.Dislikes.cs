using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace YouTube
{
    public sealed partial class Settings
    {
        private Storyboard _showDislikesToggleStoryboard;

        private void ShowDislikesToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !DislikeCountService.IsEnabled();
            DislikeCountService.SetEnabled(enabled);
            UpdateShowDislikesToggleVisual(enabled, true);
        }

        private void UpdateShowDislikesToggleVisual(bool isOn, bool animate)
        {
            if (ShowDislikesToggleTrack == null || ShowDislikesToggleThumbTransform == null) return;
            if (_showDislikesToggleStoryboard != null)
            {
                _showDislikesToggleStoryboard.Stop();
                _showDislikesToggleStoryboard = null;
            }
            var color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White)
                : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
            var brush = ShowDislikesToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(color);
                ShowDislikesToggleTrack.Background = brush;
            }
            if (!animate)
            {
                brush.Color = color;
                ShowDislikesToggleThumbTransform.X = isOn ? 20.0 : 0;
                return;
            }
            var thumb = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180), To = isOn ? 20.0 : 0,
                EnableDependentAnimation = true
            };
            var fill = new ColorAnimation { Duration = TimeSpan.FromMilliseconds(180), To = color };
            Storyboard.SetTarget(thumb, ShowDislikesToggleThumbTransform);
            Storyboard.SetTargetProperty(thumb, "X");
            Storyboard.SetTarget(fill, brush);
            Storyboard.SetTargetProperty(fill, "Color");
            _showDislikesToggleStoryboard = new Storyboard();
            _showDislikesToggleStoryboard.Children.Add(thumb);
            _showDislikesToggleStoryboard.Children.Add(fill);
            _showDislikesToggleStoryboard.Begin();
        }
    }
}
