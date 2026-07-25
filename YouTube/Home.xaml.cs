using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Data.Xml.Dom;
using Windows.Networking.Connectivity;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Notifications;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace YouTube
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Home : Page
    {
        private ObservableCollection<VideoCardItem> recommendationVideos;
        private ObservableCollection<string> trendingSuggestions;
        private ObservableCollection<object> placeholderCards;
        private List<HomeCategoryItem> homeCategories;
        private HomeCategoryItem selectedCategory;

        private const int RecommendationPageSize = 12;
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);

        private bool isLoadingMore = false;
        private int recommendationRequestCount = RecommendationPageSize;
        private DateTime lastLoadMoreAttemptUtc = DateTime.MinValue;
        private const int LoadMoreAttemptThrottleMs = 600;

        private const int LiveTileRecommendationCount = 5;
        private const string LiveTileFolderName = "LiveTile";
        private const string LiveTileFallbackImage = "ms-appx:///Assets/Square150x150Logo.png";
        private static readonly HttpClient liveTileHttpClient = new HttpClient();
        private bool isUpdatingLiveTile = false;

        public Home()
        {
            this.InitializeComponent();
            recommendationVideos = new ObservableCollection<VideoCardItem>();
            placeholderCards = new ObservableCollection<object>();
            homeCategories = new List<HomeCategoryItem>();
            InitializePlaceholderCards();

            this.Loaded += Page_Loaded;
            this.Unloaded += Page_Unloaded;
            Window.Current.SizeChanged += Window_SizeChanged;

            trendingSuggestions = new ObservableCollection<string>
            {
                "Music videos",
                "Gaming highlights",
                "Cooking recipes",
                "Tech reviews",
                "Movie trailers",
                "Sports highlights",
                "Comedy sketches",
                "DIY tutorials"
            };
            TrendingSuggestionsList.ItemsSource = trendingSuggestions;
            if (SkeletonCardsList != null)
            {
                SkeletonCardsList.ItemsSource = placeholderCards;
            }

            if (InlineSkeletonCardsList != null)
            {
                InlineSkeletonCardsList.ItemsSource = placeholderCards;
            }

            if (RecommendationsList != null)
            {
                RecommendationsList.ItemsSource = recommendationVideos;
            }
        }

        /// <summary>
        /// Invoked when this page is about to be displayed in a Frame.
        /// </summary>
        /// <param name="e">Event data that describes how this page was reached.
        /// This parameter is typically used to configure the page.</param>
        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Set Home tab as active
            tabbar?.SetActiveTab(Tabbar.ActiveTab.Home);
            
            await LoadHomeDataAsync();
        }

        private async Task LoadHomeDataAsync()
        {
            try
            {
                if (!IsInternetAvailable())
                {
                    recommendationVideos.Clear();
                    ShowOfflineState(true);
                    ShowCategoryPlaceholders(false);
                    return;
                }

                ShowOfflineState(false);

                if (SkeletonLoader != null)
                {
                    SkeletonLoader.Visibility = Visibility.Visible;
                }

                SetHomeLoading(false);
                ShowInlineCardPlaceholders(false);
                ShowCategoryPlaceholders(true);

                if (BottomLoadingPanel != null)
                {
                    BottomLoadingPanel.Visibility = Visibility.Collapsed;
                }

                if (BottomLoadingRing != null)
                {
                    BottomLoadingRing.IsActive = false;
                }

                Config.LoadUserToken();

                recommendationRequestCount = RecommendationPageSize;
                recommendationVideos.Clear();

                if (string.IsNullOrEmpty(Config.UserToken))
                {
                    System.Diagnostics.Debug.WriteLine("[Home] No token, setting empty list");
                    homeCategories.Clear();
                    selectedCategory = null;
                    RenderCategoryChips();
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[Home] Loading categories and recommendations...");

                var categoriesTask = Config.GetHomeCategoriesAsync(Config.UserToken);
                var recommendationsTask = Config.GetRecommendationsAsync(Config.UserToken, recommendationRequestCount);

                var categories = await categoriesTask;
                homeCategories = categories != null ? categories : new List<HomeCategoryItem>();
                selectedCategory = FindAllCategory(homeCategories);
                RenderCategoryChips();

                var recommendations = await recommendationsTask;
                System.Diagnostics.Debug.WriteLine("[Home] Got " + (recommendations != null ? recommendations.Count.ToString() : "0") + " recommendations");

                var added = AppendUniqueRecommendations(recommendations);
                UpdateResponsiveCardLayouts();
                await UpdateLiveTileFromLoadedRecommendationsAsync();
                System.Diagnostics.Debug.WriteLine("[Home] Added " + added + " videos, total: " + recommendationVideos.Count);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Home] Exception: " + ex.Message);
                recommendationVideos.Clear();

                if (!IsInternetAvailable() || IsNetworkException(ex))
                {
                    ShowOfflineState(true);
                }

                if (homeCategories == null || homeCategories.Count == 0)
                {
                    homeCategories = new List<HomeCategoryItem>();
                    selectedCategory = null;
                    RenderCategoryChips();
                }
            }
            finally
            {
                if (SkeletonLoader != null)
                {
                    SkeletonLoader.Visibility = Visibility.Collapsed;
                }

                if (BottomLoadingPanel != null)
                {
                    BottomLoadingPanel.Visibility = Visibility.Collapsed;
                }

                if (BottomLoadingRing != null)
                {
                    BottomLoadingRing.IsActive = false;
                }

                SetHomeLoading(false);
                ShowInlineCardPlaceholders(false);
            }
        }

        private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (isLoadingMore) return;

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0) return;

            // Same trigger style as Search: start loading when the user is close to the bottom.
            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 100)
            {
                await LoadMoreRecommendations();
            }
        }

        private async Task LoadMoreRecommendations()
        {
            if (isLoadingMore || !IsInternetAvailable()) return;

            Config.LoadUserToken();
            if (string.IsNullOrEmpty(Config.UserToken))
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            if ((nowUtc - lastLoadMoreAttemptUtc).TotalMilliseconds < LoadMoreAttemptThrottleMs)
            {
                return;
            }
            lastLoadMoreAttemptUtc = nowUtc;

            isLoadingMore = true;

            if (BottomLoadingPanel != null)
            {
                BottomLoadingPanel.Visibility = Visibility.Visible;
            }

            if (BottomLoadingRing != null)
            {
                BottomLoadingRing.IsActive = true;
            }

            var nextRequestCount = recommendationRequestCount + RecommendationPageSize;

            try
            {
                // Request a larger slice each time: 12 -> 24 -> 36...
                // Duplicate filtering below appends only the newly discovered cards and keeps scroll position.
                var moreRecommendations = await GetSelectedCategoryVideosAsync(nextRequestCount);
                var added = AppendUniqueRecommendations(moreRecommendations);
                if (added > 0)
                {
                    await UpdateLiveTileFromLoadedRecommendationsAsync();
                }

                recommendationRequestCount = nextRequestCount;

                // Do not permanently stop loading when YouTube returns no new IDs.
                // The next bottom-scroll event will request an even larger slice.
                System.Diagnostics.Debug.WriteLine("[Home] Load more requested " + nextRequestCount + ", added " + added + ", total " + recommendationVideos.Count);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Home] Load more error: " + ex.Message);
            }
            finally
            {
                isLoadingMore = false;

                if (BottomLoadingPanel != null)
                {
                    BottomLoadingPanel.Visibility = Visibility.Collapsed;
                }

                if (BottomLoadingRing != null)
                {
                    BottomLoadingRing.IsActive = false;
                }
            }
        }

        private int AppendUniqueRecommendations(IEnumerable<VideoCardItem> videos)
        {
            if (videos == null)
            {
                return 0;
            }

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var existing in recommendationVideos)
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
                    recommendationVideos.Add(video);
                    added++;
                }
            }

            return added;
        }

        private async void CategoryChip_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var category = button != null ? button.Tag as HomeCategoryItem : null;
            if (category == null)
            {
                return;
            }

            if (selectedCategory != null && string.Equals(selectedCategory.Key, category.Key, StringComparison.Ordinal))
            {
                return;
            }

            selectedCategory = category;
            RenderCategoryChips();
            await LoadSelectedCategoryVideosAsync();
        }

        private async Task LoadSelectedCategoryVideosAsync()
        {
            if (selectedCategory == null)
            {
                return;
            }

            if (!IsInternetAvailable())
            {
                recommendationVideos.Clear();
                ShowInlineCardPlaceholders(false);
                ShowOfflineState(true);
                return;
            }

            ShowOfflineState(false);

            Config.LoadUserToken();
            if ((selectedCategory == null || selectedCategory.IsAll) && string.IsNullOrEmpty(Config.UserToken))
            {
                return;
            }

            if (isLoadingMore)
            {
                return;
            }

            isLoadingMore = true;
            recommendationRequestCount = RecommendationPageSize;
            recommendationVideos.Clear();
            SetHomeLoading(false);
            ShowInlineCardPlaceholders(true);

            try
            {
                var videos = await GetSelectedCategoryVideosAsync(recommendationRequestCount);
                AppendUniqueRecommendations(videos);
                UpdateResponsiveCardLayouts();
                await UpdateLiveTileFromLoadedRecommendationsAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Home] Category load error: " + ex.Message);
                if (!IsInternetAvailable() || IsNetworkException(ex))
                {
                    recommendationVideos.Clear();
                    ShowInlineCardPlaceholders(false);
                    ShowOfflineState(true);
                }
            }
            finally
            {
                isLoadingMore = false;
                SetHomeLoading(false);
                ShowInlineCardPlaceholders(false);
            }
        }

        private Task<List<VideoCardItem>> GetSelectedCategoryVideosAsync(int count)
        {
            if (selectedCategory == null || selectedCategory.IsAll)
            {
                return Config.GetRecommendationsAsync(Config.UserToken, count);
            }

            return Config.GetHomeCategoryVideosAsync(Config.UserToken, selectedCategory, count);
        }

        private void RenderCategoryChips()
        {
            if (CategoryChipsPanel == null)
            {
                return;
            }

            CategoryChipsPanel.Children.Clear();

            if (homeCategories == null || homeCategories.Count == 0)
            {
                ShowCategoryPlaceholders(true);
                return;
            }

            if (selectedCategory == null)
            {
                selectedCategory = FindAllCategory(homeCategories);
            }

            for (int i = 0; i < homeCategories.Count; i++)
            {
                var category = homeCategories[i];
                if (category == null || string.IsNullOrWhiteSpace(category.Title))
                {
                    continue;
                }

                var button = CreateCategoryButton(category);
                CategoryChipsPanel.Children.Add(button);
            }

            ShowCategoryPlaceholders(false);
        }

        private Button CreateCategoryButton(HomeCategoryItem category)
        {
            var isSelected = selectedCategory != null && string.Equals(selectedCategory.Key, category.Key, StringComparison.Ordinal);

            var button = new Button
            {
                Content = category.Title,
                Tag = category,
                Style = Resources["CategoryChipButtonStyle"] as Style,
                Background = new SolidColorBrush(isSelected ? Windows.UI.Colors.White : Windows.UI.Color.FromArgb(255, 39, 39, 39)),
                Foreground = new SolidColorBrush(isSelected ? Windows.UI.Colors.Black : Windows.UI.Colors.White),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 14,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold
            };

            button.Click += CategoryChip_Click;
            return button;
        }

        private static HomeCategoryItem FindAllCategory(IEnumerable<HomeCategoryItem> categories)
        {
            if (categories != null)
            {
                foreach (var category in categories)
                {
                    if (category != null && category.IsAll)
                    {
                        return category;
                    }
                }
            }

            return new HomeCategoryItem
            {
                Title = "All",
                IsAll = true
            };
        }

        private void ShowCategoryPlaceholders(bool show)
        {
            if (CategoryPlaceholderScrollViewer != null)
            {
                CategoryPlaceholderScrollViewer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CategoryScrollViewer != null)
            {
                CategoryScrollViewer.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ShowInlineCardPlaceholders(bool show)
        {
            if (InlineSkeletonCardsList != null)
            {
                InlineSkeletonCardsList.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (RecommendationsList != null)
            {
                RecommendationsList.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            }

            if (show)
            {
                UpdateResponsiveCardLayouts();
            }
        }

        private void SetHomeLoading(bool isActive)
        {
            if (HomeLoadingRing != null)
            {
                HomeLoadingRing.IsActive = isActive;
                HomeLoadingRing.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ShowOfflineState(bool show)
        {
            if (OfflinePanel != null)
            {
                OfflinePanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }

            if (show)
            {
                if (SkeletonLoader != null)
                {
                    SkeletonLoader.Visibility = Visibility.Collapsed;
                }

                SetHomeLoading(false);
                ShowInlineCardPlaceholders(false);

                if (BottomLoadingPanel != null)
                {
                    BottomLoadingPanel.Visibility = Visibility.Collapsed;
                }

                if (BottomLoadingRing != null)
                {
                    BottomLoadingRing.IsActive = false;
                }

                if (SuggestionsSection != null)
                {
                    SuggestionsSection.Visibility = Visibility.Collapsed;
                }
            }
        }

        private static bool IsInternetAvailable()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                return profile != null
                    && profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsNetworkException(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            if (ex is HttpRequestException)
            {
                return true;
            }

            var message = ex.Message;
            if (!string.IsNullOrWhiteSpace(message))
            {
                var lower = message.ToLowerInvariant();
                if (lower.Contains("network")
                    || lower.Contains("internet")
                    || lower.Contains("host")
                    || lower.Contains("connection")
                    || lower.Contains("resolve"))
                {
                    return true;
                }
            }

            return IsNetworkException(ex.InnerException);
        }

        private sealed class LiveTileRecommendationItem
        {
            public string VideoId { get; set; }
            public string Title { get; set; }
            public string Author { get; set; }
            public string ViewCount { get; set; }
            public string ImageSource { get; set; }
        }

        private async Task UpdateLiveTileFromLoadedRecommendationsAsync()
        {
            if (isUpdatingLiveTile || recommendationVideos == null || recommendationVideos.Count == 0)
            {
                return;
            }

            var tileVideos = recommendationVideos
                .Where(v => v != null && !string.IsNullOrWhiteSpace(v.VideoId) && !string.IsNullOrWhiteSpace(v.Title))
                .Take(LiveTileRecommendationCount)
                .ToList();

            if (tileVideos.Count == 0)
            {
                return;
            }

            isUpdatingLiveTile = true;

            try
            {
                var tileItems = new List<LiveTileRecommendationItem>();
                foreach (var video in tileVideos)
                {
                    var localImage = await GetLiveTileThumbnailAsync(video);
                    tileItems.Add(new LiveTileRecommendationItem
                    {
                        VideoId = video.VideoId,
                        Title = FirstNonEmpty(video.Title, "Recommended video"),
                        Author = FirstNonEmpty(video.ChannelTitle, "YouTube"),
                        ViewCount = FirstNonEmpty(video.ViewCount, string.Empty),
                        ImageSource = FirstNonEmpty(localImage, LiveTileFallbackImage)
                    });
                }

                if (tileItems.Count == 0)
                {
                    return;
                }

                var updater = TileUpdateManager.CreateTileUpdaterForApplication();
                updater.EnableNotificationQueue(true);
                updater.Clear();

                foreach (var item in tileItems)
                {
                    var document = new XmlDocument();
                    document.LoadXml(BuildRecommendationTileXml(item));

                    var notification = new TileNotification(document);
                    notification.ExpirationTime = DateTimeOffset.Now.AddDays(1);
                    updater.Update(notification);
                }

                System.Diagnostics.Debug.WriteLine("[LiveTile] Updated recommendation queue: " + tileItems.Count);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LiveTile] Update error: " + ex.Message);
            }
            finally
            {
                isUpdatingLiveTile = false;
            }
        }

        private async Task<string> GetLiveTileThumbnailAsync(VideoCardItem video)
        {
            if (video == null || string.IsNullOrWhiteSpace(video.VideoId) || string.IsNullOrWhiteSpace(video.ThumbnailUrl))
            {
                return LiveTileFallbackImage;
            }

            var thumbnailUrl = NormalizeLiveTileImageUrl(video.ThumbnailUrl);
            if (string.IsNullOrWhiteSpace(thumbnailUrl))
            {
                return LiveTileFallbackImage;
            }

            try
            {
                var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync(LiveTileFolderName, CreationCollisionOption.OpenIfExists);
                var safeFileName = MakeSafeFileName(video.VideoId) + ".jpg";
                var file = await folder.CreateFileAsync(safeFileName, CreationCollisionOption.OpenIfExists);

                var properties = await file.GetBasicPropertiesAsync();
                if (properties != null && properties.Size > 0)
                {
                    return "ms-appdata:///local/" + LiveTileFolderName + "/" + safeFileName;
                }

                var bytes = await liveTileHttpClient.GetByteArrayAsync(thumbnailUrl);
                if (bytes == null || bytes.Length == 0)
                {
                    return LiveTileFallbackImage;
                }

                await FileIO.WriteBytesAsync(file, bytes);
                return "ms-appdata:///local/" + LiveTileFolderName + "/" + safeFileName;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[LiveTile] Thumbnail cache error: " + ex.Message);
                return FirstNonEmpty(thumbnailUrl, LiveTileFallbackImage);
            }
        }

        private static string NormalizeLiveTileImageUrl(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return string.Empty;
            }

            var value = imageUrl.Trim();
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + value;
            }

            return value;
        }

        private static string BuildRecommendationTileXml(LiveTileRecommendationItem item)
        {
            var title = EscapeTileXml(TrimForTile(FirstNonEmpty(item.Title, "Recommended video"), 90));
            var author = EscapeTileXml(TrimForTile(FirstNonEmpty(item.Author, "YouTube"), 42));
            var views = EscapeTileXml(TrimForTile(FirstNonEmpty(item.ViewCount, string.Empty), 34));
            var image = EscapeTileXml(FirstNonEmpty(item.ImageSource, LiveTileFallbackImage));
            var launch = EscapeTileXml("youtubehandler:https://www.youtube.com/watch?v=" + FirstNonEmpty(item.VideoId, string.Empty));
            var viewsLine = string.IsNullOrWhiteSpace(views)
                ? string.Empty
                : "<text hint-style=\"captionSubtle\" hint-wrap=\"false\">" + views + "</text>";

            return "<tile launch=\"" + launch + "\">"
                + "<visual branding=\"nameAndLogo\" displayName=\"YouTube\">"
                + "<binding template=\"TileMedium\" branding=\"none\">"
                + "<image src=\"" + image + "\" placement=\"peek\" hint-crop=\"none\"/>"
                + "<text hint-style=\"caption\" hint-wrap=\"true\" hint-maxLines=\"2\">" + title + "</text>"
                + "<text hint-style=\"captionSubtle\" hint-wrap=\"false\">" + author + "</text>"
                + viewsLine
                + "</binding>"
                + "<binding template=\"TileWide\" branding=\"none\">"
                + "<group>"
                + "<subgroup hint-weight=\"45\">"
                + "<image src=\"" + image + "\" hint-crop=\"none\"/>"
                + "</subgroup>"
                + "<subgroup hint-weight=\"55\">"
                + "<text hint-style=\"caption\" hint-wrap=\"true\" hint-maxLines=\"3\">" + title + "</text>"
                + "<text hint-style=\"captionSubtle\" hint-wrap=\"false\">" + author + "</text>"
                + viewsLine
                + "</subgroup>"
                + "</group>"
                + "</binding>"
                + "</visual>"
                + "</tile>";
        }

        private static string EscapeTileXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string TrimForTile(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, Math.Max(0, maxLength - 1)).TrimEnd() + "…";
        }

        private static string MakeSafeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return Guid.NewGuid().ToString("N");
            }

            var builder = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if ((c >= 'a' && c <= 'z') ||
                    (c >= 'A' && c <= 'Z') ||
                    (c >= '0' && c <= '9') ||
                    c == '_' || c == '-')
                {
                    builder.Append(c);
                }
            }

            return builder.Length > 0 ? builder.ToString() : Guid.NewGuid().ToString("N");
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }

            return string.Empty;
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            var item = button.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId)) return;

            System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Home card tapped: video=" + item.VideoId
                + ", playlist=" + (string.IsNullOrWhiteSpace(item.PlaylistId) ? "(none)" : item.PlaylistId));

            // Navigate to Video page. A mix / "jam" card carries a playlist — pass it along so
            // the video page requests /next with the playlist and shows its queue.
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

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadHomeDataAsync();
        }

        private void TrendingItem_Click(object sender, ItemClickEventArgs e)
        {
            var query = e.ClickedItem as string;
            if (!string.IsNullOrWhiteSpace(query))
            {
                Frame.Navigate(typeof(Search), query);
            }
        }

        private void InitializePlaceholderCards()
        {
            if (placeholderCards == null)
            {
                placeholderCards = new ObservableCollection<object>();
            }

            placeholderCards.Clear();
            for (int i = 0; i < 5; i++)
            {
                placeholderCards.Add(new object());
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            UpdateResponsiveCardLayouts();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
        }

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void CardsItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        // Card thumbnails are decoded at the card's on-screen width, not the source's native
        // 1280x720. In this non-virtualizing list every card stays alive, and decoding dozens of
        // full-size maxres frames at once overran the image pipeline — which showed up as cards
        // wearing each other's thumbnails while scrolling. Assigning the source per-item here
        // (rather than a Source binding to a get-only property) keeps it tied to the right item.
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
            {
                return;
            }

            var width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            var targetHeight = System.Math.Round(width / VideoThumbnailAspectRatio);
            if (double.IsNaN(host.Height) || System.Math.Abs(host.Height - targetHeight) > 0.5)
            {
                host.Height = targetHeight;
            }
        }

        private void PlaceholderCardHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            ApplyResponsiveCardMargin(host);
            UpdatePlaceholderCardHostHeight(host);
        }

        private void PlaceholderCard_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
            {
                return;
            }

            UpdatePlaceholderCardHostHeight(image.Parent as FrameworkElement);
        }

        private void UpdatePlaceholderCardHostHeight(FrameworkElement host)
        {
            if (host == null)
            {
                return;
            }

            var width = host.ActualWidth;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            var image = FindDescendant<Image>(host);
            var bitmap = image != null ? image.Source as BitmapImage : null;
            var aspect = 0.82;

            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                aspect = (double)bitmap.PixelHeight / bitmap.PixelWidth;
            }

            var targetHeight = System.Math.Round(width * aspect);
            if (double.IsNaN(host.Height) || System.Math.Abs(host.Height - targetHeight) > 0.5)
            {
                host.Height = targetHeight;
            }
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
            bool isPortrait = IsPortraitOrientation();

            SetItemsControlPadding(RecommendationsList, isPortrait, new Thickness(8, 8, 8, 16));
            SetItemsControlPadding(SkeletonCardsList, isPortrait, new Thickness(8, 8, 8, 16));
            SetItemsControlPadding(InlineSkeletonCardsList, isPortrait, new Thickness(8, 8, 8, 16));

            double baseWidth = GetItemsControlContentWidth(RecommendationsList, Window.Current.Bounds.Width);
            var itemWidth = isPortrait
                ? System.Math.Max(0, baseWidth)
                : DefaultCardWidth;
            var maxColumns = isPortrait ? 1 : 3;

            UpdateItemsWrapGrid(RecommendationsList, itemWidth, maxColumns);
            UpdateItemsWrapGrid(SkeletonCardsList, itemWidth, maxColumns);
            UpdateItemsWrapGrid(InlineSkeletonCardsList, itemWidth, maxColumns);
            UpdateResponsiveCardMargins(RecommendationsList);
            UpdateResponsiveCardMargins(SkeletonCardsList);
            UpdateResponsiveCardMargins(InlineSkeletonCardsList);
        }

        private static void SetItemsControlPadding(Control control, bool isPortrait, Thickness landscapePadding)
        {
            if (control == null)
            {
                return;
            }

            control.Padding = isPortrait
                ? new Thickness(0, landscapePadding.Top, 0, landscapePadding.Bottom)
                : landscapePadding;
        }

        private static double GetItemsControlContentWidth(Control control, double fallbackWidth)
        {
            double width = control != null && control.ActualWidth > 0
                ? control.ActualWidth
                : fallbackWidth;

            if (control != null)
            {
                width -= control.Padding.Left + control.Padding.Right;
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                width = fallbackWidth;
            }

            return System.Math.Max(0, width);
        }

        private static void UpdateItemsWrapGrid(DependencyObject root, double itemWidth, int maxColumns)
        {
            if (root == null)
            {
                return;
            }

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
            {
                return null;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typedChild = child as T;
                if (typedChild != null)
                {
                    return typedChild;
                }

                var descendant = FindDescendant<T>(child);
                if (descendant != null)
                {
                    return descendant;
                }
            }

            return null;
        }

    }
}
