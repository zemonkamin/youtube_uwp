using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Me : Page
    {
        private readonly ObservableCollection<VideoCardItem> _historyItems =
            new ObservableCollection<VideoCardItem>();
        private readonly ObservableCollection<PlaylistItem> _playlistItems =
            new ObservableCollection<PlaylistItem>();
        private readonly ObservableCollection<YouTubeAccountItem> _accountItems =
            new ObservableCollection<YouTubeAccountItem>();
        private Frame _frame;
        private string _channelTarget = string.Empty;
        private bool _accountsSheetOpen;
        private bool _accountsSheetIsDragging;
        private double _accountsSheetInitialY;
        private double _accountsSheetInitialTransformY;

        public Me()
        {
            this.InitializeComponent();
            _frame = Window.Current.Content as Frame;

            HistoryList.ItemsSource = _historyItems;
            PlaylistsList.ItemsSource = _playlistItems;
            AccountsList.ItemsSource = _accountItems;
            AccountsButtonText.Text = Localization.GetString("Accounts");
            AccountsSheetTitle.Text = Localization.GetString("SelectAccount");
            DownloadsHeaderText.Text = Localization.GetString("Downloads");
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (tabbar != null)
                tabbar.SetActiveTab(Tabbar.ActiveTab.Account);

            await LoadPageAsync();
        }

        private async Task LoadPageAsync()
        {
            ShowLoading(true);
            EmptyText.Visibility = Visibility.Collapsed;

            try
            {
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;

                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    ApplySignedOutState();
                    return;
                }

                ApplyCachedProfileImage(refreshToken);

                // Profile is deliberately refreshed here instead of using the short-lived
                // in-memory account cache. This is where avatar changes are detected.
                var profileTask = Config.GetAccountInfoFreshAsync(refreshToken);
                var historyTask = Config.GetHistoryAsync(refreshToken, 12);
                var playlistsTask = Config.GetMyPlaylistsAsync(refreshToken, 25);

                AccountInfo profile = null;
                List<VideoCardItem> history = null;
                List<PlaylistItem> playlists = null;

                try
                {
                    profile = await profileTask;
                }
                catch
                {
                    profile = null;
                }

                try
                {
                    history = await historyTask;
                }
                catch
                {
                    history = null;
                }

                try
                {
                    playlists = await playlistsTask;
                }
                catch
                {
                    playlists = null;
                }

                ApplyProfile(profile);
                await UpdateCachedProfileImageAsync(refreshToken, profile);
                ApplyHistory(history);
                ApplyPlaylists(playlists);

                if (_historyItems.Count == 0 && _playlistItems.Count == 0)
                {
                    EmptyText.Text = Localization.GetString("NoData");
                    EmptyText.Visibility = Visibility.Visible;
                }
            }
            finally
            {
                ShowLoading(false);
            }
        }

        private void ApplySignedOutState()
        {
            DisplayNameText.Text = Localization.GetString("NotSignedIn");
            ChannelHandleText.Text = "";
            GoToChannelButton.Visibility = Visibility.Collapsed;
            _channelTarget = string.Empty;
            _historyItems.Clear();
            _playlistItems.Clear();
            EmptyText.Text = Localization.GetString("MeSignIn");
            EmptyText.Visibility = Visibility.Visible;
        }

        private void ApplyProfile(AccountInfo profile)
        {
            if (profile == null)
            {
                DisplayNameText.Text = Localization.GetString("LoadingFailed");
                ChannelHandleText.Text = "";
                GoToChannelButton.Visibility = Visibility.Collapsed;
                _channelTarget = string.Empty;
                return;
            }

            DisplayNameText.Text = string.IsNullOrWhiteSpace(profile.DisplayName)
                ? Localization.GetString("NoName")
                : profile.DisplayName;

            if (!string.IsNullOrWhiteSpace(profile.ChannelHandle))
            {
                ChannelHandleText.Text = profile.ChannelHandle.StartsWith("@")
                    ? profile.ChannelHandle
                    : "@" + profile.ChannelHandle;
                _channelTarget = ChannelHandleText.Text;
                GoToChannelButton.Visibility = Visibility.Visible;
            }
            else
            {
                ChannelHandleText.Text = "";
                _channelTarget = string.Empty;
                GoToChannelButton.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(profile.ThumbnailUrl) &&
                !Config.HasCachedAccountAvatar(Config.UserToken))
            {
                try
                {
                    ProfileImageBrush.ImageSource = new BitmapImage(new Uri(profile.ThumbnailUrl));
                }
                catch { }
            }
        }

        private void ApplyCachedProfileImage(string refreshToken)
        {
            var cachedUri = Config.GetCachedAccountAvatarUri(refreshToken);
            if (string.IsNullOrWhiteSpace(cachedUri))
            {
                return;
            }

            try
            {
                var bitmap = new BitmapImage();
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.UriSource = new Uri(cachedUri);
                ProfileImageBrush.ImageSource = bitmap;
            }
            catch
            {
            }
        }

        private async Task UpdateCachedProfileImageAsync(string refreshToken, AccountInfo profile)
        {
            if (profile == null || string.IsNullOrWhiteSpace(profile.ThumbnailUrl))
            {
                return;
            }

            await Config.UpdateCachedAccountAvatarAsync(refreshToken, profile.ThumbnailUrl);

            // Re-read through ms-appdata with IgnoreImageCache so an overwritten file is
            // visible immediately on this profile and on this page's tabbar.
            ApplyCachedProfileImage(refreshToken);
            if (tabbar != null)
            {
                tabbar.RefreshAccountIconFromCache();
            }
        }

        private void ApplyHistory(List<VideoCardItem> history)
        {
            _historyItems.Clear();

            if (history == null)
                return;

            foreach (var item in history)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.VideoId))
                    _historyItems.Add(item);
            }
        }

        private void ApplyPlaylists(List<PlaylistItem> playlists)
        {
            _playlistItems.Clear();

            if (playlists == null)
                return;

            foreach (var item in playlists)
            {
                if (item != null && !string.IsNullOrWhiteSpace(item.PlaylistId))
                    _playlistItems.Add(item);
            }
        }

        private void ShowLoading(bool isLoading)
        {
            if (LoadingOverlay != null)
                LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;

            if (LoadingRing != null)
                LoadingRing.IsActive = isLoading;
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            _frame?.Navigate(typeof(Searching));
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            _frame?.Navigate(typeof(Settings));
        }

        private void DownloadsHeader_Click(object sender, RoutedEventArgs e)
        {
            _frame?.Navigate(typeof(Downloads));
        }

        private async void AccountsButton_Click(object sender, RoutedEventArgs e)
        {
            Config.LoadUserToken();
            if (string.IsNullOrWhiteSpace(Config.UserToken))
            {
                _frame?.Navigate(typeof(Login));
                return;
            }

            SetAccountsSheetVisibility(true);
            _accountItems.Clear();
            AccountsEmptyText.Visibility = Visibility.Collapsed;
            AccountsLoadingRing.Visibility = Visibility.Visible;
            AccountsLoadingRing.IsActive = true;

            try
            {
                var accounts = await Config.GetYouTubeAccountsAsync(Config.UserToken);
                if (!_accountsSheetOpen)
                {
                    return;
                }

                foreach (var account in accounts)
                {
                    if (account != null)
                    {
                        _accountItems.Add(account);
                    }
                }

                if (_accountItems.Count == 0)
                {
                    AccountsEmptyText.Text = Localization.GetString("AccountsLoadFailed");
                    AccountsEmptyText.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Accounts] UI load failed: " + ex.Message);
                AccountsEmptyText.Text = Localization.GetString("AccountsLoadFailed");
                AccountsEmptyText.Visibility = Visibility.Visible;
            }
            finally
            {
                AccountsLoadingRing.IsActive = false;
                AccountsLoadingRing.Visibility = Visibility.Collapsed;
            }
        }

        private void SetAccountsSheetVisibility(bool show)
        {
            _accountsSheetOpen = show;
            AccountsOverlay.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            AccountsSheet.Visibility = Visibility.Visible;

            var hiddenY = AccountsSheet.DismissDistance;
            var animation = new DoubleAnimation
            {
                From = AccountsSheetTransform.Y,
                To = show ? 0 : hiddenY,
                Duration = new Duration(TimeSpan.FromMilliseconds(220)),
                EnableDependentAnimation = true,
                EasingFunction = new CubicEase
                {
                    EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, AccountsSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Completed += delegate
            {
                if (!show)
                {
                    AccountsSheet.Visibility = Visibility.Collapsed;
                    AccountsOverlay.Visibility = Visibility.Collapsed;
                }
            };
            storyboard.Begin();
        }

        private void AccountsOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_accountsSheetOpen)
            {
                SetAccountsSheetVisibility(false);
            }
            e.Handled = true;
        }

        private void AccountsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _accountsSheetInitialY = e.GetCurrentPoint(element).Position.Y;
                _accountsSheetInitialTransformY = AccountsSheetTransform.Y;
                _accountsSheetIsDragging = true;
                e.Handled = true;
            }
        }

        private void AccountsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_accountsSheetIsDragging)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentY = e.GetCurrentPoint(element).Position.Y;
            var deltaY = currentY - _accountsSheetInitialY;
            AccountsSheetTransform.Y = AccountsSheet.ClampDragOffset(_accountsSheetInitialTransformY + deltaY);
            e.Handled = true;
        }

        private void AccountsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_accountsSheetIsDragging)
            {
                return;
            }

            _accountsSheetIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            SetAccountsSheetVisibility(AccountsSheetTransform.Y <= AccountsSheet.DragDismissThreshold);
            e.Handled = true;
        }

        private async void AccountItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var account = button != null ? button.DataContext as YouTubeAccountItem : null;
            if (account == null)
            {
                return;
            }

            if (account.IsSelected)
            {
                SetAccountsSheetVisibility(false);
                return;
            }

            Config.SelectYouTubeAccount(account);
            SetAccountsSheetVisibility(false);
            ProfileImageBrush.ImageSource = null;
            await LoadPageAsync();
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new MessageDialog(
                Localization.GetString("ConfirmSignOutMessage"),
                Localization.GetString("ConfirmSignOutTitle")
            );

            dialog.Commands.Add(
                new UICommand(
                    Localization.GetString("Yes"),
                    async (command) =>
                    {
                        ApplicationData.Current.LocalSettings.Values.Remove("yt_refresh_token");
                        ApplicationData.Current.LocalSettings.Values.Remove("AuthToken");
                        Config.SetUserToken("");

                        _frame?.Navigate(typeof(Login));
                    }
                )
            );

            dialog.Commands.Add(new UICommand(Localization.GetString("No"), null));

            await dialog.ShowAsync();
        }

        private void GoToChannelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(_channelTarget))
                _frame?.Navigate(typeof(Channel), _channelTarget);
        }

        private void HistoryHeader_Click(object sender, RoutedEventArgs e)
        {
            if (_frame == null)
                return;

            var historyPageType = Type.GetType("YouTube.History, YouTube");
            if (historyPageType != null)
            {
                _frame.Navigate(historyPageType);
            }
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

            if (item != null && !string.IsNullOrWhiteSpace(item.VideoId))
                _frame?.Navigate(typeof(Video), item.VideoId);
        }

        private void PlaylistCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as PlaylistItem : null;

            if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId) || _frame == null)
                return;

            var playlistPageType = Type.GetType("YouTube.Playlist, YouTube");
            if (playlistPageType != null)
            {
                _frame.Navigate(playlistPageType, item);
            }
        }
    }
}
