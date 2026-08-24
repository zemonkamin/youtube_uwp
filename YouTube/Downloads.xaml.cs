using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Downloads : Page
    {
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);
        private IList<DownloadedVideoItem> _visibleItems = new List<DownloadedVideoItem>();

        public Downloads()
        {
            InitializeComponent();
            Loaded += Downloads_Loaded;
            Unloaded += Downloads_Unloaded;
            PageTitleText.Text = Localization.GetString("Downloads");
            EmptyTitleText.Text = Localization.GetString("DownloadsEmptyTitle");
            EmptyDescriptionText.Text = Localization.GetString("DownloadsEmptyDescription");
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (tabbar != null) tabbar.SetActiveTab(Tabbar.ActiveTab.None);
        }

        private async void Downloads_Loaded(object sender, RoutedEventArgs e)
        {
            DownloadManager.Changed -= DownloadManager_Changed;
            DownloadManager.Changed += DownloadManager_Changed;
            SystemNavigationManager.GetForCurrentView().BackRequested -= Downloads_BackRequested;
            SystemNavigationManager.GetForCurrentView().BackRequested += Downloads_BackRequested;
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            await RefreshAsync();
            UpdateResponsiveCardLayouts();
        }

        private void Downloads_Unloaded(object sender, RoutedEventArgs e)
        {
            DownloadManager.Changed -= DownloadManager_Changed;
            SystemNavigationManager.GetForCurrentView().BackRequested -= Downloads_BackRequested;
            Window.Current.SizeChanged -= Window_SizeChanged;
        }

        private async void DownloadManager_Changed(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () => await RefreshAsync());
        }

        private async System.Threading.Tasks.Task RefreshAsync()
        {
            var items = await DownloadManager.GetItemsAsync();
            items = items ?? new List<DownloadedVideoItem>();
            var sameItems = _visibleItems.Count == items.Count;
            if (sameItems)
            {
                for (var i = 0; i < items.Count; i++)
                {
                    if (!ReferenceEquals(_visibleItems[i], items[i]))
                    {
                        sameItems = false;
                        break;
                    }
                }
            }
            if (!sameItems)
            {
                _visibleItems = items.ToList();
                DownloadsList.ItemsSource = _visibleItems;
            }
            else
            {
                UpdateDownloadProgressVisuals(DownloadsList);
            }
            var empty = items == null || items.Count == 0;
            EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            DownloadsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            if (!empty)
            {
                UpdateResponsiveCardLayouts();
                foreach (var item in items)
                {
                    if (item != null && string.IsNullOrWhiteSpace(item.ThumbnailFileToken))
                    {
                        var ignoredMigration = DownloadManager
                            .EnsureThumbnailBesideVideoAsync(item);
                    }
                }
            }
        }

        private async void Thumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as DownloadedVideoItem;
            if (image == null || item == null) return;
            var loadToken = new object();
            image.Tag = loadToken;
            image.Stretch = Stretch.UniformToFill;

            var localFile = await DownloadManager.GetThumbnailFileAsync(item);
            if (!ReferenceEquals(image.Tag, loadToken)) return;
            if (localFile != null)
            {
                try
                {
                    using (var stream = await localFile.OpenReadAsync())
                    {
                        var bitmap = new BitmapImage
                        {
                            DecodePixelType = DecodePixelType.Logical,
                            DecodePixelWidth = 360
                        };
                        await bitmap.SetSourceAsync(stream);
                        if (ReferenceEquals(image.Tag, loadToken)) image.Source = bitmap;
                        return;
                    }
                }
                catch
                {
                }
            }

            var source = !string.IsNullOrWhiteSpace(item.LocalThumbnailUri)
                ? item.LocalThumbnailUri : item.ThumbnailUrl;
            VideoThumbnailController.Assign(image, item.VideoId, source, 360);
        }

        private void ChannelIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as DownloadedVideoItem;
            if (image == null || item == null)
            {
                ChannelIconController.Assign(image, string.Empty);
                return;
            }
            var source = !string.IsNullOrWhiteSpace(item.LocalChannelThumbnailUri)
                ? item.LocalChannelThumbnailUri : item.ChannelThumbnailUrl;
            ChannelIconController.Assign(image, source);
        }

        private void ChannelIcon_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var item = image == null ? null : image.DataContext as DownloadedVideoItem;
            if (item == null)
            {
                ChannelIconController.Assign(image, string.Empty);
                return;
            }
            var source = !string.IsNullOrWhiteSpace(item.LocalChannelThumbnailUri)
                ? item.LocalChannelThumbnailUri : item.ChannelThumbnailUrl;
            ChannelIconController.Assign(image, source);
        }

        private void ThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null || e.NewSize.Width <= 0) return;
            host.Height = Math.Round(e.NewSize.Width / VideoThumbnailAspectRatio);
        }

        private void DownloadProgressOverlay_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateDownloadProgressVisual(sender as FrameworkElement);
        }

        private void DownloadProgressOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateDownloadProgressVisual(sender as FrameworkElement);
        }

        private void DownloadProgressOverlay_DataContextChanged(
            FrameworkElement sender,
            DataContextChangedEventArgs args)
        {
            UpdateDownloadProgressVisual(sender);
        }

        private static void UpdateDownloadProgressVisual(FrameworkElement overlay)
        {
            if (overlay == null) return;
            var item = overlay.DataContext as DownloadedVideoItem;
            var active = item != null && (item.IsDownloading || item.IsFinalizing);
            overlay.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active) return;

            var percent = Math.Max(0, Math.Min(100, item.ProgressPercent));
            var shade = overlay.FindName("DownloadRemainingShade") as FrameworkElement;
            if (shade != null)
                shade.Width = Math.Max(0, overlay.ActualWidth * (100.0 - percent) / 100.0);
            var text = overlay.FindName("DownloadProgressPercent") as TextBlock;
            if (text != null) text.Text = percent.ToString() + "%";
            ToolTipService.SetToolTip(overlay, Localization.GetString("Cancel"));
        }

        private static void UpdateDownloadProgressVisuals(DependencyObject root)
        {
            if (root == null) return;
            var element = root as FrameworkElement;
            if (element != null && string.Equals(
                element.Name, "DownloadProgressOverlay", StringComparison.Ordinal))
                UpdateDownloadProgressVisual(element);
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                UpdateDownloadProgressVisuals(VisualTreeHelper.GetChild(root, i));
        }

        private async void CancelDownload_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.DataContext as DownloadedVideoItem;
            if (item == null) return;
            button.IsEnabled = false;
            await DownloadManager.CancelAsync(item);
        }

        private void ResponsiveCard_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveCardLayout(sender as FrameworkElement);
        }

        private void ResponsiveCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveCardLayout(sender as FrameworkElement);
        }

        private void DownloadsList_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private static bool IsPortraitOrientation()
        {
            return Window.Current.Bounds.Height > Window.Current.Bounds.Width;
        }

        private void ApplyResponsiveCardLayout(FrameworkElement element)
        {
            if (element == null) return;
            var portrait = IsPortraitOrientation();
            element.Margin = portrait ? PortraitCardMargin : LandscapeCardMargin;
            VideoCardController.ApplyResponsiveLayout(element, portrait);
        }

        private void UpdateResponsiveCardLayouts()
        {
            if (DownloadsList == null) return;
            var portrait = IsPortraitOrientation();
            DownloadsList.Padding = portrait
                ? new Thickness(0, 8, 0, 16)
                : new Thickness(8, 8, 8, 16);

            var width = DownloadsList.ActualWidth > 0
                ? DownloadsList.ActualWidth : Window.Current.Bounds.Width;
            width -= DownloadsList.Padding.Left + DownloadsList.Padding.Right;
            var wrapGrid = FindDescendant<ItemsWrapGrid>(DownloadsList);
            if (wrapGrid != null)
            {
                wrapGrid.ItemWidth = portrait ? Math.Max(0, width) : DefaultCardWidth;
                wrapGrid.MaximumRowsOrColumns = portrait ? 1 : 3;
            }
            UpdateResponsiveCards(DownloadsList);
        }

        private void UpdateResponsiveCards(DependencyObject root)
        {
            if (root == null) return;
            var element = root as FrameworkElement;
            if (element != null && string.Equals(element.Tag as string, "ResponsiveCard",
                StringComparison.Ordinal)) ApplyResponsiveCardLayout(element);
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                UpdateResponsiveCards(VisualTreeHelper.GetChild(root, i));
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null) return null;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typed = child as T;
                if (typed != null) return typed;
                var nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void DownloadCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.DataContext as DownloadedVideoItem;
            if (item != null && item.IsComplete)
                Frame.Navigate(typeof(Video), new OfflineVideoNavigationArgs { Item = item });
        }

        private void Downloads_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }
    }
}
