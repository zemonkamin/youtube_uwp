using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class History : Page
    {
        private static int PageSize
        {
            get { return ResponsiveLayout.IsPhoneDevice ? 14 : 32; }
        }
        private const double LoadMoreDistance = 420.0;

        private readonly ObservableCollection<HistoryDateSectionViewModel> _sections = new ObservableCollection<HistoryDateSectionViewModel>();
        private readonly HashSet<string> _seenVideoIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> _consumedCursors = new HashSet<string>(StringComparer.Ordinal);
        private Frame _frame;
        private string _refreshToken = string.Empty;
        private string _continuationToken = string.Empty;
        private bool _isLoading;
        private bool _hasMore = true;
        private bool _isActive;
        private bool _hasLoaded;
        private int _loadGeneration;
        private string _accountKey = string.Empty;

        public History()
        {
            InitializeComponent();
            _frame = Window.Current.Content as Frame;
            HistoryGroupsList.ItemsSource = _sections;
            NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;
            Loaded += History_Loaded;
            SizeChanged += History_SizeChanged;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isActive = true;
            var generation = ++_loadGeneration;

            if (tabbar != null)
                tabbar.SetActiveTab(Tabbar.ActiveTab.None);

            var currentView = SystemNavigationManager.GetForCurrentView();
            currentView.AppViewBackButtonVisibility = _frame != null && _frame.CanGoBack
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Collapsed;
            currentView.BackRequested -= OnBackRequested;
            currentView.BackRequested += OnBackRequested;

            await WebAccountSession.InitializeAsync();
            if (!_isActive || generation != _loadGeneration) return;
            Config.LoadUserToken();
            _refreshToken = Config.UserToken;

            var owner = PlaybackResolver.AccountKey;
            if (_accountKey != owner)
            {
                _accountKey = owner;
                ResetHistory();
            }

            // Returning from a watched video keeps the older pages and scroll position.
            if (e.NavigationMode == NavigationMode.Back && _hasLoaded)
            {
                await LoadMoreIfNeededAsync();
                return;
            }

            await LoadInitialAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
            _isActive = false;
            _loadGeneration++;
            _isLoading = false;
            ShowInitialLoading(false);
            ShowBottomLoading(false);
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_frame != null && _frame.CanGoBack)
            {
                e.Handled = true;
                _frame.GoBack();
            }
        }

        private async Task LoadInitialAsync()
        {
            if (string.IsNullOrWhiteSpace(_refreshToken) && !WebAccountSession.IsSignedIn)
            {
                ResetHistory();
                EmptyText.Text = Localization.GetString("HistorySignIn");
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            var generation = _loadGeneration;
            EmptyText.Visibility = Visibility.Collapsed;
            ShowInitialLoading(_sections.Count == 0);
            try
            {
                await LoadMoreAsync(true);
            }
            finally
            {
                if (generation == _loadGeneration) ShowInitialLoading(false);
            }
        }

        private void ResetHistory()
        {
            _sections.Clear();
            _seenVideoIds.Clear();
            _consumedCursors.Clear();
            _continuationToken = string.Empty;
            _hasMore = true;
            _hasLoaded = false;
        }

        private async Task LoadMoreAsync(bool refresh = false)
        {
            if (!_isActive || _isLoading || (!refresh && !_hasMore))
                return;

            var generation = _loadGeneration;
            var owner = _accountKey;
            _isLoading = true;
            ShowBottomLoading(_sections.Count > 0);

            try
            {
                do
                {
                    var cursor = refresh ? string.Empty : _continuationToken;
                    var result = await Config.GetHistoryFeedPageAsync(_refreshToken, cursor, PageSize);
                    if (!_isActive || generation != _loadGeneration || owner != PlaybackResolver.AccountKey) return;
                    if (result == null) throw new InvalidOperationException("History response unavailable.");

                    // Refresh the head without discarding older pages or their continuation.
                    // Only a new/empty account snapshot needs its paging state initialized.
                    var keepOlderPages = refresh && _hasLoaded && _seenVideoIds.Count > 0;
                    if (refresh && !keepOlderPages) ResetHistory();
                    refresh = false;
                    MergeHistoryResult(result, keepOlderPages);
                    _hasLoaded = true;
                    if (!keepOlderPages)
                    {
                        _consumedCursors.Add(cursor);
                        _continuationToken = result.ContinuationToken ?? string.Empty;
                        _hasMore = !string.IsNullOrWhiteSpace(_continuationToken)
                            && !_consumedCursors.Contains(_continuationToken);
                    }
                    EmptyText.Text = Localization.GetString("NoHistoryYet");
                    EmptyText.Visibility = _sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                    // Short/empty pages also need their continuation consumed. Let XAML arrange
                    // the appended items before checking whether the viewport is filled.
                    await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => HistoryScroll.UpdateLayout());
                    if (!_isActive || generation != _loadGeneration) return;
                } while (_hasMore && IsNearHistoryBottom());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[History] LoadMore error: " + ex.Message);
                if (_isActive && generation == _loadGeneration && _sections.Count == 0)
                {
                    EmptyText.Text = Localization.GetString("HistoryLoadFailed");
                    EmptyText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                if (generation == _loadGeneration)
                {
                    ShowBottomLoading(false);
                    _isLoading = false;
                }
            }
        }

        private void MergeHistoryResult(HistoryFeedPage result, bool prepend = false)
        {
            if (result.Groups == null)
                return;

            for (var groupIndex = 0; groupIndex < result.Groups.Count; groupIndex++)
            {
                var group = result.Groups[prepend ? result.Groups.Count - 1 - groupIndex : groupIndex];
                if (group == null ||
                    ((group.Videos == null || group.Videos.Count == 0) &&
                     (group.Shorts == null || group.Shorts.Count == 0)))
                    continue;

                var title = string.IsNullOrWhiteSpace(group.DateTitle) ? Localization.GetString("Older") : group.DateTitle;
                var target = GetOrCreateSection(title, prepend);

                if (group.Shorts != null)
                {
                    var pendingShorts = new List<VideoCardItem>();
                    foreach (var shortVideo in group.Shorts)
                    {
                        if (shortVideo == null || string.IsNullOrWhiteSpace(shortVideo.VideoId))
                            continue;

                        if (_seenVideoIds.Contains(shortVideo.VideoId))
                            continue;

                        _seenVideoIds.Add(shortVideo.VideoId);
                        pendingShorts.Add(shortVideo);
                    }
                    AddHistoryItems(target.Shorts, pendingShorts, prepend);
                }

                if (group.Videos != null)
                {
                    var pendingVideos = new List<VideoCardItem>();
                    foreach (var video in group.Videos)
                    {
                        if (video == null || string.IsNullOrWhiteSpace(video.VideoId))
                            continue;

                        if (_seenVideoIds.Contains(video.VideoId))
                            continue;

                        _seenVideoIds.Add(video.VideoId);
                        pendingVideos.Add(video);
                    }
                    AddHistoryItems(target.Videos, pendingVideos, prepend);
                }
            }

            for (var i = _sections.Count - 1; i >= 0; i--)
            {
                if (_sections[i].Videos.Count == 0 && _sections[i].Shorts.Count == 0)
                    _sections.RemoveAt(i);
            }
        }

        private static void AddHistoryItems(ObservableCollection<VideoCardItem> target,
            List<VideoCardItem> items, bool prepend)
        {
            if (prepend)
            {
                for (var i = items.Count - 1; i >= 0; i--) target.Insert(0, items[i]);
            }
            else
            {
                foreach (var item in items) target.Add(item);
            }
        }

        private HistoryDateSectionViewModel GetOrCreateSection(string title, bool prepend)
        {
            for (var i = 0; i < _sections.Count; i++)
            {
                if (string.Equals(_sections[i].DateTitle, title, StringComparison.OrdinalIgnoreCase))
                    return _sections[i];
            }

            var section = new HistoryDateSectionViewModel
            {
                DateTitle = title,
                DateTitleVisibility = string.Equals(
                    title,
                    Localization.GetString("Older"),
                    StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Collapsed
                    : Visibility.Visible
            };
            if (prepend) _sections.Insert(0, section);
            else _sections.Add(section);
            return section;
        }

        private async void HistoryScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate) await LoadMoreIfNeededAsync();
        }

        private bool IsNearHistoryBottom()
        {
            return HistoryScroll != null && HistoryScroll.ViewportHeight > 0
                && HistoryScroll.ScrollableHeight - HistoryScroll.VerticalOffset <= LoadMoreDistance;
        }

        private async Task LoadMoreIfNeededAsync()
        {
            if (_isActive && _hasLoaded && !_isLoading && _hasMore && IsNearHistoryBottom())
                await LoadMoreAsync();
        }

        private async void History_Loaded(object sender, RoutedEventArgs e)
        {
            await LoadMoreIfNeededAsync();
        }

        private async void History_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            await LoadMoreIfNeededAsync();
        }

        private void HistoryThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as VideoCardItem;
            if (image == null || item == null)
                return;

            VideoThumbnailController.Assign(image, item.VideoId, item.ThumbnailUrl, 180);
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as VideoCardItem : null;

            if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && _frame != null)
                _frame.Navigate(typeof(Video), item.VideoId);
        }

        private void ShortCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as VideoCardItem : null;

            if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && _frame != null)
                _frame.Navigate(typeof(Shorts), item.VideoId);
        }

        private void ShowInitialLoading(bool isLoading)
        {
            if (LoadingOverlay != null)
                LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

            if (LoadingRing != null)
                LoadingRing.IsActive = isLoading;
        }

        private void ShowBottomLoading(bool isLoading)
        {
            if (BottomLoadingPanel != null)
                BottomLoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

            if (BottomLoadingRing != null)
                BottomLoadingRing.IsActive = isLoading;
        }
    }

    public sealed class HistoryDateSectionViewModel
    {
        public string DateTitle { get; set; }
        public Visibility DateTitleVisibility { get; set; }
        public ObservableCollection<VideoCardItem> Videos { get; private set; }
        public ObservableCollection<VideoCardItem> Shorts { get; private set; }

        public HistoryDateSectionViewModel()
        {
            DateTitleVisibility = Visibility.Visible;
            Videos = new ObservableCollection<VideoCardItem>();
            Shorts = new ObservableCollection<VideoCardItem>();
        }
    }
}
