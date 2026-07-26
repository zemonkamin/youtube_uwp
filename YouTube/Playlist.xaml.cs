using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Playlist : Page
    {
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";

        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);

        private readonly ObservableCollection<VideoCardItem> _videos = new ObservableCollection<VideoCardItem>();
        private Frame _frame;
        private bool _systemBackRegistered;
        private string _playlistId = string.Empty;
        private string _playlistContinuationToken = string.Empty;
        private bool _isLoadingMoreVideos;
        private bool _hasMoreVideos;
        private readonly HashSet<string> _loadedVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private PlaylistItem _seedItem;

        public Playlist()
        {
            this.InitializeComponent();
            _frame = Window.Current.Content as Frame;
            VideosItemsControl.ItemsSource = _videos;

            this.Loaded += Page_Loaded;
            this.Unloaded += Page_Unloaded;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RegisterSystemBackButton();
            ScheduleResponsiveCardLayoutUpdate();

            if (tabbar != null)
                tabbar.SetActiveTab(Tabbar.ActiveTab.Account);

            _seedItem = e.Parameter as PlaylistItem;
            if (_seedItem != null)
            {
                _playlistId = _seedItem.PlaylistId;
                ApplySeed(_seedItem);
            }
            else
            {
                _playlistId = e.Parameter as string;
            }

            if (string.IsNullOrWhiteSpace(_playlistId))
            {
                ShowError("Playlist id is empty");
                return;
            }

            await LoadPlaylistAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            UnregisterSystemBackButton();
        }

        private void ApplySeed(PlaylistItem item)
        {
            if (item == null)
                return;

            if (!string.IsNullOrWhiteSpace(item.Title))
                PlaylistTitleText.Text = item.Title;

            if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
                SetBorderImage(PlaylistHeaderImageBorder, item.ThumbnailUrl);

            var meta = BuildSeedMeta(item);
            PlaylistMetaText.Text = meta;
            PlaylistMetaText.Visibility = string.IsNullOrWhiteSpace(meta) ? Visibility.Collapsed : Visibility.Visible;

            if (OwnerRow != null)
                OwnerRow.Visibility = Visibility.Collapsed;
        }

        private static string BuildSeedMeta(PlaylistItem item)
        {
            if (item == null)
                return string.Empty;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.PrivacyText) && item.PrivacyText != "Playlist")
                parts.Add(item.PrivacyText);
            if (!string.IsNullOrWhiteSpace(item.VideoCountText))
                parts.Add(item.VideoCountText.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0 ? item.VideoCountText : item.VideoCountText + " videos");

            return string.Join(" • ", parts.ToArray());
        }

        private async Task LoadPlaylistAsync()
        {
            ShowLoading(true);
            _isLoadingMoreVideos = false;
            _hasMoreVideos = false;
            _playlistContinuationToken = string.Empty;
            SetLoadingMoreVisible(false);

            try
            {
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;
                var details = await Config.GetPlaylistDetailsAsync(_playlistId, refreshToken, 80);

                if (details == null)
                {
                    ShowError("Could not load playlist");
                    return;
                }

                ApplyDetails(details);
                ShowContent();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playlist] Load error: " + ex.Message);
                ShowError("Could not load playlist");
            }
            finally
            {
                ShowLoading(false);
            }
        }

        private void ApplyDetails(PlaylistDetails details)
        {
            if (!string.IsNullOrWhiteSpace(details.Title) && !string.Equals(details.Title, "Playlist", StringComparison.OrdinalIgnoreCase))
                PlaylistTitleText.Text = details.Title;

            if (!string.IsNullOrWhiteSpace(details.ThumbnailUrl))
                SetBorderImage(PlaylistHeaderImageBorder, details.ThumbnailUrl);

            var hasOwnerName = !string.IsNullOrWhiteSpace(details.OwnerName);
            var hasOwnerAvatar = !string.IsNullOrWhiteSpace(details.OwnerThumbnailUrl);

            if (hasOwnerName)
            {
                OwnerText.Text = details.OwnerName;
                OwnerText.Visibility = Visibility.Visible;
            }
            else
            {
                OwnerText.Text = string.Empty;
                OwnerText.Visibility = Visibility.Collapsed;
            }

            if (hasOwnerAvatar)
            {
                SetEllipseImage(OwnerAvatarEllipse, details.OwnerThumbnailUrl);
                OwnerAvatarEllipse.Visibility = Visibility.Visible;
                OwnerText.Margin = hasOwnerName ? new Thickness(10, 0, 0, 0) : new Thickness(0);
            }
            else
            {
                OwnerAvatarEllipse.Visibility = Visibility.Collapsed;
                OwnerText.Margin = new Thickness(0);
            }

            OwnerRow.Visibility = (hasOwnerName || hasOwnerAvatar) ? Visibility.Visible : Visibility.Collapsed;

            PlaylistMetaText.Text = details.MetadataText ?? string.Empty;
            PlaylistMetaText.Visibility = string.IsNullOrWhiteSpace(PlaylistMetaText.Text) ? Visibility.Collapsed : Visibility.Visible;

            PlaylistDescriptionText.Text = details.Description ?? string.Empty;
            PlaylistDescriptionText.Visibility = string.IsNullOrWhiteSpace(PlaylistDescriptionText.Text) ? Visibility.Collapsed : Visibility.Visible;

            _videos.Clear();
            _loadedVideoIds.Clear();
            AppendVideos(details.Videos);

            _playlistContinuationToken = details.ContinuationToken ?? string.Empty;
            _hasMoreVideos = !string.IsNullOrWhiteSpace(_playlistContinuationToken);
            SetLoadingMoreVisible(false);

            EmptyText.Visibility = _videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ScheduleResponsiveCardLayoutUpdate();
        }

        private void AppendVideos(IEnumerable<VideoCardItem> videos)
        {
            if (videos == null)
            {
                return;
            }

            foreach (var video in videos)
            {
                if (video == null || string.IsNullOrWhiteSpace(video.VideoId))
                {
                    continue;
                }

                if (_loadedVideoIds.Add(video.VideoId))
                {
                    _videos.Add(video);
                }
            }
        }

        private async Task LoadMoreVideosAsync()
        {
            if (_isLoadingMoreVideos || !_hasMoreVideos || string.IsNullOrWhiteSpace(_playlistContinuationToken))
            {
                return;
            }

            _isLoadingMoreVideos = true;
            SetLoadingMoreVisible(true);

            try
            {
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;
                var oldToken = _playlistContinuationToken;
                var details = await Config.GetPlaylistContinuationAsync(refreshToken, oldToken, 50);

                if (details == null)
                {
                    _hasMoreVideos = false;
                    _playlistContinuationToken = string.Empty;
                    return;
                }

                var beforeCount = _videos.Count;
                AppendVideos(details.Videos);
                _playlistContinuationToken = details.ContinuationToken ?? string.Empty;
                _hasMoreVideos = !string.IsNullOrWhiteSpace(_playlistContinuationToken);

                EmptyText.Visibility = _videos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ScheduleResponsiveCardLayoutUpdate();

                if (_videos.Count == beforeCount)
                {
                    _hasMoreVideos = false;
                    _playlistContinuationToken = string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Playlist] Load more error: " + ex.Message);
            }
            finally
            {
                _isLoadingMoreVideos = false;
                SetLoadingMoreVisible(false);
            }
        }

        private void SetLoadingMoreVisible(bool visible)
        {
            if (LoadingMorePanel != null)
            {
                LoadingMorePanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }

            if (LoadingMoreRing != null)
            {
                LoadingMoreRing.IsActive = visible;
            }
        }

        private static void SetBorderImage(Border border, string url)
        {
            if (border == null || string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                border.Background = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(url)),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
            }
        }

        private static void SetEllipseImage(Windows.UI.Xaml.Shapes.Ellipse ellipse, string url)
        {
            if (ellipse == null || string.IsNullOrWhiteSpace(url))
                return;

            try
            {
                ellipse.Fill = new ImageBrush
                {
                    ImageSource = new BitmapImage(new Uri(url)),
                    Stretch = Stretch.UniformToFill
                };
            }
            catch
            {
            }
        }

        private void ShowLoading(bool isLoading)
        {
            if (LoadingRing != null)
                LoadingRing.IsActive = isLoading;

            if (LoadingGrid != null)
                LoadingGrid.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowContent()
        {
            if (ErrorPanel != null)
                ErrorPanel.Visibility = Visibility.Collapsed;
            if (MainContent != null)
                MainContent.Visibility = Visibility.Visible;

            ScheduleResponsiveCardLayoutUpdate();
        }

        private void ShowError(string message)
        {
            if (ErrorText != null)
                ErrorText.Text = string.IsNullOrWhiteSpace(message) ? "Could not load playlist" : message;
            if (MainContent != null)
                MainContent.Visibility = Visibility.Collapsed;
            if (LoadingGrid != null)
                LoadingGrid.Visibility = Visibility.Collapsed;
            if (ErrorPanel != null)
                ErrorPanel.Visibility = Visibility.Visible;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadPlaylistAsync();
        }

        private async void MainContent_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e != null && e.IsIntermediate)
            {
                return;
            }

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null)
            {
                return;
            }

            var distanceToBottom = scrollViewer.ScrollableHeight - scrollViewer.VerticalOffset;
            if (distanceToBottom <= 500)
            {
                await LoadMoreVideosAsync();
            }
        }

        private void RegisterSystemBackButton()
        {
            try
            {
                var nav = SystemNavigationManager.GetForCurrentView();
                nav.AppViewBackButtonVisibility = (_frame != null && _frame.CanGoBack)
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;

                if (!_systemBackRegistered)
                {
                    nav.BackRequested += SystemBackButton_BackRequested;
                    _systemBackRegistered = true;
                }
            }
            catch
            {
            }
        }

        private void UnregisterSystemBackButton()
        {
            try
            {
                if (_systemBackRegistered)
                {
                    SystemNavigationManager.GetForCurrentView().BackRequested -= SystemBackButton_BackRequested;
                    _systemBackRegistered = false;
                }
            }
            catch
            {
            }
        }

        private void SystemBackButton_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_frame != null && _frame.CanGoBack)
            {
                e.Handled = true;
                _frame.GoBack();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_frame != null)
                _frame.Navigate(typeof(Searching));
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;

            var item = button.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
                return;

            if (_frame != null)
            {
                // Carry the playlist context so the video page shows the queue and, for an
                // auto-generated mix ("jam"), keeps it going.
                _frame.Navigate(typeof(Video), new VideoNavigationArgs
                {
                    VideoId = item.VideoId,
                    PlaylistId = _playlistId,
                    PlaylistTitle = PlaylistTitleText != null ? PlaylistTitleText.Text : null
                });
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            ScheduleResponsiveCardLayoutUpdate();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            ScheduleResponsiveCardLayoutUpdate();
        }

        // Decoded at card width with a maxres->API-thumbnail fallback; see ThumbnailImageLoader
        // for why a Source binding was replaced by per-item assignment.
        private void CardThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            if (image == null)
            {
                return;
            }

            var item = image.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.LargeThumbnailUrl))
            {
                image.Source = null;
                return;
            }

            ThumbnailImageLoader.Assign(image, item.LargeThumbnailUrl, item.ThumbnailUrl, 360);
        }

        private void VideoThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null)
                return;

            var width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var targetHeight = Math.Round(width / VideoThumbnailAspectRatio);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
                host.Height = targetHeight;
        }

        private void VideosItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleResponsiveCardLayoutUpdate();
        }

        private void ResponsiveCard_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ResponsiveCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ScheduleResponsiveCardLayoutUpdate()
        {
            UpdateResponsiveCardLayouts();
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => UpdateResponsiveCardLayouts());
        }

        private void UpdateResponsiveCardLayouts()
        {
            bool isPortrait = IsPortraitOrientation();

            if (VideosItemsControl != null)
            {
                VideosItemsControl.Padding = isPortrait
                    ? new Thickness(0, 8, 0, 16)
                    : new Thickness(8, 8, 8, 16);
            }

            double baseWidth = GetItemsControlContentWidth(VideosItemsControl, Window.Current.Bounds.Width);
            var itemWidth = isPortrait ? Math.Max(0, baseWidth) : DefaultCardWidth;
            var maxColumns = isPortrait ? 1 : 3;

            UpdateItemsWrapGrid(VideosItemsControl, itemWidth, maxColumns);
            UpdateResponsiveCardMargins(VideosItemsControl);
        }

        private void ApplyResponsiveCardMargin(FrameworkElement element)
        {
            if (element == null)
                return;

            var targetMargin = IsPortraitOrientation() ? PortraitCardMargin : LandscapeCardMargin;
            if (Math.Abs(element.Margin.Left - targetMargin.Left) > 0.5 ||
                Math.Abs(element.Margin.Top - targetMargin.Top) > 0.5 ||
                Math.Abs(element.Margin.Right - targetMargin.Right) > 0.5 ||
                Math.Abs(element.Margin.Bottom - targetMargin.Bottom) > 0.5)
            {
                element.Margin = targetMargin;
            }
        }

        private void UpdateResponsiveCardMargins(DependencyObject root)
        {
            if (root == null)
                return;

            var element = root as FrameworkElement;
            if (element != null && IsResponsiveCardElement(element))
                ApplyResponsiveCardMargin(element);

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
                UpdateResponsiveCardMargins(VisualTreeHelper.GetChild(root, i));
        }

        private static bool IsResponsiveCardElement(FrameworkElement element)
        {
            if (element == null)
                return false;

            return string.Equals(element.Tag as string, ResponsiveCardTag, StringComparison.Ordinal);
        }

        private static bool IsPortraitOrientation()
        {
            return Window.Current.Bounds.Height > Window.Current.Bounds.Width;
        }

        private static double GetItemsControlContentWidth(Control control, double fallbackWidth)
        {
            double width = control != null && control.ActualWidth > 0
                ? control.ActualWidth
                : fallbackWidth;

            if (control != null)
                width -= control.Padding.Left + control.Padding.Right;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = fallbackWidth;

            return Math.Max(0, width);
        }

        private static void UpdateItemsWrapGrid(DependencyObject root, double itemWidth, int maxColumns)
        {
            if (root == null)
                return;

            var wrapGrid = FindDescendant<ItemsWrapGrid>(root);
            if (wrapGrid != null)
            {
                wrapGrid.ItemWidth = itemWidth;
                wrapGrid.MaximumRowsOrColumns = maxColumns;
            }
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
                return null;

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typedChild = child as T;
                if (typedChild != null)
                    return typedChild;

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                    return descendant;
            }

            return null;
        }
    }
}
