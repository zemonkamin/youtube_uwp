using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class History : Page
    {
        private const int PageSize = 32;
        private const double LoadMoreDistance = 420.0;

        private readonly ObservableCollection<HistoryDateSectionViewModel> _sections = new ObservableCollection<HistoryDateSectionViewModel>();
        private readonly HashSet<string> _seenVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private Frame _frame;
        private string _refreshToken = string.Empty;
        private string _continuationToken = string.Empty;
        private bool _isLoading;
        private bool _hasMore = true;

        public History()
        {
            InitializeComponent();
            _frame = Window.Current.Content as Frame;
            HistoryGroupsList.ItemsSource = _sections;
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (tabbar != null)
                tabbar.SetActiveTab(Tabbar.ActiveTab.Account);

            var currentView = SystemNavigationManager.GetForCurrentView();
            currentView.AppViewBackButtonVisibility = _frame != null && _frame.CanGoBack
                ? AppViewBackButtonVisibility.Visible
                : AppViewBackButtonVisibility.Collapsed;
            currentView.BackRequested += OnBackRequested;

            Config.LoadUserToken();
            _refreshToken = Config.UserToken;

            await LoadInitialAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);
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
            if (string.IsNullOrWhiteSpace(_refreshToken))
            {
                EmptyText.Text = "Sign in to see your watch history";
                EmptyText.Visibility = Visibility.Visible;
                return;
            }

            _sections.Clear();
            _seenVideoIds.Clear();
            _continuationToken = string.Empty;
            _hasMore = true;

            ShowInitialLoading(true);
            try
            {
                await LoadMoreAsync();
            }
            finally
            {
                ShowInitialLoading(false);
            }
        }

        private async Task LoadMoreAsync()
        {
            if (_isLoading || !_hasMore)
                return;

            _isLoading = true;
            ShowBottomLoading(_sections.Count > 0);

            try
            {
                var result = await Config.GetHistoryFeedPageAsync(_refreshToken, _continuationToken, PageSize);
                if (result == null)
                {
                    _hasMore = false;
                    return;
                }

                MergeHistoryResult(result);

                _continuationToken = result.ContinuationToken ?? string.Empty;
                _hasMore = !string.IsNullOrWhiteSpace(_continuationToken);

                EmptyText.Visibility = _sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[History] LoadMore error: " + ex.Message);
                if (_sections.Count == 0)
                {
                    EmptyText.Text = "Failed to load history";
                    EmptyText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                ShowBottomLoading(false);
                _isLoading = false;
            }
        }

        private void MergeHistoryResult(HistoryFeedPage result)
        {
            if (result.Groups == null)
                return;

            foreach (var group in result.Groups)
            {
                if (group == null || group.Videos == null || group.Videos.Count == 0)
                    continue;

                var title = string.IsNullOrWhiteSpace(group.DateTitle) ? "Older" : group.DateTitle;
                var target = GetOrCreateSection(title);

                foreach (var video in group.Videos)
                {
                    if (video == null || string.IsNullOrWhiteSpace(video.VideoId))
                        continue;

                    if (_seenVideoIds.Contains(video.VideoId))
                        continue;

                    _seenVideoIds.Add(video.VideoId);
                    target.Videos.Add(video);
                }
            }

            for (var i = _sections.Count - 1; i >= 0; i--)
            {
                if (_sections[i].Videos.Count == 0)
                    _sections.RemoveAt(i);
            }
        }

        private HistoryDateSectionViewModel GetOrCreateSection(string title)
        {
            for (var i = 0; i < _sections.Count; i++)
            {
                if (string.Equals(_sections[i].DateTitle, title, StringComparison.OrdinalIgnoreCase))
                    return _sections[i];
            }

            var section = new HistoryDateSectionViewModel { DateTitle = title };
            _sections.Add(section);
            return section;
        }

        private async void HistoryScroll_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (e.IsIntermediate || _isLoading || !_hasMore)
                return;

            if (HistoryScroll.ScrollableHeight <= 0)
                return;

            var distanceToBottom = HistoryScroll.ScrollableHeight - HistoryScroll.VerticalOffset;
            if (distanceToBottom <= LoadMoreDistance)
                await LoadMoreAsync();
        }

        private void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as VideoCardItem : null;

            if (item != null && !string.IsNullOrWhiteSpace(item.VideoId) && _frame != null)
                _frame.Navigate(typeof(Video), item.VideoId);
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
        public ObservableCollection<VideoCardItem> Videos { get; private set; }

        public HistoryDateSectionViewModel()
        {
            Videos = new ObservableCollection<VideoCardItem>();
        }
    }
}
