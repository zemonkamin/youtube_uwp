using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Media.Imaging;

namespace YouTube
{
    public sealed partial class Tabbar : Page
    {
        public event EventHandler HomeTabClicked;
        public event EventHandler ShortsTabClicked;
        public event EventHandler SubscriptionsTabClicked;
        public event EventHandler AccountTabClicked;

        // Enum to track current active tab
        public enum ActiveTab
        {
            Home,
            Shorts,
            Subscriptions,
            Account
        }

        private ActiveTab _currentActiveTab = ActiveTab.Home;

        public Tabbar()
        {
            this.InitializeComponent();
            InitializeTabBar();
        }

        private void InitializeTabBar()
        {
            // Load token from Config
            Config.LoadUserToken();

            // Set Home as active by default
            SetActiveTab(ActiveTab.Home);

            // Enable or disable authenticated tabs based on login state
            UpdateShortsButtonState();
            UpdateSubscriptionsButtonState();
        }

        public void UpdateShortsButtonState()
        {
            Config.LoadUserToken();
            bool isAuthenticated = !string.IsNullOrEmpty(Config.UserToken);

            if (isAuthenticated)
            {
                ShortsButton.IsEnabled = true;

                var textBlock = FindTextBlockInButton(ShortsButton);
                if (textBlock != null)
                {
                    textBlock.Foreground = new SolidColorBrush(Colors.White);
                }

                var image = FindImageInButton(ShortsButton);
                if (image != null)
                {
                    image.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/shorts-icon.png"));
                    image.Opacity = 1.0;
                }
            }
            else
            {
                SetButtonDisabled(ShortsButton);
            }
        }

        // Method to update Subscriptions button state based on authentication
        public void UpdateSubscriptionsButtonState()
        {
            Config.LoadUserToken();
            bool isAuthenticated = !string.IsNullOrEmpty(Config.UserToken);

            if (isAuthenticated)
            {
                SubscriptionsButton.IsEnabled = true;

                var textBlock = FindTextBlockInButton(SubscriptionsButton);
                if (textBlock != null)
                {
                    textBlock.Foreground = new SolidColorBrush(Colors.White);
                }

                var image = FindImageInButton(SubscriptionsButton);
                if (image != null)
                {
                    image.Opacity = 1.0;
                }
            }
            else
            {
                SetButtonDisabled(SubscriptionsButton);
            }
        }

        // Public method to set active tab from outside
        public void SetActiveTab(ActiveTab tab)
        {
            // First, set all tabs to inactive (except disabled ones)
            if (HomeButton.IsEnabled) SetHomeTabActive(false);
            if (ShortsButton.IsEnabled) SetShortsTabActive(false);
            if (SubscriptionsButton.IsEnabled) SetSubscriptionsTabActive(false);
            if (AccountButton.IsEnabled) SetAccountTabActive(false);

            // Then, set the specified tab to active (if it's enabled)
            switch (tab)
            {
                case ActiveTab.Home:
                    if (HomeButton.IsEnabled) SetHomeTabActive(true);
                    break;
                case ActiveTab.Shorts:
                    if (ShortsButton.IsEnabled) SetShortsTabActive(true);
                    break;
                case ActiveTab.Subscriptions:
                    if (SubscriptionsButton.IsEnabled) SetSubscriptionsTabActive(true);
                    break;
                case ActiveTab.Account:
                    if (AccountButton.IsEnabled) SetAccountTabActive(true);
                    break;
            }

            _currentActiveTab = tab;
        }

        private void SetButtonDisabled(Button button)
        {
            if (button != null)
            {
                button.IsEnabled = false;

                // Find the TextBlock in the button and set its color to gray
                var textBlock = FindTextBlockInButton(button);
                if (textBlock != null)
                {
                    textBlock.Foreground = new SolidColorBrush(Colors.Gray);
                }

                // Find the Image in the button and set its opacity to 0.5
                var image = FindImageInButton(button);
                if (image != null)
                {
                    image.Opacity = 0.5;
                }
            }
        }

        private void SetHomeTabActive(bool isActive)
        {
            if (isActive)
            {
                // Change to active icon
                var homeImage = FindImageInButton(HomeButton);
                if (homeImage != null)
                {
                    homeImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/home-icon-active.png"));
                    homeImage.Opacity = 1.0;
                }

                // Change text color to white
                var homeText = FindTextBlockInButton(HomeButton);
                if (homeText != null)
                {
                    homeText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            else
            {
                // Change to inactive icon
                var homeImage = FindImageInButton(HomeButton);
                if (homeImage != null)
                {
                    homeImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/home-icon.png"));
                    homeImage.Opacity = 1.0; // Keep full opacity for enabled buttons
                }

                // Change text color to white (enabled buttons stay white)
                var homeText = FindTextBlockInButton(HomeButton);
                if (homeText != null)
                {
                    homeText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
        }

        private void SetShortsTabActive(bool isActive)
        {
            var shortsImage = FindImageInButton(ShortsButton);
            if (shortsImage != null)
            {
                shortsImage.Source = new BitmapImage(new Uri(isActive
                    ? "ms-appx:///Assets/tabbar/shorts-icon-active.png"
                    : "ms-appx:///Assets/tabbar/shorts-icon.png"));
                shortsImage.Opacity = 1.0;
            }

            var shortsText = FindTextBlockInButton(ShortsButton);
            if (shortsText != null)
            {
                shortsText.Foreground = new SolidColorBrush(Colors.White);
            }
        }

        private void SetSubscriptionsTabActive(bool isActive)
        {
            if (isActive)
            {
                // Change to active icon
                var subscriptionsImage = FindImageInButton(SubscriptionsButton);
                if (subscriptionsImage != null)
                {
                    subscriptionsImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/sub-icon-active.png"));
                    subscriptionsImage.Opacity = 1.0;
                }

                // Change text color to white
                var subscriptionsText = FindTextBlockInButton(SubscriptionsButton);
                if (subscriptionsText != null)
                {
                    subscriptionsText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            else
            {
                // Change to inactive icon
                var subscriptionsImage = FindImageInButton(SubscriptionsButton);
                if (subscriptionsImage != null)
                {
                    subscriptionsImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/sub-icon.png"));
                    subscriptionsImage.Opacity = 1.0; // Keep full opacity for enabled buttons
                }

                // Change text color to white (enabled buttons stay white)
                var subscriptionsText = FindTextBlockInButton(SubscriptionsButton);
                if (subscriptionsText != null)
                {
                    subscriptionsText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
        }

        private void SetAccountTabActive(bool isActive)
        {
            if (isActive)
            {
                // Change to active icon
                var accountImage = FindImageInButton(AccountButton);
                if (accountImage != null)
                {
                    accountImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/user-icon-active.png"));
                    accountImage.Opacity = 1.0;
                }

                // Change text color to white
                var accountText = FindTextBlockInButton(AccountButton);
                if (accountText != null)
                {
                    accountText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
            else
            {
                // Change to inactive icon
                var accountImage = FindImageInButton(AccountButton);
                if (accountImage != null)
                {
                    accountImage.Source = new BitmapImage(new Uri("ms-appx:///Assets/tabbar/user-icon.png"));
                    accountImage.Opacity = 1.0; // Keep full opacity for enabled buttons
                }

                // Change text color to white (enabled buttons stay white)
                var accountText = FindTextBlockInButton(AccountButton);
                if (accountText != null)
                {
                    accountText.Foreground = new SolidColorBrush(Colors.White);
                }
            }
        }

        private Image FindImageInButton(Button button)
        {
            var stackPanel = button.Content as StackPanel;
            if (stackPanel != null)
            {
                foreach (var child in stackPanel.Children)
                {
                    Image image = child as Image;
                    if (image != null)
                    {
                        return image;
                    }
                }
            }
            return null;
        }

        private TextBlock FindTextBlockInButton(Button button)
        {
            var stackPanel = button.Content as StackPanel;
            if (stackPanel != null)
            {
                foreach (var child in stackPanel.Children)
                {
                    TextBlock textBlock = child as TextBlock;
                    if (textBlock != null)
                    {
                        return textBlock;
                    }
                }
            }
            return null;
        }

        private TextBlock FindTextBlockInButton(Button button, string text)
        {
            var stackPanel = button.Content as StackPanel;
            if (stackPanel != null)
            {
                foreach (var child in stackPanel.Children)
                {
                    TextBlock textBlock = child as TextBlock;
                    if (textBlock != null && textBlock.Text == text)
                    {
                        return textBlock;
                    }
                }
            }
            return null;
        }

        private void HomeTab_Click(object sender, RoutedEventArgs e)
        {
            // Set Home tab as active
            SetActiveTab(ActiveTab.Home);

            // Navigate to Home page
            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Home))
            {
                frame.Navigate(typeof(Home));
            }

            HomeTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void ShortsTab_Click(object sender, RoutedEventArgs e)
        {
            Config.LoadUserToken();
            if (string.IsNullOrEmpty(Config.UserToken))
            {
                SetButtonDisabled(ShortsButton);
                return;
            }

            SetActiveTab(ActiveTab.Shorts);

            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Shorts))
            {
                frame.Navigate(typeof(Shorts));
            }

            ShortsTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SubscriptionsTab_Click(object sender, RoutedEventArgs e)
        {
            // Set Subscriptions tab as active
            SetActiveTab(ActiveTab.Subscriptions);

            // Navigate to Subscriptions page
            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Subscriptions))
            {
                frame.Navigate(typeof(Subscriptions));
            }

            SubscriptionsTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            // Load token from Config to check if user is authenticated
            Config.LoadUserToken();
            bool isAuthenticated = !string.IsNullOrEmpty(Config.UserToken);

            // Set Account tab as active
            SetActiveTab(ActiveTab.Account);

            // Navigate to Me page if authenticated, otherwise to Login
            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                if (isAuthenticated)
                {
                    frame.Navigate(typeof(Me));
                }
                else
                {
                    frame.Navigate(typeof(Login));
                }
            }

            AccountTabClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}