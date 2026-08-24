using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    // Shared presentation rules for the reusable video cards. This keeps portrait/mobile
    // behavior identical across Home, Search, Subscriptions, Playlist and Channel pages.
    internal static class VideoCardController
    {
        public const string ThumbnailChromeTag = "VideoCardThumbnailChrome";
        public const string MetadataTag = "VideoCardMetadata";
        private const string RoundingTextureTag = "VideoCardRoundingTexture";

        public static void ApplyResponsiveLayout(DependencyObject root, bool isPortrait)
        {
            if (root == null)
                return;

            var element = root as FrameworkElement;
            if (element != null)
            {
                var tag = element.Tag as string;
                if (string.Equals(tag, ThumbnailChromeTag, StringComparison.Ordinal))
                {
                    var border = element as Border;
                    if (border != null)
                    {
                        // UWP's Border clipping is inconsistent on the older mobile compositor.
                        // The landscape cards use the same alpha-mask texture on every page.
                        border.CornerRadius = new CornerRadius(0);
                    }

                    var thumbnailGrid = element as Grid;
                    if (thumbnailGrid != null)
                    {
                        var texture = EnsureRoundingTexture(thumbnailGrid);
                        texture.Visibility = isPortrait
                            ? Visibility.Collapsed
                            : Visibility.Visible;
                    }

                }
                else if (string.Equals(tag, MetadataTag, StringComparison.Ordinal))
                {
                    element.Margin = isPortrait
                        ? new Thickness(8, 12, 8, 0)
                        : new Thickness(0, 12, 0, 0);
                }
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
                ApplyResponsiveLayout(VisualTreeHelper.GetChild(root, i), isPortrait);
        }

        private static Image EnsureRoundingTexture(Grid thumbnailGrid)
        {
            for (var i = 0; i < thumbnailGrid.Children.Count; i++)
            {
                var existing = thumbnailGrid.Children[i] as Image;
                if (existing != null
                    && string.Equals(existing.Tag as string, RoundingTextureTag, StringComparison.Ordinal))
                {
                    return existing;
                }
            }

            var texture = new Image
            {
                Tag = RoundingTextureTag,
                Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsHitTestVisible = false
            };
            App.SetThemeImageSource(texture, "Assets/video_rounding.png");
            Canvas.SetZIndex(texture, 1000);
            thumbnailGrid.Children.Add(texture);
            return texture;
        }
    }
}
