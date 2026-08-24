using System;
using Windows.Storage;

namespace YouTube
{
    internal static class ShortsFeatureController
    {
        public const string SettingKey = "ShortsFeatureEnabled";

        public static event EventHandler EnabledChanged;

        public static bool IsEnabled()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out raw) || raw == null)
                    return true;

                if (raw is bool)
                    return (bool)raw;

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
            var changed = IsEnabled() != enabled;
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = enabled;
            }
            catch
            {
            }

            if (changed && EnabledChanged != null)
                EnabledChanged(null, EventArgs.Empty);
        }
    }
}
