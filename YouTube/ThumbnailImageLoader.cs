using System;
using System.Collections.Generic;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;
using YouTube.Innertube;

namespace YouTube
{
    public sealed class ThumbnailImageLoader : DependencyObject
    {
        private ThumbnailImageLoader()
        {
        }

        private static readonly SemaphoreSlim RoundedDecodeGate = new SemaphoreSlim(2, 2);
        private static readonly DependencyProperty LoadTokenProperty =
            DependencyProperty.RegisterAttached(
                "LoadToken",
                typeof(object),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(null));
        private static readonly DependencyProperty LoadKeyProperty =
            DependencyProperty.RegisterAttached(
                "LoadKey",
                typeof(string),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(string.Empty));
        private static readonly DependencyProperty LoadStateProperty =
            DependencyProperty.RegisterAttached(
                "LoadState",
                typeof(int),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(0));

        // XAML-friendly URL binding used by every remaining thumbnail template. Direct
        // Image.Source bindings can briefly present a recycled container's previous bitmap on
        // Windows 10 Mobile. This property clears the surface first and routes Image controls
        // through the same generation-token checks as the code-behind card loaders.
        public static readonly DependencyProperty SourceUrlProperty =
            DependencyProperty.RegisterAttached(
                "SourceUrl",
                typeof(string),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(string.Empty, BoundSourceChanged));

        public static readonly DependencyProperty DecodeWidthProperty =
            DependencyProperty.RegisterAttached(
                "DecodeWidth",
                typeof(int),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(360, BoundSourceChanged));

        private static readonly DependencyProperty BoundHandlersAttachedProperty =
            DependencyProperty.RegisterAttached(
                "BoundHandlersAttached",
                typeof(bool),
                typeof(ThumbnailImageLoader),
                new PropertyMetadata(false));

        public static void SetSourceUrl(DependencyObject target, string value)
        {
            if (target != null) target.SetValue(SourceUrlProperty, value ?? string.Empty);
        }

        public static string GetSourceUrl(DependencyObject target)
        {
            return target == null ? string.Empty : target.GetValue(SourceUrlProperty) as string;
        }

        public static void SetDecodeWidth(DependencyObject target, int value)
        {
            if (target != null) target.SetValue(DecodeWidthProperty, value);
        }

        public static int GetDecodeWidth(DependencyObject target)
        {
            return target == null ? 360 : (int)target.GetValue(DecodeWidthProperty);
        }

        private static void BoundSourceChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
        {
            AssignBoundSource(target);
        }

        private static void AssignBoundSource(DependencyObject target)
        {
            if (target == null)
                return;

            var url = VideoThumbnailController.NormalizeBoundVideoThumbnailUrl(
                GetSourceUrl(target));
            var decodeWidth = Math.Max(1, GetDecodeWidth(target));
            var image = target as Image;
            if (image != null)
            {
                if (!(bool)image.GetValue(BoundHandlersAttachedProperty))
                {
                    image.SetValue(BoundHandlersAttachedProperty, true);
                    image.Loaded += BoundImage_Loaded;
                    image.Unloaded += BoundImage_Unloaded;
                }

                Assign(image, url, null, decodeWidth);
                return;
            }

            var imageBrush = target as ImageBrush;
            if (imageBrush == null)
                return;

            // ImageBrush has no opacity surface to gate, but replacing its generation token and
            // clearing ImageSource synchronously still prevents a recycled brush from flashing.
            var token = new object();
            imageBrush.SetValue(LoadTokenProperty, token);
            imageBrush.ImageSource = null;
            if (string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                var bitmap = NewBitmap(decodeWidth);
                bitmap.ImageFailed += (sender, args) =>
                {
                    if (ReferenceEquals(imageBrush.GetValue(LoadTokenProperty), token)
                        && ReferenceEquals(imageBrush.ImageSource, bitmap))
                    {
                        imageBrush.ImageSource = null;
                    }
                };
                bitmap.UriSource = new Uri(url, UriKind.Absolute);
                if (ReferenceEquals(imageBrush.GetValue(LoadTokenProperty), token))
                    imageBrush.ImageSource = bitmap;
            }
            catch
            {
                if (ReferenceEquals(imageBrush.GetValue(LoadTokenProperty), token))
                    imageBrush.ImageSource = null;
            }
        }

        private static void BoundImage_Loaded(object sender, RoutedEventArgs e)
        {
            AssignBoundSource(sender as Image);
        }

        private static void BoundImage_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelAssignment(sender as Image);
        }

        public static void Assign(Image image, string primaryUrl, string fallbackUrl, int decodeWidth)
        {
            var urls = new List<string>();
            if (!string.IsNullOrWhiteSpace(primaryUrl)) urls.Add(primaryUrl);
            if (!string.IsNullOrWhiteSpace(fallbackUrl) && !string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase)) urls.Add(fallbackUrl);
            AssignCandidates(image, urls, decodeWidth);
        }

        public static void AssignCandidates(Image image, IList<string> urls, int decodeWidth)
        {
            if (image == null)
                return;

            if (ResponsiveLayout.IsPhoneDevice && decodeWidth > 320)
                decodeWidth = 320;

            urls = NormalizeVideoThumbnailCandidates(urls);

            var loadKey = BuildLoadKey(urls, decodeWidth, 0);
            if (CanReuseAssignment(image, loadKey))
                return;

            // Image controls in virtualized/reused card templates can receive a new DataContext while
            // an older thumbnail is still downloading. Keep a per-assignment token so an old request
            // can never overwrite a newer card image.
            var loadToken = new object();
            image.SetValue(LoadTokenProperty, loadToken);
            image.SetValue(LoadKeyProperty, loadKey);
            image.SetValue(LoadStateProperty, 1);
            // A recycled Image must not keep showing the previous card while the new URI is
            // downloading. Clearing it exposes the gray VideoPlaceholderBrush underneath.
            image.Source = null;
            image.Opacity = 0;

            if (urls == null || urls.Count == 0)
            {
                MarkLoadFailed(image, loadToken);
                return;
            }

            LoadCandidate(image, urls, 0, decodeWidth, loadToken);
        }

        // Invalidates any asynchronous decode that still targets an Image leaving the visual
        // tree. A cached page can later load the same controls with unchanged DataContext, so the
        // key is cleared as well and the next Loaded event starts a fresh assignment.
        public static void CancelAssignment(Image image)
        {
            if (image == null)
                return;

            image.SetValue(LoadTokenProperty, new object());
            image.SetValue(LoadKeyProperty, string.Empty);
            image.SetValue(LoadStateProperty, 0);
            image.Source = null;
            image.Opacity = 0;
        }

        private static void LoadCandidate(Image image, IList<string> urls, int index, int decodeWidth, object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken))
                return;

            if (urls == null || index >= urls.Count)
            {
                MarkLoadFailed(image, loadToken);
                return;
            }

            var url = urls[index];
            if (string.IsNullOrWhiteSpace(url))
            {
                LoadCandidate(image, urls, index + 1, decodeWidth, loadToken);
                return;
            }

            // YouTube's default/high/standard video thumbnails are 4:3 canvases. For 16:9 videos
            // the top and bottom letterbox bands are physically encoded into those JPEGs, so XAML
            // Stretch alone is not a reliable way to remove them on every old UWP renderer.
            // Decode and crop the source pixels to a centered 16:9 rectangle instead.
            var requiresWideCrop = RequiresPhysicalWideCrop(url);
            if (requiresWideCrop)
            {
                LoadCroppedYoutubeCandidateAsync(image, urls, index, decodeWidth, loadToken);
                return;
            }

            var bitmap = NewBitmap(decodeWidth);
            bitmap.ImageOpened += (s, e) =>
            {
                if (!IsCurrentLoad(image, loadToken) || !ReferenceEquals(image.Source, bitmap))
                    return;

                MarkLoadSucceeded(image, loadToken);
            };
            bitmap.ImageFailed += (s, e) =>
            {
                if (!IsCurrentLoad(image, loadToken) || !ReferenceEquals(image.Source, bitmap))
                    return;

                LoadCandidate(image, urls, index + 1, decodeWidth, loadToken);
            };

            try
            {
                bitmap.UriSource = new Uri(url);
                if (IsCurrentLoad(image, loadToken))
                    image.Source = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Thumb] Load failed: " + ex.Message);
                LoadCandidate(image, urls, index + 1, decodeWidth, loadToken);
            }
        }

        private static async void LoadCroppedYoutubeCandidateAsync(Image image, IList<string> urls, int index, int decodeWidth, object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken) || urls == null || index >= urls.Count)
                return;

            var url = urls[index];

            try
            {
                using (var input = await YouTubeThumbnailClient.OpenStreamAsync(url))
                using (var randomAccess = new InMemoryRandomAccessStream())
                {
                    if (!IsCurrentLoad(image, loadToken))
                        return;

                    await RandomAccessStream.CopyAsync(input, randomAccess);
                    randomAccess.Seek(0);

                    var decoder = await BitmapDecoder.CreateAsync(randomAccess);
                    if (!IsCurrentLoad(image, loadToken))
                        return;

                    var sourceWidth = decoder.PixelWidth;
                    var sourceHeight = decoder.PixelHeight;
                    if (sourceWidth == 0 || sourceHeight == 0)
                        throw new InvalidOperationException("Thumbnail has invalid dimensions.");

                    var scaledWidth = sourceWidth;
                    if (decodeWidth > 0 && scaledWidth > (uint)decodeWidth)
                        scaledWidth = (uint)decodeWidth;

                    var scaledHeight = (uint)Math.Max(1.0, Math.Round(sourceHeight * (scaledWidth / (double)sourceWidth)));
                    var cropHeight = (uint)Math.Round(scaledWidth * 9.0 / 16.0);
                    if (cropHeight < 1)
                        cropHeight = 1;
                    if (cropHeight > scaledHeight)
                        cropHeight = scaledHeight;

                    var cropY = (scaledHeight - cropHeight) / 2;
                    var transform = new BitmapTransform
                    {
                        ScaledWidth = scaledWidth,
                        ScaledHeight = scaledHeight,
                        InterpolationMode = BitmapInterpolationMode.Fant,
                        Bounds = new BitmapBounds
                        {
                            X = 0,
                            Y = cropY,
                            Width = scaledWidth,
                            Height = cropHeight
                        }
                    };

                    var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    try
                    {
                        if (!IsCurrentLoad(image, loadToken))
                            return;

                        var source = new SoftwareBitmapSource();
                        await source.SetBitmapAsync(softwareBitmap);
                        if (!IsCurrentLoad(image, loadToken))
                        {
                            source.Dispose();
                            return;
                        }

                        image.Source = source;
                        MarkLoadSucceeded(image, loadToken);
                    }
                    finally
                    {
                        softwareBitmap.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Thumb] Cropped load failed: " + ex.Message);
                if (IsCurrentLoad(image, loadToken))
                    LoadCandidate(image, urls, index + 1, decodeWidth, loadToken);
            }
        }

        // The original Windows 10 Mobile XAML compositor can terminate the process when a
        // decoded SoftwareBitmap is presented through ImageBrush/Shape. Keep one ordinary Image
        // and put the rounded alpha directly into its pixels using Windows.Graphics.Imaging.
        public static void AssignRoundedCandidates(
            Image image,
            IList<string> urls,
            int decodeWidth,
            int cornerRadius)
        {
            if (image == null)
                return;

            if (ResponsiveLayout.IsPhoneDevice && decodeWidth > 320)
                decodeWidth = 320;
            urls = NormalizeVideoThumbnailCandidates(urls);

            var loadToken = new object();
            var loadKey = BuildLoadKey(urls, decodeWidth, Math.Max(1, cornerRadius));
            if (CanReuseAssignment(image, loadKey))
                return;

            image.SetValue(LoadTokenProperty, loadToken);
            image.SetValue(LoadKeyProperty, loadKey);
            image.SetValue(LoadStateProperty, 1);
            image.Source = null;
            image.Opacity = 0;

            if (urls == null || urls.Count == 0)
            {
                MarkLoadFailed(image, loadToken);
                return;
            }

            LoadRoundedCandidateAsync(
                image,
                urls,
                0,
                decodeWidth,
                Math.Max(1, cornerRadius),
                loadToken);
        }

        private static async void LoadRoundedCandidateAsync(
            Image image,
            IList<string> urls,
            int index,
            int decodeWidth,
            int cornerRadius,
            object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken))
                return;

            if (urls == null || index >= urls.Count)
            {
                MarkLoadFailed(image, loadToken);
                return;
            }

            var url = urls[index];
            if (string.IsNullOrWhiteSpace(url))
            {
                LoadRoundedCandidateAsync(
                    image,
                    urls,
                    index + 1,
                    decodeWidth,
                    cornerRadius,
                    loadToken);
                return;
            }

            await RoundedDecodeGate.WaitAsync();
            try
            {
                try
                {
                    using (var input = await YouTubeThumbnailClient.OpenStreamAsync(url))
                    using (var randomAccess = new InMemoryRandomAccessStream())
                    {
                        if (!IsCurrentLoad(image, loadToken))
                            return;

                        await RandomAccessStream.CopyAsync(input, randomAccess);
                        randomAccess.Seek(0);

                        var decoder = await BitmapDecoder.CreateAsync(randomAccess);
                        if (!IsCurrentLoad(image, loadToken))
                            return;

                        var sourceWidth = decoder.PixelWidth;
                        var sourceHeight = decoder.PixelHeight;
                        if (sourceWidth == 0 || sourceHeight == 0)
                            throw new InvalidOperationException("Thumbnail has invalid dimensions.");

                        var scaledWidth = sourceWidth;
                        if (decodeWidth > 0 && scaledWidth > (uint)decodeWidth)
                            scaledWidth = (uint)decodeWidth;
                        var scaledHeight = (uint)Math.Max(
                            1.0,
                            Math.Round(sourceHeight * (scaledWidth / (double)sourceWidth)));

                        var outputHeight = scaledHeight;
                        var cropY = 0u;
                        if (RequiresPhysicalWideCrop(url))
                        {
                            outputHeight = (uint)Math.Round(scaledWidth * 9.0 / 16.0);
                            if (outputHeight < 1)
                                outputHeight = 1;
                            if (outputHeight > scaledHeight)
                                outputHeight = scaledHeight;
                            cropY = (scaledHeight - outputHeight) / 2;
                        }

                        var transform = new BitmapTransform
                        {
                            ScaledWidth = scaledWidth,
                            ScaledHeight = scaledHeight,
                            InterpolationMode = BitmapInterpolationMode.Fant,
                            Bounds = new BitmapBounds
                            {
                                X = 0,
                                Y = cropY,
                                Width = scaledWidth,
                                Height = outputHeight
                            }
                        };

                        var pixelData = await decoder.GetPixelDataAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            transform,
                            ExifOrientationMode.IgnoreExifOrientation,
                            ColorManagementMode.DoNotColorManage);
                        if (!IsCurrentLoad(image, loadToken))
                            return;

                        var pixels = pixelData.DetachPixelData();
                        ApplyRoundedAlpha(pixels, scaledWidth, outputHeight, cornerRadius);

                        var softwareBitmap = SoftwareBitmap.CreateCopyFromBuffer(
                            pixels.AsBuffer(),
                            BitmapPixelFormat.Bgra8,
                            (int)scaledWidth,
                            (int)outputHeight,
                            BitmapAlphaMode.Premultiplied);
                        try
                        {
                            var source = new SoftwareBitmapSource();
                            await source.SetBitmapAsync(softwareBitmap);
                            if (!IsCurrentLoad(image, loadToken))
                            {
                                source.Dispose();
                                return;
                            }

                            image.Source = source;
                            MarkLoadSucceeded(image, loadToken);
                        }
                        finally
                        {
                            softwareBitmap.Dispose();
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Thumb] Rounded pixel load failed: " + ex.Message);
                    if (IsCurrentLoad(image, loadToken))
                    {
                        LoadRoundedCandidateAsync(
                            image,
                            urls,
                            index + 1,
                            decodeWidth,
                            cornerRadius,
                            loadToken);
                    }
                }
            }
            finally
            {
                RoundedDecodeGate.Release();
            }
        }

        private static void ApplyRoundedAlpha(
            byte[] pixels,
            uint width,
            uint height,
            int requestedRadius)
        {
            if (pixels == null || width == 0 || height == 0)
                return;

            var radius = Math.Min(
                requestedRadius,
                (int)Math.Min(width, height) / 2);
            if (radius <= 0)
                return;

            ApplyCornerAlpha(pixels, width, height, radius, true, true);
            ApplyCornerAlpha(pixels, width, height, radius, false, true);
            ApplyCornerAlpha(pixels, width, height, radius, true, false);
            ApplyCornerAlpha(pixels, width, height, radius, false, false);
        }

        private static void ApplyCornerAlpha(
            byte[] pixels,
            uint width,
            uint height,
            int radius,
            bool left,
            bool top)
        {
            var centerX = left ? radius : width - radius;
            var centerY = top ? radius : height - radius;
            var startX = left ? 0 : (int)width - radius;
            var startY = top ? 0 : (int)height - radius;

            for (var localY = 0; localY < radius; localY++)
            {
                var y = startY + localY;
                for (var localX = 0; localX < radius; localX++)
                {
                    var x = startX + localX;
                    var dx = x + 0.5 - centerX;
                    var dy = y + 0.5 - centerY;
                    var coverage = radius + 0.5 - Math.Sqrt(dx * dx + dy * dy);
                    if (coverage >= 1.0)
                        continue;

                    if (coverage < 0)
                        coverage = 0;
                    var offset = (y * (int)width + x) * 4;
                    pixels[offset] = (byte)Math.Round(pixels[offset] * coverage);
                    pixels[offset + 1] = (byte)Math.Round(pixels[offset + 1] * coverage);
                    pixels[offset + 2] = (byte)Math.Round(pixels[offset + 2] * coverage);
                    pixels[offset + 3] = (byte)Math.Round(pixels[offset + 3] * coverage);
                }
            }
        }

        internal static bool RequiresPhysicalWideCrop(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return false;

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                return false;

            var host = uri.Host ?? string.Empty;
            if (host.IndexOf("img.youtube.com", StringComparison.OrdinalIgnoreCase) < 0
                && host.IndexOf("ytimg.com", StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            var path = uri.AbsolutePath ?? string.Empty;
            return path.EndsWith("/default.jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/hqdefault.jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/sddefault.jpg", StringComparison.OrdinalIgnoreCase);
        }

        private static IList<string> NormalizeVideoThumbnailCandidates(IList<string> urls)
        {
            if (urls == null || urls.Count == 0
                || !ResponsiveLayout.IsPhoneDevice
                || !string.Equals(
                    VideoThumbnailController.GetSelectedQuality(),
                    VideoThumbnailController.MobileDefaultQuality,
                    StringComparison.OrdinalIgnoreCase))
                return urls;

            var normalized = new List<string>();
            for (var i = 0; i < urls.Count; i++)
            {
                var value = VideoThumbnailController.NormalizeBoundVideoThumbnailUrl(urls[i]);
                if (string.IsNullOrWhiteSpace(value))
                    continue;

                var duplicate = false;
                for (var j = 0; j < normalized.Count; j++)
                {
                    if (string.Equals(normalized[j], value, StringComparison.OrdinalIgnoreCase))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    normalized.Add(value);
            }

            return normalized;
        }

        private static bool IsCurrentLoad(Image image, object loadToken)
        {
            return image != null
                && ReferenceEquals(image.GetValue(LoadTokenProperty), loadToken);
        }

        private static bool CanReuseAssignment(Image image, string loadKey)
        {
            if (image == null || string.IsNullOrEmpty(loadKey)
                || !string.Equals(image.GetValue(LoadKeyProperty) as string, loadKey, StringComparison.Ordinal))
                return false;

            var state = (int)image.GetValue(LoadStateProperty);
            return state == 1 || (state == 2 && image.Source != null);
        }

        private static string BuildLoadKey(IList<string> urls, int decodeWidth, int cornerRadius)
        {
            var builder = new StringBuilder();
            builder.Append(decodeWidth).Append('|').Append(cornerRadius);
            if (urls != null)
            {
                for (var i = 0; i < urls.Count; i++)
                    builder.Append('|').Append(urls[i] ?? string.Empty);
            }
            return builder.ToString();
        }

        private static void MarkLoadSucceeded(Image image, object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken))
                return;

            image.SetValue(LoadStateProperty, 2);
            image.Opacity = 1;
        }

        private static void MarkLoadFailed(Image image, object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken))
                return;

            image.SetValue(LoadStateProperty, 3);
            image.Source = null;
            image.Opacity = 0;
        }

        private static BitmapImage NewBitmap(int decodeWidth)
        {
            var bitmap = new BitmapImage();
            if (decodeWidth > 0)
            {
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.DecodePixelWidth = decodeWidth;
            }
            return bitmap;
        }
    }
}
