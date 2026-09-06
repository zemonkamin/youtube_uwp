using System;
using Windows.Storage;

namespace YouTube
{
    internal static class LandscapeNavbarSearchModeController
    {
        private const string SettingKey = "LandscapeNavbarCenteredSearch";

        public static event EventHandler EnabledChanged;

        public static void EnsureDefault()
        {
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
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = enabled;
            }
            catch
            {
            }

            var handler = EnabledChanged;
            if (handler != null)
            {
                handler(null, EventArgs.Empty);
            }
        }
    }
}
