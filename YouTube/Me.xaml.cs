using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
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
        private Frame _frame;
        private string _channelTarget = string.Empty;

        public Me()
        {
            this.InitializeComponent();
            _frame = Window.Current.Content as Frame;

            HistoryList.ItemsSource = _historyItems;
            PlaylistsList.ItemsSource = _playlistItems;
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
            ProfileMetaSeparator.Visibility = Visibility.Collapsed;
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
                ProfileMetaSeparator.Visibility = Visibility.Collapsed;
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
                ProfileMetaSeparator.Visibility = Visibility.Visible;
                GoToChannelButton.Visibility = Visibility.Visible;
            }
            else
            {
                ChannelHandleText.Text = "";
                _channelTarget = string.Empty;
                ProfileMetaSeparator.Visibility = Visibility.Collapsed;
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
