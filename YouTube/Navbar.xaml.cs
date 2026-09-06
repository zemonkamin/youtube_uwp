using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class Navbar : Page
    {
        private const string SearchHistorySetting = "SearchHistory";
        private const int MaxSearchHistoryItems = 200;
        private const double MaximumCenteredSearchWidth = 700.0;
        private const double MinimumCenteredSearchWidth = 320.0;

        private CancellationTokenSource _suggestionsCancellationTokenSource;
        private List<SearchHistoryItem> _searchHistory = new List<SearchHistoryItem>();
        private bool _horizontalLayoutActive;
        private bool _searchInputSessionActive;
        private bool _searchTextStateReady;

        public sealed class SearchHistoryItem
        {
            public string SearchText { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public Navbar()
        {
            InitializeComponent();
            Loaded += Navbar_Loaded;
            Unloaded += Navbar_Unloaded;
            LandscapeNavbarSearchModeController.EnsureDefault();
            _searchHistory = LoadSearchHistory();
            _searchTextStateReady = true;
            RestoreSearchText();
            if (SearchInputHorizontal != null)
            {
                SearchInputHorizontal.AddHandler(
                    UIElement.PointerPressedEvent,
                    new PointerEventHandler(SearchInputHorizontal_PointerPressed),
                    true);
            }
            UpdateAccountButtonsVisibility();
            UpdateLayoutForCurrentWindow();
        }

        public void SetHorizontalLayout()
        {
            _horizontalLayoutActive = true;
            if (VerticalLayout != null) VerticalLayout.Visibility = Visibility.Collapsed;
            if (HorizontalLayout != null) HorizontalLayout.Visibility = Visibility.Visible;
            ApplyHorizontalSearchMode();
            UpdatePageSpecificButtonsVisibility();
            UpdateCenteredSearchWidth();
        }

        public void SetVerticalLayout()
        {
            _horizontalLayoutActive = false;
            _searchInputSessionActive = false;
            CloseSearchPopup();
            if (VerticalLayout != null) VerticalLayout.Visibility = Visibility.Visible;
            if (HorizontalLayout != null) HorizontalLayout.Visibility = Visibility.Collapsed;
            UpdatePageSpecificButtonsVisibility();
        }

        private void Navbar_Loaded(object sender, RoutedEventArgs e)
        {
            LandscapeNavbarSearchModeController.EnabledChanged -= LandscapeNavbarSearchMode_EnabledChanged;
            LandscapeNavbarSearchModeController.EnabledChanged += LandscapeNavbarSearchMode_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.SizeChanged += Window_SizeChanged;
            Window.Current.CoreWindow.PointerPressed -= CoreWindow_PointerPressed;
            Window.Current.CoreWindow.PointerPressed += CoreWindow_PointerPressed;

            UpdateAccountButtonsVisibility();
            RestoreSearchText();
            UpdateLayoutForCurrentWindow();
            UpdatePageSpecificButtonsVisibility();
            UpdateCenteredSearchWidth();

            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            FluentGlassEffectHelper.EnabledChanged += GlassEffect_EnabledChanged;
            if (NavbarGlassHost != null)
            {
                NavbarGlassHost.SizeChanged -= NavbarGlassHost_SizeChanged;
                NavbarGlassHost.SizeChanged += NavbarGlassHost_SizeChanged;
                ApplyGlassEffect();
            }
        }

        private void Navbar_Unloaded(object sender, RoutedEventArgs e)
        {
            LandscapeNavbarSearchModeController.EnabledChanged -= LandscapeNavbarSearchMode_EnabledChanged;
            Window.Current.SizeChanged -= Window_SizeChanged;
            Window.Current.CoreWindow.PointerPressed -= CoreWindow_PointerPressed;
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            if (NavbarGlassHost != null)
                NavbarGlassHost.SizeChanged -= NavbarGlassHost_SizeChanged;

            CancelSuggestionRequest();
            _searchInputSessionActive = false;
            CloseSearchPopup();
            FluentGlassEffectHelper.Detach(NavbarGlassHost);
        }

        private void LandscapeNavbarSearchMode_EnabledChanged(object sender, EventArgs e)
        {
            ApplyHorizontalSearchMode();
        }

        private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            UpdateLayoutForCurrentWindow();
            UpdateCenteredSearchWidth();
            if (SearchSuggestionsPopup != null && SearchSuggestionsPopup.IsOpen)
            {
                PositionSearchPopup();
            }
        }

        private void UpdateLayoutForCurrentWindow()
        {
            if (Window.Current == null)
                return;

            var bounds = Window.Current.Bounds;
            if (bounds.Width >= bounds.Height)
                SetHorizontalLayout();
            else
                SetVerticalLayout();

            UpdateCompactOrientationLayout();
        }

        private void UpdateCompactOrientationLayout()
        {
            var compact = ResponsiveLayout.IsCompactLandscape;
            if (HorizontalYouTubeLogo != null)
                HorizontalYouTubeLogo.Visibility = compact
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            if (VoiceButtonHost != null)
                VoiceButtonHost.Visibility = compact
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            if (VoiceColumn != null)
                VoiceColumn.Width = new GridLength(compact ? 0.0 : 48.0);
        }

        private void ApplyHorizontalSearchMode()
        {
            var centeredSearchEnabled = LandscapeNavbarSearchModeController.IsEnabled();
            if (CenteredSearchHost != null)
            {
                CenteredSearchHost.Visibility = centeredSearchEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (LegacySearchButtonHorizontal != null)
            {
                LegacySearchButtonHorizontal.Visibility = centeredSearchEnabled
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            if (!centeredSearchEnabled || !_horizontalLayoutActive)
            {
                _searchInputSessionActive = false;
                CloseSearchPopup();
            }
        }

        private void UpdateCenteredSearchWidth()
        {
            if (CenteredSearchHost == null || Window.Current == null)
                return;

            // Reserve enough room for the logo on the left and notification controls on the
            // right, while keeping the search field centered relative to the whole window.
            var compact = ResponsiveLayout.IsCompactLandscape;
            var available = Window.Current.Bounds.Width - (compact ? 120.0 : 360.0);
            var minimum = compact ? 240.0 : MinimumCenteredSearchWidth;
            CenteredSearchHost.Width = Math.Max(
                minimum,
                Math.Min(MaximumCenteredSearchWidth, available));
        }

        private void NavbarGlassHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyGlassEffect();
        }

        private void GlassEffect_EnabledChanged(object sender, EventArgs e)
        {
            ApplyGlassEffect();
        }

        private void ApplyGlassEffect()
        {
            if (NavbarGlassHost == null
                || NavbarGlassHost.ActualWidth <= 1.0
                || NavbarGlassHost.ActualHeight <= 1.0)
                return;

            FluentGlassEffectHelper.AttachTopBar(
                NavbarGlassHost,
                App.GetThemeBrush("AppBackgroundBrush"));
        }

        private void UpdateAccountButtonsVisibility()
        {
            Config.LoadUserToken();
            var visibility = !string.IsNullOrWhiteSpace(Config.UserToken)
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (NotificationsButtonVertical != null)
                NotificationsButtonVertical.Visibility = visibility;
            if (NotificationsButtonHorizontal != null)
                NotificationsButtonHorizontal.Visibility = visibility;
        }

        private void UpdatePageSpecificButtonsVisibility()
        {
            if (SearchFiltersButtonHorizontal == null)
                return;

            var frame = Window.Current == null ? null : Window.Current.Content as Frame;
            SearchFiltersButtonHorizontal.Visibility = _horizontalLayoutActive
                && frame != null
                && frame.Content is Search
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SearchFiltersButtonHorizontal_Click(object sender, RoutedEventArgs e)
        {
            CloseSearchPopup();
            var frame = Window.Current == null ? null : Window.Current.Content as Frame;
            var searchPage = frame == null ? null : frame.Content as Search;
            if (searchPage != null)
                searchPage.OpenSearchFilters();
        }

        private void RestoreSearchText()
        {
            if (SearchInputHorizontal == null)
                return;

            var query = SearchQueryStateController.CurrentQuery;
            if (!string.Equals(SearchInputHorizontal.Text, query, StringComparison.Ordinal))
                SearchInputHorizontal.Text = query;
        }

        public void SetSearchText(string query)
        {
            SearchQueryStateController.SetCurrentQuery(query);
            RestoreSearchText();
        }

        private void NotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            CloseSearchPopup();
            var frame = Window.Current.Content as Frame;
            if (frame != null)
                frame.Navigate(typeof(Notifications));
        }

        private void YouTubeLogo_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseSearchPopup();
            var frame = Window.Current.Content as Frame;
            if (frame != null && !(frame.Content is Home))
                frame.Navigate(typeof(Home));
            e.Handled = true;
        }

        private void ShowSearchButton_Click(object sender, RoutedEventArgs e)
        {
            CloseSearchPopup();
            var frame = Window.Current.Content as Frame;
            if (frame != null)
                frame.Navigate(typeof(Searching));
        }

        private void SearchInputHorizontal_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                PerformSearch(SearchInputHorizontal.Text);
            }
            else if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseSearchPopup();
            }
        }

        private async void SearchInputHorizontal_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var searchText = SearchInputHorizontal == null
                    ? string.Empty
                    : SearchInputHorizontal.Text;
                if (_searchTextStateReady)
                    SearchQueryStateController.SetCurrentQuery(searchText);
                if (ClearSearchButtonHorizontal != null)
                {
                    ClearSearchButtonHorizontal.Visibility = string.IsNullOrEmpty(searchText)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    CancelSuggestionRequest();
                    if (IsSearchInputActive())
                        ShowSearchHistory();
                    else
                        CloseSearchPopup();
                    return;
                }

                if (!IsSearchInputActive())
                {
                    CancelSuggestionRequest();
                    CloseSearchPopup();
                    return;
                }

                var previous = _suggestionsCancellationTokenSource;
                _suggestionsCancellationTokenSource = new CancellationTokenSource();
                if (previous != null)
                {
                    try { previous.Cancel(); } catch { }
                    try { previous.Dispose(); } catch { }
                }

                await LoadSuggestionsAsync(
                    searchText,
                    _suggestionsCancellationTokenSource.Token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[NavbarSearch] TextChanged failed: " + ex.Message);
            }
        }

        private void SearchInputHorizontal_GotFocus(object sender, RoutedEventArgs e)
        {
            // UWP may focus the first visible TextBox while a page is loading or restoring
            // navigation state. Only a real pointer press on this field starts a search session.
            if (SearchInputHorizontal == null)
                return;

            if (!_searchInputSessionActive)
            {
                DismissUnexpectedSearchFocus();
                return;
            }

            ShowSearchSurfaceForCurrentText();
        }

        private async void SearchInputHorizontal_PointerPressed(
            object sender,
            PointerRoutedEventArgs e)
        {
            _searchInputSessionActive = true;
            SearchInputHorizontal.Focus(FocusState.Pointer);

            // When the TextBox was already focused by the platform, GotFocus will not fire
            // again. Run after pointer focus processing so the same deliberate click still
            // activates history or suggestions.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (_searchInputSessionActive)
                    ShowSearchSurfaceForCurrentText();
            });
        }

        private void ShowSearchSurfaceForCurrentText()
        {
            if (SearchInputHorizontal == null || !_searchInputSessionActive)
                return;

            if (string.IsNullOrWhiteSpace(SearchInputHorizontal.Text))
                ShowSearchHistory();
            else if (SuggestionsList != null && SuggestionsList.Items.Count > 0)
                OpenSearchPopup();
        }

        private async void SearchInputHorizontal_LostFocus(object sender, RoutedEventArgs e)
        {
            // Let a click inside the dropdown complete before deciding whether focus really
            // left the whole search surface. Unlike Popup light-dismiss, this never consumes
            // a click intended for the TextBox.
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                var focusedElement = FocusManager.GetFocusedElement() as DependencyObject;
                if (IsElementInside(focusedElement, CenteredSearchBar)
                    || IsElementInside(focusedElement, SearchSuggestionsPopupBorder))
                    return;

                _searchInputSessionActive = false;
                CloseSearchPopup();
            });
        }

        private async void DismissUnexpectedSearchFocus()
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (_searchInputSessionActive
                    || SearchInputHorizontal == null
                    || SearchInputHorizontal.FocusState == FocusState.Unfocused)
                    return;

                if (SearchFocusSink != null)
                    SearchFocusSink.Focus(FocusState.Programmatic);
            });
        }

        private void CoreWindow_PointerPressed(
            CoreWindow sender,
            Windows.UI.Core.PointerEventArgs e)
        {
            if (!_searchInputSessionActive || e == null)
                return;

            var point = e.CurrentPoint.Position;
            if (IsPointInsideElement(CenteredSearchBar, point)
                || (SearchSuggestionsPopup != null
                    && SearchSuggestionsPopup.IsOpen
                    && IsPointInsideElement(SearchSuggestionsPopupBorder, point)))
                return;

            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                _searchInputSessionActive = false;
                CancelSuggestionRequest();
                CloseSearchPopup();

                // A clicked control owns its new focus. Use the sink only when the click was
                // on empty/non-focusable content and the caret otherwise remained in TextBox.
                if (SearchInputHorizontal != null
                    && SearchInputHorizontal.FocusState != FocusState.Unfocused
                    && SearchFocusSink != null)
                {
                    SearchFocusSink.Focus(FocusState.Programmatic);
                }
            });
        }

        private static bool IsPointInsideElement(FrameworkElement element, Point point)
        {
            if (element == null
                || element.Visibility != Visibility.Visible
                || element.ActualWidth <= 0
                || element.ActualHeight <= 0
                || Window.Current == null)
                return false;

            try
            {
                var root = Window.Current.Content as UIElement;
                if (root == null)
                    return false;

                var origin = element.TransformToVisual(root).TransformPoint(new Point(0, 0));
                return new Rect(origin.X, origin.Y, element.ActualWidth, element.ActualHeight)
                    .Contains(point);
            }
            catch
            {
                return false;
            }
        }

        private bool IsSearchInputActive()
        {
            return SearchInputHorizontal != null && _searchInputSessionActive;
        }

        private static bool IsElementInside(DependencyObject element, DependencyObject ancestor)
        {
            if (element == null || ancestor == null)
                return false;

            var current = element;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;

                current = VisualTreeHelper.GetParent(current);
            }

            return false;
        }

        private void SearchButtonHorizontal_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch(SearchInputHorizontal == null ? null : SearchInputHorizontal.Text);
        }

        private void ClearSearchButtonHorizontal_Click(object sender, RoutedEventArgs e)
        {
            CancelSuggestionRequest();
            if (SearchInputHorizontal == null)
                return;

            SearchInputHorizontal.Text = string.Empty;
            SearchInputHorizontal.Focus(FocusState.Programmatic);
            ShowSearchHistory();
        }

        private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var item = e.ClickedItem as SearchHistoryItem;
            if (item == null || string.IsNullOrWhiteSpace(item.SearchText))
                return;

            SearchInputHorizontal.Text = item.SearchText;
            PerformSearch(item.SearchText);
        }

        private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.DataContext as SearchHistoryItem;
            if (item == null)
                return;

            _searchHistory.RemoveAll(entry => string.Equals(
                entry.SearchText,
                item.SearchText,
                StringComparison.OrdinalIgnoreCase));
            SaveSearchHistory();
            ShowSearchHistory();
        }

        private void SuggestionsList_ItemClick(object sender, ItemClickEventArgs e)
        {
            var query = e.ClickedItem as string;
            if (string.IsNullOrWhiteSpace(query))
                return;

            SearchInputHorizontal.Text = query;
            PerformSearch(query);
        }

        private async Task LoadSuggestionsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(140, cancellationToken);
                var suggestions = await SearchSuggestionsClient.GetAsync(
                    query,
                    Config.Hl,
                    Config.Gl,
                    cancellationToken);

                if (cancellationToken.IsCancellationRequested)
                    return;

                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (cancellationToken.IsCancellationRequested || SuggestionsList == null)
                        return;

                    SuggestionsList.ItemsSource = suggestions;
                    SuggestionsList.Visibility = suggestions != null && suggestions.Count > 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    if (HistoryList != null)
                        HistoryList.Visibility = Visibility.Collapsed;

                    if (suggestions != null && suggestions.Count > 0)
                        OpenSearchPopup();
                    else
                        CloseSearchPopup();
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[NavbarSearch] Suggestions failed: " + ex.Message);
                if (SuggestionsList != null)
                    SuggestionsList.Visibility = Visibility.Collapsed;
            }
        }

        private void PerformSearch(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
                return;

            var normalized = searchText.Trim();
            SearchQueryStateController.SetCurrentQuery(normalized);
            AddToSearchHistory(normalized);
            CancelSuggestionRequest();
            CloseSearchPopup();

            var frame = Window.Current.Content as Frame;
            if (frame != null)
                frame.Navigate(typeof(Search), normalized);
        }

        private void ShowSearchHistory()
        {
            if (!_horizontalLayoutActive
                || !LandscapeNavbarSearchModeController.IsEnabled()
                || !IsSearchInputActive())
                return;

            _searchHistory = LoadSearchHistory();
            if (HistoryList != null)
            {
                HistoryList.ItemsSource = null;
                HistoryList.ItemsSource = _searchHistory;
                HistoryList.Visibility = _searchHistory.Count > 0
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (SuggestionsList != null)
                SuggestionsList.Visibility = Visibility.Collapsed;

            if (_searchHistory.Count > 0)
                OpenSearchPopup();
            else
                CloseSearchPopup();
        }

        private void OpenSearchPopup()
        {
            if (SearchSuggestionsPopup == null
                || !_horizontalLayoutActive
                || !LandscapeNavbarSearchModeController.IsEnabled()
                || !IsSearchInputActive())
                return;

            PositionSearchPopup();
            SearchSuggestionsPopup.IsOpen = true;
        }

        private void PositionSearchPopup()
        {
            if (CenteredSearchBar == null
                || SearchSuggestionsPopup == null
                || SearchSuggestionsPopupBorder == null
                || Window.Current == null)
                return;

            try
            {
                var windowRoot = Window.Current.Content as UIElement;
                if (windowRoot == null)
                    return;

                var point = CenteredSearchBar.TransformToVisual(windowRoot).TransformPoint(
                    new Point(0, CenteredSearchBar.ActualHeight + 4));
                var width = Math.Max(1.0, CenteredSearchBar.ActualWidth);
                SearchSuggestionsPopup.HorizontalOffset = point.X;
                SearchSuggestionsPopup.VerticalOffset = point.Y;
                SearchSuggestionsPopupBorder.Width = width;
                SearchSuggestionsPopupBorder.MaxHeight = Math.Max(
                    120.0,
                    Math.Min(560.0, Window.Current.Bounds.Height - point.Y - 12.0));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[NavbarSearch] Popup positioning failed: " + ex.Message);
            }
        }

        private void CloseSearchPopup()
        {
            if (SearchSuggestionsPopup != null)
                SearchSuggestionsPopup.IsOpen = false;
        }

        private void CancelSuggestionRequest()
        {
            var cancellation = _suggestionsCancellationTokenSource;
            _suggestionsCancellationTokenSource = null;
            if (cancellation == null)
                return;

            try { cancellation.Cancel(); } catch { }
            try { cancellation.Dispose(); } catch { }
        }

        private static List<SearchHistoryItem> LoadSearchHistory()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    SearchHistorySetting,
                    out raw)
                    || raw == null)
                    return new List<SearchHistoryItem>();

                var jsonArray = JsonArray.Parse(raw.ToString());
                var history = new List<SearchHistoryItem>();
                foreach (var value in jsonArray)
                {
                    if (value.ValueType != JsonValueType.Object)
                        continue;

                    var item = value.GetObject();
                    var searchText = item.GetNamedString("SearchText", string.Empty);
                    if (string.IsNullOrWhiteSpace(searchText))
                        continue;

                    long ticks;
                    var timestamp = DateTime.MinValue;
                    if (long.TryParse(item.GetNamedString("Timestamp", "0"), out ticks)
                        && ticks > 0)
                    {
                        try { timestamp = new DateTime(ticks); } catch { }
                    }

                    history.Add(new SearchHistoryItem
                    {
                        SearchText = searchText,
                        Timestamp = timestamp
                    });
                }

                return history.Take(MaxSearchHistoryItems).ToList();
            }
            catch
            {
                return new List<SearchHistoryItem>();
            }
        }

        private void AddToSearchHistory(string searchText)
        {
            _searchHistory = LoadSearchHistory();
            _searchHistory.RemoveAll(item => string.Equals(
                item.SearchText,
                searchText,
                StringComparison.OrdinalIgnoreCase));
            _searchHistory.Insert(0, new SearchHistoryItem
            {
                SearchText = searchText,
                Timestamp = DateTime.Now
            });
            if (_searchHistory.Count > MaxSearchHistoryItems)
                _searchHistory = _searchHistory.Take(MaxSearchHistoryItems).ToList();
            SaveSearchHistory();
        }

        private void SaveSearchHistory()
        {
            try
            {
                var jsonArray = new JsonArray();
                foreach (var item in _searchHistory)
                {
                    var value = new JsonObject();
                    value["SearchText"] = JsonValue.CreateStringValue(item.SearchText ?? string.Empty);
                    value["Timestamp"] = JsonValue.CreateStringValue(item.Timestamp.Ticks.ToString());
                    jsonArray.Add(value);
                }
                ApplicationData.Current.LocalSettings.Values[SearchHistorySetting] = jsonArray.Stringify();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[NavbarSearch] History save failed: " + ex.Message);
            }
        }
    }
}
