using System;
using Windows.ApplicationModel.Resources;
using Windows.ApplicationModel.Resources.Core;
using Windows.Globalization;
using Windows.Storage;

namespace YouTube
{
    internal sealed class LanguageInfo
    {
        public string AppTag { get; set; }
        public string YouTubeHl { get; set; }
        public string DisplayName { get; set; }
    }

    internal static class Localization
    {
        public const string LanguageSettingKey = "AppLanguage";
        public const string EnglishLanguage = "en-US";
        public const string RussianLanguage = "ru-RU";

        // YouTube UI languages from i18nLanguages. Right-to-left languages are intentionally
        // omitted because this UWP client uses an LTR-only layout.
        private static readonly LanguageInfo[] _supportedLanguages = new[]
        {
            new LanguageInfo { AppTag = "af-ZA", YouTubeHl = "af", DisplayName = "Afrikaans" },
            new LanguageInfo { AppTag = "az-Latn-AZ", YouTubeHl = "az", DisplayName = "Azərbaycan" },
            new LanguageInfo { AppTag = "id-ID", YouTubeHl = "id", DisplayName = "Bahasa Indonesia" },
            new LanguageInfo { AppTag = "ms-MY", YouTubeHl = "ms", DisplayName = "Bahasa Malaysia" },
            new LanguageInfo { AppTag = "bs-Latn-BA", YouTubeHl = "bs", DisplayName = "Bosanski" },
            new LanguageInfo { AppTag = "ca-ES", YouTubeHl = "ca", DisplayName = "Català" },
            new LanguageInfo { AppTag = "cs-CZ", YouTubeHl = "cs", DisplayName = "Čeština" },
            new LanguageInfo { AppTag = "da-DK", YouTubeHl = "da", DisplayName = "Dansk" },
            new LanguageInfo { AppTag = "de-DE", YouTubeHl = "de", DisplayName = "Deutsch" },
            new LanguageInfo { AppTag = "et-EE", YouTubeHl = "et", DisplayName = "Eesti" },
            new LanguageInfo { AppTag = "en-IN", YouTubeHl = "en-IN", DisplayName = "English (India)" },
            new LanguageInfo { AppTag = "en-GB", YouTubeHl = "en-GB", DisplayName = "English (UK)" },
            new LanguageInfo { AppTag = EnglishLanguage, YouTubeHl = "en", DisplayName = "English (US)" },
            new LanguageInfo { AppTag = "es-ES", YouTubeHl = "es", DisplayName = "Español (España)" },
            new LanguageInfo { AppTag = "es-MX", YouTubeHl = "es-419", DisplayName = "Español (Latinoamérica)" },
            new LanguageInfo { AppTag = "es-US", YouTubeHl = "es-US", DisplayName = "Español (US)" },
            new LanguageInfo { AppTag = "eu-ES", YouTubeHl = "eu", DisplayName = "Euskara" },
            new LanguageInfo { AppTag = "fil-PH", YouTubeHl = "fil", DisplayName = "Filipino" },
            new LanguageInfo { AppTag = "fr-FR", YouTubeHl = "fr", DisplayName = "Français" },
            new LanguageInfo { AppTag = "fr-CA", YouTubeHl = "fr-CA", DisplayName = "Français (Canada)" },
            new LanguageInfo { AppTag = "gl-ES", YouTubeHl = "gl", DisplayName = "Galego" },
            new LanguageInfo { AppTag = "hr-HR", YouTubeHl = "hr", DisplayName = "Hrvatski" },
            new LanguageInfo { AppTag = "zu-ZA", YouTubeHl = "zu", DisplayName = "IsiZulu" },
            new LanguageInfo { AppTag = "is-IS", YouTubeHl = "is", DisplayName = "Íslenska" },
            new LanguageInfo { AppTag = "it-IT", YouTubeHl = "it", DisplayName = "Italiano" },
            new LanguageInfo { AppTag = "sw-KE", YouTubeHl = "sw", DisplayName = "Kiswahili" },
            new LanguageInfo { AppTag = "lv-LV", YouTubeHl = "lv", DisplayName = "Latviešu valoda" },
            new LanguageInfo { AppTag = "lt-LT", YouTubeHl = "lt", DisplayName = "Lietuvių" },
            new LanguageInfo { AppTag = "hu-HU", YouTubeHl = "hu", DisplayName = "Magyar" },
            new LanguageInfo { AppTag = "nl-NL", YouTubeHl = "nl", DisplayName = "Nederlands" },
            new LanguageInfo { AppTag = "nb-NO", YouTubeHl = "no", DisplayName = "Norsk" },
            new LanguageInfo { AppTag = "uz-Latn-UZ", YouTubeHl = "uz", DisplayName = "O‘zbek" },
            new LanguageInfo { AppTag = "pl-PL", YouTubeHl = "pl", DisplayName = "Polski" },
            new LanguageInfo { AppTag = "pt-PT", YouTubeHl = "pt-PT", DisplayName = "Português" },
            new LanguageInfo { AppTag = "pt-BR", YouTubeHl = "pt", DisplayName = "Português (Brasil)" },
            new LanguageInfo { AppTag = "ro-RO", YouTubeHl = "ro", DisplayName = "Română" },
            new LanguageInfo { AppTag = "sq-AL", YouTubeHl = "sq", DisplayName = "Shqip" },
            new LanguageInfo { AppTag = "sk-SK", YouTubeHl = "sk", DisplayName = "Slovenčina" },
            new LanguageInfo { AppTag = "sl-SI", YouTubeHl = "sl", DisplayName = "Slovenščina" },
            new LanguageInfo { AppTag = "sr-Latn-RS", YouTubeHl = "sr-Latn", DisplayName = "Srpski" },
            new LanguageInfo { AppTag = "fi-FI", YouTubeHl = "fi", DisplayName = "Suomi" },
            new LanguageInfo { AppTag = "sv-SE", YouTubeHl = "sv", DisplayName = "Svenska" },
            new LanguageInfo { AppTag = "vi-VN", YouTubeHl = "vi", DisplayName = "Tiếng Việt" },
            new LanguageInfo { AppTag = "tr-TR", YouTubeHl = "tr", DisplayName = "Türkçe" },
            new LanguageInfo { AppTag = "be-BY", YouTubeHl = "be", DisplayName = "Беларуская" },
            new LanguageInfo { AppTag = "bg-BG", YouTubeHl = "bg", DisplayName = "Български" },
            new LanguageInfo { AppTag = "ky-KG", YouTubeHl = "ky", DisplayName = "Кыргызча" },
            new LanguageInfo { AppTag = "kk-KZ", YouTubeHl = "kk", DisplayName = "Қазақ Тілі" },
            new LanguageInfo { AppTag = "mk-MK", YouTubeHl = "mk", DisplayName = "Македонски" },
            new LanguageInfo { AppTag = "mn-MN", YouTubeHl = "mn", DisplayName = "Монгол" },
            new LanguageInfo { AppTag = RussianLanguage, YouTubeHl = "ru", DisplayName = "Русский" },
            new LanguageInfo { AppTag = "sr-Cyrl-RS", YouTubeHl = "sr", DisplayName = "Српски" },
            new LanguageInfo { AppTag = "uk-UA", YouTubeHl = "uk", DisplayName = "Українська" },
            new LanguageInfo { AppTag = "el-GR", YouTubeHl = "el", DisplayName = "Ελληνικά" },
            new LanguageInfo { AppTag = "hy-AM", YouTubeHl = "hy", DisplayName = "Հայերեն" },
            new LanguageInfo { AppTag = "ne-NP", YouTubeHl = "ne", DisplayName = "नेपाली" },
            new LanguageInfo { AppTag = "mr-IN", YouTubeHl = "mr", DisplayName = "मराठी" },
            new LanguageInfo { AppTag = "hi-IN", YouTubeHl = "hi", DisplayName = "हिन्दी" },
            new LanguageInfo { AppTag = "as-IN", YouTubeHl = "as", DisplayName = "অসমীয়া" },
            new LanguageInfo { AppTag = "bn-BD", YouTubeHl = "bn", DisplayName = "বাংলা" },
            new LanguageInfo { AppTag = "pa-IN", YouTubeHl = "pa", DisplayName = "ਪੰਜਾਬੀ" },
            new LanguageInfo { AppTag = "gu-IN", YouTubeHl = "gu", DisplayName = "ગુજરાતી" },
            new LanguageInfo { AppTag = "or-IN", YouTubeHl = "or", DisplayName = "ଓଡ଼ିଆ" },
            new LanguageInfo { AppTag = "ta-IN", YouTubeHl = "ta", DisplayName = "தமிழ்" },
            new LanguageInfo { AppTag = "te-IN", YouTubeHl = "te", DisplayName = "తెలుగు" },
            new LanguageInfo { AppTag = "kn-IN", YouTubeHl = "kn", DisplayName = "ಕನ್ನಡ" },
            new LanguageInfo { AppTag = "ml-IN", YouTubeHl = "ml", DisplayName = "മലയാളം" },
            new LanguageInfo { AppTag = "si-LK", YouTubeHl = "si", DisplayName = "සිංහල" },
            new LanguageInfo { AppTag = "th-TH", YouTubeHl = "th", DisplayName = "ภาษาไทย" },
            new LanguageInfo { AppTag = "lo-LA", YouTubeHl = "lo", DisplayName = "ລາວ" },
            new LanguageInfo { AppTag = "my-MM", YouTubeHl = "my", DisplayName = "ဗမာ" },
            new LanguageInfo { AppTag = "ka-GE", YouTubeHl = "ka", DisplayName = "ქართული" },
            new LanguageInfo { AppTag = "am-ET", YouTubeHl = "am", DisplayName = "አማርኛ" },
            new LanguageInfo { AppTag = "km-KH", YouTubeHl = "km", DisplayName = "ខ្មែរ" },
            new LanguageInfo { AppTag = "zh-CN", YouTubeHl = "zh-CN", DisplayName = "中文 (简体)" },
            new LanguageInfo { AppTag = "zh-TW", YouTubeHl = "zh-TW", DisplayName = "中文 (繁體)" },
            new LanguageInfo { AppTag = "zh-HK", YouTubeHl = "zh-HK", DisplayName = "中文 (香港)" },
            new LanguageInfo { AppTag = "ja-JP", YouTubeHl = "ja", DisplayName = "日本語" },
            new LanguageInfo { AppTag = "ko-KR", YouTubeHl = "ko", DisplayName = "한국어" }
        };

        public static LanguageInfo[] SupportedLanguages
        {
            get { return _supportedLanguages; }
        }

        public static void InitializeLanguage()
        {
            ApplyLanguageOverride(GetSavedLanguage());
        }

        public static string GetSavedLanguage()
        {
            try
            {
                object value;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(LanguageSettingKey, out value) && value != null)
                {
                    var normalized = NormalizeLanguage(value.ToString());
                    if (!string.IsNullOrEmpty(normalized))
                        return normalized;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        public static string GetEffectiveSupportedLanguage()
        {
            var saved = GetSavedLanguage();
            if (!string.IsNullOrEmpty(saved))
                return saved;

            try
            {
                var languages = ApplicationLanguages.Languages;
                if (languages != null)
                {
                    for (int i = 0; i < languages.Count; i++)
                    {
                        var matched = MatchSystemLanguage(languages[i]);
                        if (!string.IsNullOrEmpty(matched))
                            return matched;
                    }
                }
            }
            catch
            {
            }

            return EnglishLanguage;
        }

        public static string GetLanguageDisplayName(string language)
        {
            var normalized = NormalizeLanguage(language);
            for (int i = 0; i < _supportedLanguages.Length; i++)
            {
                if (string.Equals(_supportedLanguages[i].AppTag, normalized, StringComparison.OrdinalIgnoreCase))
                    return _supportedLanguages[i].DisplayName;
            }

            return "English (US)";
        }

        public static string EffectiveYouTubeLanguageCode
        {
            get
            {
                var effective = GetEffectiveSupportedLanguage();
                for (int i = 0; i < _supportedLanguages.Length; i++)
                {
                    if (string.Equals(_supportedLanguages[i].AppTag, effective, StringComparison.OrdinalIgnoreCase))
                        return _supportedLanguages[i].YouTubeHl;
                }
                return "en";
            }
        }

        public static void SetLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values.Remove(LanguageSettingKey);
                }
                catch
                {
                }

                ApplyLanguageOverride(string.Empty);
                Config.RefreshLocale();
                return;
            }

            var normalized = NormalizeLanguage(language);
            if (string.IsNullOrEmpty(normalized))
                return;

            try
            {
                ApplicationData.Current.LocalSettings.Values[LanguageSettingKey] = normalized;
            }
            catch
            {
            }

            ApplyLanguageOverride(normalized);
            Config.RefreshLocale();
        }

        public static string GetString(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                return string.Empty;

            try
            {
                var loader = ResourceLoader.GetForViewIndependentUse();
                var value = loader.GetString(key);
                return string.IsNullOrEmpty(value) ? key : value;
            }
            catch
            {
                return key;
            }
        }

        public static string Format(string key, params object[] args)
        {
            return string.Format(GetString(key), args);
        }

        public static string GetPluralString(ulong value, string oneKey, string fewKey, string manyKey)
        {
            var tag = EffectiveLanguageTag ?? string.Empty;
            if (tag.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("uk", StringComparison.OrdinalIgnoreCase)
                || tag.StartsWith("be", StringComparison.OrdinalIgnoreCase))
            {
                var mod10 = value % 10;
                var mod100 = value % 100;
                if (mod10 == 1 && mod100 != 11)
                    return GetString(oneKey);
                if (mod10 >= 2 && mod10 <= 4 && (mod100 < 12 || mod100 > 14))
                    return GetString(fewKey);
                return GetString(manyKey);
            }

            return GetString(value == 1 ? oneKey : manyKey);
        }

        public static string AcceptLanguageHeader
        {
            get
            {
                var tag = GetEffectiveSupportedLanguage();
                var hl = EffectiveYouTubeLanguageCode;
                if (string.IsNullOrWhiteSpace(tag))
                    tag = EnglishLanguage;
                if (string.IsNullOrWhiteSpace(hl))
                    hl = "en";

                return string.Equals(tag, hl, StringComparison.OrdinalIgnoreCase)
                    ? hl
                    : tag + "," + hl + ";q=0.9";
            }
        }

        public static string EffectiveLanguageTag
        {
            get { return GetEffectiveSupportedLanguage(); }
        }

        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return string.Empty;

            for (int i = 0; i < _supportedLanguages.Length; i++)
            {
                if (string.Equals(_supportedLanguages[i].AppTag, language, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(_supportedLanguages[i].YouTubeHl, language, StringComparison.OrdinalIgnoreCase))
                    return _supportedLanguages[i].AppTag;
            }

            // Compatibility with older builds and with common Windows locale variants.
            if (string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
                return EnglishLanguage;
            if (string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase))
                return RussianLanguage;

            return string.Empty;
        }

        private static string MatchSystemLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return string.Empty;

            var exact = NormalizeLanguage(language);
            if (!string.IsNullOrEmpty(exact))
                return exact;

            var tag = language.Replace('_', '-');
            var parts = tag.Split('-');
            var primary = parts.Length > 0 ? parts[0] : tag;
            var region = string.Empty;
            var script = string.Empty;

            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i].Length == 4)
                    script = parts[i];
                else if (parts[i].Length == 2 || parts[i].Length == 3)
                    region = parts[i];
            }

            if (string.Equals(primary, "en", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(region, "IN", StringComparison.OrdinalIgnoreCase)) return "en-IN";
                if (string.Equals(region, "GB", StringComparison.OrdinalIgnoreCase)) return "en-GB";
                return EnglishLanguage;
            }
            if (string.Equals(primary, "ru", StringComparison.OrdinalIgnoreCase)) return RussianLanguage;
            if (string.Equals(primary, "es", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(region, "US", StringComparison.OrdinalIgnoreCase)) return "es-US";
                if (string.Equals(region, "ES", StringComparison.OrdinalIgnoreCase)) return "es-ES";
                return "es-MX";
            }
            if (string.Equals(primary, "fr", StringComparison.OrdinalIgnoreCase))
                return string.Equals(region, "CA", StringComparison.OrdinalIgnoreCase) ? "fr-CA" : "fr-FR";
            if (string.Equals(primary, "pt", StringComparison.OrdinalIgnoreCase))
                return string.Equals(region, "PT", StringComparison.OrdinalIgnoreCase) ? "pt-PT" : "pt-BR";
            if (string.Equals(primary, "zh", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(region, "HK", StringComparison.OrdinalIgnoreCase) || string.Equals(region, "MO", StringComparison.OrdinalIgnoreCase)) return "zh-HK";
                if (string.Equals(region, "TW", StringComparison.OrdinalIgnoreCase) || string.Equals(script, "Hant", StringComparison.OrdinalIgnoreCase)) return "zh-TW";
                return "zh-CN";
            }
            if (string.Equals(primary, "sr", StringComparison.OrdinalIgnoreCase))
                return string.Equals(script, "Latn", StringComparison.OrdinalIgnoreCase) ? "sr-Latn-RS" : "sr-Cyrl-RS";

            for (int i = 0; i < _supportedLanguages.Length; i++)
            {
                var candidate = _supportedLanguages[i].AppTag;
                var dash = candidate.IndexOf('-');
                var candidatePrimary = dash > 0 ? candidate.Substring(0, dash) : candidate;
                if (string.Equals(candidatePrimary, primary, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }

            return string.Empty;
        }

        private static void ApplyLanguageOverride(string language)
        {
            try
            {
                ApplicationLanguages.PrimaryLanguageOverride = string.IsNullOrEmpty(language) ? string.Empty : NormalizeLanguage(language);
            }
            catch
            {
            }

            try
            {
                ResourceContext.GetForViewIndependentUse().Reset();
            }
            catch
            {
            }

            try
            {
                if (Windows.UI.Core.CoreWindow.GetForCurrentThread() != null)
                    ResourceContext.GetForCurrentView().Reset();
            }
            catch
            {
            }
        }
    }
}
