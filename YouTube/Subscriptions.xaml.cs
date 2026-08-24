using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    // Build fix: XAML SizeChanged handler for VideosItemsControl and VideosSkeletonCardsList is defined below.
    public sealed partial class Subscriptions : Page
    {
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";
        private const int GeneralSubscriptionsFeedCount = 50;
        private const int SubscriptionsPageSize = 30;
        private const int LoadMoreAttemptThrottleMs = 600;
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);

        private ObservableCollection<VideoCardItem> subscriptionVideos;
        private List<SubscriptionChannel> subscribedChannels;
        private double _placeholderCardAspectRatio = 0.82;
        private bool _isLoadingMore;
        private bool _isGeneralFeedActive = true;
        private bool _subscriptionsReachedEnd;
        private string _subscriptionsContinuationToken = string.Empty;
        private DateTime _lastLoadMoreAttemptUtc = DateTime.MinValue;

        public Subscriptions()
        {
            this.InitializeComponent();
            subscriptionVideos = new ObservableCollection<VideoCardItem>();
            subscribedChannels = new List<SubscriptionChannel>();

            InitializePlaceholderCards();
            this.Loaded += Page_Loaded;
            this.Unloaded += Page_Unloaded;
            Window.Current.SizeChanged += Window_SizeChanged;
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            tabbar?.SetActiveTab(Tabbar.ActiveTab.Subscriptions);
            await LoadSubscriptionsAsync();
        }

        private async Task LoadSubscriptionsAsync()
        {
            try
            {
                LoadingRing.IsActive = true;
                LoadingRing.Visibility = Visibility.Visible;

                Config.LoadUserToken();
                if (string.IsNullOrEmpty(Config.UserToken))
                {
                    LoadingRing.IsActive = false;
                    LoadingRing.Visibility = Visibility.Collapsed;
                    return;
                }

                subscribedChannels = await Config.GetSubscribedChannelsAsync(Config.UserToken);

                if (subscribedChannels != null && subscribedChannels.Count > 0)
                {
                    SubscriptionsPanel.Children.Clear();

                    foreach (var channel in subscribedChannels)
                    {
                        var subscriptionButton = new Button();
                        subscriptionButton.Style = (Style)this.Resources["ChannelButtonStyle"];
                        subscriptionButton.Width = 100;
                        subscriptionButton.Margin = new Thickness(0, 0, 12, 0);

                        var stackPanel = new StackPanel();
                        stackPanel.Width = 100;
                        stackPanel.HorizontalAlignment = HorizontalAlignment.Center;

                        var imageGrid = new Grid();
                        imageGrid.Width = 80;
                        imageGrid.Height = 80;
                        imageGrid.HorizontalAlignment = HorizontalAlignment.Center;

                        var thumbnailImage = new Image();
                        thumbnailImage.Width = 80;
                        thumbnailImage.Height = 80;
                        thumbnailImage.Stretch = Stretch.UniformToFill;
                        thumbnailImage.Source = new BitmapImage(new Uri(channel.ThumbnailUrl));
                        imageGrid.Children.Add(thumbnailImage);

                        var roundingOverlay = new Image();
                        roundingOverlay.Width = 80;
                        roundingOverlay.Height = 80;
                        roundingOverlay.Stretch = Stretch.Fill;
                        App.SetThemeImageSource(roundingOverlay, "Assets/rounding.png");
                        imageGrid.Children.Add(roundingOverlay);

                        var titleText = new TextBlock();
                        titleText.Text = channel.ChannelName;
                        titleText.Foreground = App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White);
                        titleText.FontSize = 12;
                        titleText.TextWrapping = TextWrapping.NoWrap;
                        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
                        titleText.TextAlignment = TextAlignment.Center;
                        titleText.MaxLines = 1;
                        titleText.Margin = new Thickness(0, 8, 0, 0);

                        stackPanel.Children.Add(imageGrid);
                        stackPanel.Children.Add(titleText);
                        subscriptionButton.Content = stackPanel;

                        subscriptionButton.Tag = channel;
                        subscriptionButton.Click += SubscriptionButton_Click;

                        SubscriptionsPanel.Children.Add(subscriptionButton);
                    }

                    await LoadAllSubscriptionsVideosAsync();
                }
                else
                {
                    VideosItemsControl.ItemsSource = new ObservableCollection<VideoCardItem>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Error loading subscriptions: {ex.Message}");
            }
            finally
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private async Task LoadAllSubscriptionsVideosAsync()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("[Subscriptions] Loading general subscriptions feed");

                ShowVideoPlaceholders();

                _isGeneralFeedActive = true;
                _subscriptionsReachedEnd = false;
                _subscriptionsContinuationToken = string.Empty;
                HideBottomLoadingIndicator();

                var page = await Config.GetSubscriptionsFeedPageAsync(
                    Config.UserToken,
                    null,
                    GeneralSubscriptionsFeedCount);
                var videos = page != null ? page.Videos : null;
                _subscriptionsContinuationToken = page != null
                    ? (page.ContinuationToken ?? string.Empty)
                    : string.Empty;
                _subscriptionsReachedEnd = string.IsNullOrWhiteSpace(_subscriptionsContinuationToken);
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Got {videos?.Count ?? 0} videos from general subscriptions feed");

                ApplySubscribedChannelAvatars(videos);
                SetVideosSource(videos);
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] General feed ItemsSource set with {subscriptionVideos.Count} items");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Error loading general subscriptions feed: {ex.Message}");
                VideosItemsControl.ItemsSource = new ObservableCollection<VideoCardItem>();
            }
            finally
            {
                HideVideoPlaceholders();
            }
        }

        private async void SubscriptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var channel = button?.Tag as SubscriptionChannel;

            if (channel != null && !string.IsNullOrEmpty(channel.ChannelId))
            {
                await LoadChannelVideosAsync(channel);
            }
        }

        private async Task LoadChannelVideosAsync(SubscriptionChannel channel)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Loading videos for: {channel.ChannelName}");

                ShowVideoPlaceholders();

                // A channel card switches the page from the combined subscriptions feed to a
                // channel-specific list. Do not append the combined feed when it reaches bottom.
                _isGeneralFeedActive = false;
                _subscriptionsReachedEnd = true;
                _subscriptionsContinuationToken = string.Empty;
                HideBottomLoadingIndicator();

                var videos = await Config.GetChannelVideosAsync(channel.ChannelId);
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Got {videos?.Count ?? 0} videos for {channel.ChannelName}");

                ApplyKnownChannelAvatar(videos, channel);
                SetVideosSource(videos);
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] VideosItemsControl.ItemsSource set with {subscriptionVideos.Count} items");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Error loading channel videos: {ex.Message}");
                VideosItemsControl.ItemsSource = new ObservableCollection<VideoCardItem>();
            }
            finally
            {
                HideVideoPlaceholders();
            }
        }

        private async void MainScrollViewer_ViewChanged(
            object sender,
            ScrollViewerViewChangedEventArgs e)
        {
            if (_isLoadingMore || !_isGeneralFeedActive || _subscriptionsReachedEnd)
            {
                return;
            }

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 100)
            {
                await LoadMoreSubscriptionsAsync();
            }
        }

        private async Task LoadMoreSubscriptionsAsync()
        {
            if (_isLoadingMore
                || !_isGeneralFeedActive
                || _subscriptionsReachedEnd
                || string.IsNullOrWhiteSpace(_subscriptionsContinuationToken))
            {
                return;
            }

            Config.LoadUserToken();
            if (string.IsNullOrWhiteSpace(Config.UserToken))
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - _lastLoadMoreAttemptUtc).TotalMilliseconds < LoadMoreAttemptThrottleMs)
            {
                return;
            }
            _lastLoadMoreAttemptUtc = nowUtc;

            _isLoadingMore = true;
            ShowBottomLoadingIndicator();

            try
            {
                var tokenUsed = _subscriptionsContinuationToken;
                var page = await Config.GetSubscriptionsFeedPageAsync(
                    Config.UserToken,
                    tokenUsed,
                    SubscriptionsPageSize);

                var nextToken = page != null
                    ? (page.ContinuationToken ?? string.Empty)
                    : string.Empty;
                _subscriptionsContinuationToken = nextToken;
                _subscriptionsReachedEnd = string.IsNullOrWhiteSpace(nextToken)
                    || string.Equals(nextToken, tokenUsed, StringComparison.Ordinal);

                var moreVideos = page != null ? page.Videos : null;
                ApplySubscribedChannelAvatars(moreVideos);
                var added = AppendUniqueSubscriptionVideos(moreVideos);

                System.Diagnostics.Debug.WriteLine(
                    "[Subscriptions] Continuation page added " + added
                    + ", total=" + subscriptionVideos.Count
                    + ", hasNext=" + (!_subscriptionsReachedEnd));

                ScheduleResponsiveCardLayoutUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Subscriptions] Load more error: " + ex.Message);
            }
            finally
            {
                _isLoadingMore = false;
                HideBottomLoadingIndicator();
            }
        }

        private int AppendUniqueSubscriptionVideos(IEnumerable<VideoCardItem> videos)
        {
            if (videos == null)
            {
                return 0;
            }

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in subscriptionVideos)
            {
                if (existing != null && !string.IsNullOrWhiteSpace(existing.VideoId))
                {
                    existingIds.Add(existing.VideoId);
                }
            }

            var added = 0;
            foreach (var video in videos)
            {
                if (video == null || string.IsNullOrWhiteSpace(video.VideoId))
                {
                    continue;
                }

                if (existingIds.Add(video.VideoId))
                {
                    subscriptionVideos.Add(video);
                    added++;
                }
            }

            return added;
        }

        private void ShowBottomLoadingIndicator()
        {
            if (BottomLoadingPanel != null)
            {
                BottomLoadingPanel.Visibility = Visibility.Visible;
            }
            if (BottomLoadingRing != null)
            {
                BottomLoadingRing.IsActive = true;
            }
        }

        private void HideBottomLoadingIndicator()
        {
            if (BottomLoadingPanel != null)
            {
                BottomLoadingPanel.Visibility = Visibility.Collapsed;
            }
            if (BottomLoadingRing != null)
            {
                BottomLoadingRing.IsActive = false;
            }
        }

        private void ApplySubscribedChannelAvatars(List<VideoCardItem> videos)
        {
            if (videos == null || subscribedChannels == null || subscribedChannels.Count == 0)
                return;

            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                if (video == null || !string.IsNullOrWhiteSpace(video.ChannelThumbnailUrl))
                    continue;

                for (var j = 0; j < subscribedChannels.Count; j++)
                {
                    var channel = subscribedChannels[j];
                    if (channel == null || string.IsNullOrWhiteSpace(channel.ThumbnailUrl))
                        continue;

                    var idMatches = !string.IsNullOrWhiteSpace(video.ChannelId)
                        && !string.IsNullOrWhiteSpace(channel.ChannelId)
                        && string.Equals(video.ChannelId.Trim(), channel.ChannelId.Trim(), StringComparison.OrdinalIgnoreCase);
                    var titleMatches = !string.IsNullOrWhiteSpace(video.ChannelTitle)
                        && !string.IsNullOrWhiteSpace(channel.ChannelName)
                        && string.Equals(video.ChannelTitle.Trim(), channel.ChannelName.Trim(), StringComparison.OrdinalIgnoreCase);

                    if (!idMatches && !titleMatches)
                        continue;

                    video.ChannelThumbnailUrl = channel.ThumbnailUrl;
                    if (string.IsNullOrWhiteSpace(video.ChannelId))
                        video.ChannelId = channel.ChannelId;
                    break;
                }
            }
        }

        private static void ApplyKnownChannelAvatar(List<VideoCardItem> videos, SubscriptionChannel channel)
        {
            if (videos == null || channel == null)
                return;

            for (var i = 0; i < videos.Count; i++)
            {
                var video = videos[i];
                if (video == null)
                    continue;

                if (string.IsNullOrWhiteSpace(video.ChannelId))
                    video.ChannelId = channel.ChannelId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(video.ChannelTitle))
                    video.ChannelTitle = channel.ChannelName ?? string.Empty;
                if (string.IsNullOrWhiteSpace(video.ChannelThumbnailUrl))
                    video.ChannelThumbnailUrl = channel.ThumbnailUrl ?? string.Empty;
            }
        }

        private void SetVideosSource(List<VideoCardItem> videos)
        {
            subscriptionVideos.Clear();

            if (videos != null && videos.Count > 0)
            {
                foreach (var video in videos)
                {
                    if (video != null)
                    {
                        subscriptionVideos.Add(video);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Subscriptions] Added {subscriptionVideos.Count} videos to collection");
            }

            VideosItemsControl.ItemsSource = null;
            VideosItemsControl.ItemsSource = subscriptionVideos;
            ScheduleResponsiveCardLayoutUpdate();
        }

        private void ShowVideoPlaceholders()
        {
            InitializePlaceholderCards();

            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            if (VideosSkeletonLoader != null)
                VideosSkeletonLoader.Visibility = Visibility.Visible;

            if (VideosItemsControl != null)
                VideosItemsControl.Visibility = Visibility.Collapsed;

            ScheduleResponsiveCardLayoutUpdate();
        }

        private void HideVideoPlaceholders()
        {
            LoadingRing.IsActive = false;
            LoadingRing.Visibility = Visibility.Collapsed;

            if (VideosSkeletonLoader != null)
                VideosSkeletonLoader.Visibility = Visibility.Collapsed;

            if (VideosItemsControl != null)
                VideosItemsControl.Visibility = Visibility.Visible;

            ScheduleResponsiveCardLayoutUpdate();
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            var item = button.Tag as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId)) return;

            // Carry a mix / "jam" playlist so the video page can show its queue.
            if (!string.IsNullOrWhiteSpace(item.PlaylistId))
            {
                Frame.Navigate(typeof(Video), new VideoNavigationArgs
                {
                    VideoId = item.VideoId,
                    PlaylistId = item.PlaylistId
                });
            }
            else
            {
                Frame.Navigate(typeof(Video), item.VideoId);
            }
        }

        private void InitializePlaceholderCards()
        {
            if (VideosSkeletonCardsList != null)
            {
                VideosSkeletonCardsList.ItemsSource = new int[] { 1, 2, 3, 4, 5 };
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

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            ScheduleResponsiveCardLayoutUpdate();
        }

        public void CardsItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
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

            VideoThumbnailController.Assign(image, item.VideoId, item.ThumbnailUrl, 360);
        }

        private void ChannelIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as VideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.ChannelThumbnailUrl);
        }

        private void ChannelIcon_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var item = image == null ? null : image.DataContext as VideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.ChannelThumbnailUrl);
        }

        private void VideoThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateHostHeight(sender as FrameworkElement, e.NewSize.Width, 1.0 / VideoThumbnailAspectRatio);
        }

        private void PlaceholderCardHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            ApplyResponsiveCardMargin(host);
            UpdateHostHeight(host, e.NewSize.Width, _placeholderCardAspectRatio);
        }

        private void PlaceholderCard_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                _placeholderCardAspectRatio = (double)bitmap.PixelHeight / bitmap.PixelWidth;
            }

            var host = image.Parent as FrameworkElement;
            ApplyResponsiveCardMargin(host);
            UpdateHostHeight(host, host != null ? host.ActualWidth : 0, _placeholderCardAspectRatio);
        }

        private static void UpdateHostHeight(FrameworkElement host, double width, double heightRatio)
        {
            if (host == null)
                return;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = host.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var targetHeight = System.Math.Round(width * heightRatio);
            if (double.IsNaN(host.Height) || System.Math.Abs(host.Height - targetHeight) > 0.5)
                host.Height = targetHeight;
        }

        private void ScheduleResponsiveCardLayoutUpdate()
        {
            UpdateResponsiveCardLayouts();
            var ignored = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => UpdateResponsiveCardLayouts()
            );
        }

        private void ResponsiveCard_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ResponsiveCard_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyResponsiveCardMargin(sender as FrameworkElement);
        }

        private void ApplyResponsiveCardMargin(FrameworkElement element)
        {
            if (element == null)
            {
                return;
            }

            var targetMargin = IsPortraitOrientation()
                ? PortraitCardMargin
                : LandscapeCardMargin;

            if (Math.Abs(element.Margin.Left - targetMargin.Left) > 0.5 ||
                Math.Abs(element.Margin.Top - targetMargin.Top) > 0.5 ||
                Math.Abs(element.Margin.Right - targetMargin.Right) > 0.5 ||
                Math.Abs(element.Margin.Bottom - targetMargin.Bottom) > 0.5)
            {
                element.Margin = targetMargin;
            }

            VideoCardController.ApplyResponsiveLayout(element, IsPortraitOrientation());
        }

        private void UpdateResponsiveCardMargins(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }

            var element = root as FrameworkElement;
            if (element != null && IsResponsiveCardElement(element))
            {
                ApplyResponsiveCardMargin(element);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                UpdateResponsiveCardMargins(VisualTreeHelper.GetChild(root, i));
            }
        }

        private static bool IsResponsiveCardElement(FrameworkElement element)
        {
            if (element == null)
            {
                return false;
            }

            if (element is Button)
            {
                return true;
            }

            return string.Equals(element.Tag as string, ResponsiveCardTag, StringComparison.Ordinal);
        }

        private static bool IsPortraitOrientation()
        {
            return Window.Current.Bounds.Height > Window.Current.Bounds.Width;
        }

        private void UpdateResponsiveCardLayouts()
        {
            UpdateItemsWrapGrid(VideosItemsControl);
            UpdateItemsWrapGrid(VideosSkeletonCardsList);
            UpdateResponsiveCardMargins(VideosItemsControl);
            UpdateResponsiveCardMargins(VideosSkeletonCardsList);
        }

        private static void UpdateItemsWrapGrid(DependencyObject root)
        {
            if (root == null)
                return;

            var wrapGrid = FindDescendant<ItemsWrapGrid>(root);
            if (wrapGrid == null)
                return;

            bool isPortrait = IsPortraitOrientation();
            double itemWidth = DefaultCardWidth;
            int maxColumns = 3;

            var control = root as Control;
            if (control != null)
            {
                control.Padding = isPortrait
                    ? new Thickness(0, control.Padding.Top, 0, control.Padding.Bottom)
                    : new Thickness(8, control.Padding.Top, 8, control.Padding.Bottom);
            }

            if (isPortrait)
            {
                var rootElement = root as FrameworkElement;
                var availableWidth = rootElement != null && rootElement.ActualWidth > 0
                    ? rootElement.ActualWidth
                    : Window.Current.Bounds.Width;

                if (control != null)
                    availableWidth -= control.Padding.Left + control.Padding.Right;

                itemWidth = System.Math.Max(0, availableWidth);
                maxColumns = 1;
            }

            wrapGrid.ItemWidth = itemWidth;
            wrapGrid.MaximumRowsOrColumns = maxColumns;
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
