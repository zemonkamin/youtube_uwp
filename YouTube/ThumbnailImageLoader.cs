using System;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;

namespace YouTube
{
    // Loads card thumbnails so they behave well in the non-virtualizing card lists:
    //   * decoded at the card's on-screen width, not the source's native 1280x720 — decoding
    //     dozens of full-size maxres frames at once overran the image pipeline and made cards
    //     show each other's thumbnails while scrolling;
    //   * a missing maxresdefault (a clean 404) falls back to the API thumbnail;
    //   * every assignment supersedes the previous one on that Image, so a stale fallback from a
    //     recycled/rebound container can never overwrite the current item's picture.
    internal static class ThumbnailImageLoader
    {
        public static void Assign(Image image, string primaryUrl, string fallbackUrl, int decodeWidth)
        {
            if (image == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(primaryUrl))
            {
                image.Source = null;
                return;
            }

            var bitmap = NewBitmap(decodeWidth);

            bitmap.ImageFailed += (s, e) =>
            {
                // Only act if this bitmap is still the one on screen — otherwise the container has
                // moved on to another item and must not be disturbed.
                if (!ReferenceEquals(image.Source, bitmap))
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(fallbackUrl)
                    || string.Equals(fallbackUrl, primaryUrl, StringComparison.Ordinal))
                {
                    return;
                }

                var fallback = NewBitmap(decodeWidth);
                try
                {
                    fallback.UriSource = new Uri(fallbackUrl);
                    image.Source = fallback;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Thumb] Fallback load failed: " + ex.Message);
                }
            };

            try
            {
                bitmap.UriSource = new Uri(primaryUrl);
                image.Source = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Thumb] Primary load failed: " + ex.Message);
                image.Source = null;
            }
        }

        private static BitmapImage NewBitmap(int decodeWidth)
        {
            var bitmap = new BitmapImage();
            if (decodeWidth > 0)
            {
                // Logical (view) pixels, so it stays crisp on a high-DPI screen while still
                // decoding far smaller than the native frame.
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.DecodePixelWidth = decodeWidth;
            }
            return bitmap;
        }
    }
}
