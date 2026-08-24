using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Searching : Page
    {
        private const string SEARCH_HISTORY_SETTING = "SearchHistory";
        private const int MAX_SEARCH_HISTORY_ITEMS = 200;
        private CancellationTokenSource _suggestionsCancellationTokenSource;
        private List<SearchHistoryItem> _searchHistory;

        // ONE client for the whole app. The suggestions request used to build and dispose an
        // HttpClient on every keystroke; each one holds its own handler and sockets, and on a
        // memory-tight phone typing a few characters was enough to take the app down.
        private static readonly HttpClient SuggestionsHttpClient = CreateSuggestionsClient();

        private static HttpClient CreateSuggestionsClient()
        {
            var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
            return client;
        }

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
            UpdateSearchHistoryVisibility();
        }

        private void Searching_Loaded(object sender, RoutedEventArgs e)
        {
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            FluentGlassEffectHelper.EnabledChanged += GlassEffect_EnabledChanged;

            if (SearchNavbarGlassHost != null)
            {
                SearchNavbarGlassHost.SizeChanged -= SearchNavbarGlassHost_SizeChanged;
                SearchNavbarGlassHost.SizeChanged += SearchNavbarGlassHost_SizeChanged;
                ApplySearchNavbarGlass();
            }
        }

        private void Searching_Unloaded(object sender, RoutedEventArgs e)
        {
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
                    
                    if (history.Count > MAX_SEARCH_HISTORY_ITEMS)
                    {
                        history = history.Take(MAX_SEARCH_HISTORY_ITEMS).ToList();
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
            var localSettings = ApplicationData.Current.LocalSettings;
            var jsonArray = new JsonArray();
            
            foreach (var item in _searchHistory)
            {
                var obj = new JsonObject();
                obj["SearchText"] = JsonValue.CreateStringValue(item.SearchText);
                obj["Timestamp"] = JsonValue.CreateStringValue(item.Timestamp.Ticks.ToString());
                jsonArray.Add(obj);
            }
            
            localSettings.Values[SEARCH_HISTORY_SETTING] = jsonArray.Stringify();
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

            if (_searchHistory.Count > MAX_SEARCH_HISTORY_ITEMS)
            {
                _searchHistory = _searchHistory.Take(MAX_SEARCH_HISTORY_ITEMS).ToList();
            }

            SaveSearchHistory();
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
            SystemNavigationManager.GetForCurrentView().BackRequested -= OnBackRequested;
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;
            UpdateBackButtonVisibility();
            UpdateSearchHistoryVisibility();
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

        // async void (not a dropped Task): every keystroke runs this, and an exception escaping
        // an unobserved Task here was killing the app.
        private async void SearchInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            try
            {
                var searchText = SearchInput != null ? SearchInput.Text : null;
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
                await Task.Delay(300, cancellationToken);

                if (cancellationToken.IsCancellationRequested) return;

                var suggestionsList = await FetchSearchSuggestionsAsync(query);

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
        private async Task<List<string>> FetchSearchSuggestionsAsync(string query)
        {
            try
            {
                {
                    var httpClient = SuggestionsHttpClient;

                    var encodedQuery = Uri.EscapeDataString(query);
                    // hl follows the device language, so suggestions match what the user types.
                    var url = $"https://clients1.google.com/complete/search?client=youtube&hl={Config.Hl}&gl={Config.Gl}&ds=yt&q={encodedQuery}";

                    var response = await httpClient.GetStringAsync(url);

                    // The response is JSONP: window.google.ac.h([...]) or )]}'[...]
                    var jsonData = response;
                    
                    // Remove JSONP wrapper if present
                    if (jsonData.StartsWith("window.google.ac.h("))
                    {
                        jsonData = jsonData.Substring("window.google.ac.h(".Length);
                        if (jsonData.EndsWith(")"))
                        {
                            jsonData = jsonData.Substring(0, jsonData.Length - 1);
                        }
                    }
                    
                    // Remove security prefix if present
                    if (jsonData.StartsWith(")]}'"))
                    {
                        jsonData = jsonData.Substring(4);
                    }

                    // Parse JSON
                    var jsonArray = JsonArray.Parse(jsonData);
                    
                    // The suggestions are in the second element (index 1)
                    if (jsonArray.Count > 1)
                    {
                        var suggestionsArray = jsonArray[1].GetArray();
                        var suggestions = new List<string>();
                        
                        // Take up to 10 suggestions
                        int count = Math.Min(10, (int)suggestionsArray.Count);
                        for (int i = 0; i < count; i++)
                        {
                            var suggestionItem = suggestionsArray[i].GetArray();
                            if (suggestionItem.Count > 0)
                            {
                                var suggestionText = suggestionItem[0].GetString();
                                suggestions.Add(suggestionText);
                            }
                        }
                        
                        return suggestions;
                    }
                    
                    return new List<string>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching search suggestions: {ex}");
                return new List<string>();
            }
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (_suggestionsCancellationTokenSource != null)
            {
                try { _suggestionsCancellationTokenSource.Cancel(); } catch { }
            }

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
                SuggestionsListView.Visibility = Visibility.Collapsed;
                AddToSearchHistory(searchText);

                // Navigate to Search page with the query
                Frame.Navigate(typeof(Search), searchText);
            }
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
