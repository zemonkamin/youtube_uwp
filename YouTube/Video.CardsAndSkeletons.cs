using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Video
    {
        // Decoded at card width with a maxres->API-thumbnail fallback; see ThumbnailImageLoader
        // for why a Source binding was replaced by per-item assignment.
        private void RelatedThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Windows.UI.Xaml.Controls.Image;
            ApplyRelatedLegacyThumbnail(image);
        }

        private void RelatedThumbnail_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyRelatedLegacyThumbnail(sender as Image);
        }

        private void RelatedThumbnail_Unloaded(object sender, RoutedEventArgs e)
        {
            ThumbnailImageLoader.CancelAssignment(sender as Image);
        }

        private void ApplyRelatedLegacyThumbnail(Image image)
        {
            if (image == null)
                return;

            image.Visibility = Visibility.Visible;
            image.Clip = null;

            var item = image.DataContext as RelatedVideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.large_thumbnail))
            {
                image.Source = null;
                return;
            }

            var useRoundedPixels = string.Equals(
                image.Name,
                "RelatedLegacyThumbnail",
                StringComparison.Ordinal)
                && VideoAmbientEffectController.IsEnabled()
                && !ResponsiveLayout.IsPhoneDevice;
            if (useRoundedPixels)
            {
                ThumbnailImageLoader.AssignRoundedCandidates(
                    image,
                    VideoThumbnailController.GetCandidateUrls(item.video_id, item.thumbnail),
                    360,
                    8);
                return;
            }

            VideoThumbnailController.Assign(image, item.video_id, item.thumbnail, 360);
        }

        private void RefreshRelatedThumbnailPresentation()
        {
            var portrait = IsCurrentViewPortrait();

            VideoCardController.ApplyResponsiveLayout(
                RelatedVideosContainer,
                portrait,
                true);
            RefreshRelatedThumbnailPresentation(RelatedVideosContainer);
        }

        private void RefreshRelatedThumbnailPresentation(DependencyObject root)
        {
            if (root == null)
                return;

            var image = root as Image;
            if (image != null
                && (string.Equals(image.Name, "RelatedLegacyThumbnail", StringComparison.Ordinal)
                    || string.Equals(image.Tag as string, "RelatedVideoThumbnail", StringComparison.Ordinal)))
            {
                ApplyRelatedLegacyThumbnail(image);
            }

            var childCount = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < childCount; i++)
            {
                RefreshRelatedThumbnailPresentation(VisualTreeHelper.GetChild(root, i));
            }
        }

        private void RelatedChannelIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as RelatedVideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.channel_thumbnail);
        }

        private void RelatedChannelIcon_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var item = image == null ? null : image.DataContext as RelatedVideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.channel_thumbnail);
        }

        private void CommentAuthorImage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var ellipse = sender as Ellipse;
            var brush = ellipse == null ? null : ellipse.Fill as ImageBrush;
            var item = args.NewValue as CommentItem;
            ChannelIconController.AssignAlways(brush, item == null ? string.Empty : item.AuthorThumbnail);
        }

        private void CommentAuthorImage_Loaded(object sender, RoutedEventArgs e)
        {
            var ellipse = sender as Ellipse;
            var brush = ellipse == null ? null : ellipse.Fill as ImageBrush;
            var item = ellipse == null ? null : ellipse.DataContext as CommentItem;
            ChannelIconController.AssignAlways(brush, item == null ? string.Empty : item.AuthorThumbnail);
        }

        private void PlaylistQueueThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var rectangle = sender as Rectangle;
            AssignPlaylistQueueThumbnail(rectangle, args.NewValue as RelatedVideoCardItem);
        }

        private void PlaylistQueueThumbnail_Loaded(object sender, RoutedEventArgs e)
        {
            var rectangle = sender as Rectangle;
            AssignPlaylistQueueThumbnail(rectangle, rectangle == null ? null : rectangle.DataContext as RelatedVideoCardItem);
        }

        private static void AssignPlaylistQueueThumbnail(Rectangle rectangle, RelatedVideoCardItem item)
        {
            if (rectangle == null)
            {
                return;
            }

            var brush = rectangle.Fill as ImageBrush;
            if (brush == null)
            {
                brush = new ImageBrush { Stretch = Stretch.UniformToFill };
                rectangle.Fill = brush;
            }

            var token = new object();
            rectangle.Tag = token;
            brush.ImageSource = null;

            if (item == null)
            {
                return;
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.large_thumbnail))
            {
                candidates.Add(item.large_thumbnail);
            }
            if (!string.IsNullOrWhiteSpace(item.thumbnail)
                && !candidates.Any(url => string.Equals(url, item.thumbnail, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(item.thumbnail);
            }

            LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, 0, token);
        }

        private static void LoadPlaylistQueueThumbnailCandidate(
            Rectangle rectangle,
            ImageBrush brush,
            IList<string> candidates,
            int index,
            object token)
        {
            if (rectangle == null || brush == null || !ReferenceEquals(rectangle.Tag, token))
            {
                return;
            }

            if (candidates == null || index >= candidates.Count)
            {
                brush.ImageSource = null;
                return;
            }

            var url = candidates[index];
            if (string.IsNullOrWhiteSpace(url))
            {
                LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
                return;
            }

            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = 208
                };
                bitmap.ImageFailed += (bitmapSender, failedArgs) =>
                {
                    if (!ReferenceEquals(rectangle.Tag, token) || !ReferenceEquals(brush.ImageSource, bitmap))
                    {
                        return;
                    }

                    LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
                };
                bitmap.UriSource = new Uri(url, UriKind.Absolute);

                if (ReferenceEquals(rectangle.Tag, token))
                {
                    brush.ImageSource = bitmap;
                }
            }
            catch
            {
                LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
            }
        }

        private void RelatedThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var thumbnailHost = sender as FrameworkElement;
            if (thumbnailHost == null)
                return;

            var width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var targetHeight = Math.Round(width / RelatedThumbnailAspectRatio);
            if (double.IsNaN(thumbnailHost.Height) || Math.Abs(thumbnailHost.Height - targetHeight) > 0.5)
                thumbnailHost.Height = targetHeight;

            VideoCardController.ApplyResponsiveLayout(
                thumbnailHost,
                IsCurrentViewPortrait(),
                true);
        }

        private void RelatedSkeletonCardHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var cardHost = sender as FrameworkElement;
            if (cardHost == null)
                return;

            UpdateRelatedSkeletonCardHeight(cardHost, e.NewSize.Width);
        }

        private void TitleDescriptionSkeletonHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null)
                return;

            UpdateTitleDescriptionSkeletonHeight(host, e.NewSize.Width);
        }

        private void TitleDescriptionSkeletonImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                _titleDescriptionSkeletonAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            }

            var parent = image.Parent as FrameworkElement;
            if (parent != null)
            {
                UpdateTitleDescriptionSkeletonHeight(parent, parent.ActualWidth);
            }
        }

        private void UpdateTitleDescriptionSkeletonHeight(FrameworkElement host, double width)
        {
            if (host == null)
                return;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = host.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var aspectRatio = _titleDescriptionSkeletonAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
                aspectRatio = TitleDescriptionSkeletonDefaultAspectRatio;

            var targetHeight = Math.Round(width / aspectRatio);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
                host.Height = targetHeight;
        }

        private void SkeletonCardImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                _relatedSkeletonCardAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            }

            var parent = image.Parent as FrameworkElement;
            if (parent != null)
            {
                UpdateRelatedSkeletonCardHeight(parent, parent.ActualWidth);
            }
        }

        private void UpdateRelatedSkeletonCardHeight(FrameworkElement cardHost, double width)
        {
            if (cardHost == null)
                return;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = cardHost.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var aspectRatio = _relatedSkeletonCardAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
                aspectRatio = RelatedThumbnailAspectRatio;

            var targetHeight = Math.Round(width / aspectRatio);
            if (double.IsNaN(cardHost.Height) || Math.Abs(cardHost.Height - targetHeight) > 0.5)
                cardHost.Height = targetHeight;
        }

        private void UpdateTitleDescriptionSkeletonLayout(bool isPortrait)
        {
            if (TitleDescriptionSkeletonImagePart != null)
                TitleDescriptionSkeletonImagePart.Visibility = isPortrait ? Visibility.Visible : Visibility.Collapsed;

            if (TitleDescriptionSkeletonClassicPart != null)
                TitleDescriptionSkeletonClassicPart.Visibility = isPortrait ? Visibility.Collapsed : Visibility.Visible;

            if (isPortrait && TitleDescriptionSkeletonHost != null)
                UpdateTitleDescriptionSkeletonHeight(TitleDescriptionSkeletonHost, TitleDescriptionSkeletonHost.ActualWidth);
        }

        private void ShowRelatedVideos(List<RelatedVideoCardItem> relatedVideos)
        {
            _currentRelatedVideos = relatedVideos ?? new List<RelatedVideoCardItem>();
            _relatedFallbackVisible = false;
            ApplyRelatedContentForOrientation(IsCurrentViewPortrait());

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowRelatedVideosFallback()
        {
            _currentRelatedVideos = new List<RelatedVideoCardItem>();
            _relatedFallbackVisible = true;
            ApplyRelatedContentForOrientation(IsCurrentViewPortrait());

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void HideRelatedVideosLoadingPlaceholders()
        {
            _currentRelatedVideos = new List<RelatedVideoCardItem>();
            _relatedFallbackVisible = false;
            ApplyRelatedContentForOrientation(IsCurrentViewPortrait());

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void ApplyRelatedContentForOrientation(bool isPortrait)
        {
            var hasVideos = _currentRelatedVideos != null && _currentRelatedVideos.Count > 0;
            if (RelatedVideosContainer != null)
            {
                var landscapeItems = !isPortrait && hasVideos
                    ? _currentRelatedVideos
                    : null;
                if (!ReferenceEquals(RelatedVideosContainer.ItemsSource, landscapeItems))
                    RelatedVideosContainer.ItemsSource = landscapeItems;
                RelatedVideosContainer.Visibility = !isPortrait && hasVideos
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (RelatedVideosContainerVertical != null)
            {
                var portraitItems = isPortrait && hasVideos
                    ? _currentRelatedVideos
                    : null;
                if (!ReferenceEquals(RelatedVideosContainerVertical.ItemsSource, portraitItems))
                    RelatedVideosContainerVertical.ItemsSource = portraitItems;
                RelatedVideosContainerVertical.Visibility = isPortrait && hasVideos
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (RelatedVideosFallback != null)
                RelatedVideosFallback.Visibility = !isPortrait && _relatedFallbackVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (RelatedVideosFallbackVertical != null)
                RelatedVideosFallbackVertical.Visibility = isPortrait && _relatedFallbackVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

    }
}
