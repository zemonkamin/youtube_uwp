using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.ApplicationModel;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI;
using Windows.UI.ViewManagement;
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
            None,
            Home,
            Shorts,
            Subscriptions,
            Account
        }

        private ActiveTab _currentActiveTab = ActiveTab.None;

        public Tabbar()
        {
            this.InitializeComponent();

            // The VS2015 XAML designer instantiates this Page while rendering parent pages
            // such as Settings.xaml. Do not touch LocalSettings/network-backed account state
            // in design mode; older UWP designers can otherwise report
            // "Cannot create an instance of Tabbar".
            if (DesignMode.DesignModeEnabled)
            {
                return;
            }

            try
            {
                InitializeTabBar();
                this.Loaded += Tabbar_Loaded;
                this.Unloaded += Tabbar_Unloaded;
            }
            catch (Exception ex)
            {
                // A tab bar should never prevent its parent page from being constructed.
                // Runtime state is refreshed again by the owning page when it loads.
                System.Diagnostics.Debug.WriteLine("Tabbar initialization failed: " + ex.Message);
            }
        }

        private void InitializeTabBar()
        {
            // Load token from Config
            Config.LoadUserToken();

            // A secondary page must not inherit a highlight from whichever tab opened it.
            SetActiveTab(ActiveTab.None);

            // Enable or disable authenticated tabs based on login state
            UpdateShortsButtonState();
            UpdateSubscriptionsButtonState();

            // The cached avatar is applied immediately. On the first signed-in run,
            // fetch it once and persist it for future app/page entries.
            RefreshAccountIconFromCache();
            EnsureAccountIconCachedAsync();
        }

        private void Tabbar_Loaded(object sender, RoutedEventArgs e)
        {
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            FluentGlassEffectHelper.EnabledChanged += GlassEffect_EnabledChanged;
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            ShortsFeatureController.EnabledChanged += ShortsFeature_EnabledChanged;
            UpdateShortsButtonState();
            UpdateButtonHostWidth(TabbarRoot == null ? 0 : TabbarRoot.ActualWidth);
            UpdateCompactOrientationLayout();
            if (TabbarGlassHost != null)
            {
                TabbarGlassHost.SizeChanged -= TabbarGlassHost_SizeChanged;
                TabbarGlassHost.SizeChanged += TabbarGlassHost_SizeChanged;
                ApplyGlassEffect();
            }

            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                frame.Navigated -= RootFrame_Navigated;
                frame.Navigated += RootFrame_Navigated;
                SynchronizeWithPage(frame.Content);
                UpdateDiscordPagePresence(frame.Content);
            }
        }

        private void Tabbar_Unloaded(object sender, RoutedEventArgs e)
        {
            FluentGlassEffectHelper.EnabledChanged -= GlassEffect_EnabledChanged;
            ShortsFeatureController.EnabledChanged -= ShortsFeature_EnabledChanged;
            if (TabbarGlassHost != null)
                TabbarGlassHost.SizeChanged -= TabbarGlassHost_SizeChanged;

            FluentGlassEffectHelper.Detach(TabbarGlassHost);

            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                frame.Navigated -= RootFrame_Navigated;
            }
        }

        private void TabbarGlassHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ApplyGlassEffect();
        }

        private void TabbarRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateButtonHostWidth(e.NewSize.Width);
            UpdateCompactOrientationLayout();
            ApplyGlassEffect();
        }

        private void UpdateCompactOrientationLayout()
        {
            var compact = ResponsiveLayout.IsCompactLandscape;
            var labelVisibility = compact ? Visibility.Collapsed : Visibility.Visible;

            if (HomeLabel != null) HomeLabel.Visibility = labelVisibility;
            if (ShortsLabel != null) ShortsLabel.Visibility = labelVisibility;
            if (SubscriptionsLabel != null) SubscriptionsLabel.Visibility = labelVisibility;
            if (AccountLabel != null) AccountLabel.Visibility = labelVisibility;
            if (TabbarContentRow != null)
                TabbarContentRow.Height = new GridLength(compact ? 40.0 : 50.0);

            // The containing page keeps its safe-area row. Only the rendered chrome is
            // shortened, so pages with custom player rows are never overwritten.
            if (TabbarRoot != null)
            {
                TabbarRoot.Height = compact ? 42.0 : double.NaN;
                TabbarRoot.VerticalAlignment = compact
                    ? VerticalAlignment.Bottom
                    : VerticalAlignment.Stretch;
            }
        }

        private void UpdateButtonHostWidth(double availableWidth)
        {
            if (TabbarButtonsHost == null || availableWidth <= 0
                || double.IsNaN(availableWidth) || double.IsInfinity(availableWidth))
                return;

            if (IsLandscapeLayout())
            {
                TabbarButtonsHost.HorizontalAlignment = HorizontalAlignment.Center;
                TabbarButtonsHost.Width = availableWidth * 0.35;
            }
            else
            {
                TabbarButtonsHost.Width = double.NaN;
                TabbarButtonsHost.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        private static bool IsLandscapeLayout()
        {
            try
            {
                var view = ApplicationView.GetForCurrentView();
                if (view != null)
                    return view.Orientation == ApplicationViewOrientation.Landscape;
            }
            catch
            {
            }

            try
            {
                var bounds = Window.Current.Bounds;
                return bounds.Width >= bounds.Height;
            }
            catch
            {
                return false;
            }
        }

        private void GlassEffect_EnabledChanged(object sender, EventArgs e)
        {
            ApplyGlassEffect();
        }

        private void ShortsFeature_EnabledChanged(object sender, EventArgs e)
        {
            UpdateShortsButtonState();
            if (!ShortsFeatureController.IsEnabled() && _currentActiveTab == ActiveTab.Shorts)
                SetActiveTab(ActiveTab.None);
        }

        private void ApplyGlassEffect()
        {
            if (TabbarGlassHost == null ||
                TabbarGlassHost.ActualWidth <= 1.0 ||
                TabbarGlassHost.ActualHeight <= 1.0)
                return;

            FluentGlassEffectHelper.AttachBottomBar(TabbarGlassHost, App.GetThemeBrush("AppBackgroundBrush"));
        }

        private void RootFrame_Navigated(object sender, NavigationEventArgs e)
        {
            var page = e != null ? e.Content : null;
            SynchronizeWithPage(page);
            UpdateDiscordPagePresence(page);
        }

        private static void UpdateDiscordPagePresence(object page)
        {
            // These pages publish richer information themselves as soon as their navigation
            // parameter/metadata is available.
            if (page is Video || page is Shorts || page is Search || page is Channel || page is Playlist)
                return;

            if (page is Home)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusHome"));
            else if (page is Subscriptions)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusSubscriptions"));
            else if (page is Searching)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusSearch"));
            else if (page is Downloads)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusDownloads"));
            else if (page is History)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusHistory"));
            else if (page is Notifications)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusNotifications"));
            else if (page is Settings)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusSettings"));
            else if (page is Me)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusProfile"));
            else if (page is Login)
                YouTube.Discord.DiscordPresenceService.SetPage(Localization.GetString("DiscordStatusLogin"));
            else if (page != null)
                YouTube.Discord.DiscordPresenceService.SetPage(page.GetType().Name);
        }

        private void SynchronizeWithPage(object page)
        {
            if (page is Home)
                SetActiveTab(ActiveTab.Home);
            else if (page is Shorts)
                SetActiveTab(ShortsFeatureController.IsEnabled() ? ActiveTab.Shorts : ActiveTab.None);
            else if (page is Subscriptions)
                SetActiveTab(ActiveTab.Subscriptions);
            else if (page is Me || page is Login)
                SetActiveTab(ActiveTab.Account);
            else
                SetActiveTab(ActiveTab.None);
        }

        public void RefreshAccountIconFromCache()
        {
            Config.LoadUserToken();
            var refreshToken = Config.UserToken;

            if (string.IsNullOrWhiteSpace(refreshToken) || !TryApplyCachedAccountIcon(refreshToken))
            {
                ApplyDefaultAccountIcon(_currentActiveTab == ActiveTab.Account);
            }
        }

        private async void EnsureAccountIconCachedAsync()
        {
            try
            {
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    return;
                }

                // Once a persistent avatar exists, the tabbar never waits for the network.
                // Freshness is checked when the user opens the profile page.
                if (TryApplyCachedAccountIcon(refreshToken))
                {
                    return;
                }

                var profile = await Config.GetAccountInfoAsync(refreshToken);
                if (profile == null || string.IsNullOrWhiteSpace(profile.ThumbnailUrl))
                {
                    return;
                }

                await Config.UpdateCachedAccountAvatarAsync(refreshToken, profile.ThumbnailUrl);
                TryApplyCachedAccountIcon(refreshToken);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Tabbar account avatar initialization failed: " + ex.Message);
            }
        }

        private bool TryApplyCachedAccountIcon(string refreshToken)
        {
            var cachedUri = Config.GetCachedAccountAvatarUri(refreshToken);
            if (string.IsNullOrWhiteSpace(cachedUri) || AccountAvatarEllipse == null)
            {
                return false;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(cachedUri);

                AccountAvatarEllipse.Fill = new ImageBrush
                {
                    ImageSource = bitmap,
                    Stretch = Stretch.UniformToFill
                };
                AccountAvatarEllipse.Visibility = Visibility.Visible;

                if (AccountDefaultIcon != null)
                {
                    AccountDefaultIcon.Visibility = Visibility.Collapsed;
                    AccountDefaultIcon.Opacity = 1.0;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private void ApplyDefaultAccountIcon(bool isActive)
        {
            if (AccountDefaultIcon != null)
            {
                App.SetThemeImageSource(
                    AccountDefaultIcon,
                    isActive ? "Assets/tabbar/user-icon-active.png" : "Assets/tabbar/user-icon.png");
                AccountDefaultIcon.Stretch = Stretch.Uniform;
                AccountDefaultIcon.Visibility = Visibility.Visible;
                AccountDefaultIcon.Opacity = 1.0;
            }

            if (AccountAvatarEllipse != null)
            {
                AccountAvatarEllipse.Fill = App.GetThemeBrush("AvatarPlaceholderBrush") ?? new SolidColorBrush(Color.FromArgb(255, 42, 42, 42));
                AccountAvatarEllipse.Stroke = new SolidColorBrush(Colors.Transparent);
                AccountAvatarEllipse.Visibility = Visibility.Collapsed;
            }
        }

        public void UpdateShortsButtonState()
        {
            var featureEnabled = ShortsFeatureController.IsEnabled();
            if (ShortsColumn != null)
                ShortsColumn.Width = featureEnabled ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (ShortsButton != null)
                ShortsButton.Visibility = featureEnabled ? Visibility.Visible : Visibility.Collapsed;

            if (!featureEnabled)
                return;

            Config.LoadUserToken();
            bool isAuthenticated = !string.IsNullOrEmpty(Config.UserToken);

            if (isAuthenticated)
            {
                ShortsButton.IsEnabled = true;

                var textBlock = FindTextBlockInButton(ShortsButton);
                if (textBlock != null)
                {
                    textBlock.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
                }

                var image = FindImageInButton(ShortsButton);
                if (image != null)
                {
                    App.SetThemeImageSource(image, "Assets/tabbar/shorts-icon.png");
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
                    textBlock.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
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
                case ActiveTab.None:
                    break;
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
                    textBlock.Foreground = (App.GetThemeBrush("AppMutedTextBrush") ?? new SolidColorBrush(Colors.Gray));
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
                    App.SetThemeImageSource(homeImage, "Assets/tabbar/home-icon-active.png");
                    homeImage.Opacity = 1.0;
                }

                // Change text color to white
                var homeText = FindTextBlockInButton(HomeButton);
                if (homeText != null)
                {
                    homeText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
                }
            }
            else
            {
                // Change to inactive icon
                var homeImage = FindImageInButton(HomeButton);
                if (homeImage != null)
                {
                    App.SetThemeImageSource(homeImage, "Assets/tabbar/home-icon.png");
                    homeImage.Opacity = 1.0; // Keep full opacity for enabled buttons
                }

                // Change text color to white (enabled buttons stay white)
                var homeText = FindTextBlockInButton(HomeButton);
                if (homeText != null)
                {
                    homeText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
                }
            }
        }

        private void SetShortsTabActive(bool isActive)
        {
            var shortsImage = FindImageInButton(ShortsButton);
            if (shortsImage != null)
            {
                App.SetThemeImageSource(
                    shortsImage,
                    isActive ? "Assets/tabbar/shorts-icon-active.png" : "Assets/tabbar/shorts-icon.png");
                shortsImage.Opacity = 1.0;
            }

            var shortsText = FindTextBlockInButton(ShortsButton);
            if (shortsText != null)
            {
                shortsText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
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
                    App.SetThemeImageSource(subscriptionsImage, "Assets/tabbar/sub-icon-active.png");
                    subscriptionsImage.Opacity = 1.0;
                }

                // Change text color to white
                var subscriptionsText = FindTextBlockInButton(SubscriptionsButton);
                if (subscriptionsText != null)
                {
                    subscriptionsText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
                }
            }
            else
            {
                // Change to inactive icon
                var subscriptionsImage = FindImageInButton(SubscriptionsButton);
                if (subscriptionsImage != null)
                {
                    App.SetThemeImageSource(subscriptionsImage, "Assets/tabbar/sub-icon.png");
                    subscriptionsImage.Opacity = 1.0; // Keep full opacity for enabled buttons
                }

                // Change text color to white (enabled buttons stay white)
                var subscriptionsText = FindTextBlockInButton(SubscriptionsButton);
                if (subscriptionsText != null)
                {
                    subscriptionsText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
                }
            }
        }

        private void SetAccountTabActive(bool isActive)
        {
            Config.LoadUserToken();
            var refreshToken = Config.UserToken;

            // Authenticated users keep their cached profile image for both active and
            // inactive states. The theme-contrast ring is shown only while the Account tab is active.
            if (string.IsNullOrWhiteSpace(refreshToken) || !TryApplyCachedAccountIcon(refreshToken))
            {
                ApplyDefaultAccountIcon(isActive);
            }
            else if (AccountAvatarEllipse != null)
            {
                AccountAvatarEllipse.Stroke = isActive
                    ? (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White))
                    : new SolidColorBrush(Colors.Transparent);
            }

            var accountText = FindTextBlockInButton(AccountButton);
            if (accountText != null)
            {
                accountText.Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Colors.White));
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
            // Navigate to Home page
            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Home))
            {
                frame.Navigate(typeof(Home));
            }
            else
            {
                SetActiveTab(ActiveTab.Home);
                if (frame != null)
                    UpdateDiscordPagePresence(frame.Content);
            }

            HomeTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void ShortsTab_Click(object sender, RoutedEventArgs e)
        {
            if (!ShortsFeatureController.IsEnabled())
                return;

            Config.LoadUserToken();
            if (string.IsNullOrEmpty(Config.UserToken))
            {
                SetButtonDisabled(ShortsButton);
                return;
            }

            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Shorts))
            {
                frame.Navigate(typeof(Shorts));
            }
            else
            {
                SetActiveTab(ActiveTab.Shorts);
            }

            ShortsTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void SubscriptionsTab_Click(object sender, RoutedEventArgs e)
        {
            // Navigate to Subscriptions page
            var frame = Window.Current.Content as Frame;
            if (frame != null && frame.Content.GetType() != typeof(Subscriptions))
            {
                frame.Navigate(typeof(Subscriptions));
            }
            else
            {
                SetActiveTab(ActiveTab.Subscriptions);
            }

            SubscriptionsTabClicked?.Invoke(this, EventArgs.Empty);
        }

        private void AccountButton_Click(object sender, RoutedEventArgs e)
        {
            // Load token from Config to check if user is authenticated
            Config.LoadUserToken();
            bool isAuthenticated = !string.IsNullOrEmpty(Config.UserToken);

            // Navigate to Me page if authenticated, otherwise to Login
            var frame = Window.Current.Content as Frame;
            if (frame != null)
            {
                if (isAuthenticated)
                {
                    if (!(frame.Content is Me)) frame.Navigate(typeof(Me));
                    else SetActiveTab(ActiveTab.Account);
                }
                else
                {
                    if (!(frame.Content is Login)) frame.Navigate(typeof(Login));
                    else SetActiveTab(ActiveTab.Account);
                }
            }

            AccountTabClicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
