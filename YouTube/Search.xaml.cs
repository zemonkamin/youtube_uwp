using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Search : Page
    {
        private readonly HttpClient httpClient = new HttpClient();
        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";

        private ObservableCollection<SearchVideoItem> searchResults;
        private ObservableCollection<SearchVideoItem> allPriorityResults;
        private ObservableCollection<SearchVideoItem> allShortsResults;
        private readonly Dictionary<string, double> _watchedProgressByVideoId =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private bool isLoadingMore = false;
        private string currentQuery = "";
        private string currentContinuation = "";
        private SearchContentType currentSearchType = SearchContentType.All;

        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        // Shorts in portrait intentionally have more breathing room than normal video cards:
        // a visible gutter between the two posters and a little more vertical separation.
        private static readonly Thickness PortraitShortsCardMargin = new Thickness(6, 0, 6, 22);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);
        private const double PlaceholderFallbackAspect = 0.82;

        private bool _isTypeBottomSheetOpen;
        private bool _isTypeDragActive;
        private double _typeDragStartY;
        private double _typeInitialTransformY;
        private bool _suppressSearchBoxNavigation;
        private bool _isNavigatingToSearching;
        private DispatcherTimer _searchBoxNavigationSuppressTimer;
        private bool? _usingAllSearchResultsPanel;

        public Search()
        {
            this.InitializeComponent();
            searchResults = new ObservableCollection<SearchVideoItem>();
            allPriorityResults = new ObservableCollection<SearchVideoItem>();
            allShortsResults = new ObservableCollection<SearchVideoItem>();
            SearchResultsList.ItemsSource = searchResults;
            AllPriorityResultsList.ItemsSource = allPriorityResults;
            AllPriorityResultsList.ItemTemplate = SearchResultsList.ItemTemplate;
            AllShortsList.ItemsSource = allShortsResults;
            UpdateSearchResultsPanel();
            InitializePlaceholderCards();
            UpdateTypeCheckMarks();
            this.Loaded += Page_Loaded;
            this.Unloaded += Page_Unloaded;
            Window.Current.SizeChanged += Window_SizeChanged;
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isNavigatingToSearching = false;

            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            UpdateBackButtonVisibility();

            // Search is a secondary page, not the Home tab itself.
            tabbar?.SetActiveTab(Tabbar.ActiveTab.None);

            var query = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(query))
            {
                searchResults.Clear();
                allPriorityResults.Clear();
                allShortsResults.Clear();
                UpdateAllShortsSectionVisibility();
                return;
            }

            currentQuery = query;
            SearchInput.Text = query;
            await PerformSearchAsync(query);
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            base.OnNavigatedFrom(e);
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private void UpdateBackButtonVisibility()
        {
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                Frame != null && Frame.CanGoBack ? AppViewBackButtonVisibility.Visible : AppViewBackButtonVisibility.Collapsed;
        }

        private void SearchInput_GotFocus(object sender, RoutedEventArgs e)
        {
            NavigateToSearchingFromSearchBox();
        }

        private void SearchInput_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (NavigateToSearchingFromSearchBox())
            {
                e.Handled = true;
            }
        }

        private void SearchBox_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (NavigateToSearchingFromSearchBox())
            {
                e.Handled = true;
            }
        }

        private bool NavigateToSearchingFromSearchBox()
        {
            if (ShouldIgnoreSearchBoxNavigation())
            {
                return false;
            }

            if (_isNavigatingToSearching || Frame == null)
            {
                return false;
            }

            _isNavigatingToSearching = true;
            Frame.Navigate(typeof(Searching), currentQuery);
            return true;
        }

        private bool ShouldIgnoreSearchBoxNavigation()
        {
            return _suppressSearchBoxNavigation
                || _isTypeBottomSheetOpen
                || _isTypeDragActive
                || (TypeOverlayGrid != null && TypeOverlayGrid.Visibility == Visibility.Visible)
                || (TypeBottomSheetPanel != null && TypeBottomSheetPanel.Visibility == Visibility.Visible);
        }

        private void SuppressSearchBoxNavigationTemporarily()
        {
            _suppressSearchBoxNavigation = true;

            if (_searchBoxNavigationSuppressTimer != null)
            {
                _searchBoxNavigationSuppressTimer.Stop();
                _searchBoxNavigationSuppressTimer.Tick -= SearchBoxNavigationSuppressTimer_Tick;
            }

            _searchBoxNavigationSuppressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _searchBoxNavigationSuppressTimer.Tick += SearchBoxNavigationSuppressTimer_Tick;
            _searchBoxNavigationSuppressTimer.Start();
        }

        private void SearchBoxNavigationSuppressTimer_Tick(object sender, object e)
        {
            if (_searchBoxNavigationSuppressTimer != null)
            {
                _searchBoxNavigationSuppressTimer.Stop();
                _searchBoxNavigationSuppressTimer.Tick -= SearchBoxNavigationSuppressTimer_Tick;
                _searchBoxNavigationSuppressTimer = null;
            }

            _suppressSearchBoxNavigation = false;
        }

        private async Task PerformSearchAsync(string query)
        {
            try
            {
                ErrorText.Visibility = Visibility.Collapsed;
                BottomLoadingPanel.Visibility = Visibility.Collapsed;
                currentContinuation = string.Empty;

                if (SearchResultsList.ItemsSource == null)
                {
                    SearchResultsList.ItemsSource = searchResults;
                }

                // Use the same smooth centered AndroidLoadingRing for every result type.
                // Previously only Channels used it while videos/shorts/playlists flashed skeletons.
                searchResults.Clear();
                allPriorityResults.Clear();
                allShortsResults.Clear();
                UpdateAllShortsSectionVisibility();
                LoadingPanel.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;

                if (SkeletonLoader != null)
                {
                    SkeletonLoader.Visibility = Visibility.Collapsed;
                }

                if (SearchLoadingGrid != null)
                {
                    SearchLoadingGrid.Visibility = Visibility.Visible;
                }

                if (SearchLoadingRing != null)
                {
                    SearchLoadingRing.IsActive = true;
                }

                var page = await SearchInnertubeAsync(query, 30, currentSearchType, null);

                ApplyInitialSearchPage(page);

                UpdateResponsiveCardLayouts();
            }
            catch (Exception ex)
            {
                searchResults.Clear();
                allPriorityResults.Clear();
                allShortsResults.Clear();
                UpdateAllShortsSectionVisibility();
                ErrorText.Text = Localization.Format("SearchErrorFormat", ex.Message);
                ErrorText.Visibility = Visibility.Visible;
            }
            finally
            {
                LoadingPanel.Visibility = Visibility.Collapsed;
                LoadingRing.IsActive = false;

                if (SkeletonLoader != null)
                {
                    SkeletonLoader.Visibility = Visibility.Collapsed;
                }

                if (SearchLoadingGrid != null)
                {
                    SearchLoadingGrid.Visibility = Visibility.Collapsed;
                }

                if (SearchLoadingRing != null)
                {
                    SearchLoadingRing.IsActive = false;
                }
            }
        }

        private void ApplyInitialSearchPage(SearchPageResult page)
        {
            searchResults.Clear();
            allPriorityResults.Clear();
            allShortsResults.Clear();

            if (page == null)
            {
                currentContinuation = string.Empty;
                UpdateAllShortsSectionVisibility();
                return;
            }

            currentContinuation = page.Continuation ?? string.Empty;
            var items = page.Items ?? new List<SearchVideoItem>();
            var showShortsShelf = currentSearchType == SearchContentType.All
                && ShortsFeatureController.IsEnabled()
                && page.Shorts != null
                && page.Shorts.Count > 0;

            if (showShortsShelf)
            {
                for (int i = 0; i < page.Shorts.Count; i++)
                    allShortsResults.Add(page.Shorts[i]);
            }

            // YouTube keeps channel matches as full-width rows. Keeping them in a
            // separate vertical ItemsControl also prevents a short channel row from
            // defining the height of video cells in ItemsWrapGrid.
            for (int i = 0; i < items.Count; i++)
            {
                if (currentSearchType == SearchContentType.All && items[i].IsChannel)
                    allPriorityResults.Add(items[i]);
                else
                    searchResults.Add(items[i]);
            }

            UpdateAllShortsSectionVisibility();
        }

        private async void ScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (isLoadingMore) return;

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null) return;

            // Check if we're near the bottom (within 100 pixels)
            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 100)
            {
                await LoadMoreResults();
            }
        }

        private async Task LoadMoreResults()
        {
            if (isLoadingMore || string.IsNullOrEmpty(currentQuery) || string.IsNullOrWhiteSpace(currentContinuation)) return;

            isLoadingMore = true;

            // Show bottom loading indicator
            BottomLoadingPanel.Visibility = Visibility.Visible;
            BottomLoadingRing.IsActive = true;

            try
            {
                var page = await SearchInnertubeAsync(currentQuery, 30, currentSearchType, currentContinuation);

                if (page != null)
                {
                    currentContinuation = page.Continuation ?? string.Empty;

                    if (page.Items != null && page.Items.Count > 0)
                    {
                        // Add new items to the ObservableCollection (avoid duplicates)
                        var existingIds = new HashSet<string>(
                            allPriorityResults.Concat(searchResults).Select(v => v.ItemKey),
                            StringComparer.OrdinalIgnoreCase);
                        foreach (var item in page.Items)
                        {
                            if (!existingIds.Contains(item.ItemKey))
                            {
                                if (currentSearchType == SearchContentType.All && item.IsChannel)
                                    allPriorityResults.Add(item);
                                else
                                    searchResults.Add(item);
                                existingIds.Add(item.ItemKey);
                            }
                        }
                    }

                    if (page.Shorts != null && page.Shorts.Count > 0)
                    {
                        var existingShortIds = new HashSet<string>(allShortsResults.Select(v => v.ItemKey), StringComparer.OrdinalIgnoreCase);
                        foreach (var item in page.Shorts)
                        {
                            if (!existingShortIds.Contains(item.ItemKey))
                            {
                                allShortsResults.Add(item);
                                existingShortIds.Add(item.ItemKey);
                            }
                        }
                    }

                    UpdateAllShortsSectionVisibility();
                }
            }
            catch (Exception)
            {
                // Ignore errors on load more
            }
            finally
            {
                isLoadingMore = false;
                BottomLoadingPanel.Visibility = Visibility.Collapsed;
                BottomLoadingRing.IsActive = false;
            }
        }

        private async Task<SearchPageResult> SearchInnertubeAsync(string query, int count, SearchContentType type, string continuation)
        {
            Config.LoadUserToken();
            // Dedicated Shorts and Channels searches use the public WEB response shapes.
            // They must not carry the account's TV OAuth token; personalized TV search is
            // retained for All, Videos and Playlists.
            var forceWebClient = type == SearchContentType.Shorts
                || type == SearchContentType.Channels;
            var accessToken = forceWebClient || string.IsNullOrWhiteSpace(Config.UserToken)
                ? string.Empty
                : await Config.RefreshAccessTokenAsync(Config.UserToken);
            var useTvClient = !string.IsNullOrWhiteSpace(accessToken);
            Task<List<VideoCardItem>> historyProgressTask = null;
            if (useTvClient && string.IsNullOrWhiteSpace(continuation))
            {
                _watchedProgressByVideoId.Clear();
                historyProgressTask = Config.GetHistoryProgressItemsAsync(Config.UserToken, 500);
            }
            var clientName = useTvClient ? "TVHTML5" : "WEB";
            var clientVersion = useTvClient ? "7.20250209.19.00" : "2.20250101";
            var platform = useTvClient ? ",\"platform\":\"TV\"" : string.Empty;
            var context = "{\"client\":{\"clientName\":\"" + clientName + "\",\"clientVersion\":\"" + clientVersion + "\",\"hl\":\""
                + JsonEscape(Config.Hl)
                + "\",\"gl\":\""
                + JsonEscape(Config.Gl)
                + "\"" + platform + "}}";
            string payload;

            if (!string.IsNullOrWhiteSpace(continuation))
            {
                payload = "{\"context\":" + context + ",\"continuation\":\"" + JsonEscape(continuation) + "\"}";
            }
            else
            {
                var searchParams = GetSearchParams(type);
                payload = string.IsNullOrWhiteSpace(searchParams)
                    ? "{\"context\":" + context + ",\"query\":\"" + JsonEscape(query) + "\"}"
                    : "{\"context\":" + context + ",\"query\":\"" + JsonEscape(query) + "\",\"params\":\"" + searchParams + "\"}";
            }

            var url = "https://www.youtube.com/youtubei/v1/search?key=" + InnertubeApiKey;

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", useTvClient
                    ? "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0)"
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", useTvClient ? "7" : "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                if (useTvClient)
                {
                    request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + accessToken);
                    request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/tv");
                    Config.ApplySelectedAccountHeader(request, true);
                }
                request.Content = new StringContent(
                    Config.ApplySelectedAccountContext(payload, useTvClient),
                    Encoding.UTF8,
                    "application/json");

                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var page = ParseSearchPage(json, count, type);
                System.Diagnostics.Debug.WriteLine("[SearchProgress] response classic="
                    + (json.IndexOf("\"percentDurationWatched\"", StringComparison.Ordinal) >= 0)
                    + ", viewModel="
                    + (json.IndexOf("\"startPercent\"", StringComparison.Ordinal) >= 0));
                if (historyProgressTask != null)
                {
                    try
                    {
                        var historyItems = await historyProgressTask;
                        for (var i = 0; i < historyItems.Count; i++)
                        {
                            var historyItem = historyItems[i];
                            if (historyItem != null
                                && !string.IsNullOrWhiteSpace(historyItem.VideoId)
                                && historyItem.WatchedPercent > 0)
                            {
                                _watchedProgressByVideoId[historyItem.VideoId] = historyItem.WatchedPercent;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Search] TV history progress failed: " + ex.Message);
                    }
                }

                ApplySearchWatchedProgress(page);
                await HydrateSearchChannelThumbnailsAsync(page);
                return page;
            }
        }

        private void ApplySearchWatchedProgress(SearchPageResult page)
        {
            if (page == null || page.Items == null)
            {
                return;
            }

            var directCount = 0;
            var mergedCount = 0;
            for (var i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                if (item == null || item.IsShort || item.IsPlaylist || item.IsChannel)
                {
                    continue;
                }

                if (item.WatchedPercent > 0 && !string.IsNullOrWhiteSpace(item.VideoId))
                {
                    _watchedProgressByVideoId[item.VideoId] = item.WatchedPercent;
                    directCount++;
                    continue;
                }

                double percent;
                if (!string.IsNullOrWhiteSpace(item.VideoId)
                    && _watchedProgressByVideoId.TryGetValue(item.VideoId, out percent))
                {
                    item.WatchedPercent = percent;
                    mergedCount++;
                }
            }

            System.Diagnostics.Debug.WriteLine("[SearchProgress] cards=" + page.Items.Count
                + ", direct=" + directCount
                + ", historyMap=" + _watchedProgressByVideoId.Count
                + ", merged=" + mergedCount);
        }

        private static async Task HydrateSearchChannelThumbnailsAsync(SearchPageResult page)
        {
            if (page == null || page.Items == null || page.Items.Count == 0 || !ChannelIconController.IsEnabled())
                return;

            var cards = new List<VideoCardItem>();
            var sourceItems = new List<SearchVideoItem>();
            var needsLookup = false;

            for (var i = 0; i < page.Items.Count; i++)
            {
                var item = page.Items[i];
                if (item == null || string.Equals(item.ResultType, "Channel", StringComparison.OrdinalIgnoreCase))
                    continue;

                // Direct renderer URL always wins. ChannelId is only the fallback key for renderer
                // families such as playlistVideoRenderer that do not include an author avatar.
                var card = new VideoCardItem
                {
                    ChannelId = item.ChannelId,
                    ChannelTitle = item.Author,
                    ChannelThumbnailUrl = item.ChannelThumbnailUrl
                };
                cards.Add(card);
                sourceItems.Add(item);

                if (string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl)
                    && !string.IsNullOrWhiteSpace(item.ChannelId)
                    && item.ChannelId.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
                {
                    needsLookup = true;
                }
            }

            if (cards.Count == 0)
                return;

            // Populate/reuse the in-memory cache without a network request first.
            await Config.HydrateMissingChannelThumbnailsAsync(cards, string.Empty);

            if (needsLookup)
            {
                needsLookup = false;
                for (var i = 0; i < cards.Count; i++)
                {
                    var card = cards[i];
                    if (card != null
                        && string.IsNullOrWhiteSpace(card.ChannelThumbnailUrl)
                        && !string.IsNullOrWhiteSpace(card.ChannelId)
                        && card.ChannelId.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
                    {
                        needsLookup = true;
                        break;
                    }
                }
            }

            if (needsLookup)
            {
                try
                {
                    Config.LoadUserToken();
                    var refreshToken = Config.UserToken;
                    if (!string.IsNullOrWhiteSpace(refreshToken))
                    {
                        var accessToken = await Config.RefreshAccessTokenAsync(refreshToken);
                        if (!string.IsNullOrWhiteSpace(accessToken))
                            await Config.HydrateMissingChannelThumbnailsAsync(cards, accessToken);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Search] Channel avatar hydration failed: " + ex.Message);
                }
            }

            for (var i = 0; i < cards.Count && i < sourceItems.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(cards[i].ChannelThumbnailUrl))
                    sourceItems[i].ChannelThumbnailUrl = cards[i].ChannelThumbnailUrl;
            }
        }

        private static string GetSearchParams(SearchContentType type)
        {
            if (type == SearchContentType.All)
            {
                return string.Empty;
            }

            if (type == SearchContentType.Playlists)
            {
                return "EgIQAw==";
            }

            if (type == SearchContentType.Channels)
            {
                return "EgIQAg==";
            }

            // YouTube now exposes Shorts as a dedicated result type. Using no type token lets
            // the search response include the Shorts shelf / shortsLockupViewModel entries;
            // ParseShortResults below then keeps only genuine Shorts renderers.
            if (type == SearchContentType.Shorts)
            {
                return string.Empty;
            }

            return "EgIQAQ==";
        }

        private static SearchPageResult ParseSearchPage(string json, int maxCount, SearchContentType type)
        {
            var page = new SearchPageResult
            {
                Items = new List<SearchVideoItem>(),
                Shorts = new List<SearchVideoItem>(),
                ShortsInsertIndex = -1,
                Continuation = string.Empty
            };

            if (string.IsNullOrWhiteSpace(json))
            {
                return page;
            }

            try
            {
                var root = JsonValue.Parse(json);
                page.Continuation = FindContinuation(root);
                if (type == SearchContentType.All)
                {
                    int shortsInsertIndex;
                    if (ShortsFeatureController.IsEnabled())
                    {
                        page.Shorts = ParseShortResults(root, maxCount);
                        page.Items = ParseAllResults(root, maxCount, out shortsInsertIndex);
                        page.ShortsInsertIndex = shortsInsertIndex;
                    }
                    else
                    {
                        page.Items = ParseAllResults(root, maxCount, out shortsInsertIndex);
                        page.Shorts.Clear();
                        page.ShortsInsertIndex = -1;
                    }
                }
                else if (type == SearchContentType.Playlists)
                {
                    page.Items = ParsePlaylistResults(root, maxCount);
                }
                else if (type == SearchContentType.Channels)
                {
                    page.Items = ParseChannelResults(root, maxCount);
                }
                else if (type == SearchContentType.Shorts)
                {
                    page.Items = ParseShortResults(root, maxCount);
                }
                else
                {
                    page.Items = ParseVideoResults(root, maxCount);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error parsing search page: " + ex);
            }

            return page;
        }

        private static List<SearchVideoItem> ParseAllResults(IJsonValue root, int maxCount, out int shortsInsertIndex)
        {
            shortsInsertIndex = -1;
            var candidates = new List<SearchVideoItem>();
            candidates.AddRange(ParseVideoResults(root, maxCount));
            candidates.AddRange(ParseChannelResults(root, maxCount));
            candidates.AddRange(ParsePlaylistResults(root, maxCount));

            var byKey = new Dictionary<string, SearchVideoItem>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < candidates.Count; i++)
            {
                var item = candidates[i];
                if (item != null && !string.IsNullOrWhiteSpace(item.ItemKey) && !byKey.ContainsKey(item.ItemKey))
                    byKey.Add(item.ItemKey, item);
            }

            // Keep the order of renderer blocks sent by YouTube. This is important in All:
            // an exact channel match or playlist may appear between ordinary videos.
            var ordered = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectAllResultOrder(root, byKey, seen, ordered, maxCount, ref shortsInsertIndex);

            for (int i = 0; i < candidates.Count && ordered.Count < maxCount; i++)
            {
                var item = candidates[i];
                if (item != null && seen.Add(item.ItemKey))
                    ordered.Add(item);
            }

            return ordered;
        }

        private static void CollectAllResultOrder(
            IJsonValue value,
            IDictionary<string, SearchVideoItem> byKey,
            ISet<string> seen,
            IList<SearchVideoItem> result,
            int maxCount,
            ref int shortsInsertIndex)
        {
            if (value == null || result.Count >= maxCount) return;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                string itemKey = string.Empty;

                if (obj.ContainsKey("reelItemRenderer") || obj.ContainsKey("shortsLockupViewModel"))
                {
                    if (shortsInsertIndex < 0)
                        shortsInsertIndex = result.Count;
                    return;
                }

                if (obj.ContainsKey("videoRenderer") && obj["videoRenderer"].ValueType == JsonValueType.Object)
                {
                    itemKey = "video:" + GetJsonString(obj["videoRenderer"].GetObject(), "videoId");
                }
                else if (obj.ContainsKey("gridVideoRenderer") && obj["gridVideoRenderer"].ValueType == JsonValueType.Object)
                {
                    itemKey = "video:" + GetJsonString(obj["gridVideoRenderer"].GetObject(), "videoId");
                }
                else if (obj.ContainsKey("compactVideoRenderer") && obj["compactVideoRenderer"].ValueType == JsonValueType.Object)
                {
                    itemKey = "video:" + GetJsonString(obj["compactVideoRenderer"].GetObject(), "videoId");
                }
                else if (obj.ContainsKey("tileRenderer") && obj["tileRenderer"].ValueType == JsonValueType.Object)
                {
                    var card = Config.ParseTileRenderer(obj["tileRenderer"].GetObject());
                    if (card != null)
                        itemKey = "video:" + card.VideoId;
                }
                else if (obj.ContainsKey("channelRenderer") && obj["channelRenderer"].ValueType == JsonValueType.Object)
                {
                    var renderer = obj["channelRenderer"].GetObject();
                    itemKey = "channel:" + FirstNonEmpty(
                        GetJsonString(renderer, "channelId"),
                        ExtractChannelIdFromNavigation(renderer));
                }
                else if (obj.ContainsKey("playlistRenderer") && obj["playlistRenderer"].ValueType == JsonValueType.Object)
                {
                    var renderer = obj["playlistRenderer"].GetObject();
                    itemKey = "playlist:" + FirstNonEmpty(
                        GetJsonString(renderer, "playlistId"),
                        ExtractPlaylistIdFromNavigation(renderer));
                }
                else if (obj.ContainsKey("lockupViewModel") && obj["lockupViewModel"].ValueType == JsonValueType.Object)
                {
                    var renderer = obj["lockupViewModel"].GetObject();
                    var contentId = FirstNonEmpty(
                        GetJsonString(renderer, "contentId"),
                        ExtractPlaylistIdFromNavigation(renderer),
                        ExtractChannelIdFromNavigation(renderer));
                    var contentType = GetJsonString(renderer, "contentType");

                    if (contentType.IndexOf("CHANNEL", StringComparison.OrdinalIgnoreCase) >= 0 || IsLikelyChannelId(contentId))
                        itemKey = "channel:" + contentId;
                    else if (contentType.IndexOf("PLAYLIST", StringComparison.OrdinalIgnoreCase) >= 0 || IsLikelyPlaylistId(contentId))
                        itemKey = "playlist:" + contentId;
                    else
                    {
                        var videoId = FirstNonEmpty(contentId, FindStringByKey(renderer, "videoId"));
                        if (videoId.Length == 11)
                            itemKey = "video:" + videoId;
                    }
                }

                SearchVideoItem item;
                if (!string.IsNullOrWhiteSpace(itemKey)
                    && byKey.TryGetValue(itemKey, out item)
                    && seen.Add(itemKey))
                {
                    result.Add(item);
                    return;
                }

                foreach (var pair in obj)
                {
                    CollectAllResultOrder(pair.Value, byKey, seen, result, maxCount, ref shortsInsertIndex);
                    if (result.Count >= maxCount) return;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < array.Count && result.Count < maxCount; i++)
                    CollectAllResultOrder(array[i], byKey, seen, result, maxCount, ref shortsInsertIndex);
            }
        }

        private static List<SearchVideoItem> ParseVideoResults(IJsonValue root, int maxCount)
        {
            var result = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var videoRenderers = new List<JsonObject>();
            var lockupViewModels = new List<JsonObject>();
            var tileRenderers = new List<JsonObject>();

            FindVideoRenderers(root, videoRenderers);
            FindObjectsByKey(root, "lockupViewModel", lockupViewModels);
            FindObjectsByKey(root, "tileRenderer", tileRenderers);

            foreach (var renderer in videoRenderers)
            {
                if (result.Count >= maxCount)
                    break;

                var videoId = GetJsonString(renderer, "videoId");
                if (string.IsNullOrWhiteSpace(videoId) || seen.Contains(videoId))
                {
                    continue;
                }

                seen.Add(videoId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Video",
                    VideoId = videoId,
                    Title = SimplifyText(renderer, "title", Localization.GetString("Untitled")),
                    Author = FirstNonEmpty(Config.ExtractVideoCardAuthor(renderer), Localization.GetString("Unknown")),
                    Views = FirstNonEmpty(
                        SimplifyText(renderer, "viewCountText", string.Empty),
                        SimplifyText(renderer, "shortViewCountText", string.Empty)),
                    Duration = SimplifyText(renderer, "lengthText", string.Empty),
                    Thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg",
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer),
                    WatchedPercent = Config.ExtractWatchedPercent(renderer)
                });
            }

            // New WEB search responses increasingly use lockupViewModel for ordinary
            // videos. youtube-ios includes that renderer in its common video pass too.
            for (int i = 0; i < lockupViewModels.Count && result.Count < maxCount; i++)
            {
                var renderer = lockupViewModels[i];
                var contentId = FirstNonEmpty(
                    GetJsonString(renderer, "contentId"),
                    FindStringByKey(renderer, "videoId"));
                var contentType = GetJsonString(renderer, "contentType");

                if (contentId.Length != 11
                    || seen.Contains(contentId)
                    || contentType.IndexOf("CHANNEL", StringComparison.OrdinalIgnoreCase) >= 0
                    || contentType.IndexOf("PLAYLIST", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                seen.Add(contentId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Video",
                    VideoId = contentId,
                    Title = FirstNonEmpty(ExtractLockupTitle(renderer), Localization.GetString("Untitled")),
                    Author = FirstNonEmpty(
                        Config.ExtractVideoCardAuthor(renderer),
                        ExtractLockupSubtitle(renderer),
                        Localization.GetString("Unknown")),
                    Views = FirstNonEmpty(
                        FindRenderedTextByKey(renderer, "shortViewCountText"),
                        FindRenderedTextByKey(renderer, "viewCountText"),
                        ExtractLockupMetadata(renderer)),
                    Duration = FirstNonEmpty(
                        FindRenderedTextByKey(renderer, "lengthText"),
                        ExtractPlaylistBadgeText(renderer)),
                    Thumbnail = FirstNonEmpty(
                        ExtractThumbnailUrl(renderer),
                        "https://i.ytimg.com/vi/" + contentId + "/hqdefault.jpg"),
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer),
                    WatchedPercent = Config.ExtractWatchedPercent(renderer)
                });
            }

            for (int i = 0; i < tileRenderers.Count && result.Count < maxCount; i++)
            {
                var card = Config.ParseTileRenderer(tileRenderers[i]);
                if (card == null || string.IsNullOrWhiteSpace(card.VideoId) || !seen.Add(card.VideoId))
                {
                    continue;
                }

                result.Add(new SearchVideoItem
                {
                    ResultType = "Video",
                    VideoId = card.VideoId,
                    PlaylistId = card.PlaylistId,
                    Title = FirstNonEmpty(card.Title, Localization.GetString("Untitled")),
                    Author = FirstNonEmpty(card.ChannelTitle, Localization.GetString("Unknown")),
                    Views = card.ViewCount,
                    Duration = card.Duration,
                    Thumbnail = FirstNonEmpty(card.ThumbnailUrl,
                        "https://i.ytimg.com/vi/" + card.VideoId + "/hqdefault.jpg"),
                    ChannelId = card.ChannelId,
                    ChannelThumbnailUrl = card.ChannelThumbnailUrl,
                    WatchedPercent = card.WatchedPercent
                });
            }

            return result;
        }

        private static List<SearchVideoItem> ParseShortResults(IJsonValue root, int maxCount)
        {
            var result = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reelRenderers = new List<JsonObject>();
            var lockupViewModels = new List<JsonObject>();

            FindObjectsByKey(root, "reelItemRenderer", reelRenderers);
            FindObjectsByKey(root, "shortsLockupViewModel", lockupViewModels);

            for (int i = 0; i < reelRenderers.Count && result.Count < maxCount; i++)
            {
                var renderer = reelRenderers[i];
                var videoId = FirstNonEmpty(
                    GetJsonString(renderer, "videoId"),
                    FindStringByKey(renderer, "videoId"));

                if (string.IsNullOrWhiteSpace(videoId) || seen.Contains(videoId))
                {
                    continue;
                }

                seen.Add(videoId);
                var views = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "secondaryText"),
                    FindRenderedTextByKey(renderer, "shortViewCountText"),
                    FindRenderedTextByKey(renderer, "viewCountText"));
                result.Add(new SearchVideoItem
                {
                    ResultType = "Short",
                    VideoId = videoId,
                    Title = FirstNonEmpty(
                        SimplifyText(renderer, "headline", string.Empty),
                        SimplifyText(renderer, "title", string.Empty),
                        Localization.GetString("Shorts")),
                    Author = string.Empty,
                    Views = views,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(
                        ExtractThumbnailUrl(renderer),
                        "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg"),
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer)
                });
            }

            for (int i = 0; i < lockupViewModels.Count && result.Count < maxCount; i++)
            {
                var renderer = lockupViewModels[i];
                var videoId = FindStringByKey(renderer, "videoId");
                if (string.IsNullOrWhiteSpace(videoId) || seen.Contains(videoId))
                {
                    continue;
                }

                var title = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "primaryText"),
                    ExtractLockupTitle(renderer),
                    "Shorts");
                var views = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "secondaryText"),
                    FindRenderedTextByKey(renderer, "shortViewCountText"),
                    FindRenderedTextByKey(renderer, "viewCountText"));

                seen.Add(videoId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Short",
                    VideoId = videoId,
                    Title = title,
                    Author = string.Empty,
                    Views = views,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(
                        ExtractThumbnailUrl(renderer),
                        "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg"),
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer)
                });
            }

            return result;
        }

        // youtube-ios first reads overlayMetadata.primaryText/secondaryText and then
        // searches the same uncommon field anywhere inside the Shorts card. Keep the
        // same order because WEB moves overlayMetadata between view-model wrappers.
        private static string ExtractShortsMetadataText(JsonObject renderer, string fieldName)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(fieldName)) return string.Empty;

            var overlays = new List<JsonObject>();
            FindObjectsByKey(renderer, "overlayMetadata", overlays);
            for (int i = 0; i < overlays.Count; i++)
            {
                var text = ExtractRenderedField(overlays[i], fieldName);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return FindRenderedTextByKey(renderer, fieldName);
        }

        private static string FindRenderedTextByKey(IJsonValue value, string key)
        {
            if (value == null || string.IsNullOrWhiteSpace(key)) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                var direct = ExtractRenderedField(obj, key);
                if (!string.IsNullOrWhiteSpace(direct)) return direct;

                foreach (var pair in obj)
                {
                    var found = FindRenderedTextByKey(pair.Value, key);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < array.Count; i++)
                {
                    var found = FindRenderedTextByKey(array[i], key);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }

            return string.Empty;
        }

        private static string ExtractRenderedField(JsonObject parent, string key)
        {
            if (parent == null || !parent.ContainsKey(key) || parent[key] == null)
                return string.Empty;

            var value = parent[key];
            if (value.ValueType == JsonValueType.String)
                return value.GetString();

            return value.ValueType == JsonValueType.Object
                ? ExtractText(value.GetObject())
                : string.Empty;
        }

        private static List<SearchVideoItem> ParsePlaylistResults(IJsonValue root, int maxCount)
        {
            var result = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var playlistRenderers = new List<JsonObject>();
            var lockupViewModels = new List<JsonObject>();

            FindObjectsByKey(root, "playlistRenderer", playlistRenderers);
            FindObjectsByKey(root, "lockupViewModel", lockupViewModels);

            foreach (var renderer in playlistRenderers)
            {
                if (result.Count >= maxCount)
                    break;

                var playlistId = FirstNonEmpty(GetJsonString(renderer, "playlistId"), ExtractPlaylistIdFromNavigation(renderer));
                if (string.IsNullOrWhiteSpace(playlistId) || seen.Contains(playlistId))
                {
                    continue;
                }

                seen.Add(playlistId);

                var videoCountText = FirstNonEmpty(
                    ExtractPlaylistBadgeText(renderer),
                    SimplifyText(renderer, "videoCountText", string.Empty),
                    ExtractPlaylistVideoCount(renderer));

                result.Add(new SearchVideoItem
                {
                    ResultType = "Playlist",
                    PlaylistId = playlistId,
                    Title = SimplifyText(renderer, "title", Localization.GetString("Playlist")),
                    Author = FirstNonEmpty(
                        Config.ExtractPlaylistCardAuthor(renderer),
                        SimplifyText(renderer, "shortBylineText", string.Empty),
                        SimplifyText(renderer, "longBylineText", string.Empty),
                        Localization.GetString("Unknown")),
                    Views = videoCountText,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractThumbnailUrl(renderer), App.GetThemeAssetUri("Assets/yt_skeleton/video.png").ToString()),
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer)
                });
            }

            foreach (var renderer in lockupViewModels)
            {
                if (result.Count >= maxCount)
                    break;

                var playlistId = FirstNonEmpty(GetJsonString(renderer, "contentId"), ExtractPlaylistIdFromNavigation(renderer));
                var contentType = GetJsonString(renderer, "contentType");
                if (string.IsNullOrWhiteSpace(playlistId) || seen.Contains(playlistId))
                {
                    continue;
                }

                if (!IsLikelyPlaylistId(playlistId) && contentType.IndexOf("PLAYLIST", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                seen.Add(playlistId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Playlist",
                    PlaylistId = playlistId,
                    Title = FirstNonEmpty(ExtractLockupTitle(renderer), Localization.GetString("Playlist")),
                    Author = FirstNonEmpty(
                        Config.ExtractPlaylistCardAuthor(renderer),
                        ExtractLockupSubtitle(renderer),
                        Localization.GetString("Unknown")),
                    Views = FirstNonEmpty(
                        ExtractPlaylistBadgeText(renderer),
                        SimplifyText(renderer, "videoCountText", string.Empty),
                        ExtractPlaylistVideoCount(renderer),
                        ExtractLockupMetadata(renderer),
                        string.Empty),
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractThumbnailUrl(renderer), App.GetThemeAssetUri("Assets/yt_skeleton/video.png").ToString()),
                    ChannelId = Config.ExtractVideoCardChannelId(renderer),
                    ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer)
                });
            }

            return result;
        }

        private static List<SearchVideoItem> ParseChannelResults(IJsonValue root, int maxCount)
        {
            var result = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var channelRenderers = new List<JsonObject>();
            var lockupViewModels = new List<JsonObject>();

            FindObjectsByKey(root, "channelRenderer", channelRenderers);
            FindObjectsByKey(root, "lockupViewModel", lockupViewModels);

            foreach (var renderer in channelRenderers)
            {
                if (result.Count >= maxCount)
                    break;

                var channelId = FirstNonEmpty(
                    GetJsonString(renderer, "channelId"),
                    ExtractChannelIdFromNavigation(renderer));

                if (string.IsNullOrWhiteSpace(channelId) || seen.Contains(channelId))
                {
                    continue;
                }

                seen.Add(channelId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Channel",
                    ChannelId = channelId,
                    Title = NormalizeChannelTitle(
                        SimplifyText(renderer, "title", Localization.GetString("Channel")),
                        FirstNonEmpty(ExtractChannelHandle(renderer), ExtractChannelHandleFromBrowse(renderer))),
                    Author = FirstNonEmpty(
                        ExtractChannelHandle(renderer),
                        ExtractChannelHandleFromBrowse(renderer)),
                    Views = BuildChannelMetadata(renderer),
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractChannelThumbnailUrl(renderer), "Assets/profile.png")
                });
            }

            foreach (var renderer in lockupViewModels)
            {
                if (result.Count >= maxCount)
                    break;

                var contentType = GetJsonString(renderer, "contentType");
                var channelId = FirstNonEmpty(
                    GetJsonString(renderer, "contentId"),
                    ExtractChannelIdFromNavigation(renderer));

                if (string.IsNullOrWhiteSpace(channelId)
                    || seen.Contains(channelId)
                    || (!IsLikelyChannelId(channelId) && contentType.IndexOf("CHANNEL", StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                seen.Add(channelId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Channel",
                    ChannelId = channelId,
                    Title = NormalizeChannelTitle(
                        FirstNonEmpty(ExtractLockupTitle(renderer), Localization.GetString("Channel")),
                        FirstNonEmpty(ExtractChannelHandle(renderer), ExtractLockupSubtitle(renderer))),
                    Author = FirstNonEmpty(ExtractChannelHandle(renderer), ExtractLockupSubtitle(renderer)),
                    Views = BuildCleanChannelMetadata(
                        FirstNonEmpty(ExtractLockupMetadata(renderer), string.Empty),
                        FirstNonEmpty(ExtractChannelHandle(renderer), ExtractLockupSubtitle(renderer))),
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractChannelThumbnailUrl(renderer), "Assets/profile.png")
                });
            }

            return result;
        }

        private static string NormalizeChannelTitle(string title, string handle)
        {
            title = (title ?? string.Empty).Trim();
            handle = (handle ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(title)
                && !string.IsNullOrWhiteSpace(handle)
                && string.Equals(title, handle, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(title)
                && title.StartsWith("@", StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(handle))
            {
                return string.Empty;
            }

            return title;
        }

        private static string BuildCleanChannelMetadata(string metadata, string handle)
        {
            metadata = (metadata ?? string.Empty).Trim();
            handle = (handle ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(metadata))
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(handle))
            {
                var parts = metadata.Split(new[] { '•' }, StringSplitOptions.RemoveEmptyEntries);
                var cleaned = new List<string>();
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i].Trim();
                    if (!ShouldRemoveChannelHandlePart(part, handle))
                    {
                        cleaned.Add(part);
                    }
                }

                if (cleaned.Count > 0)
                {
                    return string.Join(" • ", cleaned);
                }
            }

            return metadata;
        }

        private static string ExtractChannelIdFromNavigation(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            var browseId = FindStringByKey(obj, "browseId");
            if (!string.IsNullOrWhiteSpace(browseId) && IsLikelyChannelId(browseId))
            {
                return browseId;
            }

            var channelId = FindStringByKey(obj, "channelId");
            if (!string.IsNullOrWhiteSpace(channelId) && IsLikelyChannelId(channelId))
            {
                return channelId;
            }

            return string.Empty;
        }

        private static string ExtractChannelHandle(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            var handle = FirstNonEmpty(
                SimplifyText(obj, "subscriberCountText", string.Empty),
                SimplifyText(obj, "handleText", string.Empty),
                FindHandleText(obj));

            if (!string.IsNullOrWhiteSpace(handle) && handle.StartsWith("@", StringComparison.Ordinal))
            {
                return handle;
            }

            return FindHandleText(obj);
        }

        private static string ExtractChannelHandleFromBrowse(JsonObject obj)
        {
            var canonicalBaseUrl = FindStringByKey(obj, "canonicalBaseUrl");
            if (!string.IsNullOrWhiteSpace(canonicalBaseUrl))
            {
                var slash = canonicalBaseUrl.LastIndexOf('/');
                var tail = slash >= 0 ? canonicalBaseUrl.Substring(slash + 1) : canonicalBaseUrl;
                if (!string.IsNullOrWhiteSpace(tail))
                {
                    return tail.StartsWith("@", StringComparison.Ordinal) ? tail : "@" + tail;
                }
            }

            return string.Empty;
        }

        private static string FindHandleText(IJsonValue value)
        {
            if (value == null) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey("text") && obj["text"].ValueType == JsonValueType.String)
                {
                    var text = obj["text"].GetString();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith("@", StringComparison.Ordinal))
                    {
                        return text.Trim();
                    }
                }

                if (obj.ContainsKey("simpleText") && obj["simpleText"].ValueType == JsonValueType.String)
                {
                    var text = obj["simpleText"].GetString();
                    if (!string.IsNullOrWhiteSpace(text) && text.Trim().StartsWith("@", StringComparison.Ordinal))
                    {
                        return text.Trim();
                    }
                }

                foreach (var pair in obj)
                {
                    var found = FindHandleText(pair.Value);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    var found = FindHandleText(arr[i]);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }

            return string.Empty;
        }

        private static string BuildChannelMetadata(JsonObject renderer)
        {
            var handle = FirstNonEmpty(ExtractChannelHandle(renderer), ExtractChannelHandleFromBrowse(renderer));
            var subscribers = FirstNonEmpty(
                SimplifyText(renderer, "subscriberCountText", string.Empty),
                SimplifyText(renderer, "videoCountText", string.Empty));
            var videos = SimplifyText(renderer, "videoCountText", string.Empty);
            var description = SimplifyText(renderer, "descriptionSnippet", string.Empty);

            subscribers = RemoveChannelHandleFromMetadata(subscribers, handle);
            videos = RemoveChannelHandleFromMetadata(videos, handle);
            description = RemoveChannelHandleFromMetadata(description, handle);

            if (!string.IsNullOrWhiteSpace(subscribers) && !string.IsNullOrWhiteSpace(videos) && !string.Equals(subscribers, videos, StringComparison.OrdinalIgnoreCase))
            {
                return BuildCleanChannelMetadata(subscribers + " • " + videos, handle);
            }

            return BuildCleanChannelMetadata(FirstNonEmpty(subscribers, videos, description), handle);
        }

        private static string RemoveChannelHandleFromMetadata(string metadata, string handle)
        {
            metadata = (metadata ?? string.Empty).Trim();
            handle = (handle ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(metadata))
            {
                return string.Empty;
            }

            var parts = metadata.Split(new[] { '•' }, StringSplitOptions.RemoveEmptyEntries);
            var cleaned = new List<string>();

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i].Trim();
                if (ShouldRemoveChannelHandlePart(part, handle))
                {
                    continue;
                }

                cleaned.Add(part);
            }

            return cleaned.Count > 0 ? string.Join(" • ", cleaned) : string.Empty;
        }

        private static bool ShouldRemoveChannelHandlePart(string part, string handle)
        {
            part = (part ?? string.Empty).Trim();
            handle = (handle ?? string.Empty).Trim();

            if (string.IsNullOrWhiteSpace(part))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(handle) && string.Equals(part, handle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // In channel search results YouTube sometimes puts the handle into the metadata line,
            // so the card becomes: Title / @handle / @handle. Metadata should not contain handles.
            if (part.StartsWith("@", StringComparison.Ordinal))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Recursively finds all videoRenderer objects in JSON.
        /// </summary>
        private static void FindVideoRenderers(IJsonValue value, List<JsonObject> outList)
        {
            if (value == null) return;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey("videoRenderer"))
                {
                    var videoRenderer = obj.GetNamedObject("videoRenderer");
                    outList.Add(videoRenderer);
                }
                else if (obj.ContainsKey("gridVideoRenderer"))
                {
                    outList.Add(obj.GetNamedObject("gridVideoRenderer"));
                }
                else if (obj.ContainsKey("compactVideoRenderer"))
                {
                    outList.Add(obj.GetNamedObject("compactVideoRenderer"));
                }
                else
                {
                    foreach (var pair in obj)
                    {
                        FindVideoRenderers(pair.Value, outList);
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    FindVideoRenderers(arr[i], outList);
                }
            }
        }

        private static void FindObjectsByKey(IJsonValue value, string key, List<JsonObject> outList)
        {
            if (value == null || string.IsNullOrWhiteSpace(key)) return;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.Object)
                {
                    outList.Add(obj[key].GetObject());
                }

                foreach (var pair in obj)
                {
                    FindObjectsByKey(pair.Value, key, outList);
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    FindObjectsByKey(arr[i], key, outList);
                }
            }
        }

        private static string FindContinuation(IJsonValue value)
        {
            if (value == null) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                var token = ExtractContinuationToken(obj);
                if (!string.IsNullOrWhiteSpace(token))
                    return token;

                foreach (var pair in obj)
                {
                    token = FindContinuation(pair.Value);
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    var token = FindContinuation(arr[i]);
                    if (!string.IsNullOrWhiteSpace(token))
                        return token;
                }
            }

            return string.Empty;
        }

        private static string ExtractContinuationToken(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            var nextContinuationData = GetObject(obj, "nextContinuationData");
            var token = GetJsonString(nextContinuationData, "continuation");
            if (!string.IsNullOrWhiteSpace(token)) return token;

            var reloadContinuationData = GetObject(obj, "reloadContinuationData");
            token = GetJsonString(reloadContinuationData, "continuation");
            if (!string.IsNullOrWhiteSpace(token)) return token;

            var continuationCommand = GetObject(obj, "continuationCommand");
            token = FirstNonEmpty(GetJsonString(continuationCommand, "token"), GetJsonString(continuationCommand, "continuation"));
            if (!string.IsNullOrWhiteSpace(token)) return token;

            return string.Empty;
        }

        /// <summary>
        /// Simplifies text extraction.
        /// </summary>
        private static string SimplifyText(JsonObject obj, string fieldName, string fallback)
        {
            if (obj == null || !obj.ContainsKey(fieldName))
            {
                return fallback;
            }

            var field = obj.GetNamedValue(fieldName);
            if (field == null || field.ValueType != JsonValueType.Object)
            {
                return fallback;
            }

            var value = ExtractText(field.GetObject());
            return string.IsNullOrWhiteSpace(value) ? fallback : value;
        }

        private static string ExtractText(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            if (obj.ContainsKey("simpleText"))
            {
                var text = obj.GetNamedString("simpleText", string.Empty);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            if (obj.ContainsKey("text"))
            {
                var textValue = obj["text"];
                if (textValue.ValueType == JsonValueType.String)
                {
                    var text = textValue.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
                else if (textValue.ValueType == JsonValueType.Object)
                {
                    var text = ExtractText(textValue.GetObject());
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            if (obj.ContainsKey("content"))
            {
                var content = obj["content"];
                if (content.ValueType == JsonValueType.String)
                {
                    var text = content.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            if (obj.ContainsKey("runs"))
            {
                var runsValue = obj["runs"];
                if (runsValue.ValueType == JsonValueType.Array)
                {
                    var runs = runsValue.GetArray();
                    var sb = new StringBuilder();
                    for (int i = 0; i < runs.Count; i++)
                    {
                        if (runs[i].ValueType != JsonValueType.Object) continue;
                        var runObj = runs[i].GetObject();
                        var piece = GetJsonString(runObj, "text");
                        if (!string.IsNullOrWhiteSpace(piece))
                        {
                            sb.Append(piece);
                        }
                    }

                    var value = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return string.Empty;
        }

        private static string ExtractLockupTitle(JsonObject renderer)
        {
            var metadata = GetObject(renderer, "metadata");
            var lockupMetadata = GetObject(metadata, "lockupMetadataViewModel");
            var title = GetObject(lockupMetadata, "title");
            return ExtractText(title);
        }

        private static string ExtractLockupSubtitle(JsonObject renderer)
        {
            var metadata = GetObject(renderer, "metadata");
            var lockupMetadata = GetObject(metadata, "lockupMetadataViewModel");
            var subtitle = GetObject(lockupMetadata, "subtitle");
            return ExtractText(subtitle);
        }

        private static string ExtractLockupMetadata(JsonObject renderer)
        {
            var metadata = GetObject(renderer, "metadata");
            var lockupMetadata = GetObject(metadata, "lockupMetadataViewModel");
            var metadataObj = GetObject(lockupMetadata, "metadata");
            return ExtractText(metadataObj);
        }

        private static string ExtractPlaylistVideoCount(JsonObject renderer)
        {
            if (renderer == null) return string.Empty;

            var videoCount = GetJsonString(renderer, "videoCount");
            if (!string.IsNullOrWhiteSpace(videoCount))
            {
                return Localization.Format("VideosSuffixFormat", videoCount);
            }

            return string.Empty;
        }

        private static string ExtractPlaylistBadgeText(JsonObject renderer)
        {
            if (renderer == null) return string.Empty;

            foreach (var key in new[]
            {
                "thumbnailBadgeViewModel",
                "thumbnailOverlayTimeStatusRenderer",
                "thumbnailOverlayBottomPanelRenderer"
            })
            {
                var nodes = new List<JsonObject>();
                FindObjectsByKey(renderer, key, nodes);
                for (var i = 0; i < nodes.Count; i++)
                {
                    var text = ExtractText(nodes[i]);
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }
            }

            return FirstNonEmpty(
                SimplifyText(renderer, "videoCountShortText", string.Empty),
                SimplifyText(renderer, "videoCountText", string.Empty),
                ExtractPlaylistVideoCount(renderer));
        }

        private static string ExtractPlaylistIdFromNavigation(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            var playlistId = FindStringByKey(obj, "playlistId");
            if (!string.IsNullOrWhiteSpace(playlistId))
                return playlistId;

            var browseId = FindStringByKey(obj, "browseId");
            if (!string.IsNullOrWhiteSpace(browseId) && browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
                return browseId.Substring(2);

            return string.Empty;
        }

        private static string FindStringByKey(IJsonValue value, string key)
        {
            if (value == null) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(key) && obj[key].ValueType == JsonValueType.String)
                {
                    var text = obj[key].GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return text;
                }

                foreach (var pair in obj)
                {
                    var found = FindStringByKey(pair.Value, key);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    var found = FindStringByKey(arr[i], key);
                    if (!string.IsNullOrWhiteSpace(found))
                        return found;
                }
            }

            return string.Empty;
        }

        private static string ExtractChannelThumbnailUrl(JsonObject renderer)
        {
            if (renderer == null) return string.Empty;

            var thumbnail = GetObject(renderer, "thumbnail");
            var url = ExtractBestThumbnailFromObject(thumbnail);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            var avatar = GetObject(renderer, "avatar");
            url = ExtractBestThumbnailFromObject(avatar);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            var channelThumbnail = GetObject(renderer, "channelThumbnail");
            url = ExtractBestThumbnailFromObject(channelThumbnail);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            var thumbnailViewModel = GetObject(renderer, "thumbnailViewModel");
            url = FindFirstImageUrl(thumbnailViewModel);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            return FindFirstImageUrl(renderer);
        }

        private static string ExtractThumbnailUrl(JsonObject renderer)
        {
            if (renderer == null) return string.Empty;

            var thumbnail = GetObject(renderer, "thumbnail");
            var url = ExtractBestThumbnailFromObject(thumbnail);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            var thumbnails = GetObject(renderer, "thumbnails");
            url = ExtractBestThumbnailFromObject(thumbnails);
            if (!string.IsNullOrWhiteSpace(url))
                return url;

            return FindFirstImageUrl(renderer);
        }

        private static string ExtractBestThumbnailFromObject(JsonObject obj)
        {
            if (obj == null) return string.Empty;

            var array = GetArray(obj, "thumbnails");
            if (array == null) array = GetArray(obj, "sources");
            if (array == null || array.Count == 0) return string.Empty;

            string bestUrl = string.Empty;
            double bestWidth = -1;

            for (int i = 0; i < array.Count; i++)
            {
                if (array[i].ValueType != JsonValueType.Object) continue;

                var item = array[i].GetObject();
                var url = NormalizeImageUrl(GetJsonString(item, "url"));
                if (string.IsNullOrWhiteSpace(url) || !IsImageUrl(url)) continue;

                double width = 0;
                var widthValue = GetValue(item, "width");
                if (widthValue != null && widthValue.ValueType == JsonValueType.Number)
                    width = widthValue.GetNumber();

                if (bestUrl.Length == 0 || width > bestWidth)
                {
                    bestUrl = url;
                    bestWidth = width;
                }
            }

            return bestUrl;
        }

        private static string FindFirstImageUrl(IJsonValue value)
        {
            if (value == null) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey("url") && obj["url"].ValueType == JsonValueType.String)
                {
                    var url = NormalizeImageUrl(obj["url"].GetString());
                    if (IsImageUrl(url))
                        return url;
                }

                foreach (var pair in obj)
                {
                    var url = FindFirstImageUrl(pair.Value);
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    var url = FindFirstImageUrl(arr[i]);
                    if (!string.IsNullOrWhiteSpace(url))
                        return url;
                }
            }

            return string.Empty;
        }

        private static string NormalizeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return string.Empty;

            url = url.Trim()
                .Replace("\\u0026", "&")
                .Replace("\u0026", "&")
                .Replace("&amp;", "&");

            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                url = "https:" + url;
            }
            else if (url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal))
            {
                url = "https://www.youtube.com" + url;
            }

            return url;
        }

        private static bool IsImageUrl(string url)
        {
            url = NormalizeImageUrl(url);
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.IndexOf("ytimg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("yt3", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("ggpht", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("googleusercontent", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsLikelyPlaylistId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.StartsWith("PL", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("OLAK", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("RD", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("UU", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("LL", StringComparison.OrdinalIgnoreCase) ||
                   value.StartsWith("FL", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLikelyChannelId(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.StartsWith("UC", StringComparison.OrdinalIgnoreCase)
                && value.Length >= 10;
        }

        private static JsonObject GetObject(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                return null;

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        private static JsonArray GetArray(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                return null;

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Array ? value.GetArray() : null;
        }

        private static IJsonValue GetValue(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                return null;

            return obj[key];
        }

        private static string GetJsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            var value = obj.GetNamedValue(key);
            return value != null && value.ValueType == JsonValueType.String ? value.GetString() : string.Empty;
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                    return values[i];
            }
            return string.Empty;
        }

        private void InitializePlaceholderCards()
        {
            if (SkeletonCardsList != null)
            {
                SkeletonCardsList.ItemsSource = new List<int> { 1, 2, 3, 4, 5 };
            }
        }

        private void Page_Loaded(object sender, RoutedEventArgs e)
        {
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            ShortsFeatureController.EnabledChanged += ShortsFeature_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            InitializePlaceholderCards();
            UpdateResponsiveCardLayouts();
            UpdateShortsFeatureVisibility();
            UpdateTypeCheckMarks();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;

            if (_searchBoxNavigationSuppressTimer != null)
            {
                _searchBoxNavigationSuppressTimer.Stop();
                _searchBoxNavigationSuppressTimer.Tick -= SearchBoxNavigationSuppressTimer_Tick;
                _searchBoxNavigationSuppressTimer = null;
            }
        }

        private async void ShortsFeature_EnabledChanged(object sender, EventArgs e)
        {
            var wasShowingShorts = currentSearchType == SearchContentType.Shorts;
            var wasShowingAll = currentSearchType == SearchContentType.All;
            UpdateShortsFeatureVisibility();
            UpdateAllShortsSectionVisibility();
            UpdateTypeCheckMarks();

            if ((wasShowingAll || (wasShowingShorts && !ShortsFeatureController.IsEnabled()))
                && !string.IsNullOrWhiteSpace(currentQuery))
                await PerformSearchAsync(currentQuery);
        }

        private void UpdateShortsFeatureVisibility()
        {
            var enabled = ShortsFeatureController.IsEnabled();
            if (ShortsTypeButton != null)
                ShortsTypeButton.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

            if (!enabled && currentSearchType == SearchContentType.Shorts)
                currentSearchType = SearchContentType.All;
        }

        private void UpdateAllShortsSectionVisibility()
        {
            var isAll = currentSearchType == SearchContentType.All;

            if (AllPriorityResultsList != null)
            {
                AllPriorityResultsList.Visibility = isAll
                    && allPriorityResults != null
                    && allPriorityResults.Count > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
            }

            if (AllShortsSection == null) return;

            AllShortsSection.Visibility = isAll
                && ShortsFeatureController.IsEnabled()
                && allShortsResults != null
                && allShortsResults.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void CardsItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void SearchThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as SearchVideoItem;
            if (image == null || item == null)
                return;

            if (string.Equals(item.ResultType, "Video", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(item.VideoId))
            {
                VideoThumbnailController.Assign(image, item.VideoId, item.Thumbnail, 360);
                return;
            }

            try
            {
                image.Source = string.IsNullOrWhiteSpace(item.Thumbnail)
                    ? null
                    : new BitmapImage(new Uri(item.Thumbnail, UriKind.RelativeOrAbsolute));
            }
            catch
            {
                image.Source = null;
            }
        }

        private void ChannelIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as SearchVideoItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.ChannelThumbnailUrl);
        }

        private void ChannelIcon_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var item = image == null ? null : image.DataContext as SearchVideoItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.ChannelThumbnailUrl);
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
                return;

            UpdatePlaceholderCardHostHeight(image.Parent as FrameworkElement);
        }

        private void UpdatePlaceholderCardHostHeight(FrameworkElement host)
        {
            if (host == null)
                return;

            var width = host.ActualWidth;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            double aspect = PlaceholderFallbackAspect;
            var image = FindDescendant<Image>(host);
            var bitmap = image != null ? image.Source as BitmapImage : null;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                aspect = (double)bitmap.PixelHeight / bitmap.PixelWidth;
            }

            var targetHeight = Math.Round(width * aspect);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
                host.Height = targetHeight;
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
                ? (currentSearchType == SearchContentType.Shorts
                    ? PortraitShortsCardMargin
                    : PortraitCardMargin)
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
            bool isPortrait = IsPortraitOrientation();
            UpdateSearchResultsPanel();

            if (currentSearchType == SearchContentType.Channels)
            {
                var channelPadding = isPortrait
                    ? new Thickness(18, 0, 18, 16)
                    : new Thickness(32, 0, 32, 16);
                if (SearchResultsList != null)
                {
                    SearchResultsList.Padding = channelPadding;
                }
            }
            else if (currentSearchType == SearchContentType.Shorts && isPortrait)
            {
                // Keep the two-column Shorts grid away from the screen edges.
                if (SearchResultsList != null)
                {
                    SearchResultsList.Padding = new Thickness(20, 0, 20, 20);
                }
            }
            else
            {
                SetItemsControlPadding(SearchResultsList, isPortrait, new Thickness(8, 0, 8, 16));
            }

            SetItemsControlPadding(AllPriorityResultsList, isPortrait, new Thickness(8, 0, 8, 0));

            if (currentSearchType == SearchContentType.Shorts && isPortrait)
            {
                if (SkeletonCardsList != null)
                {
                    SkeletonCardsList.Padding = new Thickness(20, 0, 20, 20);
                }
            }
            else
            {
                SetItemsControlPadding(SkeletonCardsList, isPortrait, new Thickness(8, 0, 8, 16));
            }

            double availableWidth = GetItemsControlContentWidth(SearchResultsList, Window.Current.Bounds.Width);
            double itemWidth;
            int maxColumns;

            if (currentSearchType == SearchContentType.Channels)
            {
                itemWidth = Math.Max(0, availableWidth);
                maxColumns = 1;
            }
            else if (currentSearchType == SearchContentType.Shorts)
            {
                // Portrait: exactly two posters per row. Landscape: fixed poster width so as many
                // columns as fit are shown automatically.
                itemWidth = isPortrait
                    ? Math.Max(112, (availableWidth - 20) / 2.0)
                    : 190.0;
                maxColumns = isPortrait ? 2 : 8;
            }
            else
            {
                itemWidth = isPortrait ? Math.Max(0, availableWidth) : DefaultCardWidth;
                maxColumns = isPortrait ? 1 : 3;
            }

            UpdateItemsWrapGrid(SearchResultsList, itemWidth, maxColumns);
            UpdateItemsWrapGrid(SkeletonCardsList, itemWidth, maxColumns);
            UpdateResponsiveCardMargins(SearchResultsList);
            UpdateResponsiveCardMargins(AllPriorityResultsList);
            UpdateResponsiveCardMargins(SkeletonCardsList);
        }

        private void UpdateSearchResultsPanel()
        {
            var useAllPanel = currentSearchType == SearchContentType.All;
            if (_usingAllSearchResultsPanel.HasValue && _usingAllSearchResultsPanel.Value == useAllPanel)
                return;

            var templateKey = useAllPanel
                ? "AllSearchResultsPanelTemplate"
                : "FilteredSearchResultsPanelTemplate";
            var template = Resources[templateKey] as ItemsPanelTemplate;
            if (SearchResultsList != null && template != null)
                SearchResultsList.ItemsPanel = template;

            _usingAllSearchResultsPanel = useAllPanel;
        }

        private static void SetItemsControlPadding(Control control, bool isPortrait, Thickness landscapePadding)
        {
            if (control == null)
                return;

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

        private void ShortThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null || e.NewSize.Width <= 0)
            {
                return;
            }

            // Native Shorts/search cards are portrait. Keep the visual at 9:16 for every width.
            host.Height = Math.Round(e.NewSize.Width * 16.0 / 9.0);
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null) return;

            var item = button.DataContext as SearchVideoItem;
            if (item == null) return;

            if (item.IsChannel)
            {
                var channelTarget = FirstNonEmpty(item.ChannelId, item.Author, item.Title);
                if (!string.IsNullOrWhiteSpace(channelTarget))
                {
                    Frame.Navigate(typeof(Channel), channelTarget);
                }
                return;
            }

            if (item.IsPlaylist)
            {
                NavigateToPlaylist(item);
                return;
            }

            if (string.IsNullOrWhiteSpace(item.VideoId)) return;

            if (item.IsShort)
            {
                // Shorts accepts a raw 11-character video id as its navigation parameter.
                Frame.Navigate(typeof(Shorts), item.VideoId);
                return;
            }

            // Navigate to Video page
            Frame.Navigate(typeof(Video), item.VideoId);
        }

        private void NavigateToPlaylist(SearchVideoItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId)) return;

            var playlistPageType = Type.GetType("YouTube.Playlist");
            if (playlistPageType == null)
            {
                playlistPageType = Type.GetType("YouTube.Playlist, YouTube");
            }

            if (playlistPageType != null)
            {
                var seed = new PlaylistItem
                {
                    PlaylistId = item.PlaylistId,
                    Title = item.Title,
                    AuthorName = item.Author,
                    ThumbnailUrl = item.Thumbnail,
                    PrivacyText = string.Empty,
                    VideoCountText = item.Views
                };

                Frame.Navigate(playlistPageType, seed);
            }
        }

        private void MoreButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateTypeBottomSheet(true);
        }

        private async void AllTypeButton_Click(object sender, RoutedEventArgs e)
        {
            await SetSearchContentTypeAsync(SearchContentType.All);
        }

        private async void VideosTypeButton_Click(object sender, RoutedEventArgs e)
        {
            await SetSearchContentTypeAsync(SearchContentType.Videos);
        }

        private async void ShortsTypeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ShortsFeatureController.IsEnabled())
                return;

            await SetSearchContentTypeAsync(SearchContentType.Shorts);
        }

        private async void PlaylistsTypeButton_Click(object sender, RoutedEventArgs e)
        {
            await SetSearchContentTypeAsync(SearchContentType.Playlists);
        }

        private async void ChannelsTypeButton_Click(object sender, RoutedEventArgs e)
        {
            await SetSearchContentTypeAsync(SearchContentType.Channels);
        }

        private async Task SetSearchContentTypeAsync(SearchContentType type)
        {
            SuppressSearchBoxNavigationTemporarily();

            if (currentSearchType == type)
            {
                AnimateTypeBottomSheet(false);
                return;
            }

            currentSearchType = type;
            UpdateSearchResultsPanel();
            UpdateTypeCheckMarks();
            UpdateAllShortsSectionVisibility();
            AnimateTypeBottomSheet(false);

            if (!string.IsNullOrWhiteSpace(currentQuery))
            {
                await PerformSearchAsync(currentQuery);
            }
        }

        private void UpdateTypeCheckMarks()
        {
            if (AllTypeCheck != null)
            {
                AllTypeCheck.Visibility = currentSearchType == SearchContentType.All ? Visibility.Visible : Visibility.Collapsed;
            }

            if (VideosTypeCheck != null)
            {
                VideosTypeCheck.Visibility = currentSearchType == SearchContentType.Videos ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ShortsTypeCheck != null)
            {
                ShortsTypeCheck.Visibility = currentSearchType == SearchContentType.Shorts ? Visibility.Visible : Visibility.Collapsed;
            }

            if (PlaylistsTypeCheck != null)
            {
                PlaylistsTypeCheck.Visibility = currentSearchType == SearchContentType.Playlists ? Visibility.Visible : Visibility.Collapsed;
            }

            if (ChannelsTypeCheck != null)
            {
                ChannelsTypeCheck.Visibility = currentSearchType == SearchContentType.Channels ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AnimateTypeBottomSheet(bool show)
        {
            _isTypeBottomSheetOpen = show;
            TypeOverlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            TypeBottomSheetPanel.Visibility = Visibility.Visible;

            var animation = new DoubleAnimation
            {
                From = TypeBottomSheetTransform.Y,
                To = show ? 0 : 421,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true
            };

            var easing = new CubicEase();
            easing.EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn;
            animation.EasingFunction = easing;

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, TypeBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Completed += delegate
            {
                if (!show)
                {
                    TypeBottomSheetPanel.Visibility = Visibility.Collapsed;
                    TypeOverlayGrid.Visibility = Visibility.Collapsed;
                }
            };
            storyboard.Begin();
        }

        private void TypeOverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SuppressSearchBoxNavigationTemporarily();
            AnimateTypeBottomSheet(false);
            e.Handled = true;
        }

        private void TypeDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            SuppressSearchBoxNavigationTemporarily();
            AnimateTypeBottomSheet(false);
            e.Handled = true;
        }

        private void TypeDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!_isTypeBottomSheetOpen) return;

            _isTypeDragActive = true;
            _typeDragStartY = e.GetCurrentPoint(TypeBottomSheetPanel).Position.Y;
            _typeInitialTransformY = TypeBottomSheetTransform.Y;
            TypeDragArea.CapturePointer(e.Pointer);
        }

        private void TypeDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isTypeDragActive) return;

            var currentY = e.GetCurrentPoint(TypeBottomSheetPanel).Position.Y;
            var delta = currentY - _typeDragStartY;
            var newY = Math.Max(0, _typeInitialTransformY + delta);
            TypeBottomSheetTransform.Y = newY;
        }

        private void TypeDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isTypeDragActive) return;

            _isTypeDragActive = false;
            TypeDragArea.ReleasePointerCapture(e.Pointer);

            if (TypeBottomSheetTransform.Y > 120)
            {
                SuppressSearchBoxNavigationTemporarily();
                AnimateTypeBottomSheet(false);
            }
            else
            {
                AnimateTypeBottomSheet(true);
            }
        }

        private enum SearchContentType
        {
            All,
            Videos,
            Shorts,
            Playlists,
            Channels
        }

        private sealed class SearchPageResult
        {
            public List<SearchVideoItem> Items { get; set; }
            public List<SearchVideoItem> Shorts { get; set; }
            public int ShortsInsertIndex { get; set; }
            public string Continuation { get; set; }
        }
    }

    public sealed class SearchVideoItem
    {
        public string VideoId { get; set; }
        public string PlaylistId { get; set; }
        public string ChannelId { get; set; }
        public string ResultType { get; set; }
        public string Title { get; set; }
        public string Author { get; set; }
        public string Views { get; set; }
        public string Duration { get; set; }
        public string Thumbnail { get; set; }
        public string ChannelThumbnailUrl { get; set; }
        public double WatchedPercent { get; set; }

        public Visibility WatchedProgressVisibility
        {
            get
            {
                return !IsPlaylist && !IsChannel && !IsShort && WatchedPercent > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        public ImageSource ChannelAvatarSource
        {
            get
            {
                var url = Thumbnail;
                if (string.IsNullOrWhiteSpace(url))
                {
                    url = "Assets/profile.png";
                }

                url = url.Trim()
                    .Replace("\\u0026", "&")
                    .Replace("\u0026", "&")
                    .Replace("&amp;", "&");

                if (url.StartsWith("//", StringComparison.Ordinal))
                {
                    url = "https:" + url;
                }

                try
                {
                    if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        return new BitmapImage(new Uri(url, UriKind.Absolute));
                    }

                    return new BitmapImage(new Uri("ms-appx:///" + url.TrimStart('/'), UriKind.Absolute));
                }
                catch
                {
                    return new BitmapImage(new Uri("ms-appx:///Assets/profile.png", UriKind.Absolute));
                }
            }
        }

        public bool IsPlaylist
        {
            get { return string.Equals(ResultType, "Playlist", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsChannel
        {
            get { return string.Equals(ResultType, "Channel", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsShort
        {
            get { return string.Equals(ResultType, "Short", StringComparison.OrdinalIgnoreCase); }
        }

        public bool IsCardTabStop
        {
            get { return !IsShort; }
        }

        public Visibility MediaCardVisibility
        {
            get { return (IsChannel || IsShort) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public Visibility ShortsCardVisibility
        {
            get { return IsShort ? Visibility.Visible : Visibility.Collapsed; }
        }

        public Visibility ChannelCardVisibility
        {
            get { return IsChannel ? Visibility.Visible : Visibility.Collapsed; }
        }

        public string ItemKey
        {
            get
            {
                if (IsChannel)
                    return "channel:" + (ChannelId ?? string.Empty);

                if (IsPlaylist)
                    return "playlist:" + (PlaylistId ?? string.Empty);

                if (IsShort)
                    return "short:" + (VideoId ?? string.Empty);

                return "video:" + (VideoId ?? string.Empty);
            }
        }

        public Visibility TitleVisibility
        {
            get { return string.IsNullOrWhiteSpace(Title) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string MetadataLine
        {
            get
            {
                var author = (Author ?? string.Empty).Trim();
                var views = (Views ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(author))
                {
                    return views;
                }

                if (string.IsNullOrWhiteSpace(views))
                {
                    return author;
                }

                return author + " • " + views;
            }
        }

        public Visibility DurationVisibility
        {
            get { return string.IsNullOrWhiteSpace(Duration) ? Visibility.Collapsed : Visibility.Visible; }
        }

        public string MediaBadgeText
        {
            get { return IsPlaylist ? (Views ?? string.Empty) : (Duration ?? string.Empty); }
        }

        public Visibility MediaBadgeVisibility
        {
            get { return string.IsNullOrWhiteSpace(MediaBadgeText) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }
}
