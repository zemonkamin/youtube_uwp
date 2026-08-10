using System;
using System.Collections.Generic;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Web.Http;

namespace YouTube
{
    internal static class ThumbnailImageLoader
    {
        private static readonly HttpClient CropHttpClient = new HttpClient();

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

            // Image controls in virtualized/reused card templates can receive a new DataContext while
            // an older thumbnail is still downloading. Keep a per-assignment token so an old request
            // can never overwrite a newer card image.
            var loadToken = new object();
            image.Tag = loadToken;

            if (urls == null || urls.Count == 0)
            {
                image.Source = null;
                return;
            }

            LoadCandidate(image, urls, 0, decodeWidth, loadToken);
        }

        private static void LoadCandidate(Image image, IList<string> urls, int index, int decodeWidth, object loadToken)
        {
            if (!IsCurrentLoad(image, loadToken))
                return;

            if (urls == null || index >= urls.Count)
            {
                image.Source = null;
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
            if (RequiresPhysicalWideCrop(url))
            {
                LoadCroppedYoutubeCandidateAsync(image, urls, index, decodeWidth, loadToken);
                return;
            }

            var bitmap = NewBitmap(decodeWidth);
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
                using (var input = await CropHttpClient.GetInputStreamAsync(new Uri(url)))
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

                    // A centered 16:9 crop removes the baked-in bands exactly:
                    // hqdefault 480x360 -> 480x270 (45 px removed from both sides vertically)
                    // sddefault 640x480 -> 640x360 (60 px removed from both sides vertically)
                    // default   120x90  -> about 120x68.
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

        private static bool RequiresPhysicalWideCrop(string url)
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

        private static bool IsCurrentLoad(Image image, object loadToken)
        {
            return image != null && ReferenceEquals(image.Tag, loadToken);
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
