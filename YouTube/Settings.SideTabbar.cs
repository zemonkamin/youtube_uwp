using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace YouTube
{
    public sealed partial class Settings
    {
        private Storyboard _sideTabbarToggleStoryboard;

        private void SideTabbarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !SideTabbarController.IsEnabled();
            SideTabbarController.SetEnabled(enabled);
            UpdateSideTabbarToggleVisual(enabled, true);
        }

        private void UpdateSideTabbarToggleVisual(bool isOn, bool animate)
        {
            if (_sideTabbarToggleStoryboard != null)
            {
                _sideTabbarToggleStoryboard.Stop();
                _sideTabbarToggleStoryboard = null;
            }
            var color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White)
                : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
            var brush = SideTabbarToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(color);
                SideTabbarToggleTrack.Background = brush;
            }
            if (!animate)
            {
                brush.Color = color;
                SideTabbarToggleThumbTransform.X = isOn ? 20.0 : 0;
                return;
            }
            var thumb = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180), To = isOn ? 20.0 : 0,
                EnableDependentAnimation = true
            };
            var fill = new ColorAnimation { Duration = TimeSpan.FromMilliseconds(180), To = color };
            Storyboard.SetTarget(thumb, SideTabbarToggleThumbTransform);
            Storyboard.SetTargetProperty(thumb, "X");
            Storyboard.SetTarget(fill, brush);
            Storyboard.SetTargetProperty(fill, "Color");
            _sideTabbarToggleStoryboard = new Storyboard();
            _sideTabbarToggleStoryboard.Children.Add(thumb);
            _sideTabbarToggleStoryboard.Children.Add(fill);
            _sideTabbarToggleStoryboard.Begin();
        }
    }
}
