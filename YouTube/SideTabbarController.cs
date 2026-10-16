using System;
using Windows.Storage;
using Windows.System.Profile;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;

namespace YouTube
{
    public static class SideTabbarController
    {
        private const string SettingKey = "LandscapeSideTabbar";
        public const double RailWidth = 72.0;
        public static event EventHandler EnabledChanged;

        // Marks only the padding that reserves space for the bottom tabbar.
        public static readonly DependencyProperty BottomInsetProperty =
            DependencyProperty.RegisterAttached("BottomInset", typeof(double),
                typeof(SideTabbarController), new PropertyMetadata(0.0));

        public static double GetBottomInset(DependencyObject element)
        {
            return (double)element.GetValue(BottomInsetProperty);
        }

        public static void SetBottomInset(DependencyObject element, double value)
        {
            element.SetValue(BottomInsetProperty, value);
        }

        public static bool IsEnabled()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out value)
                    && value is bool) return (bool)value;
            }
            catch { }
            return true;
        }

        public static void SetEnabled(bool enabled)
        {
            try { ApplicationData.Current.LocalSettings.Values[SettingKey] = enabled; }
            catch { }
            var handler = EnabledChanged;
            if (handler != null) handler(null, EventArgs.Empty);
        }

        public static bool ShouldUseSideLayout()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                var family = AnalyticsInfo.VersionInfo.DeviceFamily;
                // On Mobile this describes the current view, including the external
                // Continuum display. Connecting a mouse alone does not change this mode.
                var mouseView = string.Equals(family, "Windows.Mobile", StringComparison.OrdinalIgnoreCase)
                    && UIViewSettings.GetForCurrentView().UserInteractionMode == UserInteractionMode.Mouse;
                return UseSideLayout(IsEnabled(), family, mouseView, bounds.Width, bounds.Height);
            }
            catch { return false; }
        }

        internal static bool UseSideLayout(bool enabled, string family, bool mouseView, double width, double height)
        {
            if (!enabled || width <= 0 || height <= 0 || width < height
                || double.IsNaN(width) || double.IsNaN(height)
                || double.IsInfinity(width) || double.IsInfinity(height)) return false;
            return string.Equals(family, "Windows.Desktop", StringComparison.OrdinalIgnoreCase)
                || (mouseView && string.Equals(family, "Windows.Mobile", StringComparison.OrdinalIgnoreCase));
        }
    }
}
