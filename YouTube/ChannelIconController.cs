using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Core;
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
        public const int SubscriptionStripDecodeSize = 128;

        private static readonly object CacheLock = new object();
        private static bool? _enabledCache;
        private static readonly Dictionary<string, WeakReference> BitmapCache =
            new Dictionary<string, WeakReference>(StringComparer.OrdinalIgnoreCase);
        private static readonly DependencyProperty BindingSubscriptionProperty =
            DependencyProperty.RegisterAttached(
                "BindingSubscription",
                typeof(AvatarBindingSubscription),
                typeof(ChannelIconController),
                new PropertyMetadata(null));

        private sealed class AvatarBindingSubscription
        {
            internal Image Image { get; set; }
            internal INotifyPropertyChanged Model { get; set; }
            internal CoreDispatcher Dispatcher { get; set; }
            internal bool HydrationQueued { get; set; }

            internal void Model_PropertyChanged(object sender, PropertyChangedEventArgs e)
            {
                ChannelIconController.RefreshSubscription(this, e);
            }
        }

        public static bool IsEnabled()
        {
            if (_enabledCache.HasValue)
                return _enabledCache.Value;

            var enabled = false;
            try
            {
                object raw;
                if (ApplicationData.Current.LocalSettings.Values.TryGetValue(SettingKey, out raw) && raw != null)
                {
                    if (raw is bool)
                        enabled = (bool)raw;

                    bool parsed;
                    if (!(raw is bool) && bool.TryParse(raw.ToString(), out parsed))
                        enabled = parsed;
                }
            }
            catch
            {
            }

            // Channel avatars add one extra request per card. Keep them opt-in on fresh installs;
            // an existing saved user choice remains authoritative above.
            _enabledCache = enabled;
            return enabled;
        }

        public static void SetEnabled(bool enabled)
        {
            // Update the process cache before rebuilding any visible cards. This avoids a WinRT
            // LocalSettings lookup for every DataContextChanged/Loaded event in a feed.
            _enabledCache = enabled;
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

            if (!IsEnabled())
            {
                // Disabled must be the original cheap path: no model subscription, dispatcher
                // work, URL normalization or bitmap cache access for this card.
                var old = image.GetValue(BindingSubscriptionProperty) as AvatarBindingSubscription;
                if (old != null)
                    DetachSubscription(image, old);

                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                var host = image.Parent as FrameworkElement;
                if (host != null)
                    host.Visibility = Visibility.Collapsed;
                return;
            }

            AttachToCurrentCard(image);
            Apply(image, url);
            QueueVisibleCardHydration(image, url);
        }

        private static void QueueVisibleCardHydration(Image image, string url)
        {
            if (!ResponsiveLayout.IsPhoneDevice || !IsEnabled()
                || !string.IsNullOrWhiteSpace(url) || image == null)
                return;

            var subscription = image.GetValue(BindingSubscriptionProperty) as AvatarBindingSubscription;
            // DataContextChanged can run while a recycled template is still off-screen. Loaded
            // calls Assign again with a measured image, which is the point at which work may start.
            var cardAvatarHost = image.Parent as FrameworkElement;
            if (subscription == null || subscription.HydrationQueued
                || cardAvatarHost == null
                || cardAvatarHost.ActualWidth <= 0 || cardAvatarHost.ActualHeight <= 0)
                return;

            subscription.HydrationQueued = true;
#pragma warning disable 4014
            image.Dispatcher.RunAsync(CoreDispatcherPriority.Low, delegate
            {
                if (ReferenceEquals(image.GetValue(BindingSubscriptionProperty), subscription))
                    HydrateVisibleCardAsync(subscription);
            });
#pragma warning restore 4014
        }

        private static async void HydrateVisibleCardAsync(AvatarBindingSubscription subscription)
        {
            try
            {
                var video = subscription.Model as VideoCardItem;
                if (video != null)
                {
                    await Config.HydrateVisibleChannelThumbnailAsync(video);
                    return;
                }

                var search = subscription.Model as SearchVideoItem;
                if (search == null || !string.IsNullOrWhiteSpace(search.ChannelThumbnailUrl))
                    return;

                var proxy = new VideoCardItem
                {
                    VideoId = search.VideoId,
                    ChannelId = search.ChannelId,
                    ChannelTitle = search.Author
                };
                await Config.HydrateVisibleChannelThumbnailAsync(proxy);
                if (!string.IsNullOrWhiteSpace(proxy.ChannelThumbnailUrl))
                    search.ChannelThumbnailUrl = proxy.ChannelThumbnailUrl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ChannelIcons] Visible card hydration failed: " + ex.Message);
            }
        }

        private static void Apply(Image image, string url)
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

            var normalized = NormalizeUrl(url, DecodeSize);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                if (host != null)
                    host.Visibility = Visibility.Visible;
                return;
            }

            image.Visibility = Visibility.Visible;
            if (host != null)
                host.Visibility = Visibility.Visible;

            try
            {
                var bitmap = GetOrCreateBitmap(normalized, DecodeSize);
                if (ReferenceEquals(image.Source, bitmap))
                    return;

                image.Source = null;
                image.Opacity = 0;
                if (bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
                {
                    image.Source = bitmap;
                    image.Opacity = 1;
                    return;
                }

                RoutedEventHandler openedHandler = null;
                ExceptionRoutedEventHandler failedHandler = null;
                openedHandler = delegate
                {
                    bitmap.ImageOpened -= openedHandler;
                    bitmap.ImageFailed -= failedHandler;
                    if (ReferenceEquals(image.Source, bitmap))
                        image.Opacity = 1;
                };
                failedHandler = delegate
                {
                    bitmap.ImageOpened -= openedHandler;
                    bitmap.ImageFailed -= failedHandler;
                    RemoveCachedBitmap(normalized, DecodeSize, bitmap);
                    if (ReferenceEquals(image.Source, bitmap))
                    {
                        image.Source = null;
                        image.Opacity = 0;
                    }
                };
                bitmap.ImageOpened += openedHandler;
                bitmap.ImageFailed += failedHandler;
                image.Source = bitmap;
            }
            catch
            {
                // Leave the placeholder Border visible under the Image.
                image.Source = null;
                image.Opacity = 0;
            }
        }

        private static void AttachToCurrentCard(Image image)
        {
            var old = image.GetValue(BindingSubscriptionProperty) as AvatarBindingSubscription;
            var current = image.DataContext as INotifyPropertyChanged;
            if (old != null && ReferenceEquals(old.Model, current))
                return;

            DetachSubscription(image, old);
            if (current == null)
                return;

            var subscription = new AvatarBindingSubscription
            {
                Image = image,
                Model = current,
                Dispatcher = image.Dispatcher
            };
            image.SetValue(BindingSubscriptionProperty, subscription);
            current.PropertyChanged += subscription.Model_PropertyChanged;
            image.Unloaded -= BoundImage_Unloaded;
            image.Unloaded += BoundImage_Unloaded;
        }

        private static void BoundImage_Unloaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;
            DetachSubscription(
                image,
                image.GetValue(BindingSubscriptionProperty) as AvatarBindingSubscription);
        }

        private static void DetachSubscription(
            Image image,
            AvatarBindingSubscription subscription)
        {
            if (subscription != null && subscription.Model != null)
                subscription.Model.PropertyChanged -= subscription.Model_PropertyChanged;
            if (image != null)
                image.ClearValue(BindingSubscriptionProperty);
        }

        private static void RefreshSubscription(
            AvatarBindingSubscription subscription,
            PropertyChangedEventArgs e)
        {
            if (subscription == null || subscription.Image == null)
                return;
            if (e != null && !string.IsNullOrEmpty(e.PropertyName)
                && !string.Equals(e.PropertyName, "ChannelThumbnailUrl", StringComparison.Ordinal))
                return;

            var dispatcher = subscription.Dispatcher;
            if (dispatcher == null || dispatcher.HasThreadAccess)
            {
                Apply(subscription.Image, GetCardAvatarUrl(subscription.Model));
                return;
            }

#pragma warning disable 4014
            dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                delegate
                {
                    if (ReferenceEquals(
                        subscription.Image.GetValue(BindingSubscriptionProperty),
                        subscription))
                    {
                        Apply(subscription.Image, GetCardAvatarUrl(subscription.Model));
                    }
                });
#pragma warning restore 4014
        }

        private static string GetCardAvatarUrl(INotifyPropertyChanged model)
        {
            var video = model as VideoCardItem;
            if (video != null)
                return video.ChannelThumbnailUrl;
            var search = model as SearchVideoItem;
            return search == null ? string.Empty : search.ChannelThumbnailUrl;
        }

        // Generic 64px avatar loader for UI that is not controlled by the video-card setting
        // (for example comment authors). Reuses the same normalized URL + BitmapImage cache.
        public static void AssignAlways(Image image, string url)
        {
            AssignAlways(image, url, DecodeSize);
        }

        public static void AssignAlways(Image image, string url, int decodeSize)
        {
            if (image == null)
                return;

            decodeSize = Math.Max(1, decodeSize);
            var normalized = NormalizeUrl(url, decodeSize);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                image.Source = null;
                return;
            }

            try
            {
                image.Source = GetOrCreateBitmap(normalized, decodeSize);
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

            var normalized = NormalizeUrl(url, DecodeSize);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                imageBrush.ImageSource = null;
                return;
            }

            try
            {
                imageBrush.Stretch = Stretch.UniformToFill;
                imageBrush.ImageSource = GetOrCreateBitmap(normalized, DecodeSize);
            }
            catch
            {
                imageBrush.ImageSource = null;
            }
        }

        private static BitmapImage GetOrCreateBitmap(string url, int decodeSize)
        {
            lock (CacheLock)
            {
                var cacheKey = url + "#decode=" + decodeSize.ToString();
                WeakReference weak;
                if (BitmapCache.TryGetValue(cacheKey, out weak))
                {
                    var cached = weak.Target as BitmapImage;
                    if (cached != null)
                        return cached;

                    BitmapCache.Remove(cacheKey);
                }

                var bitmap = new BitmapImage
                {
                    DecodePixelWidth = decodeSize,
                    DecodePixelHeight = decodeSize
                };
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                BitmapCache[cacheKey] = new WeakReference(bitmap);

                if (BitmapCache.Count > 192)
                    PruneCache();

                return bitmap;
            }
        }

        private static void RemoveCachedBitmap(string url, int decodeSize, BitmapImage bitmap)
        {
            lock (CacheLock)
            {
                var cacheKey = url + "#decode=" + decodeSize.ToString();
                WeakReference weak;
                if (!BitmapCache.TryGetValue(cacheKey, out weak))
                    return;

                if (weak == null || ReferenceEquals(weak.Target, bitmap))
                    BitmapCache.Remove(cacheKey);
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

        private static string NormalizeUrl(string url, int decodeSize)
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
            // Ask the CDN for the same square size that will be decoded locally.
            var sizeMarker = value.LastIndexOf("=s", StringComparison.OrdinalIgnoreCase);
            if (sizeMarker >= 0 && sizeMarker + 2 < value.Length)
            {
                var digitStart = sizeMarker + 2;
                var digitEnd = digitStart;
                while (digitEnd < value.Length && char.IsDigit(value[digitEnd]))
                    digitEnd++;

                if (digitEnd > digitStart)
                    value = value.Substring(0, digitStart) + decodeSize.ToString() + value.Substring(digitEnd);
            }

            return value;
        }
    }
}
