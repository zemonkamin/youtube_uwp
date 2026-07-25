using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Notifications : Page
    {
        private bool _systemBackRegistered;

        public Notifications()
        {
            this.InitializeComponent();
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            RegisterSystemBackButton();
            await LoadNotificationsAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            UnregisterSystemBackButton();
            base.OnNavigatedFrom(e);
        }

        private void RegisterSystemBackButton()
        {
            try
            {
                var nav = SystemNavigationManager.GetForCurrentView();
                nav.AppViewBackButtonVisibility = Frame != null && Frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;

                if (!_systemBackRegistered)
                {
                    nav.BackRequested += SystemBackButton_BackRequested;
                    _systemBackRegistered = true;
                }
            }
            catch
            {
            }
        }

        private void UnregisterSystemBackButton()
        {
            try
            {
                if (_systemBackRegistered)
                {
                    SystemNavigationManager.GetForCurrentView().BackRequested -= SystemBackButton_BackRequested;
                    _systemBackRegistered = false;
                }
            }
            catch
            {
            }
        }

        private void SystemBackButton_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (Frame != null && Frame.CanGoBack)
            {
                e.Handled = true;
                Frame.GoBack();
            }
        }

        private async Task LoadNotificationsAsync()
        {
            ShowLoading();

            try
            {
                Config.LoadUserToken();
                if (string.IsNullOrWhiteSpace(Config.UserToken))
                {
                    ShowEmpty("Sign in required", "Sign in to see your YouTube notifications.");
                    return;
                }

                var items = await Config.GetNotificationsAsync(Config.UserToken, 60);
                if (items == null || items.Count == 0)
                {
                    ShowEmpty("No notifications", "New updates from your subscriptions will appear here.");
                    return;
                }

                RenderNotifications(items);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Notifications] Load error: " + ex.Message);
                ShowEmpty("Could not load notifications", "Pull up this page again later.");
            }
        }

        private void ShowLoading()
        {
            if (MainScrollViewer != null)
                MainScrollViewer.Visibility = Visibility.Collapsed;

            if (EmptyPanel != null)
                EmptyPanel.Visibility = Visibility.Collapsed;

            if (LoadingGrid != null)
                LoadingGrid.Visibility = Visibility.Visible;

            if (LoadingRing != null)
                LoadingRing.IsActive = true;
        }

        private void HideLoading()
        {
            if (LoadingRing != null)
                LoadingRing.IsActive = false;

            if (LoadingGrid != null)
                LoadingGrid.Visibility = Visibility.Collapsed;
        }

        private void ShowEmpty(string title, string subtitle)
        {
            HideLoading();

            if (NotificationsPanel != null)
                NotificationsPanel.Children.Clear();

            if (MainScrollViewer != null)
                MainScrollViewer.Visibility = Visibility.Collapsed;

            if (EmptyTitleText != null)
                EmptyTitleText.Text = string.IsNullOrWhiteSpace(title) ? "No notifications" : title;

            if (EmptySubtitleText != null)
                EmptySubtitleText.Text = string.IsNullOrWhiteSpace(subtitle) ? string.Empty : subtitle;

            if (EmptyPanel != null)
                EmptyPanel.Visibility = Visibility.Visible;
        }

        private void RenderNotifications(List<NotificationItem> items)
        {
            HideLoading();

            if (NotificationsPanel == null)
                return;

            NotificationsPanel.Children.Clear();
            for (var i = 0; i < items.Count; i++)
            {
                NotificationsPanel.Children.Add(CreateNotificationRow(items[i]));
            }

            if (MainScrollViewer != null)
                MainScrollViewer.Visibility = Visibility.Visible;

            if (EmptyPanel != null)
                EmptyPanel.Visibility = Visibility.Collapsed;
        }

        private void AddSectionHeader(string text)
        {
            var header = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 170)),
                FontSize = 20,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(20, 8, 20, 18)
            };

            NotificationsPanel.Children.Add(header);
        }

        private FrameworkElement CreateNotificationRow(NotificationItem item)
        {
            var button = new Button
            {
                Style = Resources["FlatButtonStyle"] as Style,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0, 0, 0, 8),
                Tag = item
            };
            button.Click += NotificationRow_Click;

            var root = new Grid
            {
                MinHeight = 68,
                Padding = new Thickness(0, 0, 16, 0)
            };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(104) });

            if (item != null && !item.IsRead)
            {
                var unreadDot = new Ellipse
                {
                    Width = 4,
                    Height = 4,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 58, 138, 220)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(unreadDot, 0);
                root.Children.Add(unreadDot);
            }

            var avatar = new Ellipse
            {
                Width = 38,
                Height = 38,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0),
                Fill = CreateImageBrush(item == null ? string.Empty : item.AvatarUrl, Stretch.UniformToFill, Windows.UI.Color.FromArgb(255, 32, 32, 32))
            };
            Grid.SetColumn(avatar, 1);
            root.Children.Add(avatar);

            var textStack = new StackPanel
            {
                Margin = new Thickness(8, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Top
            };

            var title = new TextBlock
            {
                Text = item == null ? string.Empty : item.Title,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 15,
                FontWeight = Windows.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1
            };
            textStack.Children.Add(title);

            var message = new TextBlock
            {
                Text = item == null ? string.Empty : item.Message,
                Foreground = new SolidColorBrush(Windows.UI.Colors.White),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 2,
                Margin = new Thickness(0, 1, 0, 0)
            };
            textStack.Children.Add(message);

            var time = new TextBlock
            {
                Text = item == null ? string.Empty : item.TimeText,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 170, 170, 170)),
                FontSize = 12,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1,
                Margin = new Thickness(0, 2, 0, 0)
            };
            textStack.Children.Add(time);

            Grid.SetColumn(textStack, 2);
            root.Children.Add(textStack);

            var thumbnail = new Border
            {
                Width = 96,
                Height = 54,
                CornerRadius = new CornerRadius(6),
                Background = CreateImageBrush(item == null ? string.Empty : item.ThumbnailUrl, Stretch.UniformToFill, Windows.UI.Color.FromArgb(255, 55, 55, 55)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Grid.SetColumn(thumbnail, 3);
            root.Children.Add(thumbnail);


            button.Content = root;
            return button;
        }

        private Brush CreateImageBrush(string url, Stretch stretch, Windows.UI.Color fallbackColor)
        {
            var source = CreateBitmapImage(url);
            if (source == null)
            {
                return new SolidColorBrush(fallbackColor);
            }

            return new ImageBrush
            {
                ImageSource = source,
                Stretch = stretch
            };
        }

        private BitmapImage CreateBitmapImage(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            try
            {
                if (url.StartsWith("//", StringComparison.Ordinal))
                {
                    url = "https:" + url;
                }

                return new BitmapImage(new Uri(url));
            }
            catch
            {
                return null;
            }
        }

        private void NotificationRow_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button == null ? null : button.Tag as NotificationItem;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            Frame.Navigate(typeof(Video), item.VideoId);
        }

        private void SearchButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null)
            {
                Frame.Navigate(typeof(Searching));
            }
        }
    }
}
