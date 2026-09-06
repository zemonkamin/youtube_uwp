using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class Searching : Page
    {
        private const string SEARCH_HISTORY_SETTING = "SearchHistory";
        private const int DesktopMaxSearchHistoryItems = 200;
        private const int MobileMaxSearchHistoryItems = 30;
        private CancellationTokenSource _suggestionsCancellationTokenSource;
        private List<SearchHistoryItem> _searchHistory;

        public class SearchHistoryItem
        {
            public string SearchText { get; set; }
            public DateTime Timestamp { get; set; }
        }

        public Searching()
        {
            this.InitializeComponent();
            this.Loaded += Searching_Loaded;
            this.Unloaded += Searching_Unloaded;
            _searchHistory = LoadSearchHistory();
            var savedQuery = SearchQueryStateController.CurrentQuery;
            if (!string.IsNullOrEmpty(savedQuery))
                SearchInput.Text = savedQuery;
            UpdateSearchHistoryVisibility();
        }

        private void Searching_Loaded(object sender, RoutedEventArgs e)
        {
            WarmSearchAuthenticationAsync();
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            FluentGlassEffectHelper.EnabledChanged += GlassEffect_EnabledChanged;

            if (SearchNavbarGlassHost != null)
            {
                SearchNavbarGlassHost.SizeChanged -= SearchNavbarGlassHost_SizeChanged;
                SearchNavbarGlassHost.SizeChanged += SearchNavbarGlassHost_SizeChanged;
                ApplySearchNavbarGlass();
            }
        }

        private async void WarmSearchAuthenticationAsync()
        {
            try
            {
                Config.LoadUserToken();
                if (!string.IsNullOrWhiteSpace(Config.UserToken))
                    await Config.RefreshAccessTokenAsync(Config.UserToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Search] Authentication warmup failed: " + ex.Message);
            }
        }

        private void Searching_Unloaded(object sender, RoutedEventArgs e)
        {
            CancelSuggestionRequest();
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            if (SearchNavbarGlassHost != null)
                SearchNavbarGlassHost.SizeChanged -= SearchNavbarGlassHost_SizeChanged;

            FluentGlassEffectHelper.Detach(SearchNavbarGlassHost);
        }

        private void SearchNavbarGlassHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplySearchNavbarGlass();
        }

        private void GlassEffect_EnabledChanged(object sender, EventArgs e)
        {
            ApplySearchNavbarGlass();
        }

        private void ApplySearchNavbarGlass()
        {
            if (ResponsiveLayout.IsPhoneDevice)
            {
                FluentGlassEffectHelper.Detach(SearchNavbarGlassHost);
                return;
            }

            if (SearchNavbarGlassHost == null ||
                SearchNavbarGlassHost.ActualWidth <= 1.0 ||
                SearchNavbarGlassHost.ActualHeight <= 1.0)
                return;

            FluentGlassEffectHelper.AttachSearchBar(
                SearchNavbarGlassHost,
                App.GetThemeBrush("AppBackgroundBrush"));
        }

        private List<SearchHistoryItem> LoadSearchHistory()
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            if (localSettings.Values.ContainsKey(SEARCH_HISTORY_SETTING))
            {
                try
                {
                    string json = localSettings.Values[SEARCH_HISTORY_SETTING].ToString();
                    var jsonArray = JsonArray.Parse(json);
                    var history = new List<SearchHistoryItem>();
                    
                    foreach (var item in jsonArray)
                    {
                        if (item.ValueType == JsonValueType.Object)
                        {
                            var obj = item.GetObject();
                            var historyItem = new SearchHistoryItem();
                            
                            if (obj.ContainsKey("SearchText"))
                            {
                                historyItem.SearchText = obj.GetNamedString("SearchText", string.Empty);
                            }
                            
                            if (obj.ContainsKey("Timestamp"))
                            {
                                long ticks;
                                if (long.TryParse(obj.GetNamedString("Timestamp", "0"), out ticks))
                                {
                                    historyItem.Timestamp = new DateTime(ticks);
                                }
                            }
                            
                            history.Add(historyItem);
                        }
                    }
                    
                    var maxItems = ResponsiveLayout.IsPhoneDevice
                        ? MobileMaxSearchHistoryItems
                        : DesktopMaxSearchHistoryItems;
                    if (history.Count > maxItems)
                    {
                        history = history.Take(maxItems).ToList();
                    }

                    return history;
                }
                catch
                {
                    return new List<SearchHistoryItem>();
                }
            }
            return new List<SearchHistoryItem>();
        }

        private void SaveSearchHistory()
        {
            SaveSearchHistory(new List<SearchHistoryItem>(_searchHistory));
        }

        private static void SaveSearchHistory(IList<SearchHistoryItem> history)
        {
            var localSettings = ApplicationData.Current.LocalSettings;
            var jsonArray = new JsonArray();

            foreach (var item in history)
            {
                var obj = new JsonObject();
                obj["SearchText"] = JsonValue.CreateStringValue(item.SearchText);
                obj["Timestamp"] = JsonValue.CreateStringValue(item.Timestamp.Ticks.ToString());
                jsonArray.Add(obj);
            }
            
            localSettings.Values[SEARCH_HISTORY_SETTING] = jsonArray.Stringify();
        }

        private void SaveSearchHistoryDeferred()
        {
            var snapshot = new List<SearchHistoryItem>(_searchHistory);
            Task.Run(delegate
            {
                try
                {
                    SaveSearchHistory(snapshot);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Search] Deferred history save failed: " + ex.Message);
                }
            });
        }

        private void AddToSearchHistory(string searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText)) return;

            _searchHistory.RemoveAll(x => x.SearchText.Equals(searchText, StringComparison.OrdinalIgnoreCase));
            _searchHistory.Insert(0, new SearchHistoryItem
            {
                SearchText = searchText,
                Timestamp = DateTime.Now
            });

            var maxItems = ResponsiveLayout.IsPhoneDevice
                ? MobileMaxSearchHistoryItems
                : DesktopMaxSearchHistoryItems;
            if (_searchHistory.Count > maxItems)
            {
                _searchHistory = _searchHistory.Take(maxItems).ToList();
            }
        }

        private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var historyItem = button.DataContext as SearchHistoryItem;
                if (historyItem != null)
                {
                    _searchHistory.Remove(historyItem);
                    SaveSearchHistory();
                    UpdateSearchHistoryVisibility();
                }
            }
        }

        private void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var historyItem = button.DataContext as SearchHistoryItem;
                if (historyItem != null)
                {
                    SearchInput.Text = historyItem.SearchText;
                    PerformSearch();
                }
            }
        }

        private void RefreshSearchHistoryItems()
        {
            if (SearchHistoryListView == null)
            {
                return;
            }

            // List<T> does not notify ListView when an item is removed. Resetting the
            // source keeps the existing persistence format while updating the UI now.
            SearchHistoryListView.ItemsSource = null;
            SearchHistoryListView.ItemsSource = _searchHistory;
        }

        private void UpdateSearchHistoryVisibility()
        {
            if (string.IsNullOrWhiteSpace(SearchInput.Text))
            {
                if (_searchHistory != null && _searchHistory.Any())
                {
                    RefreshSearchHistoryItems();
                    SearchHistoryListView.Visibility = Visibility.Visible;
                }
                else
                {
                    SearchHistoryListView.ItemsSource = null;
                    SearchHistoryListView.Visibility = Visibility.Collapsed;
                }
                SuggestionsListView.Visibility = Visibility.Collapsed;
            }
            else
            {
                SearchHistoryListView.Visibility = Visibility.Collapsed;
            }
        }

        private void SuggestionsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            var query = e.ClickedItem as string;
            if (query != null)
            {
                SearchInput.Text = query;
                PerformSearch();
                return;
            }

            var historyItem = e.ClickedItem as SearchHistoryItem;
            if (historyItem != null)
            {
                SearchInput.Text = historyItem.SearchText;
                PerformSearch();
            }
        }


        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            var query = e.Parameter as string;
            if (string.IsNullOrWhiteSpace(query))
                query = SearchQueryStateController.CurrentQuery;
            if (SearchInput != null
                && !string.Equals(SearchInput.Text, query, StringComparison.Ordinal))
            {
                SearchInput.Text = query ?? string.Empty;
            }
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            UpdateBackButtonVisibility();
            UpdateSearchHistoryVisibility();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            CancelSuggestionRequest();
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

        // async void (not a dropped Task): every keystroke runs this, and an exception escaping
        // an unobserved Task here was killing the app.
        private async void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var searchText = SearchInput != null ? SearchInput.Text : null;
                SearchQueryStateController.SetCurrentQuery(searchText);
                if (ClearButton != null)
                {
                    ClearButton.Visibility = string.IsNullOrEmpty(searchText)
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                }

                if (string.IsNullOrWhiteSpace(searchText))
                {
                    UpdateSearchHistoryVisibility();
                    return;
                }

                // Retire the previous debounce, disposing it — one CTS per keystroke used to leak.
                var previous = _suggestionsCancellationTokenSource;
                _suggestionsCancellationTokenSource = new CancellationTokenSource();
                var token = _suggestionsCancellationTokenSource.Token;

                if (previous != null)
                {
                    try { previous.Cancel(); } catch { }
                    try { previous.Dispose(); } catch { }
                }

                await GetSearchSuggestions(searchText, token);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Search] TextChanged failed: " + ex.Message);
            }
        }

        private async Task GetSearchSuggestions(string query, CancellationToken cancellationToken)
        {
            try
            {
                // Небольшая задержка для предотвращения слишком частых запросов
                await Task.Delay(140, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                var suggestionsList = await FetchSearchSuggestionsAsync(query, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        if (cancellationToken.IsCancellationRequested) return;
                        if (SuggestionsListView == null) return;

                        if (suggestionsList != null && suggestionsList.Count > 0)
                        {
                            SuggestionsListView.ItemsSource = suggestionsList;
                            SuggestionsListView.Visibility = Visibility.Visible;
                            if (SearchHistoryListView != null)
                            {
                                SearchHistoryListView.Visibility = Visibility.Collapsed;
                            }
                        }
                        else
                        {
                            SuggestionsListView.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch (Exception uiEx)
                    {
                        System.Diagnostics.Debug.WriteLine("[Search] Suggestion UI update failed: " + uiEx.Message);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // Debounce cancelled by the next keystroke — expected. TaskCanceledException
                // derives from this, so both are covered.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetSearchSuggestions: {ex}");
                try
                {
                    await Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                    {
                        if (SuggestionsListView != null)
                        {
                            SuggestionsListView.Visibility = Visibility.Collapsed;
                        }
                    });
                }
                catch { }
            }
        }

        /// <summary>
        /// Fetches search suggestions from YouTube's autocomplete API
        /// This is the same endpoint used by yt-api-legacy-main
        /// </summary>
        private async Task<List<string>> FetchSearchSuggestionsAsync(
            string query,
            CancellationToken cancellationToken)
        {
            try
            {
                return await SearchSuggestionsClient.GetAsync(
                    query,
                    Config.Hl,
                    Config.Gl,
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching search suggestions: {ex}");
                return new List<string>();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            CancelSuggestionRequest();

            SearchInput.Text = string.Empty;
            SearchInput.Focus(FocusState.Programmatic);
            UpdateSearchHistoryVisibility();
        }

        private void SearchInput_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                PerformSearch();
            }
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            PerformSearch();
        }

        private void PerformSearch()
        {
            var searchText = SearchInput.Text;
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                searchText = searchText.Trim();
                CancelSuggestionRequest();
                SearchQueryStateController.SetCurrentQuery(searchText);
                SuggestionsListView.Visibility = Visibility.Collapsed;
                AddToSearchHistory(searchText);

                // Navigation starts the result request synchronously up to its first await.
                // Persist history afterwards so LocalSettings JSON never delays the request.
                Frame.Navigate(typeof(Search), searchText);
                SaveSearchHistoryDeferred();
            }
        }

        private void CancelSuggestionRequest()
        {
            var source = _suggestionsCancellationTokenSource;
            _suggestionsCancellationTokenSource = null;
            if (source == null)
                return;

            try { source.Cancel(); } catch { }
            try { source.Dispose(); } catch { }
        }

        private void SuggestionItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var selectedSuggestion = button.DataContext as string;
                if (!string.IsNullOrEmpty(selectedSuggestion))
                {
                    SearchInput.Text = selectedSuggestion;
                    PerformSearch();
                }
            }
        }
    }
}
