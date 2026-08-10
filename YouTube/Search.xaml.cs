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

namespace YouTube
{
    public sealed partial class Search : Page
    {
        private readonly HttpClient httpClient = new HttpClient();
        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";

        private ObservableCollection<SearchVideoItem> searchResults;
        private bool isLoadingMore = false;
        private string currentQuery = "";
        private string currentContinuation = "";
        private SearchContentType currentSearchType = SearchContentType.Videos;

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

        public Search()
        {
            this.InitializeComponent();
            searchResults = new ObservableCollection<SearchVideoItem>();
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

            // Set Home tab as active
            tabbar?.SetActiveTab(Tabbar.ActiveTab.Home);

            var query = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(query))
            {
                SearchResultsList.ItemsSource = new ObservableCollection<SearchVideoItem>();
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

                searchResults.Clear();
                if (page != null && page.Items != null)
                {
                    foreach (var item in page.Items)
                    {
                        searchResults.Add(item);
                    }

                    currentContinuation = page.Continuation ?? string.Empty;
                }

                UpdateResponsiveCardLayouts();
            }
            catch (Exception ex)
            {
                searchResults.Clear();
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
                        var existingIds = new HashSet<string>(searchResults.Select(v => v.ItemKey), StringComparer.OrdinalIgnoreCase);
                        foreach (var item in page.Items)
                        {
                            if (!existingIds.Contains(item.ItemKey))
                            {
                                searchResults.Add(item);
                                existingIds.Add(item.ItemKey);
                            }
                        }
                    }
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
            var context = "{\"client\":{\"clientName\":\"WEB\",\"clientVersion\":\"2.20250101\",\"hl\":\""
                + JsonEscape(Config.Hl)
                + "\",\"gl\":\""
                + JsonEscape(Config.Gl)
                + "\"}}";
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
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20250101");
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await httpClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                return ParseSearchPage(json, count, type);
            }
        }

        private static string GetSearchParams(SearchContentType type)
        {
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
                if (type == SearchContentType.Playlists)
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

        private static List<SearchVideoItem> ParseVideoResults(IJsonValue root, int maxCount)
        {
            var result = new List<SearchVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var videoRenderers = new List<JsonObject>();

            FindVideoRenderers(root, videoRenderers);

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
                    Author = SimplifyText(renderer, "ownerText", Localization.GetString("Unknown")),
                    Views = SimplifyText(renderer, "viewCountText", string.Empty),
                    Duration = SimplifyText(renderer, "lengthText", string.Empty),
                    Thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg"
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
                result.Add(new SearchVideoItem
                {
                    ResultType = "Short",
                    VideoId = videoId,
                    Title = FirstNonEmpty(
                        SimplifyText(renderer, "headline", string.Empty),
                        SimplifyText(renderer, "title", string.Empty),
                        Localization.GetString("Shorts")),
                    Author = string.Empty,
                    Views = string.Empty,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(
                        ExtractThumbnailUrl(renderer),
                        "https://i.ytimg.com/vi/" + videoId + "/oardefault.jpg",
                        "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg")
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

                // shortsLockupViewModel has overlayMetadata.primaryText.content on current WEB
                // responses. Fall back to the generic lockup title extractor for older shapes.
                var overlayMetadata = GetObject(renderer, "overlayMetadata");
                var primaryText = GetObject(overlayMetadata, "primaryText");
                var title = FirstNonEmpty(
                    ExtractText(primaryText),
                    ExtractLockupTitle(renderer),
                    "Shorts");

                seen.Add(videoId);
                result.Add(new SearchVideoItem
                {
                    ResultType = "Short",
                    VideoId = videoId,
                    Title = title,
                    Author = string.Empty,
                    Views = string.Empty,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(
                        ExtractThumbnailUrl(renderer),
                        "https://i.ytimg.com/vi/" + videoId + "/oardefault.jpg",
                        "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg")
                });
            }

            return result;
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
                    SimplifyText(renderer, "videoCountText", string.Empty),
                    ExtractPlaylistVideoCount(renderer));

                result.Add(new SearchVideoItem
                {
                    ResultType = "Playlist",
                    PlaylistId = playlistId,
                    Title = SimplifyText(renderer, "title", Localization.GetString("Playlist")),
                    Author = FirstNonEmpty(
                        SimplifyText(renderer, "shortBylineText", string.Empty),
                        SimplifyText(renderer, "longBylineText", string.Empty),
                        Localization.GetString("Playlist")),
                    Views = videoCountText,
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractThumbnailUrl(renderer), App.GetThemeAssetUri("Assets/yt_skeleton/video.png").ToString())
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
                    Author = FirstNonEmpty(ExtractLockupSubtitle(renderer), Localization.GetString("Playlist")),
                    Views = FirstNonEmpty(ExtractLockupMetadata(renderer), string.Empty),
                    Duration = string.Empty,
                    Thumbnail = FirstNonEmpty(ExtractThumbnailUrl(renderer), App.GetThemeAssetUri("Assets/yt_skeleton/video.png").ToString())
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
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            InitializePlaceholderCards();
            UpdateResponsiveCardLayouts();
            UpdateTypeCheckMarks();
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            Window.Current.SizeChanged -= Window_SizeChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;

            if (_searchBoxNavigationSuppressTimer != null)
            {
                _searchBoxNavigationSuppressTimer.Stop();
                _searchBoxNavigationSuppressTimer.Tick -= SearchBoxNavigationSuppressTimer_Tick;
                _searchBoxNavigationSuppressTimer = null;
            }
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
            UpdateResponsiveCardMargins(SkeletonCardsList);
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

        private async void VideosTypeButton_Click(object sender, RoutedEventArgs e)
        {
            await SetSearchContentTypeAsync(SearchContentType.Videos);
        }

        private async void ShortsTypeButton_Click(object sender, RoutedEventArgs e)
        {
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
            UpdateTypeCheckMarks();
            AnimateTypeBottomSheet(false);

            if (!string.IsNullOrWhiteSpace(currentQuery))
            {
                await PerformSearchAsync(currentQuery);
            }
        }

        private void UpdateTypeCheckMarks()
        {
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
                To = show ? 0 : 365,
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
            Videos,
            Shorts,
            Playlists,
            Channels
        }

        private sealed class SearchPageResult
        {
            public List<SearchVideoItem> Items { get; set; }
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
    }
}
