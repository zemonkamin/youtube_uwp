using System;
using Windows.System.Profile;
using Windows.UI.Xaml;

namespace YouTube
{
    // A single low-cost layout profile shared by the old VS2015/C# 6 pages.  Keeping
    // orientation decisions here prevents every page from using a slightly different
    // phone-width heuristic.
    internal static class ResponsiveLayout
    {
        private static readonly bool PhoneDevice = DetectPhoneDevice();

        private static readonly DependencyProperty HiddenByCompactLayoutProperty =
            DependencyProperty.RegisterAttached(
                "HiddenByCompactLayout",
                typeof(bool),
                typeof(ResponsiveLayout),
                new PropertyMetadata(false));

        public static bool IsPhoneDevice
        {
            get { return PhoneDevice; }
        }

        public static bool IsLandscape
        {
            get
            {
                try
                {
                    var bounds = Window.Current.Bounds;
                    return bounds.Width >= bounds.Height;
                }
                catch
                {
                    return false;
                }
            }
        }

        // The short side check also makes the profile testable in a resized desktop
        // window and covers Continuum-style phone windows where DeviceFamily can vary.
        public static bool IsCompactLandscape
        {
            get
            {
                if (!IsLandscape)
                    return false;

                try
                {
                    var bounds = Window.Current.Bounds;
                    return PhoneDevice || Math.Min(bounds.Width, bounds.Height) <= 600.0;
                }
                catch
                {
                    return PhoneDevice;
                }
            }
        }

        public static void ShowInRegularLayout(FrameworkElement element, bool show)
        {
            if (element == null)
                return;

            var hiddenByUs = (bool)element.GetValue(HiddenByCompactLayoutProperty);
            if (!show)
            {
                if (element.Visibility == Visibility.Visible)
                {
                    element.SetValue(HiddenByCompactLayoutProperty, true);
                    element.Visibility = Visibility.Collapsed;
                }
                return;
            }

            // Restore only elements hidden by this profile. Data-driven Collapsed state
            // (no banner, no description, empty sections) remains untouched.
            if (hiddenByUs)
            {
                element.Visibility = Visibility.Visible;
                element.SetValue(HiddenByCompactLayoutProperty, false);
            }
        }

        public static void ClearVisibilityOverride(FrameworkElement element)
        {
            if (element != null)
                element.SetValue(HiddenByCompactLayoutProperty, false);
        }

        private static bool DetectPhoneDevice()
        {
            try
            {
                return string.Equals(
                    AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Mobile",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
