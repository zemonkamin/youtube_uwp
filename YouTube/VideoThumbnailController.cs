using System;
using System.Collections.Generic;
using Windows.Storage;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    internal sealed class VideoThumbnailQualityOption
    {
        public string Key { get; private set; }
        public string FileName { get; private set; }

        public VideoThumbnailQualityOption(string key, string fileName)
        {
            Key = key;
            FileName = fileName;
        }
    }

    // Central source of truth for every horizontal video-card thumbnail.
    // Keeps the old UWP/Windows 10 Mobile code paths simple and avoids each page choosing its
    // own mq/hq/maxres URL independently.
    internal static class VideoThumbnailController
    {
        public const string SettingKey = "VideoThumbnailQuality";
        // hqdefault.jpg is available for every YouTube video and is a better default for old
        // phones than optional maxres/sd images. Users can still choose any other tier.
        public const string DefaultQuality = "high";

        private static readonly VideoThumbnailQualityOption[] _options =
        {
            new VideoThumbnailQualityOption("default", "default.jpg"),
            new VideoThumbnailQualityOption("medium", "mqdefault.jpg"),
            new VideoThumbnailQualityOption("high", "hqdefault.jpg"),
            new VideoThumbnailQualityOption("standard", "sddefault.jpg"),
            new VideoThumbnailQualityOption("maxres", "maxresdefault.jpg")
        };

        public static VideoThumbnailQualityOption[] Options
        {
            get { return _options; }
        }

        public static string GetSelectedQuality()
        {
            try
            {
                object raw;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out raw) && raw != null)
                {
                    var value = raw.ToString();
                    if (FindOption(value) != null)
                        return value;
                }
            }
            catch
            {
            }

            return DefaultQuality;
        }

        public static void SetSelectedQuality(string quality)
        {
            var option = FindOption(quality) ?? FindOption(DefaultQuality);
            try
            {
                ApplicationData.Current.LocalSettings.Values[SettingKey] = option.Key;
            }
            catch
            {
            }
        }

        public static VideoThumbnailQualityOption GetSelectedOption()
        {
            return FindOption(GetSelectedQuality()) ?? FindOption(DefaultQuality);
        }

        public static string GetThumbnailUrl(string videoId)
        {
            return BuildUrl(videoId, GetSelectedOption().FileName);
        }

        public static string BuildUrl(string videoId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            return "https://img.youtube.com/vi/" + Uri.EscapeDataString(videoId.Trim()) + "/" + fileName;
        }

        public static IList<string> GetCandidateUrls(string videoId, string fallbackUrl)
        {
            var result = new List<string>();
            var selected = GetSelectedQuality();

            // Fall only toward smaller/more widely available files. This keeps the selected
            // quality authoritative while preventing a missing maxres/sd frame from making a
            // card blank.
            if (string.Equals(selected, "maxres", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(result, BuildUrl(videoId, "maxresdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "sddefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "hqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "mqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "default.jpg"));
            }
            else if (string.Equals(selected, "standard", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(result, BuildUrl(videoId, "sddefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "hqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "mqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "default.jpg"));
            }
            else if (string.Equals(selected, "high", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(result, BuildUrl(videoId, "hqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "mqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "default.jpg"));
            }
            else if (string.Equals(selected, "medium", StringComparison.OrdinalIgnoreCase))
            {
                AddCandidate(result, BuildUrl(videoId, "mqdefault.jpg"));
                AddCandidate(result, BuildUrl(videoId, "default.jpg"));
            }
            else
            {
                AddCandidate(result, BuildUrl(videoId, "default.jpg"));
            }

            AddCandidate(result, fallbackUrl);
            return result;
        }

        public static void Assign(Image image, string videoId, string fallbackUrl, int decodeWidth)
        {
            if (image == null)
                return;

            // hqdefault/sddefault/default are 4:3 and may contain letterbox bands. The card hosts
            // are 16:9, so UniformToFill crops those bands instead of displaying black bars.
            image.Stretch = Stretch.UniformToFill;
            image.HorizontalAlignment = Windows.UI.Xaml.HorizontalAlignment.Stretch;
            image.VerticalAlignment = Windows.UI.Xaml.VerticalAlignment.Stretch;
            ThumbnailImageLoader.AssignCandidates(image, GetCandidateUrls(videoId, fallbackUrl), decodeWidth);
        }

        private static VideoThumbnailQualityOption FindOption(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
                return null;

            for (var i = 0; i < _options.Length; i++)
            {
                if (string.Equals(_options[i].Key, quality.Trim(), StringComparison.OrdinalIgnoreCase))
                    return _options[i];
            }

            return null;
        }

        private static void AddCandidate(IList<string> list, string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            for (var i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], url, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            list.Add(url);
        }
    }
}
