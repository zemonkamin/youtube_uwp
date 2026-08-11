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
                    var radius = isPortrait ? new CornerRadius(0) : new CornerRadius(8);
                    var border = element as Border;
                    if (border != null)
                        border.CornerRadius = radius;

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
    }
}
