using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage.Streams;
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
    public sealed partial class Channel : Page
    {
        private enum ChannelContentTab
        {
            Videos,
            Shorts,
            Playlists,
            Posts
        }

        private enum ChannelSubscriptionState
        {
            Unknown,
            NotSubscribed,
            Subscribed
        }

        private enum ChannelNotificationState
        {
            Unknown,
            Default,
            All,
            None
        }

        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string WebClientVersion = "2.20260220.00.00";
        // Protobuf selector for a channel's "Videos" tab.
        private const string ChannelVideosTabParams = "EgZ2aWRlb3PyBgQKAjoA";
        private const string ChannelShortsTabParams = "EgZzaG9ydHPyBgUKA5oBAA==";
        private const string ChannelPlaylistsTabParams = "EglwbGF5bGlzdHPyBgQKAjoA";
        private const string ChannelPostsTabParams = "Egljb21tdW5pdHnyBgQKAkoA";
        private const string TvClientName = "TVHTML5";
        private const string TvClientVersion = "7.20260429.11.00";
        private const string TvClientHeaderName = "85";
        private const string TvUserAgent = "Mozilla/5.0 (Web0S; Linux; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.79 Safari/537.36 YouTube/7.20260429.11.00";
        private const string MwebClientName = "MWEB";
        private const string MwebClientVersion = "2.20251222.01.00";
        private const string MwebClientHeaderName = "2";
        private const string MwebUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
        private const string DefaultSubscribeParams = "CgIIAxgA";
        private const string DefaultUnsubscribeParams = "EgIIAxgA";
        private const int InitialVideoCount = 50;
        private const int MaxParserNodes = 7000;
        private const double DefaultCardWidth = 360.0;
        private const double VideoThumbnailAspectRatio = 16.0 / 9.0;
        private const string ResponsiveCardTag = "ResponsiveCard";
        private static readonly Thickness PortraitCardMargin = new Thickness(0, 0, 0, 16);
        private static readonly Thickness LandscapeCardMargin = new Thickness(8, 0, 8, 16);

        private readonly HttpClient _httpClient = new HttpClient();
        private readonly ObservableCollection<VideoCardItem> _videos = new ObservableCollection<VideoCardItem>();
        private readonly ObservableCollection<ShortsVideoItem> _shorts = new ObservableCollection<ShortsVideoItem>();
        private readonly ObservableCollection<PlaylistItem> _playlists = new ObservableCollection<PlaylistItem>();
        private readonly ObservableCollection<ChannelPostItem> _posts = new ObservableCollection<ChannelPostItem>();
        private readonly Dictionary<string, double> _watchedProgressByVideoId =
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        private ChannelContentTab _activeTab = ChannelContentTab.Videos;
        private readonly Dictionary<ChannelContentTab, string> _tabContinuations =
            new Dictionary<ChannelContentTab, string>();

        // Same strategy as YouTube.js Channel.getVideos/getShorts/getPlaylists/getCommunity:
        // use the real browseEndpoint attached to the tab returned by YouTube instead of
        // assuming a protobuf params value will remain stable.
        private sealed class ChannelTabEndpoint
        {
            public string BrowseId;
            public string Params;
            public string Url;
            public string Title;
        }

        private readonly Dictionary<ChannelContentTab, ChannelTabEndpoint> _tabEndpoints =
            new Dictionary<ChannelContentTab, ChannelTabEndpoint>();
        private readonly List<ChannelContentTab> _tabOrder = new List<ChannelContentTab>();

        private readonly HashSet<ChannelContentTab> _loadedTabs = new HashSet<ChannelContentTab>();
        private bool _isLoadingTab;
        private bool _isLoadingMoreTab;
        private string _channelAvatarUrl = string.Empty;
        private string _channelParameter = string.Empty;
        private string _currentChannelId = string.Empty;
        private string _fullDescription = string.Empty;
        private bool _isDescriptionBottomSheetOpen;
        private bool _isSubscriptionMenuOpen;
        private ChannelSubscriptionState _currentSubscriptionState = ChannelSubscriptionState.Unknown;
        private ChannelNotificationState _currentNotificationState = ChannelNotificationState.Default;
        private bool _subscriptionRequestInProgress;
        private int _subscriptionStateGeneration;
        private string _subscribeParams = string.Empty;
        private string _unsubscribeParams = string.Empty;
        private string _subscribeClickTrackingParams = string.Empty;
        private string _unsubscribeClickTrackingParams = string.Empty;

        private double _descriptionInitialY;
        private double _descriptionInitialTransformY;
        private bool _descriptionIsDragging;

        private double _subscriptionMenuInitialY;
        private double _subscriptionMenuInitialTransformY;
        private bool _subscriptionMenuIsDragging;

        public Channel()
        {
            this.InitializeComponent();
            VideosItemsControl.ItemsSource = _videos;
            ShortsItemsControl.ItemsSource = _shorts;
            PlaylistsItemsControl.ItemsSource = _playlists;
            PostsItemsControl.ItemsSource = _posts;
            VideosItemsControl.SizeChanged += VideosItemsControl_SizeChanged;
            ShortsItemsControl.SizeChanged += VideosItemsControl_SizeChanged;
            PlaylistsItemsControl.SizeChanged += VideosItemsControl_SizeChanged;

            this.Loaded += Channel_Loaded;
            this.Unloaded += Channel_Unloaded;
            Window.Current.SizeChanged += Window_SizeChanged;
            SystemNavigationManager.GetForCurrentView().BackRequested += Channel_BackRequested;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            _channelParameter = e.Parameter == null ? string.Empty : e.Parameter.ToString();
            await LoadChannelDataAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            SystemNavigationManager.GetForCurrentView().BackRequested -= Channel_BackRequested;
        }

        private void Channel_Loaded(object sender, RoutedEventArgs e)
        {
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            ShortsFeatureController.EnabledChanged += ShortsFeature_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            VideosItemsControl.SizeChanged -= VideosItemsControl_SizeChanged;
            VideosItemsControl.SizeChanged += VideosItemsControl_SizeChanged;
            ApplyAvailableChannelTabs();
            UpdateResponsiveCardLayouts();
        }

        private void Channel_Unloaded(object sender, RoutedEventArgs e)
        {
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            VideosItemsControl.SizeChanged -= VideosItemsControl_SizeChanged;
        }

        private async void ShortsFeature_EnabledChanged(object sender, EventArgs e)
        {
            ApplyAvailableChannelTabs();
            if (ShortsFeatureController.IsEnabled() || _activeTab != ChannelContentTab.Shorts)
                return;

            ChannelContentTab replacement;
            if (TryGetFirstEnabledChannelTab(out replacement))
                await SwitchChannelTabAsync(replacement);
            else
            {
                _activeTab = ChannelContentTab.Videos;
                UpdateChannelTabVisuals();
            }
        }

        private async Task LoadChannelDataAsync()
        {
            if (string.IsNullOrWhiteSpace(_channelParameter))
            {
                ShowErrorPanel(Localization.GetString("ChannelWasNotProvided"));
                return;
            }

            try
            {
                LoadingGrid.Visibility = Visibility.Visible;
                if (LoadingRing != null)
                {
                    LoadingRing.IsActive = true;
                }
                MainContent.Visibility = Visibility.Collapsed;
                ErrorPanel.Visibility = Visibility.Collapsed;
                _videos.Clear();
                _shorts.Clear();
                _playlists.Clear();
                _posts.Clear();
                _loadedTabs.Clear();
                _tabContinuations.Clear();
                _tabEndpoints.Clear();
                _tabOrder.Clear();
                _watchedProgressByVideoId.Clear();
                _activeTab = ChannelContentTab.Videos;
                ResetSubscriptionUi();

                var data = await FetchChannelDataAsync(_channelParameter, InitialVideoCount);
                if (data == null || data.Info == null)
                {
                    ShowErrorPanel(Localization.GetString("CouldNotLoadChannel"));
                    return;
                }

                ApplyChannelInfo(data.Info);
                ApplySubscriptionEndpointData(data.SubscriptionState);

                ApplyAvailableChannelTabs();

                if (_tabEndpoints.ContainsKey(ChannelContentTab.Videos))
                {
                    _activeTab = ChannelContentTab.Videos;
                    foreach (var video in data.Videos)
                    {
                        _videos.Add(video);
                    }

                    _loadedTabs.Add(ChannelContentTab.Videos);
                    _tabContinuations[ChannelContentTab.Videos] = data.Continuation ?? string.Empty;
                }
                else
                {
                    ChannelContentTab initialTab;
                    if (TryGetFirstEnabledChannelTab(out initialTab))
                    {
                        _activeTab = initialTab;
                        await LoadChannelTabFirstPageAsync(_activeTab);
                    }
                }
                UpdateChannelTabVisuals();

                LoadingGrid.Visibility = Visibility.Collapsed;
                if (LoadingRing != null)
                {
                    LoadingRing.IsActive = false;
                }
                MainContent.Visibility = Visibility.Visible;
                UpdateResponsiveCardLayouts();

                // Authentication and subscription-state discovery can require an additional
                // TV/MWEB request. It must not hold the whole channel page behind the spinner.
                BeginLoadChannelSubscriptionState(_currentChannelId, data.SubscriptionState);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Load error: " + ex.Message);
                ShowErrorPanel(Localization.GetString("ChannelLoadingError"));
            }
        }

        private async void BeginLoadChannelSubscriptionState(
            string channelId,
            SubscriptionLoadResult preliminaryResult)
        {
            try
            {
                await LoadChannelSubscriptionStateAsync(channelId, preliminaryResult);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Deferred subscription-state load failed: " + ex.Message);
            }
        }

        private void ChannelPlaylistCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as PlaylistItem : null;
            if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId))
            {
                return;
            }

            Frame.Navigate(typeof(Playlist), item);
        }

        private void ApplyChannelInfo(ChannelPageInfo info)
        {
            _currentChannelId = FirstNonEmpty(info.ChannelId, NormalizeChannelId(_channelParameter));
            ChannelTitle.Text = FirstNonEmpty(info.Title, Localization.GetString("Channel"));
            ChannelHandle.Text = FirstNonEmpty(info.Handle, string.Empty);
            ChannelHandle.Visibility = string.IsNullOrWhiteSpace(ChannelHandle.Text) ? Visibility.Collapsed : Visibility.Visible;

            ChannelStats.Text = BuildStatsText(info.SubscriberCount, info.VideoCount);
            _fullDescription = FirstNonEmpty(info.Description, Localization.GetString("NoDescriptionAvailable"));
            ChannelDescription.Text = _fullDescription;
            FullDescriptionText.Text = _fullDescription;
            DescriptionButton.Visibility = string.IsNullOrWhiteSpace(info.Description) ? Visibility.Collapsed : Visibility.Visible;

            _channelAvatarUrl = info.ThumbnailUrl ?? string.Empty;
            SetImageBrushSource(ChannelIconBrush, info.ThumbnailUrl);

            // Many channels simply have no banner in the API response — show the strip only
            // when there is artwork, instead of an empty grey box.
            var hasBanner = !string.IsNullOrWhiteSpace(info.BannerUrl);
            if (ChannelBannerSection != null)
            {
                ChannelBannerSection.Visibility = hasBanner ? Visibility.Visible : Visibility.Collapsed;
            }
            if (hasBanner)
            {
                SetImageBrushSource(ChannelBannerBrush, info.BannerUrl);
            }

            UpdateSubscriptionVisualState();
        }

        private static void SetImageBrushSource(ImageBrush brush, string url)
        {
            if (brush == null || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                brush.ImageSource = new BitmapImage(new Uri(NormalizeImageUrl(url)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Image error: " + ex.Message);
            }
        }

        private static string NormalizeImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + url;
            }

            return url;
        }

        private void ShowErrorPanel(string errorMessage)
        {
            LoadingGrid.Visibility = Visibility.Collapsed;
            if (LoadingRing != null)
            {
                LoadingRing.IsActive = false;
            }
            MainContent.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Visible;
            ErrorText.Text = errorMessage;
        }

        private async void MainContent_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isLoadingMoreTab || _isLoadingTab)
            {
                return;
            }

            var scrollViewer = sender as ScrollViewer;
            if (scrollViewer == null || scrollViewer.ScrollableHeight <= 0)
            {
                return;
            }

            if (scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 180)
            {
                await LoadMoreActiveTabAsync();
            }
        }

        private async void VideosTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await SwitchChannelTabAsync(ChannelContentTab.Videos);
        }

        private async void ShortsTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (!ShortsFeatureController.IsEnabled())
                return;

            await SwitchChannelTabAsync(ChannelContentTab.Shorts);
        }

        private async void PlaylistsTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await SwitchChannelTabAsync(ChannelContentTab.Playlists);
        }

        private async void PostsTab_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await SwitchChannelTabAsync(ChannelContentTab.Posts);
        }

        private async Task SwitchChannelTabAsync(ChannelContentTab tab)
        {
            if (tab == ChannelContentTab.Shorts && !ShortsFeatureController.IsEnabled())
                return;

            if (_activeTab == tab && _loadedTabs.Contains(tab))
            {
                return;
            }

            _activeTab = tab;
            UpdateChannelTabVisuals();

            if (_loadedTabs.Contains(tab))
            {
                UpdateResponsiveCardLayouts();
                return;
            }

            await LoadChannelTabFirstPageAsync(tab);
        }

        private async Task LoadChannelTabFirstPageAsync(ChannelContentTab tab)
        {
            if (_isLoadingTab || string.IsNullOrWhiteSpace(_currentChannelId))
            {
                return;
            }

            _isLoadingTab = true;
            SetTabLoading(true);
            try
            {
                var page = await FetchChannelTabPageAsync(tab, string.Empty);
                ApplyTabPage(tab, page, true);
                _loadedTabs.Add(tab);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Tab load failed (" + tab + "): " + ex.Message);
            }
            finally
            {
                _isLoadingTab = false;
                SetTabLoading(false);
                UpdateResponsiveCardLayouts();
            }
        }

        private async Task LoadMoreActiveTabAsync()
        {
            string continuation;
            if (_isLoadingMoreTab
                || !_tabContinuations.TryGetValue(_activeTab, out continuation)
                || string.IsNullOrWhiteSpace(continuation))
            {
                return;
            }

            _isLoadingMoreTab = true;
            SetBottomLoading(true);
            try
            {
                var page = await FetchChannelTabPageAsync(_activeTab, continuation);
                ApplyTabPage(_activeTab, page, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Continuation failed (" + _activeTab + "): " + ex.Message);
            }
            finally
            {
                _isLoadingMoreTab = false;
                SetBottomLoading(false);
            }
        }

        private void SetTabLoading(bool loading)
        {
            if (ChannelTabLoadingPanel != null)
            {
                ChannelTabLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ChannelTabLoadingRing != null)
            {
                ChannelTabLoadingRing.IsActive = loading;
            }
        }

        private void SetBottomLoading(bool loading)
        {
            if (ChannelBottomLoadingPanel != null)
            {
                ChannelBottomLoadingPanel.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ChannelBottomLoadingRing != null)
            {
                ChannelBottomLoadingRing.IsActive = loading;
            }
        }

        private void UpdateChannelTabVisuals()
        {
            if (VideosItemsControl != null)
                VideosItemsControl.Visibility = _activeTab == ChannelContentTab.Videos ? Visibility.Visible : Visibility.Collapsed;
            if (ShortsItemsControl != null)
                ShortsItemsControl.Visibility = _activeTab == ChannelContentTab.Shorts ? Visibility.Visible : Visibility.Collapsed;
            if (PlaylistsItemsControl != null)
                PlaylistsItemsControl.Visibility = _activeTab == ChannelContentTab.Playlists ? Visibility.Visible : Visibility.Collapsed;
            if (PostsItemsControl != null)
                PostsItemsControl.Visibility = _activeTab == ChannelContentTab.Posts ? Visibility.Visible : Visibility.Collapsed;

            SetTabIndicator(VideosTabIndicator, VideosTabText, _activeTab == ChannelContentTab.Videos);
            SetTabIndicator(ShortsTabIndicator, ShortsTabText, _activeTab == ChannelContentTab.Shorts);
            SetTabIndicator(PlaylistsTabIndicator, PlaylistsTabText, _activeTab == ChannelContentTab.Playlists);
            SetTabIndicator(PostsTabIndicator, PostsTabText, _activeTab == ChannelContentTab.Posts);
        }

        private void ApplyAvailableChannelTabs()
        {
            ApplyAvailableChannelTab(ChannelContentTab.Videos, VideosTabButton, VideosTabText);
            ApplyAvailableChannelTab(ChannelContentTab.Shorts, ShortsTabButton, ShortsTabText);
            ApplyAvailableChannelTab(ChannelContentTab.Playlists, PlaylistsTabButton, PlaylistsTabText);
            ApplyAvailableChannelTab(ChannelContentTab.Posts, PostsTabButton, PostsTabText);
        }

        private void ApplyAvailableChannelTab(ChannelContentTab tab, FrameworkElement container, TextBlock text)
        {
            ChannelTabEndpoint endpoint;
            var available = _tabEndpoints.TryGetValue(tab, out endpoint)
                && endpoint != null
                && (tab != ChannelContentTab.Shorts || ShortsFeatureController.IsEnabled());
            if (container != null)
            {
                container.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            }
            if (available && text != null && !string.IsNullOrWhiteSpace(endpoint.Title))
            {
                text.Text = endpoint.Title;
            }
        }

        private bool TryGetFirstEnabledChannelTab(out ChannelContentTab tab)
        {
            for (var i = 0; i < _tabOrder.Count; i++)
            {
                var candidate = _tabOrder[i];
                if (candidate != ChannelContentTab.Shorts || ShortsFeatureController.IsEnabled())
                {
                    tab = candidate;
                    return true;
                }
            }

            tab = ChannelContentTab.Videos;
            return false;
        }

        private static void SetTabIndicator(Border indicator, TextBlock text, bool selected)
        {
            if (indicator != null)
            {
                // Keep the underline slot in layout and only fade the marker itself.
                // This prevents the row height from changing when another tab is selected.
                indicator.Visibility = Visibility.Visible;
                indicator.Opacity = selected ? 1.0 : 0.0;
            }

            if (text != null)
            {
                text.Foreground = selected
                    ? (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White))
                    : (App.GetThemeBrush("AppSecondaryTextBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 170)));
            }
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadChannelDataAsync();
        }

        private async Task PreloadPostImagesAsync(List<ChannelPostItem> posts)
        {
            if (posts == null || posts.Count == 0)
            {
                return;
            }

            // Keep network fan-out modest for Windows 10 Mobile. Six simultaneous image requests
            // are enough to make the first screen appear quickly without exhausting sockets/RAM.
            const int BatchSize = 6;
            for (int offset = 0; offset < posts.Count; offset += BatchSize)
            {
                var tasks = new List<Task>();
                var end = Math.Min(posts.Count, offset + BatchSize);

                for (int i = offset; i < end; i++)
                {
                    var item = posts[i];
                    if (item == null || string.IsNullOrWhiteSpace(item.ImageUrl))
                    {
                        continue;
                    }

                    tasks.Add(PreloadPostImageAsync(item));
                }

                if (tasks.Count > 0)
                {
                    await Task.WhenAll(tasks);
                }
            }
        }

        private async void BeginPreloadPostImages(List<ChannelPostItem> posts)
        {
            try
            {
                await PreloadPostImagesAsync(posts);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Deferred post image preload failed: " + ex.Message);
            }
        }

        private async Task PreloadPostImageAsync(ChannelPostItem item)
        {
            try
            {
                var bitmap = await LoadRemoteBitmapWithFallbackAsync(item.ImageUrl, 1200);
                item.ImageSource = bitmap;

                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Post image preload "
                    + (bitmap != null ? "OK: " : "FAILED: ")
                    + item.ImageUrl);
            }
            catch (Exception ex)
            {
                item.ImageSource = CreateRemoteBitmapSource(item.ImageUrl, 1200);
                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Post image preload exception for "
                    + item.ImageUrl + ": " + ex.Message);
            }
        }

        private static BitmapImage CreateRemoteBitmapSource(string rawUrl, int decodePixelWidth)
        {
            var candidates = BuildCommunityImageCandidates(rawUrl);
            if (candidates.Count == 0)
            {
                return null;
            }

            try
            {
                // Prefer the plain-size ggpht variant for the native UWP image pipeline.
                var url = candidates.Count > 1 ? candidates[1] : candidates[0];
                var bitmap = new BitmapImage();
                if (decodePixelWidth > 0)
                {
                    bitmap.DecodePixelType = DecodePixelType.Logical;
                    bitmap.DecodePixelWidth = decodePixelWidth;
                }
                bitmap.UriSource = new Uri(url);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }

        private async Task<BitmapImage> LoadRemoteBitmapWithFallbackAsync(
            string rawUrl,
            int decodePixelWidth)
        {
            var candidates = BuildCommunityImageCandidates(rawUrl);
            Exception lastError = null;

            for (int i = 0; i < candidates.Count; i++)
            {
                try
                {
                    var bitmap = await LoadRemoteBitmapFromExactUrlAsync(
                        candidates[i],
                        decodePixelWidth);
                    if (bitmap != null)
                    {
                        return bitmap;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] Community image candidate failed: "
                        + candidates[i] + " => " + ex.Message);
                }
            }

            if (lastError != null)
            {
                throw lastError;
            }

            return null;
        }

        private static List<string> BuildCommunityImageCandidates(string rawUrl)
        {
            var result = new List<string>();
            var original = NormalizeImageUrl(rawUrl);
            if (string.IsNullOrWhiteSpace(original))
            {
                return result;
            }

            AddUniqueImageCandidate(result, original);

            // ggpht community attachments often end in a WEBP-oriented transformation such as
            // "=s640-c-fcrop64=...-rw-nd-v1". Old Win10 image codecs may reject that payload.
            // Try plain size transforms as separate URLs, while retaining the original first.
            if (original.IndexOf("ggpht", StringComparison.OrdinalIgnoreCase) >= 0
                || original.IndexOf("googleusercontent", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var equals = original.LastIndexOf('=');
                if (equals > original.IndexOf("://", StringComparison.Ordinal) + 3)
                {
                    var baseUrl = original.Substring(0, equals);
                    AddUniqueImageCandidate(result, baseUrl + "=s1200");
                    AddUniqueImageCandidate(result, baseUrl + "=s800");
                    AddUniqueImageCandidate(result, baseUrl + "=s640");
                }
            }

            return result;
        }

        private static void AddUniqueImageCandidate(List<string> list, string value)
        {
            if (list == null || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i], value, StringComparison.Ordinal))
                {
                    return;
                }
            }

            list.Add(value);
        }

        private async Task<BitmapImage> LoadRemoteBitmapFromExactUrlAsync(
            string url,
            int decodePixelWidth)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; ARM; Touch) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Edge/15.15063");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                request.Headers.TryAddWithoutValidation(
                    "Accept",
                    "image/jpeg,image/png,image/*;q=0.8,*/*;q=0.5");

                using (var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception("HTTP " + (int)response.StatusCode);
                    }

                    var bytes = await response.Content.ReadAsByteArrayAsync();
                    if (bytes == null || bytes.Length == 0)
                    {
                        throw new Exception("empty image response");
                    }

                    var contentType = response.Content.Headers.ContentType != null
                        ? response.Content.Headers.ContentType.MediaType
                        : string.Empty;
                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] Community image HTTP "
                        + bytes.Length + " bytes, type=" + contentType);

                    var stream = new InMemoryRandomAccessStream();
                    using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                    }

                    stream.Seek(0);

                    var bitmap = new BitmapImage();
                    if (decodePixelWidth > 0)
                    {
                        bitmap.DecodePixelType = DecodePixelType.Logical;
                        bitmap.DecodePixelWidth = decodePixelWidth;
                    }

                    await bitmap.SetSourceAsync(stream);
                    return bitmap;
                }
            }
        }


        private async void PostAvatar_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = image != null ? image.DataContext as ChannelPostItem : null;
            if (image == null || item == null || string.IsNullOrWhiteSpace(item.AuthorThumbnailUrl))
            {
                if (image != null) image.Source = null;
                return;
            }

            var expectedItem = item;
            try
            {
                var bitmap = await LoadRemoteBitmapWithFallbackAsync(item.AuthorThumbnailUrl, 96);
                if (ReferenceEquals(image.DataContext, expectedItem))
                {
                    image.Source = bitmap;
                }
            }
            catch
            {
                if (ReferenceEquals(image.DataContext, expectedItem))
                {
                    image.Source = null;
                }
            }
        }

        private void ShortThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null || e.NewSize.Width <= 0)
            {
                return;
            }

            host.Height = Math.Round(e.NewSize.Width * 16.0 / 9.0);
        }

        private void ShortCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as ShortsVideoItem : null;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            Frame.Navigate(typeof(Shorts), item.VideoId);
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button == null)
            {
                return;
            }

            var item = button.DataContext as VideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

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

        private async Task<ChannelPageData> FetchChannelDataAsync(string channelInput, int count)
        {
            var channelId = NormalizeChannelId(channelInput);
            if (string.IsNullOrWhiteSpace(channelId))
            {
                channelId = await ResolveHandleToChannelIdAsync(channelInput);
            }

            if (string.IsNullOrWhiteSpace(channelId))
            {
                return null;
            }

            var canonicalPayload = new JsonObject();
            canonicalPayload["context"] = BuildContext();
            canonicalPayload["browseId"] = JsonValue.CreateStringValue(channelId);

            // The Videos response and the canonical channel response are independent. Starting
            // both together removes one complete network round trip from initial page loading.
            var videosRequest = PostInnertubeAsync("browse", BuildBrowsePayload(channelId));
            var canonicalRequest = PostInnertubeAsync("browse", canonicalPayload.Stringify());
            // Public WEB channel renderers frequently omit resume overlays. Ask the signed-in
            // TV client for the same Videos tab in parallel and merge only the progress field.
            var tvVideosRequest = Config.GetChannelVideosAsync(channelId);
            var json = await videosRequest;
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var root = JsonValue.Parse(json).GetObject();

            // The Videos-tab response is useful for the initial video grid, but YouTube.js does
            // not use it as the source of sibling tab endpoints. It first loads the ordinary
            // channel page and then calls the endpoint attached to the requested Tab.
            //
            // Keep the endpoints from this response as a fallback, then refresh them from a
            // canonical browse without params. This is important for Playlists on current WEB.
            ExtractChannelTabEndpoints(root, channelId);
            try
            {
                var canonicalJson = await canonicalRequest;
                if (!string.IsNullOrWhiteSpace(canonicalJson))
                {
                    ApplyCanonicalChannelTabEndpoints(
                        JsonValue.Parse(canonicalJson).GetObject(),
                        channelId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Parallel canonical browse failed: " + ex.Message);
            }

            var info = ExtractChannelInfo(root, channelId);
            var subscriptionState = ExtractSubscriptionStateFromBrowse(root, "public /browse", channelId);
            var videosContent = FindVideosContent(root);
            var videos = new List<VideoCardItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visited = 0;

            ExtractVideosRecursively(videosContent ?? root, videos, info.Title, seen, count, ref visited);
            ApplyKnownChannelAvatarToVideos(videos, info.ChannelId, info.ThumbnailUrl);
            try
            {
                RememberAndApplyWatchedProgress(videos, await tvVideosRequest);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] TV progress merge failed: " + ex.Message);
            }

            return new ChannelPageData
            {
                Info = info,
                Videos = videos,
                Continuation = ExtractContinuationToken(root),
                SubscriptionState = subscriptionState
            };
        }

        private sealed class ChannelTabPage
        {
            public List<VideoCardItem> Videos { get; set; }
            public List<ShortsVideoItem> Shorts { get; set; }
            public List<PlaylistItem> Playlists { get; set; }
            public List<ChannelPostItem> Posts { get; set; }
            public string Continuation { get; set; }
        }

        private async Task<ChannelTabPage> FetchChannelTabPageAsync(ChannelContentTab tab, string continuation)
        {
            var payload = new JsonObject();
            payload["context"] = BuildContext();

            if (!string.IsNullOrWhiteSpace(continuation))
            {
                // Channel continuations are requested exactly as YouTube.js does: the token is
                // the continuation payload; no tab params are mixed into a continuation call.
                payload["continuation"] = JsonValue.CreateStringValue(continuation);
            }
            else
            {
                ChannelTabEndpoint endpoint;
                if (_tabEndpoints.TryGetValue(tab, out endpoint) && endpoint != null)
                {
                    payload["browseId"] = JsonValue.CreateStringValue(
                        string.IsNullOrWhiteSpace(endpoint.BrowseId)
                            ? _currentChannelId
                            : endpoint.BrowseId);

                    if (!string.IsNullOrWhiteSpace(endpoint.Params))
                    {
                        payload["params"] = JsonValue.CreateStringValue(endpoint.Params);
                    }

                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] Opening tab " + tab
                        + " using page endpoint; browseId=" + endpoint.BrowseId
                        + ", params=" + endpoint.Params);
                }
                else
                {
                    // Old/fallback path for unusual channel responses that omit the tab endpoint.
                    payload["browseId"] = JsonValue.CreateStringValue(_currentChannelId);
                    var fallbackParams = GetChannelTabParams(tab);
                    if (!string.IsNullOrWhiteSpace(fallbackParams))
                    {
                        payload["params"] = JsonValue.CreateStringValue(fallbackParams);
                    }

                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] No dynamic endpoint for " + tab + "; using fallback params");
                }
            }

            var json = await PostInnertubeAsync("browse", payload.Stringify());
            if (string.IsNullOrWhiteSpace(json))
            {
                return new ChannelTabPage();
            }

            var root = JsonValue.Parse(json).GetObject();
            System.Diagnostics.Debug.WriteLine(
                "[Channel] Tab response " + tab + ": bytes=" + json.Length
                + ", continuationRequest=" + (!string.IsNullOrWhiteSpace(continuation)));

            var page = new ChannelTabPage
            {
                Videos = new List<VideoCardItem>(),
                Shorts = new List<ShortsVideoItem>(),
                Playlists = new List<PlaylistItem>(),
                Posts = new List<ChannelPostItem>(),
                Continuation = ExtractContinuationToken(root)
            };

            if (tab == ChannelContentTab.Videos)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var visited = 0;
                ExtractVideosRecursively(root, page.Videos, ChannelTitle != null ? ChannelTitle.Text : string.Empty,
                    seen, 60, ref visited);
                ApplyKnownChannelAvatarToVideos(page.Videos, _currentChannelId, _channelAvatarUrl);
                ApplyRememberedWatchedProgress(page.Videos);
            }
            else if (tab == ChannelContentTab.Shorts)
            {
                page.Shorts = ParseChannelShorts(root, 60);
            }
            else if (tab == ChannelContentTab.Playlists)
            {
                page.Playlists = ParseChannelPlaylists(
                    root,
                    200,
                    ChannelTitle != null ? ChannelTitle.Text : string.Empty);

                // Some YouTube builds accept the tab endpoint but return a shell whose actual
                // playlist data is hydrated only on the /playlists web route. YouTube.js follows
                // the tab's navigation URL, so do the same as a second path instead of guessing
                // another protobuf params value.
                if ((page.Playlists == null || page.Playlists.Count == 0)
                    && string.IsNullOrWhiteSpace(continuation))
                {
                    try
                    {
                        var htmlPage = await LoadPlaylistsFromChannelWebPageCoreAsync();
                        if (htmlPage != null
                            && htmlPage.Items != null
                            && htmlPage.Items.Count > 0)
                        {
                            page.Playlists = htmlPage.Items;
                            if (!string.IsNullOrWhiteSpace(htmlPage.Continuation))
                            {
                                page.Continuation = htmlPage.Continuation;
                            }

                            System.Diagnostics.Debug.WriteLine(
                                "[Channel] /playlists HTML fallback loaded "
                                + htmlPage.Items.Count + " item(s)");
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Channel] /playlists HTML fallback failed: " + ex.Message);
                    }
                }
            }
            else if (tab == ChannelContentTab.Posts)
            {
                page.Posts = ParseChannelPosts(root, 50, _channelAvatarUrl);

                // Show post text immediately. ImageSource notifies the template when each
                // attachment finishes, so media no longer blocks the entire Posts page.
                BeginPreloadPostImages(page.Posts);
            }

            return page;
        }

        private void RememberAndApplyWatchedProgress(
            IList<VideoCardItem> target,
            IEnumerable<VideoCardItem> tvVideos)
        {
            if (tvVideos != null)
            {
                foreach (var video in tvVideos)
                {
                    if (video != null && !string.IsNullOrWhiteSpace(video.VideoId) && video.WatchedPercent > 0)
                    {
                        _watchedProgressByVideoId[video.VideoId] = video.WatchedPercent;
                    }
                }
            }

            ApplyRememberedWatchedProgress(target);
        }

        private void ApplyRememberedWatchedProgress(IEnumerable<VideoCardItem> videos)
        {
            if (videos == null)
            {
                return;
            }

            foreach (var video in videos)
            {
                double percent;
                if (video != null
                    && !string.IsNullOrWhiteSpace(video.VideoId)
                    && _watchedProgressByVideoId.TryGetValue(video.VideoId, out percent))
                {
                    video.WatchedPercent = percent;
                }
            }
        }

        private sealed class PlaylistHtmlPage
        {
            public List<PlaylistItem> Items;
            public string Continuation;
        }

        private async Task<PlaylistHtmlPage> LoadPlaylistsFromChannelWebPageCoreAsync()
        {
            var endpointUrl = string.Empty;
            ChannelTabEndpoint endpoint;
            if (_tabEndpoints.TryGetValue(ChannelContentTab.Playlists, out endpoint)
                && endpoint != null)
            {
                endpointUrl = endpoint.Url;
            }

            if (string.IsNullOrWhiteSpace(endpointUrl))
            {
                endpointUrl = "/channel/" + _currentChannelId + "/playlists";
            }

            if (endpointUrl.StartsWith("/", StringComparison.Ordinal))
            {
                endpointUrl = "https://www.youtube.com" + endpointUrl;
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, endpointUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);

                using (var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseContentRead))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new Exception(
                            "playlist page HTTP " + (int)response.StatusCode);
                    }

                    var html = await response.Content.ReadAsStringAsync();
                    var initialDataJson = ExtractYtInitialDataJson(html);
                    if (string.IsNullOrWhiteSpace(initialDataJson))
                    {
                        throw new Exception("ytInitialData not found in /playlists HTML");
                    }

                    var root = JsonValue.Parse(initialDataJson);
                    return new PlaylistHtmlPage
                    {
                        Items = ParseChannelPlaylists(
                            root,
                            200,
                            ChannelTitle != null ? ChannelTitle.Text : string.Empty),
                        Continuation = ExtractContinuationToken(root)
                    };
                }
            }
        }

        private async Task<List<PlaylistItem>> LoadPlaylistsFromChannelWebPageAsync()
        {
            var page = await LoadPlaylistsFromChannelWebPageCoreAsync();
            return page != null && page.Items != null
                ? page.Items
                : new List<PlaylistItem>();
        }

        private static string ExtractYtInitialDataJson(string html)
        {
            if (string.IsNullOrWhiteSpace(html))
            {
                return string.Empty;
            }

            var markers = new[]
            {
                "var ytInitialData =",
                "window[\\\"ytInitialData\\\"] =",
                "ytInitialData ="
            };

            for (int i = 0; i < markers.Length; i++)
            {
                var markerIndex = html.IndexOf(markers[i], StringComparison.Ordinal);
                if (markerIndex < 0)
                {
                    continue;
                }

                var braceStart = html.IndexOf('{', markerIndex + markers[i].Length);
                if (braceStart < 0)
                {
                    continue;
                }

                var json = ExtractBalancedJsonObject(html, braceStart);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    return json;
                }
            }

            return string.Empty;
        }

        private static string ExtractBalancedJsonObject(string text, int start)
        {
            if (string.IsNullOrEmpty(text)
                || start < 0
                || start >= text.Length
                || text[start] != '{')
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;

            for (int i = start; i < text.Length; i++)
            {
                var c = text[i];

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (c == '\\')
                    {
                        escaped = true;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return text.Substring(start, i - start + 1);
                    }
                }
            }

            return string.Empty;
        }

        private async Task RefreshCanonicalChannelTabEndpointsAsync(string channelId)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return;
            }

            try
            {
                var payload = new JsonObject();
                payload["context"] = BuildContext();
                payload["browseId"] = JsonValue.CreateStringValue(channelId);

                var json = await PostInnertubeAsync("browse", payload.Stringify());
                if (string.IsNullOrWhiteSpace(json))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] Canonical browse returned no data; keeping video-tab endpoints");
                    return;
                }

                ApplyCanonicalChannelTabEndpoints(JsonValue.Parse(json).GetObject(), channelId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Canonical tab endpoint refresh failed: " + ex.Message);
            }
        }

        private void ApplyCanonicalChannelTabEndpoints(JsonObject root, string channelId)
        {
            // The canonical page is the source of truth, just like youtube-ios. Keep the
            // earlier list only when parsing the canonical response found no tabs at all.
            var oldEndpoints = new Dictionary<ChannelContentTab, ChannelTabEndpoint>(_tabEndpoints);
            var oldOrder = new List<ChannelContentTab>(_tabOrder);
            ExtractChannelTabEndpoints(root, channelId);

            if (_tabEndpoints.Count == 0)
            {
                foreach (var pair in oldEndpoints)
                {
                    _tabEndpoints[pair.Key] = pair.Value;
                }
                _tabOrder.Clear();
                _tabOrder.AddRange(oldOrder);
            }

            System.Diagnostics.Debug.WriteLine(
                "[Channel] Canonical tab endpoints refreshed: " + _tabEndpoints.Count);
        }

        private void ExtractChannelTabEndpoints(JsonObject root, string fallbackChannelId)
        {
            _tabEndpoints.Clear();
            _tabOrder.Clear();

            if (root == null)
            {
                return;
            }

            var tabRenderers = new List<JsonObject>();
            FindObjectsByKey(root, "tabRenderer", tabRenderers, 0, 12);
            var expandableTabs = new List<JsonObject>();
            FindObjectsByKey(root, "expandableTabRenderer", expandableTabs, 0, 12);
            tabRenderers.AddRange(expandableTabs);

            for (int i = 0; i < tabRenderers.Count; i++)
            {
                var tabRenderer = tabRenderers[i];
                if (tabRenderer == null)
                {
                    continue;
                }

                var endpoint = FirstObject(
                    GetObject(tabRenderer, "endpoint"),
                    GetObject(tabRenderer, "navigationEndpoint"));

                var browse = GetObject(endpoint, "browseEndpoint");
                if (browse == null)
                {
                    continue;
                }

                var browseId = FirstNonEmpty(
                    GetString(browse, "browseId"),
                    fallbackChannelId);
                var browseParams = GetString(browse, "params");

                var metadata = GetObject(GetObject(endpoint, "commandMetadata"), "webCommandMetadata");
                var url = FirstNonEmpty(
                    GetString(metadata, "url"),
                    GetString(browse, "canonicalBaseUrl"));

                var title = FirstNonEmpty(
                    GetString(tabRenderer, "title"),
                    ExtractText(GetObject(tabRenderer, "title")));

                ChannelContentTab tab;
                if (!TryMapChannelTab(title, url, out tab))
                {
                    continue;
                }

                if (string.Equals(title, "Search", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(title, Localization.GetString("Search"), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                _tabEndpoints[tab] = new ChannelTabEndpoint
                {
                    BrowseId = browseId,
                    Params = browseParams,
                    Url = url,
                    Title = title
                };
                if (!_tabOrder.Contains(tab))
                {
                    _tabOrder.Add(tab);
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Channel] Captured tab endpoint " + tab
                    + ": url=" + url
                    + ", browseId=" + browseId
                    + ", params=" + browseParams);
            }

        }

        private static JsonObject FirstObject(params JsonObject[] objects)
        {
            if (objects == null)
            {
                return null;
            }

            for (int i = 0; i < objects.Length; i++)
            {
                if (objects[i] != null)
                {
                    return objects[i];
                }
            }

            return null;
        }

        private static bool TryMapChannelTab(string title, string url, out ChannelContentTab tab)
        {
            var normalizedTitle = (title ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedUrl = (url ?? string.Empty).Trim().ToLowerInvariant();

            if (normalizedUrl.EndsWith("/shorts", StringComparison.Ordinal)
                || normalizedUrl.IndexOf("/shorts?", StringComparison.Ordinal) >= 0
                || normalizedTitle == "shorts")
            {
                tab = ChannelContentTab.Shorts;
                return true;
            }

            if (normalizedUrl.EndsWith("/playlists", StringComparison.Ordinal)
                || normalizedUrl.IndexOf("/playlists?", StringComparison.Ordinal) >= 0
                || normalizedTitle == "playlists")
            {
                tab = ChannelContentTab.Playlists;
                return true;
            }

            if (normalizedUrl.EndsWith("/posts", StringComparison.Ordinal)
                || normalizedUrl.IndexOf("/posts?", StringComparison.Ordinal) >= 0
                || normalizedUrl.EndsWith("/community", StringComparison.Ordinal)
                || normalizedTitle == "posts"
                || normalizedTitle == "community")
            {
                tab = ChannelContentTab.Posts;
                return true;
            }

            if (normalizedUrl.EndsWith("/videos", StringComparison.Ordinal)
                || normalizedUrl.IndexOf("/videos?", StringComparison.Ordinal) >= 0
                || normalizedTitle == "videos")
            {
                tab = ChannelContentTab.Videos;
                return true;
            }

            tab = ChannelContentTab.Videos;
            return false;
        }

        private static string GetChannelTabParams(ChannelContentTab tab)
        {
            if (tab == ChannelContentTab.Shorts) return ChannelShortsTabParams;
            if (tab == ChannelContentTab.Playlists) return ChannelPlaylistsTabParams;
            if (tab == ChannelContentTab.Posts) return ChannelPostsTabParams;
            return ChannelVideosTabParams;
        }

        private void ApplyTabPage(ChannelContentTab tab, ChannelTabPage page, bool replace)
        {
            if (page == null)
            {
                _tabContinuations[tab] = string.Empty;
                return;
            }

            _tabContinuations[tab] = page.Continuation ?? string.Empty;

            if (tab == ChannelContentTab.Videos)
            {
                if (replace) _videos.Clear();
                var seen = new HashSet<string>(_videos.Select(v => v.VideoId), StringComparer.OrdinalIgnoreCase);
                if (page.Videos != null)
                {
                    foreach (var item in page.Videos)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && seen.Add(item.VideoId))
                            _videos.Add(item);
                    }
                }
            }
            else if (tab == ChannelContentTab.Shorts)
            {
                if (replace) _shorts.Clear();
                var seen = new HashSet<string>(_shorts.Select(v => v.VideoId), StringComparer.OrdinalIgnoreCase);
                if (page.Shorts != null)
                {
                    foreach (var item in page.Shorts)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && seen.Add(item.VideoId))
                            _shorts.Add(item);
                    }
                }
            }
            else if (tab == ChannelContentTab.Playlists)
            {
                if (replace) _playlists.Clear();
                var seen = new HashSet<string>(_playlists.Select(v => v.PlaylistId), StringComparer.OrdinalIgnoreCase);
                if (page.Playlists != null)
                {
                    foreach (var item in page.Playlists)
                    {
                        if (item != null && !string.IsNullOrWhiteSpace(item.PlaylistId) && seen.Add(item.PlaylistId))
                            _playlists.Add(item);
                    }
                }
            }
            else
            {
                if (replace) _posts.Clear();
                if (page.Posts != null)
                {
                    foreach (var item in page.Posts)
                        if (item != null) _posts.Add(item);
                }
            }
        }

        private static List<ShortsVideoItem> ParseChannelShorts(IJsonValue root, int maxCount)
        {
            var result = new List<ShortsVideoItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var reelRenderers = new List<JsonObject>();
            var lockups = new List<JsonObject>();

            FindObjectsByKey(root, "reelItemRenderer", reelRenderers, 0, 14);
            FindObjectsByKey(root, "shortsLockupViewModel", lockups, 0, 14);

            for (int i = 0; i < reelRenderers.Count && result.Count < maxCount; i++)
            {
                var renderer = reelRenderers[i];
                var videoId = FirstNonEmpty(
                    GetString(renderer, "videoId"),
                    FindStringByKey(renderer, "videoId", 0, 14));

                if (string.IsNullOrWhiteSpace(videoId) || !seen.Add(videoId))
                {
                    continue;
                }

                var title = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "primaryText"),
                    ExtractText(GetObject(renderer, "headline")),
                    ExtractText(GetObject(renderer, "title")),
                    FindFirstTextByKey(renderer, "headline", 0, 10),
                    "Shorts");

                var thumbnail = FirstNonEmpty(
                    ExtractThumbnailUrl(GetObject(renderer, "thumbnail")),
                    ExtractFirstImageUrl(renderer, "thumbnail"),
                    ExtractFirstImageUrl(renderer, "image"),
                    "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg");

                var viewCount = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "secondaryText"),
                    ExtractText(GetObject(renderer, "shortViewCountText")),
                    ExtractText(GetObject(renderer, "viewCountText")),
                    FindFirstTextByKey(renderer, "shortViewCountText", 0, 12),
                    FindFirstTextByKey(renderer, "viewCountText", 0, 12));

                result.Add(new ShortsVideoItem
                {
                    VideoId = videoId,
                    Title = title,
                    ChannelName = string.Empty,
                    ThumbnailUrl = thumbnail,
                    ViewCount = viewCount
                });
            }

            for (int i = 0; i < lockups.Count && result.Count < maxCount; i++)
            {
                var renderer = lockups[i];

                // Current shortsLockupViewModel keeps the id under onTap.reelWatchEndpoint.
                var videoId = FindStringByKey(renderer, "videoId", 0, 14);
                if (string.IsNullOrWhiteSpace(videoId) || !seen.Add(videoId))
                {
                    continue;
                }

                var title = FirstNonEmpty(
                    ExtractShortsMetadataText(renderer, "primaryText"),
                    FindFirstTextByKey(renderer, "primaryText", 0, 10),
                    FindFirstTextByKey(renderer, "title", 0, 10),
                    "Shorts");

                var thumbnail = FirstNonEmpty(
                    ExtractThumbnailUrl(GetObject(renderer, "thumbnail")),
                    ExtractFirstImageUrl(renderer, "thumbnail"),
                    ExtractFirstImageUrl(renderer, "image"),
                    "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg");

                result.Add(new ShortsVideoItem
                {
                    VideoId = videoId,
                    Title = title,
                    ChannelName = string.Empty,
                    ThumbnailUrl = thumbnail,
                    ViewCount = FirstNonEmpty(
                        ExtractShortsMetadataText(renderer, "secondaryText"),
                        FindFirstTextByKey(renderer, "secondaryText", 0, 12),
                        FindFirstTextByKey(renderer, "viewCountText", 0, 12))
                });
            }

            System.Diagnostics.Debug.WriteLine(
                "[Channel] Shorts parsed: reel=" + reelRenderers.Count
                + ", lockup=" + lockups.Count
                + ", items=" + result.Count);

            return result;
        }

        // Matches youtube-ios: prefer overlayMetadata and then find the rendered
        // primary/secondary value anywhere in the Shorts view-model.
        private static string ExtractShortsMetadataText(JsonObject renderer, string fieldName)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(fieldName)) return string.Empty;

            var overlays = new List<JsonObject>();
            FindObjectsByKey(renderer, "overlayMetadata", overlays, 0, 32);
            for (int i = 0; i < overlays.Count; i++)
            {
                var text = ExtractRenderedField(overlays[i], fieldName);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }

            return FindFirstTextByKey(renderer, fieldName, 0, 32);
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

        private static List<PlaylistItem> ParseChannelPlaylists(
            IJsonValue root,
            int maxCount,
            string fallbackAuthor)
        {
            var result = new List<PlaylistItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Current WEB channel playlists are richGridRenderer -> richItemRenderer ->
            // lockupViewModel. This is the same shape YouTube currently uses elsewhere for
            // playlists: contentId + LOCKUP_CONTENT_TYPE_PLAYLIST + lockupMetadataViewModel.
            var lockups = new List<JsonObject>();
            FindObjectsByKey(root, "lockupViewModel", lockups, 0, 18);

            for (int i = 0; i < lockups.Count && result.Count < maxCount; i++)
            {
                var renderer = lockups[i];
                if (renderer == null)
                {
                    continue;
                }

                var contentType = GetString(renderer, "contentType");
                if (!string.IsNullOrWhiteSpace(contentType)
                    && contentType.IndexOf("PLAYLIST", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var playlistId = FirstNonEmpty(
                    GetString(renderer, "contentId"),
                    FindPlaylistIdFromNavigation(renderer));

                if (string.IsNullOrWhiteSpace(playlistId) || !seen.Add(playlistId))
                {
                    continue;
                }

                var metadata = GetObject(renderer, "metadata");
                var lockupMetadata = GetObject(metadata, "lockupMetadataViewModel");
                var titleNode = GetObject(lockupMetadata, "title");

                var title = FirstNonEmpty(
                    GetString(titleNode, "content"),
                    ExtractText(titleNode),
                    FindFirstTextByKey(renderer, "title", 0, 12),
                    "Playlist");

                var thumbnail = ExtractPlaylistLockupThumbnail(renderer);
                var countText = FirstNonEmpty(
                    ExtractPlaylistBadgeText(renderer),
                    ExtractPlaylistLockupCount(lockupMetadata));

                result.Add(new PlaylistItem
                {
                    PlaylistId = playlistId,
                    Title = title,
                    AuthorName = FirstNonEmpty(
                        fallbackAuthor,
                        Config.ExtractPlaylistCardAuthor(renderer)),
                    ThumbnailUrl = thumbnail,
                    VideoCountText = countText
                });
            }

            // Legacy shapes are still used on some channels / clients.
            var legacy = new List<JsonObject>();
            FindObjectsByKey(root, "playlistRenderer", legacy, 0, 18);
            FindObjectsByKey(root, "gridPlaylistRenderer", legacy, 0, 18);
            FindObjectsByKey(root, "tileRenderer", legacy, 0, 18);

            for (int i = 0; i < legacy.Count && result.Count < maxCount; i++)
            {
                var renderer = legacy[i];
                var playlistId = FirstNonEmpty(
                    GetString(renderer, "playlistId"),
                    GetString(renderer, "contentId"),
                    FindPlaylistIdFromNavigation(renderer));

                if (playlistId.StartsWith("VL", StringComparison.OrdinalIgnoreCase))
                {
                    playlistId = playlistId.Substring(2);
                }

                if (string.IsNullOrWhiteSpace(playlistId) || !seen.Add(playlistId))
                {
                    continue;
                }

                result.Add(new PlaylistItem
                {
                    PlaylistId = playlistId,
                    Title = FirstNonEmpty(
                        ExtractText(GetObject(renderer, "title")),
                        FindFirstTextByKey(renderer, "title", 0, 10),
                        "Playlist"),
                    AuthorName = FirstNonEmpty(
                        fallbackAuthor,
                        Config.ExtractPlaylistCardAuthor(renderer)),
                    ThumbnailUrl = FirstNonEmpty(
                        ExtractFirstImageUrl(GetObject(GetObject(renderer, "header"), "tileHeaderRenderer"), "thumbnail"),
                        ExtractThumbnailUrl(GetObject(renderer, "thumbnail")),
                        ExtractFirstImageUrl(renderer, "thumbnail"),
                        ExtractFirstImageUrl(renderer, "image")),
                    VideoCountText = FirstNonEmpty(
                        ExtractPlaylistBadgeText(renderer),
                        ExtractText(GetObject(renderer, "videoCountShortText")),
                        ExtractText(GetObject(renderer, "videoCountText")),
                        GetString(renderer, "videoCount"),
                        FindFirstTextByKey(renderer, "videoCountText", 0, 12))
                });
            }

            System.Diagnostics.Debug.WriteLine(
                "[Channel] Playlists exact parser: lockups=" + lockups.Count
                + ", legacy=" + legacy.Count
                + ", items=" + result.Count);

            return result;
        }

        private static string ExtractPlaylistLockupThumbnail(JsonObject renderer)
        {
            try
            {
                var contentImage = GetObject(renderer, "contentImage");
                var collection = GetObject(contentImage, "collectionThumbnailViewModel");
                var primary = GetObject(collection, "primaryThumbnail");
                var thumbnailView = GetObject(primary, "thumbnailViewModel");
                var image = GetObject(thumbnailView, "image");

                var url = ExtractLargestThumbnailSource(image);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }

                // Some lockups use thumbnailViewModel directly without collectionThumbnailViewModel.
                thumbnailView = GetObject(contentImage, "thumbnailViewModel");
                image = GetObject(thumbnailView, "image");
                return ExtractLargestThumbnailSource(image);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractPlaylistLockupCount(JsonObject lockupMetadata)
        {
            if (lockupMetadata == null)
            {
                return string.Empty;
            }

            var metadata = GetObject(lockupMetadata, "metadata");
            var contentMetadata = GetObject(metadata, "contentMetadataViewModel");
            var rows = GetArray(contentMetadata, "metadataRows");
            if (rows == null)
            {
                return string.Empty;
            }

            for (int r = 0; r < (int)rows.Count; r++)
            {
                var row = rows.GetObjectAt((uint)r);
                var parts = GetArray(row, "metadataParts");
                if (parts == null)
                {
                    continue;
                }

                for (int p = 0; p < (int)parts.Count; p++)
                {
                    var part = parts.GetObjectAt((uint)p);
                    var textNode = GetObject(part, "text");
                    var value = FirstNonEmpty(
                        GetString(textNode, "content"),
                        ExtractText(textNode));

                    if (!string.IsNullOrWhiteSpace(value)
                        && (value.IndexOf("video", StringComparison.OrdinalIgnoreCase) >= 0
                            || value.IndexOf("видео", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        return value;
                    }
                }
            }

            return string.Empty;
        }

        private static string ExtractPlaylistBadgeText(JsonObject renderer)
        {
            if (renderer == null)
            {
                return string.Empty;
            }

            foreach (var key in new[]
            {
                "thumbnailBadgeViewModel",
                "thumbnailOverlayTimeStatusRenderer",
                "thumbnailOverlayBottomPanelRenderer"
            })
            {
                var nodes = new List<JsonObject>();
                FindObjectsByKey(renderer, key, nodes, 0, 18);
                for (var i = 0; i < nodes.Count; i++)
                {
                    var text = FirstNonEmpty(
                        GetString(nodes[i], "text"),
                        ExtractText(GetObject(nodes[i], "text")),
                        ExtractText(nodes[i]));
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }

            return FirstNonEmpty(
                GetString(renderer, "videoCountShortText"),
                GetString(renderer, "videoCountText"),
                ExtractText(GetObject(renderer, "videoCountShortText")),
                ExtractText(GetObject(renderer, "videoCountText")),
                FindFirstTextByKey(renderer, "videoCountShortText", 0, 14),
                FindFirstTextByKey(renderer, "videoCountText", 0, 14));
        }

        private static string ExtractLargestThumbnailSource(JsonObject image)
        {
            if (image == null)
            {
                return string.Empty;
            }

            var sources = GetArray(image, "sources");
            if (sources == null)
            {
                sources = GetArray(image, "thumbnails");
            }

            if (sources == null || sources.Count == 0)
            {
                return string.Empty;
            }

            string bestUrl = string.Empty;
            double bestArea = -1;

            for (int i = 0; i < (int)sources.Count; i++)
            {
                var source = sources.GetObjectAt((uint)i);
                var url = GetString(source, "url");
                var width = source != null
                    && source.ContainsKey("width")
                    && source["width"] != null
                    && source["width"].ValueType == JsonValueType.Number
                    ? source["width"].GetNumber()
                    : 0;
                var height = source != null
                    && source.ContainsKey("height")
                    && source["height"] != null
                    && source["height"].ValueType == JsonValueType.Number
                    ? source["height"].GetNumber()
                    : 0;
                var area = width > 0 && height > 0 ? width * height : i + 1;

                if (!string.IsNullOrWhiteSpace(url) && area >= bestArea)
                {
                    bestUrl = url;
                    bestArea = area;
                }
            }

            return bestUrl;
        }


        private static string FindPlaylistIdFromNavigation(IJsonValue value)
        {
            var playlistId = FindStringByKey(value, "playlistId", 0, 14);
            if (!string.IsNullOrWhiteSpace(playlistId))
            {
                return playlistId;
            }

            var browseId = FindStringByKey(value, "browseId", 0, 14);
            if (!string.IsNullOrWhiteSpace(browseId)
                && browseId.StartsWith("VL", StringComparison.OrdinalIgnoreCase)
                && browseId.Length > 2)
            {
                return browseId.Substring(2);
            }

            return string.Empty;
        }

        private static List<ChannelPostItem> ParseChannelPosts(IJsonValue root, int maxCount, string channelAvatarUrl)
        {
            var result = new List<ChannelPostItem>();
            var renderers = new List<JsonObject>();
            FindObjectsByKey(root, "backstagePostRenderer", renderers, 0, 14);
            FindObjectsByKey(root, "postRenderer", renderers, 0, 14);
            FindObjectsByKey(root, "sharedPostRenderer", renderers, 0, 14);

            // Current WEB responses often wrap the actual post renderer in a thread.
            var threads = new List<JsonObject>();
            FindObjectsByKey(root, "backstagePostThreadRenderer", threads, 0, 14);
            for (int i = 0; i < threads.Count; i++)
            {
                var wrappedPost = GetObject(threads[i], "post");
                if (wrappedPost == null) continue;

                var backstage = GetObject(wrappedPost, "backstagePostRenderer");
                if (backstage != null) renderers.Add(backstage);

                var post = GetObject(wrappedPost, "postRenderer");
                if (post != null) renderers.Add(post);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < renderers.Count && result.Count < maxCount; i++)
            {
                var renderer = renderers[i];
                if (renderer == null) continue;

                var text = FirstNonEmpty(
                    ExtractText(GetObject(renderer, "contentText")),
                    ExtractText(GetObject(renderer, "text")),
                    FindFirstTextByKey(renderer, "contentText", 0, 10));

                var published = FirstNonEmpty(
                    ExtractText(GetObject(renderer, "publishedTimeText")),
                    FindFirstTextByKey(renderer, "publishedTimeText", 0, 10));

                var author = FirstNonEmpty(
                    ExtractText(GetObject(renderer, "authorText")),
                    FindFirstTextByKey(renderer, "authorText", 0, 10));

                var authorThumbnail = FirstNonEmpty(
                    ExtractThumbnailUrl(GetObject(renderer, "authorThumbnail")),
                    ExtractFirstImageUrl(renderer, "authorThumbnail"),
                    channelAvatarUrl);

                var image = ExtractPostImageUrl(renderer);

                if (!string.IsNullOrWhiteSpace(image))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Channel] Community attachment URL: " + image);
                }

                // Image-only posts are valid too, so do not throw them away when contentText is empty.
                if (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                var key = (text ?? string.Empty) + "|" + (published ?? string.Empty) + "|" + (image ?? string.Empty);
                if (!seen.Add(key)) continue;

                result.Add(new ChannelPostItem
                {
                    Text = text ?? string.Empty,
                    Author = author ?? string.Empty,
                    PublishedText = published ?? string.Empty,
                    AuthorThumbnailUrl = authorThumbnail ?? string.Empty,
                    ImageUrl = image ?? string.Empty
                });
            }

            System.Diagnostics.Debug.WriteLine(
                "[Channel] Posts parsed: renderers=" + renderers.Count + ", items=" + result.Count);
            return result;
        }

        private static string ExtractPostImageUrl(JsonObject renderer)
        {
            if (renderer == null)
            {
                return string.Empty;
            }

            var attachment = FirstObject(
                GetObject(renderer, "backstageAttachment"),
                GetObject(renderer, "attachment"),
                GetObject(renderer, "postAttachment"),
                GetObject(renderer, "contentAttachment"),
                GetObject(renderer, "imageAttachmentViewModel"),
                GetObject(renderer, "postMultiImageRenderer"),
                GetObject(renderer, "backstageImageRenderer"));
            if (attachment == null)
            {
                return string.Empty;
            }

            // Single-image community post:
            // backstageAttachment.backstageImageRenderer.image.thumbnails[]
            var single = GetObject(attachment, "backstageImageRenderer");
            if (single == null && attachment.ContainsKey("image"))
            {
                single = attachment;
            }
            if (single != null)
            {
                var image = GetObject(single, "image");
                var url = ExtractLargestThumbnailSource(image);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return NormalizeCommunityImageUrl(url);
                }
            }

            // Multi-image community post:
            // backstageAttachment.postMultiImageRenderer.images[]
            //     .backstageImageRenderer.image.thumbnails[]
            var multi = GetObject(attachment, "postMultiImageRenderer");
            if (multi == null && attachment.ContainsKey("images"))
            {
                multi = attachment;
            }
            var images = GetArray(multi, "images");
            if (images != null)
            {
                for (int i = 0; i < (int)images.Count; i++)
                {
                    var imageWrapper = images.GetObjectAt((uint)i);
                    var backstage = GetObject(imageWrapper, "backstageImageRenderer");
                    if (backstage == null && imageWrapper.ContainsKey("image"))
                    {
                        // Parser.parseArray(data.images, BackstageImage) means some API shapes
                        // already expose the BackstageImage payload without the renderer wrapper.
                        backstage = imageWrapper;
                    }
                    var image = GetObject(backstage, "image");
                    var url = ExtractLargestThumbnailSource(image);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        // The UI currently displays the first image; the parser no longer loses
                        // the entire attachment just because it is a multi-image post.
                        return NormalizeCommunityImageUrl(url);
                    }
                }
            }

            // YouTube periodically wraps the same media in a new view-model layer.
            // Restrict the fallback to the attachment, then select its largest image.
            // This catches imageAttachmentViewModel/new community wrappers without ever
            // walking the full post/page and accidentally selecting an avatar.
            var fallback = ExtractLargestImageUrl(attachment);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return NormalizeCommunityImageUrl(fallback);
            }

            return string.Empty;
        }

        private static string NormalizeCommunityImageUrl(string url)
        {
            url = NormalizeImageUrl(url);
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            // Preserve the server URL here. BuildCommunityImageCandidates will try it first and
            // then derive plain JPEG-compatible size transforms for older Windows image codecs.
            return url.Replace("&amp;", "&");
        }


        private static string ExtractLargestImageUrl(IJsonValue value)
        {
            var candidates = new List<ImageUrlCandidate>();
            CollectImageUrlCandidates(value, candidates, 0, 14);

            ImageUrlCandidate best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Url))
                {
                    continue;
                }

                if (best == null || candidate.Area > best.Area)
                {
                    best = candidate;
                }
            }

            return best != null ? best.Url : string.Empty;
        }

        private sealed class ImageUrlCandidate
        {
            public string Url;
            public double Area;
        }

        private static void CollectImageUrlCandidates(
            IJsonValue value,
            List<ImageUrlCandidate> result,
            int depth,
            int maxDepth)
        {
            if (value == null || result == null || depth > maxDepth)
            {
                return;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                if (obj.ContainsKey("url")
                    && obj["url"] != null
                    && obj["url"].ValueType == JsonValueType.String)
                {
                    var url = obj["url"].GetString();
                    if (!string.IsNullOrWhiteSpace(url)
                        && (url.IndexOf("ytimg", StringComparison.OrdinalIgnoreCase) >= 0
                            || url.IndexOf("ggpht", StringComparison.OrdinalIgnoreCase) >= 0
                            || url.IndexOf("googleusercontent", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        var width = obj.ContainsKey("width")
                            && obj["width"].ValueType == JsonValueType.Number
                            ? obj["width"].GetNumber()
                            : 0;
                        var height = obj.ContainsKey("height")
                            && obj["height"].ValueType == JsonValueType.Number
                            ? obj["height"].GetNumber()
                            : 0;

                        result.Add(new ImageUrlCandidate
                        {
                            Url = url,
                            Area = width > 0 && height > 0 ? width * height : 1
                        });
                    }
                }

                foreach (var pair in obj)
                {
                    if (pair.Value != null
                        && (pair.Value.ValueType == JsonValueType.Object
                            || pair.Value.ValueType == JsonValueType.Array))
                    {
                        CollectImageUrlCandidates(pair.Value, result, depth + 1, maxDepth);
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < (int)array.Count; i++)
                {
                    CollectImageUrlCandidates(array[i], result, depth + 1, maxDepth);
                }
            }
        }

        private static void FindObjectsByKey(IJsonValue value, string key, List<JsonObject> result, int depth, int maxDepth)
        {
            if (value == null || result == null || depth > maxDepth) return;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                foreach (var pair in obj)
                {
                    if (string.Equals(pair.Key, key, StringComparison.Ordinal)
                        && pair.Value != null
                        && pair.Value.ValueType == JsonValueType.Object)
                    {
                        result.Add(pair.Value.GetObject());
                    }

                    if (pair.Value != null
                        && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                    {
                        FindObjectsByKey(pair.Value, key, result, depth + 1, maxDepth);
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < (int)array.Count; i++)
                {
                    FindObjectsByKey(array[i], key, result, depth + 1, maxDepth);
                }
            }
        }

        private static string FindStringByKey(IJsonValue value, string key, int depth, int maxDepth)
        {
            if (value == null || depth > maxDepth) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(key) && obj[key] != null && obj[key].ValueType == JsonValueType.String)
                    return obj[key].GetString();

                foreach (var pair in obj)
                {
                    if (pair.Value != null
                        && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                    {
                        var found = FindStringByKey(pair.Value, key, depth + 1, maxDepth);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < (int)array.Count; i++)
                {
                    var found = FindStringByKey(array[i], key, depth + 1, maxDepth);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }

            return string.Empty;
        }

        private static string FindFirstTextByKey(IJsonValue value, string key, int depth, int maxDepth)
        {
            if (value == null || depth > maxDepth) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(key) && obj[key] != null)
                {
                    if (obj[key].ValueType == JsonValueType.Object)
                    {
                        var text = ExtractText(obj[key].GetObject());
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    if (obj[key].ValueType == JsonValueType.String)
                        return obj[key].GetString();
                }

                foreach (var pair in obj)
                {
                    if (pair.Value != null
                        && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                    {
                        var found = FindFirstTextByKey(pair.Value, key, depth + 1, maxDepth);
                        if (!string.IsNullOrWhiteSpace(found)) return found;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < (int)array.Count; i++)
                {
                    var found = FindFirstTextByKey(array[i], key, depth + 1, maxDepth);
                    if (!string.IsNullOrWhiteSpace(found)) return found;
                }
            }

            return string.Empty;
        }

        private static string ExtractContinuationToken(IJsonValue value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            // YouTube.js takes the ContinuationItem from the current channel tab. Prefer those
            // explicit list-tail nodes so we do not accidentally use an unrelated continuation
            // from another part of the channel page.
            var continuationItems = new List<JsonObject>();
            FindObjectsByKey(value, "continuationItemRenderer", continuationItems, 0, 14);
            for (int i = 0; i < continuationItems.Count; i++)
            {
                var token = ExtractContinuationToken(continuationItems[i], 0, 8);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    return token;
                }
            }

            return ExtractContinuationToken(value, 0, 14);
        }

        private static string ExtractContinuationToken(IJsonValue value, int depth, int maxDepth)
        {
            if (value == null || depth > maxDepth) return string.Empty;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                var next = GetObject(obj, "nextContinuationData");
                var token = GetString(next, "continuation");
                if (!string.IsNullOrWhiteSpace(token)) return token;

                var reload = GetObject(obj, "reloadContinuationData");
                token = GetString(reload, "continuation");
                if (!string.IsNullOrWhiteSpace(token)) return token;

                var command = GetObject(obj, "continuationCommand");
                token = FirstNonEmpty(GetString(command, "token"), GetString(command, "continuation"));
                if (!string.IsNullOrWhiteSpace(token)) return token;

                foreach (var pair in obj)
                {
                    if (pair.Value != null
                        && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                    {
                        token = ExtractContinuationToken(pair.Value, depth + 1, maxDepth);
                        if (!string.IsNullOrWhiteSpace(token)) return token;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < (int)array.Count; i++)
                {
                    var token = ExtractContinuationToken(array[i], depth + 1, maxDepth);
                    if (!string.IsNullOrWhiteSpace(token)) return token;
                }
            }

            return string.Empty;
        }

        private async Task<string> ResolveHandleToChannelIdAsync(string input)
        {
            var handle = NormalizeHandle(input);
            if (string.IsNullOrWhiteSpace(handle))
            {
                return string.Empty;
            }

            var payload = new JsonObject();
            payload["context"] = BuildContext();
            payload["url"] = JsonValue.CreateStringValue("https://www.youtube.com/@" + handle);

            var json = await PostInnertubeAsync("navigation/resolve_url", payload.Stringify());
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                var endpoint = GetObject(root, "endpoint");
                var browseEndpoint = GetObject(endpoint, "browseEndpoint");
                return GetString(browseEndpoint, "browseId");
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<string> PostInnertubeAsync(string endpoint, string payload)
        {
            var url = "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", WebClientVersion);
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

                var response = await _httpClient.SendAsync(request);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    System.Diagnostics.Debug.WriteLine("[Channel] Innertube " + endpoint + " failed: " + response.StatusCode + " " + error);
                    return string.Empty;
                }

                return await response.Content.ReadAsStringAsync();
            }
        }

        private static string BuildBrowsePayload(string channelId)
        {
            var payload = new JsonObject();
            payload["context"] = BuildContext();
            payload["browseId"] = JsonValue.CreateStringValue(channelId);
            // Ask for the Videos tab. The default (home) tab returns promo shelves that may hold
            // other channels' content and no plain video list, which left the page empty. The
            // channel metadata we need is present in this response too.
            payload["params"] = JsonValue.CreateStringValue(ChannelVideosTabParams);
            return payload.Stringify();
        }

        private static JsonObject BuildContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue(WebClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);

            var context = new JsonObject();
            context["client"] = client;
            return context;
        }

        private static ChannelPageInfo ExtractChannelInfo(JsonObject root, string channelId)
        {
            var info = new ChannelPageInfo();
            var metadata = GetObject(GetObject(root, "metadata"), "channelMetadataRenderer");
            info.Title = FirstNonEmpty(GetString(metadata, "title"), Localization.GetString("Channel"));
            info.Description = GetString(metadata, "description");

            var externalId = FirstNonEmpty(GetString(metadata, "externalId"), channelId);
            info.ChannelId = externalId;
            info.ThumbnailUrl = ExtractThumbnailUrl(GetObject(metadata, "avatar"));
            if (string.IsNullOrWhiteSpace(info.ThumbnailUrl))
            {
                info.ThumbnailUrl = ExtractFirstImageUrl(root, "avatar");
            }

            info.BannerUrl = ExtractBannerUrl(root);
            info.Handle = ExtractHandle(root, metadata, externalId);
            ExtractStats(root, info);
            return info;
        }

        private static void ExtractStats(JsonObject root, ChannelPageInfo info)
        {
            // Same layout used by yt-api-legacy channel.rs:
            // header.pageHeaderRenderer.content.pageHeaderViewModel.metadata.contentMetadataViewModel.metadataRows[1].metadataParts
            var metadataViewModel = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "metadata", "contentMetadataViewModel");
            var rows = GetArray(metadataViewModel, "metadataRows");

            if (rows != null && rows.Count > 1)
            {
                var countRow = rows.GetObjectAt(1);
                var parts = GetArray(countRow, "metadataParts");

                if (parts != null && parts.Count > 0)
                {
                    var subscriberText = ExtractMetadataPartText(parts.GetObjectAt(0));
                    if (!string.IsNullOrWhiteSpace(subscriberText))
                    {
                        info.SubscriberCount = ParseNumberText(subscriberText);
                    }
                }

                if (parts != null && parts.Count > 1)
                {
                    var videoText = ExtractMetadataPartText(parts.GetObjectAt(1));
                    if (!string.IsNullOrWhiteSpace(videoText))
                    {
                        info.VideoCount = ParseNumberText(videoText);
                    }
                }
            }

            // Fallback for older/alternate YouTube layouts.
            if (string.IsNullOrWhiteSpace(info.SubscriberCount) || info.SubscriberCount == "0" ||
                string.IsNullOrWhiteSpace(info.VideoCount) || info.VideoCount == "0")
            {
                ExtractStatsFallback(root, info);
            }
        }

        private static string ExtractMetadataPartText(JsonObject part)
        {
            return ExtractText(GetObject(part, "text"));
        }

        private static void ExtractStatsFallback(JsonObject root, ChannelPageInfo info)
        {
            var metadataViewModel = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "metadata", "contentMetadataViewModel");
            var rows = GetArray(metadataViewModel, "metadataRows");
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            for (uint i = 0; i < rows.Count; i++)
            {
                var row = rows.GetObjectAt(i);
                var parts = GetArray(row, "metadataParts");
                if (parts == null)
                {
                    continue;
                }

                for (uint p = 0; p < parts.Count; p++)
                {
                    var text = ExtractMetadataPartText(parts.GetObjectAt(p));
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    var lower = text.ToLowerInvariant();
                    if ((string.IsNullOrWhiteSpace(info.SubscriberCount) || info.SubscriberCount == "0") &&
                        (lower.Contains("subscriber") || lower.Contains("подпис")))
                    {
                        info.SubscriberCount = ParseNumberText(text);
                    }
                    else if ((string.IsNullOrWhiteSpace(info.VideoCount) || info.VideoCount == "0") &&
                             (lower.Contains("video") || lower.Contains("видео")))
                    {
                        info.VideoCount = ParseNumberText(text);
                    }
                    else if (text.StartsWith("@") && string.IsNullOrWhiteSpace(info.Handle))
                    {
                        info.Handle = text;
                    }
                }
            }
        }

        private static string ExtractBannerUrl(JsonObject root)
        {
            var bannerImage = GetObjectPath(root, "header", "pageHeaderRenderer", "content", "pageHeaderViewModel", "banner", "imageBannerViewModel", "image");
            var sources = GetArray(bannerImage, "sources");
            var url = LastUrlFromArray(sources);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            return ExtractFirstImageUrl(root, "banner");
        }

        private static string ExtractHandle(JsonObject root, JsonObject metadata, string externalId)
        {
            var vanityUrl = FirstNonEmpty(GetString(metadata, "vanityChannelUrl"), GetString(metadata, "channelUrl"));
            if (!string.IsNullOrWhiteSpace(vanityUrl))
            {
                var marker = "/@";
                var index = vanityUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index >= 0)
                {
                    return "@" + vanityUrl.Substring(index + marker.Length).Trim('/');
                }
            }

            var fromRows = FindFirstTextStartingWithAt(root, 0);
            if (!string.IsNullOrWhiteSpace(fromRows))
            {
                return fromRows;
            }

            return string.Empty;
        }

        private static IJsonValue FindVideosContent(JsonObject root)
        {
            var tabs = GetArray(GetObject(GetObject(root, "contents"), "twoColumnBrowseResultsRenderer"), "tabs");
            if (tabs == null)
            {
                return null;
            }

            IJsonValue selectedContent = null;

            for (uint i = 0; i < tabs.Count; i++)
            {
                var tab = tabs.GetObjectAt(i);
                var renderer = GetObject(tab, "tabRenderer");
                var title = GetString(renderer, "title");

                if (renderer != null && renderer.ContainsKey("content") && IsSelected(renderer))
                {
                    selectedContent = renderer["content"];
                }

                if ((string.Equals(title, "Videos", StringComparison.OrdinalIgnoreCase) || string.Equals(title, "Видео", StringComparison.OrdinalIgnoreCase)) && renderer != null && renderer.ContainsKey("content"))
                {
                    return renderer["content"];
                }
            }

            return selectedContent;
        }

        private static bool IsSelected(JsonObject renderer)
        {
            if (renderer == null || !renderer.ContainsKey("selected"))
            {
                return false;
            }

            var selected = renderer["selected"];
            return selected != null && selected.ValueType == JsonValueType.Boolean && selected.GetBoolean();
        }

        private static void ApplyKnownChannelAvatarToVideos(List<VideoCardItem> videos, string channelId, string avatarUrl)
        {
            if (videos == null || videos.Count == 0)
                return;

            for (var i = 0; i < videos.Count; i++)
            {
                var item = videos[i];
                if (item == null)
                    continue;

                if (string.IsNullOrWhiteSpace(item.ChannelId))
                    item.ChannelId = channelId ?? string.Empty;
                if (string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl))
                    item.ChannelThumbnailUrl = avatarUrl ?? string.Empty;
            }
        }

        private static void ExtractVideosRecursively(IJsonValue value, List<VideoCardItem> videos, string channelTitle, HashSet<string> seen, int maxCount, ref int visited)
        {
            if (value == null || videos.Count >= maxCount || visited >= MaxParserNodes)
            {
                return;
            }

            visited++;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                VideoCardItem item = null;

                if (obj.ContainsKey("videoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("videoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("gridVideoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("gridVideoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("compactVideoRenderer"))
                {
                    item = ParseVideoRenderer(obj.GetNamedObject("compactVideoRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("reelItemRenderer"))
                {
                    item = ParseReelItemRenderer(obj.GetNamedObject("reelItemRenderer"), channelTitle);
                }
                else if (obj.ContainsKey("lockupViewModel"))
                {
                    // Current YouTube responses deliver channel videos as lockupViewModel cards
                    // (wrapped in richItemRenderer). Without this the list came back empty.
                    item = ParseLockupVideo(obj.GetNamedObject("lockupViewModel"), channelTitle);
                }

                if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && !seen.Contains(item.VideoId))
                {
                    seen.Add(item.VideoId);
                    videos.Add(item);
                    if (videos.Count >= maxCount)
                    {
                        return;
                    }
                }

                foreach (var child in obj)
                {
                    ExtractVideosRecursively(child.Value, videos, channelTitle, seen, maxCount, ref visited);
                    if (videos.Count >= maxCount || visited >= MaxParserNodes)
                    {
                        return;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    ExtractVideosRecursively(array[(int)i], videos, channelTitle, seen, maxCount, ref visited);
                    if (videos.Count >= maxCount || visited >= MaxParserNodes)
                    {
                        return;
                    }
                }
            }
        }

        private static VideoCardItem ParseVideoRenderer(JsonObject renderer, string channelTitle)
        {
            if (renderer == null)
            {
                return null;
            }

            var videoId = GetString(renderer, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = FirstNonEmpty(ExtractText(GetObject(renderer, "title")), Localization.GetString("NoTitle")),
                ChannelTitle = FirstNonEmpty(channelTitle, ExtractText(GetObject(renderer, "ownerText")), ExtractText(GetObject(renderer, "shortBylineText"))),
                ChannelId = Config.ExtractVideoCardChannelId(renderer),
                ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer),
                Duration = FirstNonEmpty(ExtractText(GetObject(renderer, "lengthText")), ExtractDurationFromOverlays(renderer), string.Empty),
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg",
                WatchedPercent = Config.ExtractWatchedPercent(renderer)
            };
        }

        private static VideoCardItem ParseReelItemRenderer(JsonObject renderer, string channelTitle)
        {
            if (renderer == null)
            {
                return null;
            }

            var videoId = GetString(renderer, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = FirstNonEmpty(ExtractText(GetObject(renderer, "headline")), Localization.GetString("Shorts")),
                ChannelTitle = FirstNonEmpty(channelTitle, string.Empty),
                ChannelId = Config.ExtractVideoCardChannelId(renderer),
                ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(renderer),
                Duration = Localization.GetString("Shorts"),
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg"
            };
        }

        // lockupViewModel video card (the shape the Videos tab returns today).
        private static VideoCardItem ParseLockupVideo(JsonObject lockup, string channelTitle)
        {
            if (lockup == null)
            {
                return null;
            }

            // Skip playlist / channel lockups — only real videos belong in this list.
            var contentType = GetString(lockup, "contentType");
            if (!string.IsNullOrWhiteSpace(contentType) &&
                contentType.IndexOf("VIDEO", StringComparison.OrdinalIgnoreCase) < 0)
            {
                return null;
            }

            var videoId = GetString(lockup, "contentId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var meta = GetObjectPath(lockup, "metadata", "lockupMetadataViewModel");
            var title = GetString(GetObject(meta, "title"), "content");

            // On a channel's Videos tab the rows omit the channel name, so the only row is
            // ["125K views", "8 hours ago"]. Elsewhere row 0 is the channel and the stats sit in
            // the next one — reading the LAST row covers both layouts.
            var views = string.Empty;
            var published = string.Empty;
            var rows = GetArray(GetObjectPath(meta, "metadata", "contentMetadataViewModel"), "metadataRows");
            if (rows != null && rows.Count > 0)
            {
                for (var r = rows.Count - 1; r >= 0 && string.IsNullOrWhiteSpace(views); r--)
                {
                    if (rows[r].ValueType != JsonValueType.Object) continue;
                    var parts = GetArray(rows[r].GetObject(), "metadataParts");
                    if (parts == null || parts.Count == 0) continue;

                    var first = parts[0].ValueType == JsonValueType.Object
                        ? GetString(GetObject(parts[0].GetObject(), "text"), "content") : string.Empty;
                    var second = parts.Count > 1 && parts[1].ValueType == JsonValueType.Object
                        ? GetString(GetObject(parts[1].GetObject(), "text"), "content") : string.Empty;

                    // A stats row has at least two parts (views + date).
                    if (!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second))
                    {
                        views = first;
                        published = second;
                    }
                }
            }

            return new VideoCardItem
            {
                VideoId = videoId,
                Title = string.IsNullOrWhiteSpace(title) ? Localization.GetString("Untitled") : title,
                ChannelTitle = channelTitle,
                ChannelId = Config.ExtractVideoCardChannelId(lockup),
                ChannelThumbnailUrl = Config.ExtractVideoCardChannelThumbnail(lockup),
                Duration = ExtractDurationFromLockup(lockup),
                ThumbnailUrl = "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg",
                ViewCount = views,
                PublishedText = published,
                WatchedPercent = Config.ExtractWatchedPercent(lockup)
            };
        }

        // contentImage.thumbnailViewModel.overlays[].thumbnailBottomOverlayViewModel.badges[].thumbnailBadgeViewModel.text
        private static string ExtractDurationFromLockup(JsonObject lockup)
        {
            try
            {
                var overlays = GetArray(GetObjectPath(lockup, "contentImage", "thumbnailViewModel"), "overlays");
                if (overlays == null) return string.Empty;

                for (var i = 0; i < overlays.Count; i++)
                {
                    if (overlays[i].ValueType != JsonValueType.Object) continue;
                    var badges = GetArray(GetObject(overlays[i].GetObject(), "thumbnailBottomOverlayViewModel"), "badges");
                    if (badges == null) continue;

                    for (var b = 0; b < badges.Count; b++)
                    {
                        if (badges[b].ValueType != JsonValueType.Object) continue;
                        var text = GetString(GetObject(badges[b].GetObject(), "thumbnailBadgeViewModel"), "text");
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractDurationFromOverlays(JsonObject renderer)
        {
            var overlays = GetArray(renderer, "thumbnailOverlays");
            if (overlays == null)
            {
                return string.Empty;
            }

            for (uint i = 0; i < overlays.Count; i++)
            {
                var overlay = overlays.GetObjectAt(i);
                var timeRenderer = GetObject(overlay, "thumbnailOverlayTimeStatusRenderer");
                var text = ExtractText(GetObject(timeRenderer, "text"));
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }

            return string.Empty;
        }

        private static JsonObject GetObject(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return null;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Object ? value.GetObject() : null;
        }

        private static JsonArray GetArray(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return null;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.Array ? value.GetArray() : null;
        }

        private static JsonObject GetObjectPath(JsonObject root, params string[] keys)
        {
            var current = root;
            if (current == null || keys == null)
            {
                return null;
            }

            for (int i = 0; i < keys.Length; i++)
            {
                current = GetObject(current, keys[i]);
                if (current == null)
                {
                    return null;
                }
            }

            return current;
        }

        private static string GetString(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            var value = obj[key];
            return value != null && value.ValueType == JsonValueType.String ? value.GetString() : string.Empty;
        }

        private static string ExtractText(JsonObject textObject)
        {
            if (textObject == null)
            {
                return string.Empty;
            }

            var simpleText = GetString(textObject, "simpleText");
            if (!string.IsNullOrWhiteSpace(simpleText))
            {
                return simpleText;
            }

            var content = GetString(textObject, "content");
            if (!string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            var runs = GetArray(textObject, "runs");
            if (runs == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            for (uint i = 0; i < runs.Count; i++)
            {
                var run = runs.GetObjectAt(i);
                sb.Append(GetString(run, "text"));
            }

            return sb.ToString();
        }

        private static string ExtractThumbnailUrl(JsonObject imageObject)
        {
            if (imageObject == null)
            {
                return string.Empty;
            }

            return FirstNonEmpty(LastUrlFromArray(GetArray(imageObject, "thumbnails")), LastUrlFromArray(GetArray(imageObject, "sources")));
        }

        private static string LastUrlFromArray(JsonArray array)
        {
            if (array == null || array.Count == 0)
            {
                return string.Empty;
            }

            for (int i = (int)array.Count - 1; i >= 0; i--)
            {
                var obj = array.GetObjectAt((uint)i);
                var url = GetString(obj, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private static string ExtractFirstImageUrl(IJsonValue value, string preferredKey)
        {
            var visited = 0;
            return ExtractFirstImageUrlInternal(value, preferredKey, ref visited);
        }

        private static string ExtractFirstImageUrlInternal(IJsonValue value, string preferredKey, ref int visited)
        {
            if (value == null || visited >= MaxParserNodes)
            {
                return string.Empty;
            }

            visited++;

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (obj.ContainsKey(preferredKey))
                {
                    var url = ExtractThumbnailUrl(GetObject(obj, preferredKey));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }

                foreach (var child in obj)
                {
                    var url = ExtractFirstImageUrlInternal(child.Value, preferredKey, ref visited);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    var url = ExtractFirstImageUrlInternal(array[(int)i], preferredKey, ref visited);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }

            return string.Empty;
        }

        private static string FindFirstTextStartingWithAt(IJsonValue value, int depth)
        {
            if (value == null || depth > 7)
            {
                return string.Empty;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                var text = ExtractText(obj);
                if (!string.IsNullOrWhiteSpace(text) && text.StartsWith("@"))
                {
                    return text;
                }

                foreach (var child in obj)
                {
                    var result = FindFirstTextStartingWithAt(child.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    var result = FindFirstTextStartingWithAt(array[(int)i], depth + 1);
                    if (!string.IsNullOrWhiteSpace(result))
                    {
                        return result;
                    }
                }
            }

            return string.Empty;
        }

        private static string NormalizeChannelId(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var text = input.Trim();
            var marker = "/channel/";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                text = text.Substring(index + marker.Length).Trim('/');
            }

            return text.StartsWith("UC", StringComparison.OrdinalIgnoreCase) && text.Length >= 20 ? text : string.Empty;
        }

        private static string NormalizeHandle(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            var text = input.Trim();
            var marker = "/@";
            var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                text = text.Substring(index + marker.Length).Trim('/');
            }

            return text.TrimStart('@').Trim();
        }

        private static string ParseNumberText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return "0";
            }

            var lower = text.Trim().ToLowerInvariant();
            var multiplier = 1.0;

            if (lower.Contains("k") || lower.Contains("thousand") || lower.Contains("тыс"))
            {
                multiplier = 1000.0;
            }
            else if (lower.Contains("m") || lower.Contains("million") || lower.Contains("млн"))
            {
                multiplier = 1000000.0;
            }
            else if (lower.Contains("b") || lower.Contains("billion") || lower.Contains("млрд"))
            {
                multiplier = 1000000000.0;
            }

            // Matches channel.rs behavior: keep digits and decimal dots only.
            // This also fixes values like "1,234 videos" => "1234" instead of "1.234".
            var sb = new StringBuilder();
            foreach (var ch in lower)
            {
                if ((ch >= '0' && ch <= '9') || ch == '.')
                {
                    sb.Append(ch);
                }
            }

            double number;
            if (double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number))
            {
                return ((ulong)Math.Round(number * multiplier)).ToString(CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string BuildStatsText(string subscribers, string videos)
        {
            var subsRaw = ParseNumberText(subscribers);
            var vidsRaw = ParseNumberText(videos);

            var subs = FormatCompactNumber(subsRaw);
            var vids = FormatPlainNumber(vidsRaw);

            if (string.IsNullOrWhiteSpace(subs))
            {
                subs = "0";
            }

            if (string.IsNullOrWhiteSpace(vids))
            {
                vids = "0";
            }

            ulong subsValue;
            if (!ulong.TryParse(ParseNumberText(subsRaw), NumberStyles.Any, CultureInfo.InvariantCulture, out subsValue))
                subsValue = 0;
            ulong vidsValue;
            if (!ulong.TryParse(ParseNumberText(vidsRaw), NumberStyles.Any, CultureInfo.InvariantCulture, out vidsValue))
                vidsValue = 0;

            return subs + " " + Localization.GetPluralString(subsValue, "SubscriberOne", "SubscriberFew", "SubscriberMany")
                + " • " + vids + " " + Localization.GetPluralString(vidsValue, "VideoOne", "VideoFew", "VideoMany");
        }

        private static string FormatCompactNumber(string raw)
        {
            ulong value;
            if (!ulong.TryParse(ParseNumberText(raw), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return raw;
            }

            if (value >= 1000000000UL)
            {
                return FormatOneDecimal(value / 1000000000.0) + "B";
            }
            if (value >= 1000000UL)
            {
                return FormatOneDecimal(value / 1000000.0) + "M";
            }
            if (value >= 1000UL)
            {
                return FormatOneDecimal(value / 1000.0) + "K";
            }

            return value.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPlainNumber(string raw)
        {
            ulong value;
            if (!ulong.TryParse(ParseNumberText(raw), NumberStyles.Any, CultureInfo.InvariantCulture, out value))
            {
                return raw;
            }

            return value.ToString("N0", CultureInfo.InvariantCulture);
        }

        private static string FormatOneDecimal(double value)
        {
            var rounded = Math.Round(value, 1);
            if (Math.Abs(rounded - Math.Round(rounded)) < 0.05)
            {
                return Math.Round(rounded).ToString(CultureInfo.InvariantCulture);
            }

            return rounded.ToString("0.#", CultureInfo.InvariantCulture);
        }

        private static string Pluralize(string word, string rawNumber)
        {
            ulong value;
            if (ulong.TryParse(ParseNumberText(rawNumber), NumberStyles.Any, CultureInfo.InvariantCulture, out value) && value == 1UL)
            {
                return word;
            }

            return word + "s";
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
        }

        private void VideosItemsControl_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateResponsiveCardLayouts();
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

            var targetHeight = Math.Round(width / VideoThumbnailAspectRatio);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
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

            if (VideosItemsControl != null)
            {
                VideosItemsControl.Padding = isPortrait
                    ? new Thickness(0, 8, 0, 16)
                    : new Thickness(8, 8, 8, 16);
                double videoWidth = GetItemsControlContentWidth(VideosItemsControl, Window.Current.Bounds.Width);
                UpdateItemsWrapGrid(VideosItemsControl,
                    isPortrait ? Math.Max(0, videoWidth) : DefaultCardWidth,
                    isPortrait ? 1 : 3);
                UpdateResponsiveCardMargins(VideosItemsControl);
            }

            if (PlaylistsItemsControl != null)
            {
                PlaylistsItemsControl.Padding = isPortrait
                    ? new Thickness(0, 8, 0, 16)
                    : new Thickness(8, 8, 8, 16);
                double playlistWidth = GetItemsControlContentWidth(PlaylistsItemsControl, Window.Current.Bounds.Width);
                UpdateItemsWrapGrid(PlaylistsItemsControl,
                    isPortrait ? Math.Max(0, playlistWidth) : DefaultCardWidth,
                    isPortrait ? 1 : 3);
                UpdateResponsiveCardMargins(PlaylistsItemsControl);
            }

            if (ShortsItemsControl != null)
            {
                ShortsItemsControl.Padding = isPortrait
                    ? new Thickness(20, 8, 20, 20)
                    : new Thickness(12, 8, 12, 20);

                double shortsWidth = GetItemsControlContentWidth(ShortsItemsControl, Window.Current.Bounds.Width);
                var shortItemWidth = isPortrait
                    ? Math.Max(112, (shortsWidth - 20) / 2.0)
                    : 190.0;
                UpdateItemsWrapGrid(ShortsItemsControl, shortItemWidth, isPortrait ? 2 : 8);
            }
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

        private void DescriptionButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(true);
        }

        private void Channel_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_isSubscriptionMenuOpen)
            {
                e.Handled = true;
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            if (_isDescriptionBottomSheetOpen)
            {
                e.Handled = true;
                AnimateDescriptionBottomSheet(false);
                return;
            }

            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private void AnimateDescriptionBottomSheet(bool show)
        {
            _isDescriptionBottomSheetOpen = show;
            OverlayGrid.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            DescriptionBottomSheetPanel.Visibility = Visibility.Visible;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (show)
            {
                animation.From = 410;
                animation.To = 0;
            }
            else
            {
                animation.From = DescriptionBottomSheetTransform.Y;
                animation.To = 410;
                storyboard.Completed += (s, e) =>
                {
                    DescriptionBottomSheetPanel.Visibility = Visibility.Collapsed;
                    OverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            Storyboard.SetTarget(animation, DescriptionBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void OverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isSubscriptionMenuOpen)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            if (_isDescriptionBottomSheetOpen)
            {
                AnimateDescriptionBottomSheet(false);
                return;
            }

            OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void DescriptionDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _descriptionInitialY = e.GetCurrentPoint(element).Position.Y;
                _descriptionInitialTransformY = DescriptionBottomSheetTransform.Y;
                _descriptionIsDragging = true;
                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_descriptionIsDragging)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var deltaY = currentY - _descriptionInitialY;
            var newY = Math.Max(0, _descriptionInitialTransformY + deltaY);
            DescriptionBottomSheetTransform.Y = newY;
            e.Handled = true;
        }

        private void DescriptionDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_descriptionIsDragging)
            {
                return;
            }

            _descriptionIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (DescriptionBottomSheetTransform.Y > 205)
            {
                AnimateDescriptionBottomSheet(false);
            }
            else
            {
                AnimateDescriptionBottomSheet(true);
            }

            e.Handled = true;
        }


        private async void SubscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                ShowSubscriptionMenuBottomSheet();
                return;
            }

            await SetChannelSubscriptionStateAsync(true);
        }

        private async Task SetChannelSubscriptionStateAsync(bool subscribe)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(_currentChannelId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var channelIdAtClick = _currentChannelId;
            var oldState = _currentSubscriptionState;
            var oldNotificationState = _currentNotificationState;
            var newState = subscribe ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
            var newNotificationState = subscribe ? ChannelNotificationState.Default : ChannelNotificationState.Default;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentSubscriptionState = newState;
            _currentNotificationState = newNotificationState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var success = await SetChannelSubscriptionAsync(channelIdAtClick, subscribe, token);
                if (stateGeneration != _subscriptionStateGeneration || !string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    await ShowMessageAsync(Localization.GetString("SubscriptionFailed"), Localization.GetString("SubscriptionRejected"));
                    return;
                }

                _currentSubscriptionState = newState;
                _currentNotificationState = newNotificationState;
                UpdateSubscriptionVisualState();
                UpdateSubscriptionMenuVisualState();

                if (!subscribe)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Channel] Subscription update error: " + ex.Message);
                await ShowMessageAsync(Localization.GetString("SubscriptionFailed"), ex.Message);
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async Task<bool> SetChannelSubscriptionAsync(string channelId, bool subscribe, string accessToken)
        {
            var endpoint = subscribe ? "subscription/subscribe" : "subscription/unsubscribe";
            var parameters = subscribe ? _subscribeParams : _unsubscribeParams;
            var clickTrackingParams = subscribe ? _subscribeClickTrackingParams : _unsubscribeClickTrackingParams;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                parameters = subscribe ? DefaultSubscribeParams : DefaultUnsubscribeParams;
            }

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildSubscriptionPayload(channelId, parameters, clickTrackingParams),
                    Encoding.UTF8,
                    "application/json"
                );
                AddYouTubeAuthHeaders(request, accessToken);

                var response = await _httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Channel] Subscription update OK: " + (subscribe ? "subscribe" : "unsubscribe"));
                    return true;
                }

                var body = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription update failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + body);
                return false;
            }
        }

        private async Task<bool> ModifyChannelNotificationPreferenceAsync(ChannelNotificationState targetState)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(_currentChannelId))
            {
                return false;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var oldState = _currentNotificationState;
            var channelIdAtClick = _currentChannelId;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentNotificationState = targetState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var url = BuildInnertubeUrl("notification/modify_channel_preference");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildNotificationPreferencePayload(BuildNotificationPreferenceParams(channelIdAtClick, targetState)),
                        Encoding.UTF8,
                        "application/json"
                    );
                    AddYouTubeAuthHeaders(request, token);

                    var response = await _httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        _currentNotificationState = oldState;
                        UpdateSubscriptionVisualState();
                        UpdateSubscriptionMenuVisualState();
                        System.Diagnostics.Debug.WriteLine("[Channel] Notification update failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + body);
                        await ShowMessageAsync(Localization.GetString("NotificationsFailed"), Localization.GetString("NotificationPreferenceFailed"));
                        return false;
                    }
                }

                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = targetState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    AnimateSubscriptionMenuBottomSheet(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = oldState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Channel] Notification update error: " + ex.Message);
                await ShowMessageAsync(Localization.GetString("NotificationsFailed"), ex.Message);
                return false;
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, _currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async void NotificationAllOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.All)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.All);
        }

        private async void NotificationPersonalizedOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.Default || _currentNotificationState == ChannelNotificationState.Unknown)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.Default);
        }

        private async void NotificationNoneOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.None)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.None);
        }

        private async void NotificationUnsubscribeOptionButton_Click(object sender, RoutedEventArgs e)
        {
            await SetChannelSubscriptionStateAsync(false);
        }

        private async Task LoadChannelSubscriptionStateAsync(string channelId, SubscriptionLoadResult preliminaryResult)
        {
            var generation = _subscriptionStateGeneration;

            if (string.IsNullOrWhiteSpace(channelId))
            {
                _currentSubscriptionState = ChannelSubscriptionState.Unknown;
                UpdateSubscriptionVisualState();
                return;
            }

            ApplySubscriptionEndpointData(preliminaryResult);

            var token = await GetTvAccessTokenAsync(false);
            if (generation != _subscriptionStateGeneration || !string.Equals(channelId, _currentChannelId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    _currentNotificationState = ChannelNotificationState.Default;
                    UpdateSubscriptionVisualState();
                }
                return;
            }

            try
            {
                // Do not use YouTube Data API here. The TV OAuth project can have Data API disabled,
                // while Innertube /browse still returns the signed-in subscribeButtonRenderer.
                var browseResult = await TryLoadChannelSubscriptionFromAuthenticatedBrowseAsync(channelId, token);
                if (generation != _subscriptionStateGeneration || !string.Equals(channelId, _currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (browseResult != null)
                {
                    ApplySubscriptionEndpointData(browseResult);
                }

                if (browseResult != null && browseResult.Found)
                {
                    ApplyLoadedSubscriptionState(browseResult);
                    System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from " + browseResult.Source + ": " + _currentSubscriptionState + ", " + _currentNotificationState);
                    return;
                }

                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                    return;
                }

                _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                _currentNotificationState = ChannelNotificationState.Default;
                UpdateSubscriptionVisualState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state load error: " + ex.Message);
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    _currentNotificationState = ChannelNotificationState.Default;
                    UpdateSubscriptionVisualState();
                }
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromDataApiAsync(string channelId, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(channelId) || string.IsNullOrWhiteSpace(accessToken))
            {
                return null;
            }

            try
            {
                var url = "https://www.googleapis.com/youtube/v3/subscriptions?part=id&mine=true&forChannelId=" + Uri.EscapeDataString(channelId);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] Data API subscription check failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + json);
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    var items = GetArray(root, "items");
                    var subscribed = items != null && items.Count > 0;
                    return new SubscriptionLoadResult
                    {
                        Found = true,
                        State = subscribed ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed,
                        NotificationState = ChannelNotificationState.Default,
                        ChannelId = channelId,
                        Source = "YouTube Data API subscriptions/list"
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Data API subscription check error: " + ex.Message);
                return null;
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromAuthenticatedBrowseAsync(string channelId, string accessToken)
        {
            var tv = await TryLoadChannelSubscriptionFromBrowseClientAsync(channelId, accessToken, false, "authenticated /browse TVHTML5");
            if (tv != null && tv.Found)
            {
                return tv;
            }

            return await TryLoadChannelSubscriptionFromBrowseClientAsync(channelId, accessToken, true, "authenticated /browse MWEB");
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromBrowseClientAsync(string channelId, string accessToken, bool mobileWebClient, string sourceName)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("browse")))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedBrowsePayload(channelId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json"
                    );

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(request, accessToken, MwebClientHeaderName, MwebClientVersion, MwebUserAgent);
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(request, accessToken, TvClientHeaderName, TvClientVersion, TvUserAgent);
                    }

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] " + sourceName + " failed: " + (int)response.StatusCode + " " + response.ReasonPhrase + " " + json);
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    return ExtractSubscriptionStateFromBrowse(root, sourceName, channelId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyLoadedSubscriptionState(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            ApplySubscriptionEndpointData(result);
            if (!string.IsNullOrWhiteSpace(result.ChannelId))
            {
                _currentChannelId = result.ChannelId;
            }

            _currentSubscriptionState = result.State == ChannelSubscriptionState.Unknown
                ? ChannelSubscriptionState.NotSubscribed
                : result.State;
            _currentNotificationState = result.NotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : result.NotificationState;

            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();
        }

        private void ApplySubscriptionEndpointData(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeParams))
            {
                _subscribeParams = result.SubscribeParams;
            }
            if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams))
            {
                _unsubscribeParams = result.UnsubscribeParams;
            }
            if (!string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
            {
                _subscribeClickTrackingParams = result.SubscribeClickTrackingParams;
            }
            if (!string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
            {
                _unsubscribeClickTrackingParams = result.UnsubscribeClickTrackingParams;
            }
        }

        private void ResetSubscriptionUi()
        {
            _subscriptionStateGeneration++;
            _currentSubscriptionState = ChannelSubscriptionState.Unknown;
            _currentNotificationState = ChannelNotificationState.Default;
            _subscriptionRequestInProgress = false;
            _subscribeParams = string.Empty;
            _unsubscribeParams = string.Empty;
            _subscribeClickTrackingParams = string.Empty;
            _unsubscribeClickTrackingParams = string.Empty;
            _isSubscriptionMenuOpen = false;
            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
            }
            if (SubscriptionMenuBottomSheetTransform != null)
            {
                SubscriptionMenuBottomSheetTransform.Y = 280;
            }
            UpdateSubscriptionVisualState();
        }

        private void UpdateSubscriptionVisualState()
        {
            var isSubscribed = _currentSubscriptionState == ChannelSubscriptionState.Subscribed;

            if (SubscribeButton != null)
            {
                SubscribeButton.IsEnabled = !string.IsNullOrWhiteSpace(_currentChannelId);
                SubscribeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                SubscribeButton.Opacity = 1.0;
                SubscribeButton.Padding = isSubscribed ? new Thickness(0) : new Thickness(0);
            }

            if (SubscribeButtonContainer != null)
            {
                var containerBrush = isSubscribed
                    ? (App.GetThemeBrush("AppSurfaceBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 39, 39, 39)))
                    : (App.GetThemeBrush("PrimaryActionBackgroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 241, 241)));
                SubscribeButtonContainer.Background = containerBrush;
                SubscribeButtonContainer.BorderBrush = containerBrush;
            }

            if (SubscribeButtonText != null)
            {
                SubscribeButtonText.Text = Localization.GetString("Subscribe");
                SubscribeButtonText.Visibility = isSubscribed ? Visibility.Collapsed : Visibility.Visible;
                SubscribeButtonText.Foreground = App.GetThemeBrush("PrimaryActionForegroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 15, 15, 15));
            }

            if (SubscribeSubscribedIconsPanel != null)
            {
                SubscribeSubscribedIconsPanel.Visibility = isSubscribed ? Visibility.Visible : Visibility.Collapsed;
                SubscribeSubscribedIconsPanel.HorizontalAlignment = HorizontalAlignment.Center;
            }

            if (SubscribeNotificationIcon != null)
            {
                SubscribeNotificationIcon.Visibility = Visibility.Visible;
                SubscribeNotificationIcon.Width = 22;
                SubscribeNotificationIcon.Height = 22;
                SubscribeNotificationIcon.Stretch = Stretch.Uniform;
                SetImageSource(SubscribeNotificationIcon, GetNotificationIconAssetPath(_currentNotificationState));
            }

            if (SubscribeDownArrowIcon != null)
            {
                SubscribeDownArrowIcon.Visibility = Visibility.Visible;
                SubscribeDownArrowIcon.Width = 16;
                SubscribeDownArrowIcon.Height = 16;
                SubscribeDownArrowIcon.Margin = new Thickness(6, 0, 10, 0);
                SubscribeDownArrowIcon.Stretch = Stretch.Uniform;
                SetImageSource(SubscribeDownArrowIcon, "Assets/down_arrow.png");
            }

            UpdateSubscriptionMenuVisualState();
        }

        private void UpdateSubscriptionMenuVisualState()
        {
            var state = _currentNotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : _currentNotificationState;

            if (NotificationAllCheckmark != null)
            {
                NotificationAllCheckmark.Visibility = state == ChannelNotificationState.All ? Visibility.Visible : Visibility.Collapsed;
            }
            if (NotificationPersonalizedCheckmark != null)
            {
                NotificationPersonalizedCheckmark.Visibility = state == ChannelNotificationState.Default ? Visibility.Visible : Visibility.Collapsed;
            }
            if (NotificationNoneCheckmark != null)
            {
                NotificationNoneCheckmark.Visibility = state == ChannelNotificationState.None ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private static string GetNotificationIconAssetPath(ChannelNotificationState state)
        {
            if (state == ChannelNotificationState.All)
            {
                return "Assets/all_notifications.png";
            }
            if (state == ChannelNotificationState.None)
            {
                return "Assets/none_notifications.png";
            }
            return "Assets/notifications.png";
        }

        private void ShowSubscriptionMenuBottomSheet()
        {
            if (_currentSubscriptionState != ChannelSubscriptionState.Subscribed)
            {
                return;
            }

            _isSubscriptionMenuOpen = true;
            UpdateSubscriptionMenuVisualState();
            OverlayGrid.Visibility = Visibility.Visible;
            SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            AnimateSubscriptionMenuBottomSheet(true);
        }

        private void AnimateSubscriptionMenuBottomSheet(bool show)
        {
            _isSubscriptionMenuOpen = show;
            if (SubscriptionMenuBottomSheetPanel == null || SubscriptionMenuBottomSheetTransform == null)
            {
                return;
            }

            if (show)
            {
                OverlayGrid.Visibility = Visibility.Visible;
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            if (show)
            {
                animation.From = 280;
                animation.To = 0;
            }
            else
            {
                animation.From = SubscriptionMenuBottomSheetTransform.Y;
                animation.To = 280;
                storyboard.Completed += (s, e) =>
                {
                    SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (!_isDescriptionBottomSheetOpen)
                    {
                        OverlayGrid.Visibility = Visibility.Collapsed;
                    }
                };
            }

            Storyboard.SetTarget(animation, SubscriptionMenuBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void SubscriptionMenuDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSubscriptionMenuBottomSheet(false);
        }

        private void SubscriptionMenuDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _subscriptionMenuInitialY = e.GetCurrentPoint(element).Position.Y;
                _subscriptionMenuInitialTransformY = SubscriptionMenuBottomSheetTransform.Y;
                _subscriptionMenuIsDragging = true;
                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_subscriptionMenuIsDragging)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var deltaY = currentY - _subscriptionMenuInitialY;
            var newY = Math.Max(0, _subscriptionMenuInitialTransformY + deltaY);
            if (newY <= 280)
            {
                SubscriptionMenuBottomSheetTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SubscriptionMenuDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_subscriptionMenuIsDragging)
            {
                return;
            }

            _subscriptionMenuIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SubscriptionMenuBottomSheetTransform.Y > 140)
            {
                AnimateSubscriptionMenuBottomSheet(false);
            }
            else
            {
                AnimateSubscriptionMenuBottomSheet(true);
            }

            e.Handled = true;
        }

        private static string BuildInnertubeUrl(string endpoint)
        {
            return "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
        }

        private static string BuildAuthenticatedBrowsePayload(string channelId, bool mobileWebClient)
        {
            var payload = new JsonObject();
            payload["context"] = mobileWebClient ? BuildMwebContext() : BuildTvContext();
            payload["browseId"] = JsonValue.CreateStringValue(channelId);
            return global::Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static JsonObject BuildTvContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(TvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(TvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");

            var user = new JsonObject();
            user["enableSafetyMode"] = JsonValue.CreateBooleanValue(false);

            var request = new JsonObject();
            request["internalExperimentFlags"] = new JsonArray();
            request["consistencyTokenJars"] = new JsonArray();

            var context = new JsonObject();
            context["client"] = client;
            context["user"] = user;
            context["request"] = request;
            return context;
        }

        private static JsonObject BuildMwebContext()
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(MwebClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(MwebClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);

            var context = new JsonObject();
            context["client"] = client;
            return context;
        }

        private static string BuildSubscriptionPayload(string channelId, string parameters, string clickTrackingParams)
        {
            var payload = new JsonObject();
            payload["context"] = BuildTvContext();

            var channelIds = new JsonArray();
            channelIds.Add(JsonValue.CreateStringValue(channelId));
            payload["channelIds"] = channelIds;

            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            if (!string.IsNullOrWhiteSpace(clickTrackingParams))
            {
                var clickTracking = new JsonObject();
                clickTracking["clickTrackingParams"] = JsonValue.CreateStringValue(clickTrackingParams);
                payload["context"].GetObject()["clickTracking"] = clickTracking;
            }

            return global::Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildNotificationPreferencePayload(string parameters)
        {
            var payload = new JsonObject();
            payload["context"] = BuildTvContext();
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }
            return global::Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildNotificationPreferenceParams(string channelId, ChannelNotificationState targetState)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return string.Empty;
            }

            byte stateCode = 1;
            if (targetState == ChannelNotificationState.All)
            {
                stateCode = 2;
            }
            else if (targetState == ChannelNotificationState.None)
            {
                stateCode = 3;
            }

            var channelBytes = Encoding.UTF8.GetBytes(channelId);
            var bytes = new List<byte>();
            bytes.Add(0x0A);
            bytes.Add((byte)channelBytes.Length);
            bytes.AddRange(channelBytes);
            bytes.Add(0x12);
            bytes.Add(0x02);
            bytes.Add(0x08);
            bytes.Add(stateCode);
            bytes.Add(0x18);
            bytes.Add(0x00);
            bytes.Add(0x20);
            bytes.Add(0x04);

            return Uri.EscapeDataString(Convert.ToBase64String(bytes.ToArray()));
        }

        private static void AddYouTubeAuthHeaders(HttpRequestMessage request, string accessToken)
        {
            AddInnertubeAuthHeadersForClient(request, accessToken, TvClientHeaderName, TvClientVersion, TvUserAgent);
        }

        private static void AddInnertubeAuthHeadersForClient(HttpRequestMessage request, string accessToken, string clientNameHeader, string clientVersion, string userAgent)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            global::Config.ApplySelectedAccountHeader(request, true);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        private static async Task<string> GetTvAccessTokenAsync(bool showErrors)
        {
            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                if (showErrors)
                {
                    await ShowStaticMessageAsync(Localization.GetString("SignInRequired"), Localization.GetString("TvTokenMissingDetailed"));
                }
                return string.Empty;
            }

            var accessToken = await global::Config.RefreshAccessTokenAsync(refreshToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                if (showErrors)
                {
                    await ShowStaticMessageAsync(Localization.GetString("SignInRequired"), Localization.GetString("TvTokenExchangeFailed"));
                }
                return string.Empty;
            }

            return accessToken;
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await ShowStaticMessageAsync(title, message);
        }

        private static async Task ShowStaticMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = Localization.GetString("OK")
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Channel] " + title + ": " + message);
            }
        }

        private static void SetImageSource(Image image, string path)
        {
            if (image == null || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                App.SetThemeImageSource(image, path);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Channel] Icon load error: " + ex.Message);
            }
        }

        private static SubscriptionLoadResult ExtractSubscriptionStateFromBrowse(JsonObject root, string sourceName, string currentChannelId)
        {
            var result = new SubscriptionLoadResult
            {
                Found = false,
                State = ChannelSubscriptionState.Unknown,
                NotificationState = ChannelNotificationState.Unknown,
                Source = sourceName
            };

            if (root == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return result;
            }

            // Important: authenticated /browse can contain both subscribe and unsubscribe
            // endpoint templates. The reliable signed-in state is the subscribeButtonRenderer.subscribed
            // boolean for the current channel.
            CollectSubscriptionEndpointData(root, string.Empty, result, 0, currentChannelId);

            bool foundRendererState;
            ChannelNotificationState rendererNotificationState;
            var rendererState = FindSubscribeButtonRendererState(root, currentChannelId, out foundRendererState, out rendererNotificationState);
            if (foundRendererState)
            {
                result.Found = true;
                result.State = rendererState;
                if (rendererNotificationState != ChannelNotificationState.Unknown)
                {
                    result.NotificationState = rendererNotificationState;
                }
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from subscribeButtonRenderer.subscribed: " + rendererState + ", " + result.NotificationState);
            }

            // Fallback for WEB browse responses that expose a top-level header.
            var header = GetObject(root, "header");
            bool foundHeaderState;
            var headerState = FindVisibleSubscriptionStateInHeader(header, out foundHeaderState);
            if (result.State == ChannelSubscriptionState.Unknown && foundHeaderState)
            {
                result.Found = true;
                result.State = headerState;
                System.Diagnostics.Debug.WriteLine("[Channel] Subscription state from visible header button: " + headerState);
            }

            // Endpoints are useful for params, but they are not always a reliable state by
            // themselves. Some /browse responses contain both subscribeEndpoint and
            // unsubscribeEndpoint templates for the same channel. In that case, prefer the
            // visible header button; if it was not found, default to NotSubscribed instead of
            // incorrectly showing every channel as subscribed.
            if (result.State == ChannelSubscriptionState.Unknown)
            {
                var hasSubscribeEndpoint = !string.IsNullOrWhiteSpace(result.SubscribeParams);
                var hasUnsubscribeEndpoint = !string.IsNullOrWhiteSpace(result.UnsubscribeParams);

                if (hasUnsubscribeEndpoint && !hasSubscribeEndpoint)
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.Subscribed;
                }
                else if (hasSubscribeEndpoint)
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.NotSubscribed;
                    if (hasUnsubscribeEndpoint)
                    {
                        System.Diagnostics.Debug.WriteLine("[Channel] Both subscribe and unsubscribe endpoints found; using NotSubscribed because no visible subscribed header state was found.");
                    }
                }
            }

            // Notification state must not by itself mean "subscribed". /browse may include
            // notification menu templates even for channels we are not subscribed to.
            if (result.State == ChannelSubscriptionState.Subscribed)
            {
                bool foundNotification;
                var notification = FindNotificationState(header, string.Empty, 0, out foundNotification);
                if (foundNotification)
                {
                    result.NotificationState = notification;
                }
            }

            return result;
        }

        private static ChannelSubscriptionState FindSubscribeButtonRendererState(
            IJsonValue value,
            string currentChannelId,
            out bool found,
            out ChannelNotificationState notificationState
        )
        {
            return FindSubscribeButtonRendererState(value, currentChannelId, 0, out found, out notificationState);
        }

        private static ChannelSubscriptionState FindSubscribeButtonRendererState(
            IJsonValue value,
            string currentChannelId,
            int depth,
            out bool found,
            out ChannelNotificationState notificationState
        )
        {
            found = false;
            notificationState = ChannelNotificationState.Unknown;

            if (value == null || depth > 80 || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("subscribeButtonRenderer"))
                    {
                        var renderer = GetObject(obj, "subscribeButtonRenderer");
                        if (SubscribeButtonRendererTargetsChannel(renderer, currentChannelId))
                        {
                            ChannelSubscriptionState state;
                            if (TryReadSubscribeButtonRendererState(renderer, out state))
                            {
                                found = true;
                                notificationState = ReadNotificationStateFromSubscribeButtonRenderer(renderer);
                                return state;
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        bool nestedFound;
                        ChannelNotificationState nestedNotificationState;
                        var nested = FindSubscribeButtonRendererState(pair.Value, currentChannelId, depth + 1, out nestedFound, out nestedNotificationState);
                        if (nestedFound)
                        {
                            found = true;
                            notificationState = nestedNotificationState;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        ChannelNotificationState nestedNotificationState;
                        var nested = FindSubscribeButtonRendererState(array[(int)i], currentChannelId, depth + 1, out nestedFound, out nestedNotificationState);
                        if (nestedFound)
                        {
                            found = true;
                            notificationState = nestedNotificationState;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static bool SubscribeButtonRendererTargetsChannel(JsonObject renderer, string currentChannelId)
        {
            if (renderer == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var rendererChannelId = GetString(renderer, "channelId");
            if (string.Equals(rendererChannelId, currentChannelId, StringComparison.Ordinal))
            {
                return true;
            }

            return JsonValueContainsExactString(renderer, currentChannelId, 0);
        }

        private static bool JsonValueContainsExactString(IJsonValue value, string expected, int depth)
        {
            if (value == null || string.IsNullOrWhiteSpace(expected) || depth > 40)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.String)
                {
                    return string.Equals(value.GetString(), expected, StringComparison.Ordinal);
                }

                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        if (JsonValueContainsExactString(pair.Value, expected, depth + 1))
                        {
                            return true;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (JsonValueContainsExactString(array[(int)i], expected, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryReadSubscribeButtonRendererState(JsonObject renderer, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (renderer == null)
            {
                return false;
            }

            bool subscribed;
            if (TryGetBoolean(renderer, "subscribed", out subscribed))
            {
                state = subscribed ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            var buttonText = ExtractText(GetObject(renderer, "buttonText"));
            if (TryParseVisibleSubscriptionButtonText(buttonText, out state))
            {
                return true;
            }

            return false;
        }

        private static ChannelNotificationState ReadNotificationStateFromSubscribeButtonRenderer(JsonObject renderer)
        {
            var toggle = GetObjectPath(renderer, "notificationPreferenceButton", "subscriptionNotificationToggleButtonRenderer");
            if (toggle == null)
            {
                return ChannelNotificationState.Unknown;
            }

            int currentStateId;
            if (TryGetInt(toggle, "currentStateId", out currentStateId))
            {
                var states = GetArray(toggle, "states");
                if (states != null)
                {
                    for (uint i = 0; i < states.Count; i++)
                    {
                        if (states[(int)i].ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        var stateObject = states[(int)i].GetObject();
                        int stateId;
                        if (TryGetInt(stateObject, "stateId", out stateId) && stateId == currentStateId)
                        {
                            ChannelNotificationState parsed;
                            if (TryParseNotificationStateText(GetString(stateObject, "notificationState"), out parsed))
                            {
                                return parsed;
                            }
                        }
                    }
                }
            }

            ChannelNotificationState directParsed;
            if (TryParseNotificationStateText(GetString(toggle, "notificationState"), out directParsed))
            {
                return directParsed;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelSubscriptionState FindVisibleSubscriptionStateInHeader(IJsonValue value, out bool found)
        {
            return FindVisibleSubscriptionStateInHeader(value, string.Empty, 0, out found);
        }

        private static ChannelSubscriptionState FindVisibleSubscriptionStateInHeader(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 40)
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    if (!IsHiddenSubscriptionTemplatePath(lowerPath))
                    {
                        bool boolValue;
                        if (TryGetBoolean(obj, "subscribed", out boolValue)
                            || TryGetBoolean(obj, "isSubscribed", out boolValue)
                            || TryGetBoolean(obj, "is_subscribed", out boolValue))
                        {
                            found = true;
                            return boolValue ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                        }

                        var visibleText = CollectVisibleButtonStateText(obj);
                        ChannelSubscriptionState parsed;
                        if (TryParseVisibleSubscriptionButtonText(visibleText, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (IsHiddenSubscriptionTemplatePath(nextPath))
                        {
                            continue;
                        }

                        bool nestedFound;
                        var nested = FindVisibleSubscriptionStateInHeader(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindVisibleSubscriptionStateInHeader(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static bool IsHiddenSubscriptionTemplatePath(string lowerPath)
        {
            var p = lowerPath ?? string.Empty;
            return p.Contains("menu")
                || p.Contains("dialog")
                || p.Contains("confirmation")
                || p.Contains("modal")
                || p.Contains("sheet")
                || p.Contains("popup")
                || p.Contains("notification")
                || p.Contains("subscribeendpoint")
                || p.Contains("unsubscribeendpoint")
                || p.Contains("command")
                || p.Contains("endpoint")
                || p.Contains("tracking")
                || p.Contains("logging");
        }

        private static string CollectVisibleButtonStateText(JsonObject obj)
        {
            if (obj == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            AppendVisibleStateText(sb, obj, "title");
            AppendVisibleStateText(sb, obj, "text");
            AppendVisibleStateText(sb, obj, "label");
            AppendVisibleStateText(sb, obj, "accessibilityText");
            AppendVisibleStateText(sb, obj, "ariaLabel");
            AppendVisibleStateText(sb, obj, "accessibilityLabel");

            var accessibility = GetObject(obj, "accessibility");
            if (accessibility != null)
            {
                AppendVisibleStateText(sb, accessibility, "label");
                var data = GetObject(accessibility, "accessibilityData");
                AppendVisibleStateText(sb, data, "label");
            }

            return sb.ToString();
        }

        private static void AppendVisibleStateText(StringBuilder sb, JsonObject obj, string key)
        {
            if (sb == null || obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return;
            }

            var value = obj[key];
            if (value == null)
            {
                return;
            }

            if (value.ValueType == JsonValueType.String)
            {
                sb.Append(' ');
                sb.Append(value.GetString());
            }
            else if (value.ValueType == JsonValueType.Object)
            {
                var text = ExtractText(value.GetObject());
                if (!string.IsNullOrWhiteSpace(text))
                {
                    sb.Append(' ');
                    sb.Append(text);
                }
            }
        }

        private static bool TryParseVisibleSubscriptionButtonText(string text, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (upper.Contains("SUBSCRIBERS") || upper.Contains("SUBSCRIBER") || upper.Contains("ПОДПИСЧИК"))
            {
                return false;
            }

            if (compact == "SUBSCRIBED"
                || compact.Contains("SUBSCRIBED")
                || upper.Contains("ВЫ ПОДПИСАН")
                || upper.Contains("ПОДПИСАНЫ"))
            {
                state = ChannelSubscriptionState.Subscribed;
                return true;
            }

            if (compact == "SUBSCRIBE"
                || compact.Contains("SUBSCRIBE")
                || upper.Contains("ПОДПИСАТЬСЯ"))
            {
                state = ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            return false;
        }

        private static void CollectSubscriptionEndpointData(IJsonValue value, string path, SubscriptionLoadResult result, int depth, string currentChannelId)
        {
            if (value == null || result == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lower = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lower : path + "." + lower;

                        if (string.Equals(key, "subscribeEndpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            var endpoint = pair.Value != null && pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject() : null;
                            if (EndpointTargetsChannel(endpoint, obj, currentChannelId))
                            {
                                result.SubscribeParams = FirstNonEmpty(result.SubscribeParams, GetString(endpoint, "params"), GetString(obj, "params"));
                                result.SubscribeClickTrackingParams = FirstNonEmpty(result.SubscribeClickTrackingParams, GetString(endpoint, "clickTrackingParams"), GetString(obj, "clickTrackingParams"));
                                result.ChannelId = FirstNonEmpty(result.ChannelId, currentChannelId, ExtractChannelIdFromEndpoint(endpoint));
                            }
                        }
                        else if (string.Equals(key, "unsubscribeEndpoint", StringComparison.OrdinalIgnoreCase))
                        {
                            var endpoint = pair.Value != null && pair.Value.ValueType == JsonValueType.Object ? pair.Value.GetObject() : null;
                            if (EndpointTargetsChannel(endpoint, obj, currentChannelId))
                            {
                                result.UnsubscribeParams = FirstNonEmpty(result.UnsubscribeParams, GetString(endpoint, "params"), GetString(obj, "params"));
                                result.UnsubscribeClickTrackingParams = FirstNonEmpty(result.UnsubscribeClickTrackingParams, GetString(endpoint, "clickTrackingParams"), GetString(obj, "clickTrackingParams"));
                                result.ChannelId = FirstNonEmpty(result.ChannelId, currentChannelId, ExtractChannelIdFromEndpoint(endpoint));
                            }
                        }

                        CollectSubscriptionEndpointData(pair.Value, nextPath, result, depth + 1, currentChannelId);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectSubscriptionEndpointData(array[(int)i], path + "[]", result, depth + 1, currentChannelId);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool EndpointTargetsChannel(JsonObject endpoint, JsonObject container, string currentChannelId)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var endpointChannelId = FirstNonEmpty(
                ExtractChannelIdFromEndpoint(endpoint),
                ExtractChannelIdFromEndpoint(container),
                ExtractChannelIdFromEndpoint(GetObject(container, "commandMetadata")),
                ExtractChannelIdFromEndpoint(GetObject(container, "navigationEndpoint"))
            );

            if (string.IsNullOrWhiteSpace(endpointChannelId))
            {
                return false;
            }

            return string.Equals(endpointChannelId, currentChannelId, StringComparison.OrdinalIgnoreCase);
        }

        private static string ExtractChannelIdFromEndpoint(JsonObject endpoint)
        {
            if (endpoint == null)
            {
                return string.Empty;
            }

            var ids = GetArray(endpoint, "channelIds");
            if (ids != null && ids.Count > 0)
            {
                var first = ids[(int)0];
                if (first != null && first.ValueType == JsonValueType.String)
                {
                    return first.GetString();
                }
            }

            var direct = FirstNonEmpty(GetString(endpoint, "channelId"), GetString(endpoint, "browseId"), GetString(endpoint, "targetId"));
            if (!string.IsNullOrWhiteSpace(direct) && direct.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
            {
                return direct;
            }

            var browseEndpoint = GetObject(endpoint, "browseEndpoint");
            if (browseEndpoint != null)
            {
                var browseId = FirstNonEmpty(GetString(browseEndpoint, "browseId"), GetString(browseEndpoint, "canonicalBaseUrl"));
                if (!string.IsNullOrWhiteSpace(browseId) && browseId.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
                {
                    return browseId;
                }
            }

            var subscribeEndpoint = GetObject(endpoint, "subscribeEndpoint");
            if (subscribeEndpoint != null)
            {
                var subscribeChannelId = ExtractChannelIdFromEndpoint(subscribeEndpoint);
                if (!string.IsNullOrWhiteSpace(subscribeChannelId))
                {
                    return subscribeChannelId;
                }
            }

            var unsubscribeEndpoint = GetObject(endpoint, "unsubscribeEndpoint");
            if (unsubscribeEndpoint != null)
            {
                var unsubscribeChannelId = ExtractChannelIdFromEndpoint(unsubscribeEndpoint);
                if (!string.IsNullOrWhiteSpace(unsubscribeChannelId))
                {
                    return unsubscribeChannelId;
                }
            }

            return string.Empty;
        }

        private static ChannelSubscriptionState FindSubscriptionState(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    bool boolValue;
                    if (TryGetBoolean(obj, "subscribed", out boolValue) || TryGetBoolean(obj, "isSubscribed", out boolValue) || TryGetBoolean(obj, "is_subscribed", out boolValue))
                    {
                        found = true;
                        return boolValue ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                    }

                    if (IsSubscriptionRelatedPath(lowerPath) || HasSubscriptionRelatedKey(obj))
                    {
                        var text = CollectObjectText(obj, 0, 3);
                        ChannelSubscriptionState parsed;
                        if (TryParseSubscriptionStateText(text, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsSubscriptionRelatedPath(nextPath))
                        {
                            ChannelSubscriptionState parsed;
                            if (TryParseSubscriptionStateText(pair.Value.GetString(), out parsed))
                            {
                                found = true;
                                return parsed;
                            }
                        }

                        bool nestedFound;
                        var nested = FindSubscriptionState(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindSubscriptionState(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static ChannelNotificationState FindNotificationState(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path.ToLowerInvariant();

                    if (ObjectHasSelectedTrue(obj) && IsNotificationRelatedPath(lowerPath))
                    {
                        var text = CollectObjectText(obj, 0, 4);
                        ChannelNotificationState parsed;
                        if (TryParseNotificationStateText(text, out parsed))
                        {
                            found = true;
                            return parsed;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var compactKey = Compact(lowerKey);
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null
                            && pair.Value.ValueType == JsonValueType.String
                            && (IsCurrentNotificationStateKey(compactKey, nextPath) || (IsNotificationRelatedPath(nextPath) && IsPreferredNotificationTextKey(compactKey))))
                        {
                            ChannelNotificationState parsed;
                            if (TryParseNotificationStateText(pair.Value.GetString(), out parsed))
                            {
                                found = true;
                                return parsed;
                            }
                        }

                        bool nestedFound;
                        var nested = FindNotificationState(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindNotificationState(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
            }

            return ChannelNotificationState.Unknown;
        }

        private static bool IsSubscriptionRelatedPath(string path)
        {
            var p = path ?? string.Empty;
            return p.Contains("subscribe") || p.Contains("subscription") || p.Contains("subscribed") || p.Contains("owner") || p.Contains("header") || p.Contains("actions");
        }

        private static bool HasSubscriptionRelatedKey(JsonObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            foreach (var pair in obj)
            {
                var key = (pair.Key ?? string.Empty).ToLowerInvariant();
                if (key.Contains("subscribe") || key.Contains("subscription"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryParseSubscriptionStateText(string text, out ChannelSubscriptionState state)
        {
            state = ChannelSubscriptionState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (upper.Contains("SUBSCRIBERS") || upper.Contains("SUBSCRIBER") || upper.Contains("ПОДПИСЧИК"))
            {
                return false;
            }

            if (compact.Contains("UNSUBSCRIBE") || compact.Contains("SUBSCRIBED") || upper.Contains("ОТПИСАТЬ") || upper.Contains("ВЫПОДПИСАН") || upper.Contains("ПОДПИСАН"))
            {
                state = ChannelSubscriptionState.Subscribed;
                return true;
            }

            if (compact == "SUBSCRIBE" || compact.Contains("SUBSCRIBE") || upper.Contains("ПОДПИСАТЬСЯ"))
            {
                state = ChannelSubscriptionState.NotSubscribed;
                return true;
            }

            return false;
        }

        private static bool IsNotificationRelatedPath(string path)
        {
            var p = path ?? string.Empty;
            return p.Contains("notification") || p.Contains("bell") || p.Contains("preference");
        }

        private static bool IsCurrentNotificationStateKey(string compactKey, string path)
        {
            var key = compactKey ?? string.Empty;
            var p = path ?? string.Empty;
            return key == "currentnotificationstate"
                || key == "currentnotificationpreference"
                || key == "currentstate"
                || key == "subscriptionnotificationpreference"
                || (p.Contains("current") && p.Contains("notification"));
        }

        private static bool IsPreferredNotificationTextKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "title" || key == "text" || key == "label" || key == "accessibility" || key == "accessibilitytext" || key == "arialabel" || key == "icontype";
        }

        private static bool TryParseNotificationStateText(string text, out ChannelNotificationState state)
        {
            state = ChannelNotificationState.Unknown;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var upper = text.Trim().ToUpperInvariant();
            var compact = Compact(upper);

            if (compact.Contains("PERSONALIZED") || compact.Contains("PERSONALISED") || compact.Contains("DEFAULT") || compact.Contains("OCCASIONAL") || upper.Contains("НА ОСНОВЕ") || upper.Contains("ПРЕДПОЧТ") || upper.Contains("ПЕРСОНАЛ"))
            {
                state = ChannelNotificationState.Default;
                return true;
            }

            if (compact == "ALL" || compact.Contains("ALLNOTIFICATION") || compact.Contains("NOTIFICATIONSALL") || compact.Contains("RINGING") || upper.Contains("ВСЕ УВЕДОМ") || upper.Contains("ВСЕ ОПОВЕЩ"))
            {
                state = ChannelNotificationState.All;
                return true;
            }

            if (compact == "NONE" || compact.Contains("NONOTIFICATION") || compact.Contains("NOTIFICATIONNONE") || compact.Contains("NOTIFICATIONSOFF") || compact.Contains("MUTED") || upper.Contains("НИКАК") || upper.Contains("БЕЗ УВЕДОМ") || upper.Contains("НЕТ УВЕДОМ"))
            {
                state = ChannelNotificationState.None;
                return true;
            }

            if (compact == "NOTIFICATIONS" || compact == "NOTIFICATION")
            {
                state = ChannelNotificationState.Default;
                return true;
            }

            return false;
        }

        private static bool TryGetBoolean(JsonObject obj, string key, out bool value)
        {
            value = false;
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return false;
            }

            var jsonValue = obj[key];
            if (jsonValue != null && jsonValue.ValueType == JsonValueType.Boolean)
            {
                value = jsonValue.GetBoolean();
                return true;
            }

            return false;
        }


        private static bool TryGetInt(JsonObject obj, string key, out int value)
        {
            value = 0;
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return false;
            }

            var jsonValue = obj[key];
            if (jsonValue != null && jsonValue.ValueType == JsonValueType.Number)
            {
                value = (int)jsonValue.GetNumber();
                return true;
            }

            return false;
        }

        private static bool ObjectHasSelectedTrue(JsonObject obj)
        {
            bool value;
            return TryGetBoolean(obj, "selected", out value) && value
                || TryGetBoolean(obj, "isSelected", out value) && value
                || TryGetBoolean(obj, "checked", out value) && value
                || TryGetBoolean(obj, "isChecked", out value) && value;
        }

        private static string CollectObjectText(IJsonValue value, int depth, int maxDepth)
        {
            if (value == null || depth > maxDepth)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var extracted = ExtractText(obj);
                    if (!string.IsNullOrWhiteSpace(extracted))
                    {
                        sb.Append(extracted);
                        sb.Append(' ');
                    }

                    foreach (var pair in obj)
                    {
                        var key = Compact(pair.Key ?? string.Empty);
                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String)
                        {
                            if (IsPreferredNotificationTextKey(key) || key.Contains("subscribe") || key.Contains("label") || key.Contains("title") || key.Contains("text"))
                            {
                                sb.Append(pair.Value.GetString());
                                sb.Append(' ');
                            }
                        }
                        else if (pair.Value != null && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            var nested = CollectObjectText(pair.Value, depth + 1, maxDepth);
                            if (!string.IsNullOrWhiteSpace(nested))
                            {
                                sb.Append(nested);
                                sb.Append(' ');
                            }
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        var nested = CollectObjectText(array[(int)i], depth + 1, maxDepth);
                        if (!string.IsNullOrWhiteSpace(nested))
                        {
                            sb.Append(nested);
                            sb.Append(' ');
                        }
                    }
                }
            }
            catch
            {
            }

            return sb.ToString();
        }

        private static string Compact(string text)
        {
            return (text ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private sealed class ChannelPostItem : System.ComponentModel.INotifyPropertyChanged
        {
            private BitmapImage _imageSource;

            public string Text { get; set; }
            public string Author { get; set; }
            public string PublishedText { get; set; }
            public string AuthorThumbnailUrl { get; set; }
            public string ImageUrl { get; set; }
            public BitmapImage ImageSource
            {
                get { return _imageSource; }
                set
                {
                    if (ReferenceEquals(_imageSource, value)) return;
                    _imageSource = value;
                    RaisePropertyChanged("ImageSource");
                    RaisePropertyChanged("ImageVisibility");
                }
            }

            public string MetaLine
            {
                get
                {
                    if (string.IsNullOrWhiteSpace(Author)) return PublishedText ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(PublishedText)) return Author;
                    return Author + " • " + PublishedText;
                }
            }

            public Visibility ImageVisibility
            {
                get
                {
                    return !string.IsNullOrWhiteSpace(ImageUrl)
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }
            }

            public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

            private void RaisePropertyChanged(string propertyName)
            {
                var handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
                }
            }
        }

        private sealed class ChannelPageData
        {
            public ChannelPageInfo Info { get; set; }
            public List<VideoCardItem> Videos { get; set; }
            public string Continuation { get; set; }
            public SubscriptionLoadResult SubscriptionState { get; set; }
        }

        private sealed class SubscriptionLoadResult
        {
            public bool Found { get; set; }
            public ChannelSubscriptionState State { get; set; }
            public ChannelNotificationState NotificationState { get; set; }
            public string SubscribeParams { get; set; }
            public string UnsubscribeParams { get; set; }
            public string SubscribeClickTrackingParams { get; set; }
            public string UnsubscribeClickTrackingParams { get; set; }
            public string ChannelId { get; set; }
            public string Source { get; set; }
        }

        private sealed class ChannelPageInfo
        {
            public string ChannelId { get; set; }
            public string Title { get; set; }
            public string Handle { get; set; }
            public string Description { get; set; }
            public string ThumbnailUrl { get; set; }
            public string BannerUrl { get; set; }
            public string SubscriberCount { get; set; }
            public string VideoCount { get; set; }
        }
    }
}
