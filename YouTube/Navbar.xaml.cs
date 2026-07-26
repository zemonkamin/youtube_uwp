using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace YouTube
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Navbar : Page
    {
        public Navbar()
        {
            this.InitializeComponent();
            this.Loaded += Navbar_Loaded;
            UpdateAccountButtonsVisibility();
        }

        public void SetHorizontalLayout()
        {
            if (VerticalLayout != null) VerticalLayout.Visibility = Visibility.Collapsed;
            if (HorizontalLayout != null) HorizontalLayout.Visibility = Visibility.Visible;
        }

        public void SetVerticalLayout()
        {
            if (VerticalLayout != null) VerticalLayout.Visibility = Visibility.Visible;
            if (HorizontalLayout != null) HorizontalLayout.Visibility = Visibility.Collapsed;
        }

        private void Navbar_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateAccountButtonsVisibility();
        }

        private void UpdateAccountButtonsVisibility()
        {
            Config.LoadUserToken();
            var isSignedIn = !string.IsNullOrWhiteSpace(Config.UserToken);
            var visibility = isSignedIn ? Visibility.Visible : Visibility.Collapsed;

            if (NotificationsButtonVertical != null)
                NotificationsButtonVertical.Visibility = visibility;

            if (NotificationsButtonHorizontal != null)
                NotificationsButtonHorizontal.Visibility = visibility;
        }

        private void NotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                frame.Navigate(typeof(Notifications));
            }
        }

        private void ShowSearchButton_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to Searching page
            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                frame.Navigate(typeof(Searching));
            }
        }

        private void SearchInputHorizontal_KeyUp(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                // Perform search when Enter is pressed
                PerformSearch(SearchInputHorizontal.Text);
            }
        }

        private void SearchInputHorizontal_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Handle text change - could show suggestions
            var searchText = SearchInputHorizontal.Text;
            
            if (string.IsNullOrWhiteSpace(searchText))
            {
                // Show search history when input is empty
                ShowSearchHistory();
            }
            else
            {
                // Show suggestions based on input
                ShowSuggestions(searchText);
            }
        }

        private void SearchInputHorizontal_GotFocus(object sender, RoutedEventArgs e)
        {
            // Show search history or suggestions when input gets focus
            var searchText = SearchInputHorizontal.Text;
            
            if (string.IsNullOrWhiteSpace(searchText))
            {
                ShowSearchHistory();
            }
            else
            {
                ShowSuggestions(searchText);
            }
        }

        private void SearchButtonHorizontal_Click(object sender, RoutedEventArgs e)
        {
            // Perform search
            PerformSearch(SearchInputHorizontal.Text);
        }

        private void HistoryItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var searchText = button.Content as string;
                SearchInputHorizontal.Text = searchText;
                PerformSearch(searchText);
            }
        }

        private void RemoveHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            // Remove item from search history
            var button = sender as Button;
            if (button != null)
            {
                // Get the parent grid to find the data context
                var grid = button.Parent as Grid;
                if (grid != null)
                {
                    // Remove from history (implementation depends on your data source)
                    System.Diagnostics.Debug.WriteLine("Remove history item");
                }
            }
        }

        private void SuggestionItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null)
            {
                var searchText = button.Content as string;
                SearchInputHorizontal.Text = searchText;
                PerformSearch(searchText);
            }
        }

        private void PerformSearch(string searchText)
        {
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                // Hide suggestions/history
                if (SuggestionsList != null) SuggestionsList.Visibility = Visibility.Collapsed;
                if (HistoryList != null) HistoryList.Visibility = Visibility.Collapsed;
                
                // Navigate to search results page
                // Frame.Navigate(typeof(SearchResults), searchText);
                
                System.Diagnostics.Debug.WriteLine("Searching for: " + searchText);
            }
        }

        private void ShowSearchHistory()
        {
            if (HistoryList != null)
            {
                HistoryList.Visibility = Visibility.Visible;
                if (SuggestionsList != null) SuggestionsList.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowSuggestions(string searchText)
        {
            if (SuggestionsList != null)
            {
                SuggestionsList.Visibility = Visibility.Visible;
                if (HistoryList != null) HistoryList.Visibility = Visibility.Collapsed;
            }
        }
    }
}
