using System;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System.Profile;

namespace YouTube
{
    internal static class DesktopMiniPlayerSetting
    {
        private const string SettingKey = "DesktopMiniPlayerSeparateWindow";

        public static bool IsDesktopDevice()
        {
            try
            {
                return string.Equals(
                    AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Desktop",
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        public static bool IsBuildCapable()
        {
#if VS2017_DESKTOP_PIP
            return true;
#else
            return false;
#endif
        }

        public static bool IsRuntimeCapable()
        {
#if VS2017_DESKTOP_PIP
            try
            {
                return ApiInformation.IsTypePresent("Windows.UI.ViewManagement.ApplicationViewMode")
                    && ApiInformation.IsTypePresent("Windows.UI.ViewManagement.ViewModePreferences")
                    && ApiInformation.IsMethodPresent(
                        "Windows.UI.ViewManagement.ApplicationViewSwitcher",
                        "TryShowAsViewModeAsync");
            }
            catch
            {
                return false;
            }
#else
            return false;
#endif
        }

        public static bool IsAvailable()
        {
            return IsDesktopDevice() && IsBuildCapable() && IsRuntimeCapable();
        }

        // Supported desktop VS2017 builds default ON. Unsupported builds do not persist a false
        // value, so installing a VS2017 build later still receives the intended default.
        public static void EnsureDefault()
        {
            if (!IsAvailable())
            {
                return;
            }

            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(SettingKey))
                {
                    values[SettingKey] = true;
                }
            }
            catch
            {
            }
        }

        public static bool IsEnabled()
        {
            if (!IsAvailable())
            {
                return false;
            }

            EnsureDefault();
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out raw)
                    || raw == null)
                {
                    return true;
                }

                if (raw is bool)
                {
                    return (bool)raw;
                }

                bool parsed;
                return !bool.TryParse(raw.ToString(), out parsed) || parsed;
            }
            catch
            {
                return true;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            if (!IsAvailable())
            {
                return;
            }

            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = enabled;
            }
            catch
            {
            }
        }

        public static string GetDescriptionResourceKey()
        {
            if (!IsDesktopDevice())
            {
                return "DesktopMiniPlayerUnavailableMobile";
            }
            if (!IsBuildCapable())
            {
                return "DesktopMiniPlayerUnavailableVS2015";
            }
            if (!IsRuntimeCapable())
            {
                return "DesktopMiniPlayerUnavailableOldWindows";
            }
            return "DesktopMiniPlayerWindowDescription";
        }
    }
}
