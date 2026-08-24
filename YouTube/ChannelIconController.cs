using System;
using System.Collections.Generic;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace YouTube
{
    // Central loader for channel icons shown on video cards. The card XAML uses a normal Image
    // plus Assets/rounding.png on top, matching the existing Subscriptions avatar implementation.
    // BitmapImage DecodePixel* and the CDN size hint keep decoding/network work small on W10 Mobile.
    internal static class ChannelIconController
    {
        public const string SettingKey = "ShowChannelIconsOnVideoCards";
        public const int DecodeSize = 64;

        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, WeakReference> BitmapCache =
            new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);

        public static bool IsEnabled()
        {
            try
            {
                object raw;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out raw) && raw != null)
                {
                    if (raw is bool)
                        return (bool)raw;

                    bool parsed;
                    if (bool.TryParse(raw.ToString(), out parsed))
                        return parsed;
                }
            }
            catch
            {
            }

            // Channel avatars add one extra request per card. Keep them opt-in on fresh installs;
            // an existing saved user choice remains authoritative above.
            return false;
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
        }

        public static string GetVideoSkeletonAssetPath()
        {
            return IsEnabled()
                ? "Assets/yt_skeleton/video_channel_image.png"
                : "Assets/yt_skeleton/video.png";
        }

        public static void Assign(Image image, string url)
        {
            if (image == null)
                return;

            var host = image.Parent as FrameworkElement;
            if (!IsEnabled())
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                if (host != null)
                    host.Visibility = Visibility.Collapsed;
                return;
            }

            var normalized = NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                if (host != null)
                    host.Visibility = Visibility.Collapsed;
                return;
            }

            image.Visibility = Visibility.Visible;
            if (host != null)
                host.Visibility = Visibility.Visible;

            try
            {
                image.Source = GetOrCreateBitmap(normalized);
            }
            catch
            {
                // Leave the placeholder Border visible under the Image.
                image.Source = null;
            }
        }

        // Generic 64px avatar loader for UI that is not controlled by the video-card setting
        // (for example comment authors). Reuses the same normalized URL + BitmapImage cache.
        public static void AssignAlways(Image image, string url)
        {
            if (image == null)
                return;

            var normalized = NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                image.Source = null;
                return;
            }

            try
            {
                image.Source = GetOrCreateBitmap(normalized);
            }
            catch
            {
                image.Source = null;
            }
        }

        // Same loader for Shape.Fill scenarios (Ellipse/Rectangle ImageBrush). This avoids
        // relying on Border child clipping or texture masks on the original Windows 10 Mobile XAML renderer.
        public static void AssignAlways(ImageBrush imageBrush, string url)
        {
            if (imageBrush == null)
                return;

            var normalized = NormalizeUrl(url);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                imageBrush.ImageSource = null;
                return;
            }

            try
            {
                imageBrush.Stretch = Stretch.UniformToFill;
                imageBrush.ImageSource = GetOrCreateBitmap(normalized);
            }
            catch
            {
                imageBrush.ImageSource = null;
            }
        }

        private static BitmapImage GetOrCreateBitmap(string url)
        {
            lock (CacheLock)
            {
                WeakReference weak;
                if (BitmapCache.TryGetValue(url, out weak))
                {
                    var cached = weak.Target as BitmapImage;
                    if (cached != null)
                        return cached;

                    BitmapCache.Remove(url);
                }

                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = DecodeSize,
                    DecodePixelHeight = DecodeSize
                };
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                BitmapCache[url] = new WeakReference(bitmap);

                if (BitmapCache.Count > 192)
                    PruneCache();

                return bitmap;
            }
        }

        private static void PruneCache()
        {
            var dead = new List<string>();
            foreach (var pair in BitmapCache)
            {
                if (pair.Value == null || pair.Value.Target == null)
                    dead.Add(pair.Key);
            }

            for (var i = 0; i < dead.Count; i++)
                BitmapCache.Remove(dead[i]);

            if (BitmapCache.Count > 192)
            {
                var removeCount = BitmapCache.Count - 128;
                var keys = new List<string>(BitmapCache.Keys);
                for (var i = 0; i < removeCount && i < keys.Count; i++)
                    BitmapCache.Remove(keys[i]);
            }
        }

        private static string NormalizeUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return string.Empty;

            var value = url.Trim()
                .Replace("\\u0026", "&")
                .Replace("\u0026", "&")
                .Replace("&amp;", "&");

            // Downloaded cards keep the channel avatar in LocalFolder so it remains
            // available without a connection. BitmapImage accepts ms-appdata URIs
            // directly; do not run those through the HTTPS-only normalization below.
            if (value.StartsWith("ms-appdata:///", StringComparison.OrdinalIgnoreCase))
                return value;

            if (value.StartsWith("//", StringComparison.Ordinal))
                value = "https:" + value;

            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                value = "https://" + value.Substring("http://".Length);

            if (!value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return string.Empty;

            // Google/YouTube avatar URLs normally encode the requested square size as =sNN.
            // Ask the CDN for 64px as well as decoding to 64px locally.
            var sizeMarker = value.LastIndexOf("=s", StringComparison.OrdinalIgnoreCase);
            if (sizeMarker >= 0 && sizeMarker + 2 < value.Length)
            {
                var digitStart = sizeMarker + 2;
                var digitEnd = digitStart;
                while (digitEnd < value.Length && char.IsDigit(value[digitEnd]))
                    digitEnd++;

                if (digitEnd > digitStart)
                    value = value.Substring(0, digitStart) + DecodeSize.ToString() + value.Substring(digitEnd);
            }

            return value;
        }
    }
}
