using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Settings : Page
    {
        private const string PreferredVideoQualitySettingKey = "PreferredVideoQuality";
        private const string PreferredShortsQualitySettingKey = "PreferredShortsQuality";
        private const string ScrubPreviewEnabledSettingKey = "ScrubPreviewEnabled";
        private const string AutoFullscreenLandscapeSettingKey = "AutoFullscreenLandscape";
        // The video and Shorts quality rows share one bottom sheet; this says which one opened it.
        private bool _editingShortsQuality;
        private bool _isQualitySheetOpen;
        private bool _isAboutSheetOpen;
        private bool _isNotificationsSheetOpen;
        private double _qualityInitialY;
        private double _qualityInitialTransformY;
        private bool _qualityIsDragging;
        private double _aboutInitialY;
        private double _aboutInitialTransformY;
        private bool _aboutIsDragging;
        private double _notificationsInitialY;
        private double _notificationsInitialTransformY;
        private bool _notificationsIsDragging;
        private Storyboard _notificationsToggleStoryboard;
        private Storyboard _scrubPreviewToggleStoryboard;
        private Storyboard _autoFullscreenLandscapeToggleStoryboard;

        public Settings()
        {
            this.InitializeComponent();
            this.Loaded += Settings_Loaded;
            this.Unloaded += Settings_Unloaded;
        }

        private void Settings_Loaded(object sender, RoutedEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= Settings_BackRequested;
            SystemNavigationManager.GetForCurrentView().BackRequested += Settings_BackRequested;

            try
            {
                if (tabbar != null)
                {
                    tabbar.SetActiveTab(Tabbar.ActiveTab.Account);
                }
            }
            catch
            {
            }

            EnsurePreferredVideoQualityDefault();
            UpdatePreferredQualityText();
            BuildQualityOptions();
            EnsureScrubPreviewDefault();
            UpdateScrubPreviewToggleVisual(IsScrubPreviewEnabled(), false);
            EnsureAutoFullscreenLandscapeDefault();
            UpdateAutoFullscreenLandscapeToggleVisual(IsAutoFullscreenLandscapeEnabled(), false);
            UpdateNotificationFrequencyText();
            BuildNotificationOptions();
            UpdateNotificationToggleVisual(YouTubeNotificationService.AreNotificationsEnabled(), false);
            UpdateAboutText();
        }

        private void Settings_Unloaded(object sender, RoutedEventArgs e)
        {
            SystemNavigationManager.GetForCurrentView().BackRequested -= Settings_BackRequested;
        }

        private void Settings_BackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;
            NavigateBackOrCloseSheet();
        }

        private void NavigateBackOrCloseSheet()
        {
            if (_isQualitySheetOpen)
            {
                AnimateQualityBottomSheet(false);
                return;
            }

            if (_isAboutSheetOpen)
            {
                AnimateAboutBottomSheet(false);
                return;
            }

            if (_isNotificationsSheetOpen)
            {
                AnimateNotificationsBottomSheet(false);
                return;
            }

            if (Frame != null && Frame.CanGoBack)
            {
                Frame.GoBack();
            }
        }

        private async void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            await ResetAndExitAsync();
        }

        private async Task ResetAndExitAsync()
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values.Clear();
                await ApplicationData.Current.ClearAsync();
            }
            catch
            {
                try
                {
                    ApplicationData.Current.LocalSettings.Values.Clear();
                }
                catch
                {
                }
            }

            CoreApplication.Exit();
        }

        private static void EnsurePreferredVideoQualityDefault()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(PreferredVideoQualitySettingKey)
                    || values[PreferredVideoQualitySettingKey] == null
                    || string.IsNullOrWhiteSpace(
                        values[PreferredVideoQualitySettingKey].ToString()))
                {
                    values[PreferredVideoQualitySettingKey] = "Auto";
                }
            }
            catch
            {
            }
        }

        private void PreferredQualityButton_Click(object sender, RoutedEventArgs e)
        {
            _editingShortsQuality = false;
            ShowQualityBottomSheet();
        }

        private void PreferredShortsQualityButton_Click(object sender, RoutedEventArgs e)
        {
            _editingShortsQuality = true;
            ShowQualityBottomSheet();
        }

        private string ActiveQualityKey
        {
            get { return _editingShortsQuality ? PreferredShortsQualitySettingKey : PreferredVideoQualitySettingKey; }
        }

        private static void EnsureScrubPreviewDefault()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(ScrubPreviewEnabledSettingKey))
                {
                    values[ScrubPreviewEnabledSettingKey] = false;
                }
            }
            catch
            {
            }
        }

        private static bool IsScrubPreviewEnabled()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                object raw;
                if (!values.TryGetValue(ScrubPreviewEnabledSettingKey, out raw) || raw == null)
                {
                    return false;
                }

                if (raw is bool)
                {
                    return (bool)raw;
                }

                bool parsed;
                return bool.TryParse(raw.ToString(), out parsed) && parsed;
            }
            catch
            {
                return false;
            }
        }

        private void ScrubPreviewToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !IsScrubPreviewEnabled();

            try
            {
                ApplicationData.Current.LocalSettings.Values[ScrubPreviewEnabledSettingKey] = enabled;
            }
            catch
            {
            }

            UpdateScrubPreviewToggleVisual(enabled, true);
        }

        private void UpdateScrubPreviewToggleVisual(bool isOn, bool animate)
        {
            if (ScrubPreviewToggleTrack == null || ScrubPreviewToggleThumbTransform == null)
            {
                return;
            }

            if (_scrubPreviewToggleStoryboard != null)
            {
                _scrubPreviewToggleStoryboard.Stop();
                _scrubPreviewToggleStoryboard = null;
            }

            var brush = ScrubPreviewToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                ScrubPreviewToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;

            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                ScrubPreviewToggleThumbTransform.X = isOn ? OnOffset : 0;
                return;
            }

            var thumbAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? OnOffset : 0,
                EnableDependentAnimation = true
            };
            var colorAnimation = new ColorAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155))
            };

            Storyboard.SetTarget(thumbAnimation, ScrubPreviewToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _scrubPreviewToggleStoryboard = new Storyboard();
            _scrubPreviewToggleStoryboard.Children.Add(thumbAnimation);
            _scrubPreviewToggleStoryboard.Children.Add(colorAnimation);
            _scrubPreviewToggleStoryboard.Begin();
        }

        private static void EnsureAutoFullscreenLandscapeDefault()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(AutoFullscreenLandscapeSettingKey))
                {
                    values[AutoFullscreenLandscapeSettingKey] = true;
                }
            }
            catch
            {
            }
        }

        private static bool IsAutoFullscreenLandscapeEnabled()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                object raw;
                if (!values.TryGetValue(AutoFullscreenLandscapeSettingKey, out raw) || raw == null)
                {
                    return true;
                }

                if (raw is bool)
                {
                    return (bool)raw;
                }

                bool parsed;
                return !bool.TryParse(raw.ToString(), out parsed) || parsed;
            }
            catch
            {
                return true;
            }
        }

        private void AutoFullscreenLandscapeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !IsAutoFullscreenLandscapeEnabled();

            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    AutoFullscreenLandscapeSettingKey] = enabled;
            }
            catch
            {
            }

            UpdateAutoFullscreenLandscapeToggleVisual(enabled, true);
        }

        private void UpdateAutoFullscreenLandscapeToggleVisual(bool isOn, bool animate)
        {
            if (AutoFullscreenLandscapeToggleTrack == null
                || AutoFullscreenLandscapeToggleThumbTransform == null)
            {
                return;
            }

            if (_autoFullscreenLandscapeToggleStoryboard != null)
            {
                _autoFullscreenLandscapeToggleStoryboard.Stop();
                _autoFullscreenLandscapeToggleStoryboard = null;
            }

            var brush = AutoFullscreenLandscapeToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                AutoFullscreenLandscapeToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;

            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                AutoFullscreenLandscapeToggleThumbTransform.X = isOn ? OnOffset : 0;
                return;
            }

            var thumbAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? OnOffset : 0,
                EnableDependentAnimation = true
            };
            var colorAnimation = new ColorAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155))
            };

            Storyboard.SetTarget(thumbAnimation, AutoFullscreenLandscapeToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _autoFullscreenLandscapeToggleStoryboard = new Storyboard();
            _autoFullscreenLandscapeToggleStoryboard.Children.Add(thumbAnimation);
            _autoFullscreenLandscapeToggleStoryboard.Children.Add(colorAnimation);
            _autoFullscreenLandscapeToggleStoryboard.Begin();
        }

        private void NotificationsButton_Click(object sender, RoutedEventArgs e)
        {
            ShowNotificationsBottomSheet();
        }

        private void AboutButton_Click(object sender, RoutedEventArgs e)
        {
            ShowAboutBottomSheet();
        }

        private void BuildQualityOptions()
        {
            if (QualityOptionsPanel == null)
            {
                return;
            }

            QualityOptionsPanel.Children.Clear();
            // Shorts play as a single muxed file (no demuxer), so YouTube only offers up to 720p
            // for them — 1080p would never be available and is left out of that list.
            // Shorts now support the demuxer too, so they get the same heights up to 1080p as
            // regular video. Any non-Auto height forces the muxer path.
            var qualities = new[] { "Auto", "144p", "360p", "480p", "720p", "1080p" };
            var current = GetPreferredQualityDisplayText(ActiveQualityKey);

            for (int i = 0; i < qualities.Length; i++)
            {
                var quality = qualities[i];
                var button = new Button
                {
                    Background = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0, 4, 0, 4),
                    MinHeight = 38,
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = quality
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = quality,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Windows.UI.Colors.White)),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(text, 0);
                grid.Children.Add(text);

                var check = new FontIcon
                {
                    Glyph = "\uE73E",
                    FontFamily = new Windows.UI.Xaml.Media.FontFamily("Segoe MDL2 Assets"),
                    FontSize = 18,
                    Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Windows.UI.Colors.White)),
                    Visibility = string.Equals(current, quality, StringComparison.OrdinalIgnoreCase)
                        ? Visibility.Visible
                        : Visibility.Collapsed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(check, 1);
                grid.Children.Add(check);

                button.Content = grid;
                button.Click += QualityOptionButton_Click;
                QualityOptionsPanel.Children.Add(button);
            }
        }

        private void QualityOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var quality = button != null ? button.Tag as string : null;
            if (string.IsNullOrWhiteSpace(quality))
            {
                return;
            }

            SetPreferredQuality(ActiveQualityKey, quality);
            UpdatePreferredQualityText();
            BuildQualityOptions();
            AnimateQualityBottomSheet(false);
        }

        private static void SetPreferredQuality(string settingKey, string quality)
        {
            var valueToSave = "Auto";
            if (!string.IsNullOrWhiteSpace(quality) && !string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                var normalized = quality.Trim();
                if (normalized.EndsWith("p", StringComparison.OrdinalIgnoreCase))
                {
                    normalized = normalized.Substring(0, normalized.Length - 1);
                }
                valueToSave = normalized;
            }

            var values = ApplicationData.Current.LocalSettings.Values;
            values[settingKey] = valueToSave;

            if (settingKey == PreferredVideoQualitySettingKey)
            {
                // Keep aliases for older builds / pages that may read a previous key name.
                values["PreferredQuality"] = valueToSave;
                values["PreferredVideoQualityTag"] = valueToSave;
                values["VideoPreferredQuality"] = valueToSave;
            }
        }

        private static string GetPreferredQualityDisplayText(string settingKey)
        {
            try
            {
                if (ApplicationData.Current.LocalSettings.Values.ContainsKey(settingKey))
                {
                    var value = ApplicationData.Current.LocalSettings.Values[settingKey] as string;
                    if (!string.IsNullOrWhiteSpace(value) && !string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        return value.EndsWith("p", StringComparison.OrdinalIgnoreCase) ? value : value + "p";
                    }
                }
            }
            catch
            {
            }

            return "Auto";
        }

        private void UpdatePreferredQualityText()
        {
            if (PreferredQualityValueText != null)
            {
                PreferredQualityValueText.Text = GetPreferredQualityDisplayText(PreferredVideoQualitySettingKey);
            }
            if (PreferredShortsQualityValueText != null)
            {
                PreferredShortsQualityValueText.Text = GetPreferredQualityDisplayText(PreferredShortsQualitySettingKey);
            }
        }

        private void BuildNotificationOptions()
        {
            if (NotificationFrequencyOptionsPanel == null)
            {
                return;
            }

            NotificationFrequencyOptionsPanel.Children.Clear();
            var options = new[]
            {
                new NotificationFrequencyOption { Title = "Every 15 min", Minutes = 15 },
                new NotificationFrequencyOption { Title = "Every 30 min", Minutes = 30 },
                new NotificationFrequencyOption { Title = "Every hour", Minutes = 60 },
                new NotificationFrequencyOption { Title = "Every 2 hours", Minutes = 120 },
                new NotificationFrequencyOption { Title = "Every 6 hours", Minutes = 360 }
            };
            var current = YouTubeNotificationService.GetNotificationIntervalMinutes();

            for (int i = 0; i < options.Length; i++)
            {
                var option = options[i];
                var button = new Button
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = new Thickness(0, 4, 0, 4),
                    MinHeight = 38,
                    Margin = new Thickness(0, 0, 0, 2),
                    Tag = option
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = option.Title,
                    Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                    FontSize = 14,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(text, 0);
                grid.Children.Add(text);

                var check = new FontIcon
                {
                    Glyph = "",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 18,
                    Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                    Visibility = current == option.Minutes ? Visibility.Visible : Visibility.Collapsed,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(12, 0, 0, 0)
                };
                Grid.SetColumn(check, 1);
                grid.Children.Add(check);

                button.Content = grid;
                button.Click += NotificationFrequencyOptionButton_Click;
                NotificationFrequencyOptionsPanel.Children.Add(button);
            }
        }

        private async void NotificationFrequencyOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var option = button != null ? button.Tag as NotificationFrequencyOption : null;
            if (option == null)
            {
                return;
            }

            YouTubeNotificationService.SetNotificationIntervalMinutes(option.Minutes);
            YouTubeNotificationService.SetNotificationsEnabled(true);
            UpdateNotificationFrequencyText();
            BuildNotificationOptions();
            UpdateNotificationToggleVisual(true, true);
            await YouTubeNotificationService.ReconfigureBackgroundTaskAsync();
        }

        private async void NotificationsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !YouTubeNotificationService.AreNotificationsEnabled();
            YouTubeNotificationService.SetNotificationsEnabled(enabled);
            UpdateNotificationFrequencyText();
            UpdateNotificationToggleVisual(enabled, true);
            await YouTubeNotificationService.ReconfigureBackgroundTaskAsync();
        }

        private void UpdateNotificationFrequencyText()
        {
            if (NotificationFrequencyValueText != null)
            {
                NotificationFrequencyValueText.Text = YouTubeNotificationService.GetNotificationFrequencyDisplayText();
            }
        }

        private void UpdateNotificationToggleVisual(bool isOn, bool animate)
        {
            if (NotificationsToggleTrack == null || NotificationsToggleThumbTransform == null)
            {
                return;
            }

            if (_notificationsToggleStoryboard != null)
            {
                _notificationsToggleStoryboard.Stop();
                _notificationsToggleStoryboard = null;
            }

            var brush = NotificationsToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                NotificationsToggleTrack.Background = brush;
            }

            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                NotificationsToggleThumbTransform.X = isOn ? 24 : 0;
                return;
            }

            var thumbAnimation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? 24 : 0,
                EnableDependentAnimation = true
            };
            var colorAnimation = new ColorAnimation
            {
                Duration = TimeSpan.FromMilliseconds(180),
                To = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155))
            };

            Storyboard.SetTarget(thumbAnimation, NotificationsToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _notificationsToggleStoryboard = new Storyboard();
            _notificationsToggleStoryboard.Children.Add(thumbAnimation);
            _notificationsToggleStoryboard.Children.Add(colorAnimation);
            _notificationsToggleStoryboard.Begin();
        }

        private sealed class NotificationFrequencyOption
        {
            public string Title { get; set; }
            public int Minutes { get; set; }
        }

        private void UpdateAboutText()
        {
            if (AboutText == null)
            {
                return;
            }

            var version = Package.Current.Id.Version;
            var versionText = string.Format(
                "{0}.{1}.{2}.{3}",
                version.Major,
                version.Minor,
                version.Build,
                version.Revision
            );

            AboutText.Text = "Developed by the LegacyProjects team.\n"
                + "Supported by YouTube API Legacy.\n\n"
                + "Version " + versionText;
        }

        private void ShowQualityBottomSheet()
        {
            BuildQualityOptions();
            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }
            if (QualityBottomSheetPanel != null)
            {
                QualityBottomSheetPanel.Visibility = Visibility.Visible;
            }
            AnimateQualityBottomSheet(true);
        }

        private void ShowAboutBottomSheet()
        {
            UpdateAboutText();
            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }
            if (AboutBottomSheetPanel != null)
            {
                AboutBottomSheetPanel.Visibility = Visibility.Visible;
            }
            AnimateAboutBottomSheet(true);
        }

        private void ShowNotificationsBottomSheet()
        {
            BuildNotificationOptions();
            UpdateNotificationFrequencyText();
            UpdateNotificationToggleVisual(YouTubeNotificationService.AreNotificationsEnabled(), false);
            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }
            if (NotificationsBottomSheetPanel != null)
            {
                NotificationsBottomSheetPanel.Visibility = Visibility.Visible;
            }
            AnimateNotificationsBottomSheet(true);
        }

        private void AnimateQualityBottomSheet(bool show)
        {
            _isQualitySheetOpen = show;
            if (QualityBottomSheetTransform == null)
            {
                return;
            }

            if (show && AboutBottomSheetPanel != null && AboutBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateAboutBottomSheet(false);
            }
            if (show && NotificationsBottomSheetPanel != null && NotificationsBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateNotificationsBottomSheet(false);
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                To = show ? 0 : 340
            };

            Storyboard.SetTarget(animation, QualityBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (QualityBottomSheetPanel != null)
                    {
                        QualityBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    HideOverlayIfNoSheetOpen();
                };
            }

            storyboard.Begin();
        }

        private void AnimateAboutBottomSheet(bool show)
        {
            _isAboutSheetOpen = show;
            if (AboutBottomSheetTransform == null)
            {
                return;
            }

            if (show && QualityBottomSheetPanel != null && QualityBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateQualityBottomSheet(false);
            }
            if (show && NotificationsBottomSheetPanel != null && NotificationsBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateNotificationsBottomSheet(false);
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                To = show ? 0 : 260
            };

            Storyboard.SetTarget(animation, AboutBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (AboutBottomSheetPanel != null)
                    {
                        AboutBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    HideOverlayIfNoSheetOpen();
                };
            }

            storyboard.Begin();
        }

        private void AnimateNotificationsBottomSheet(bool show)
        {
            _isNotificationsSheetOpen = show;
            if (NotificationsBottomSheetTransform == null)
            {
                return;
            }

            if (show && QualityBottomSheetPanel != null && QualityBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateQualityBottomSheet(false);
            }
            if (show && AboutBottomSheetPanel != null && AboutBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateAboutBottomSheet(false);
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                To = show ? 0 : 380
            };

            Storyboard.SetTarget(animation, NotificationsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (NotificationsBottomSheetPanel != null)
                    {
                        NotificationsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    HideOverlayIfNoSheetOpen();
                };
            }

            storyboard.Begin();
        }

        private void HideOverlayIfNoSheetOpen()
        {
            if (OverlayGrid != null && !_isQualitySheetOpen && !_isAboutSheetOpen && !_isNotificationsSheetOpen)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void OverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isQualitySheetOpen)
            {
                AnimateQualityBottomSheet(false);
            }
            if (_isAboutSheetOpen)
            {
                AnimateAboutBottomSheet(false);
            }
            if (_isNotificationsSheetOpen)
            {
                AnimateNotificationsBottomSheet(false);
            }
        }

        private void QualityDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateQualityBottomSheet(false);
        }

        private void QualityDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _qualityInitialY = e.GetCurrentPoint(element).Position.Y;
                _qualityInitialTransformY = QualityBottomSheetTransform != null ? QualityBottomSheetTransform.Y : 0;
                _qualityIsDragging = true;
                e.Handled = true;
            }
        }

        private void QualityDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_qualityIsDragging && QualityBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double newY = _qualityInitialTransformY + currentPoint.Position.Y - _qualityInitialY;
                if (newY >= 0 && newY <= 340)
                {
                    QualityBottomSheetTransform.Y = newY;
                }
                e.Handled = true;
            }
        }

        private void QualityDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_qualityIsDragging)
            {
                _qualityIsDragging = false;
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (QualityBottomSheetTransform != null && QualityBottomSheetTransform.Y > 155)
                {
                    AnimateQualityBottomSheet(false);
                }
                else
                {
                    AnimateQualityBottomSheet(true);
                }
                e.Handled = true;
            }
        }

        private void NotificationsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateNotificationsBottomSheet(false);
        }

        private void NotificationsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _notificationsInitialY = e.GetCurrentPoint(element).Position.Y;
                _notificationsInitialTransformY = NotificationsBottomSheetTransform != null ? NotificationsBottomSheetTransform.Y : 0;
                _notificationsIsDragging = true;
                e.Handled = true;
            }
        }

        private void NotificationsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_notificationsIsDragging && NotificationsBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double newY = _notificationsInitialTransformY + currentPoint.Position.Y - _notificationsInitialY;
                if (newY >= 0 && newY <= 380)
                {
                    NotificationsBottomSheetTransform.Y = newY;
                }
                e.Handled = true;
            }
        }

        private void NotificationsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_notificationsIsDragging)
            {
                _notificationsIsDragging = false;
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (NotificationsBottomSheetTransform != null && NotificationsBottomSheetTransform.Y > 170)
                {
                    AnimateNotificationsBottomSheet(false);
                }
                else
                {
                    AnimateNotificationsBottomSheet(true);
                }
                e.Handled = true;
            }
        }

        private void AboutDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateAboutBottomSheet(false);
        }

        private void AboutDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _aboutInitialY = e.GetCurrentPoint(element).Position.Y;
                _aboutInitialTransformY = AboutBottomSheetTransform != null ? AboutBottomSheetTransform.Y : 0;
                _aboutIsDragging = true;
                e.Handled = true;
            }
        }

        private void AboutDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_aboutIsDragging && AboutBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double newY = _aboutInitialTransformY + currentPoint.Position.Y - _aboutInitialY;
                if (newY >= 0 && newY <= 260)
                {
                    AboutBottomSheetTransform.Y = newY;
                }
                e.Handled = true;
            }
        }

        private void AboutDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_aboutIsDragging)
            {
                _aboutIsDragging = false;
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (AboutBottomSheetTransform != null && AboutBottomSheetTransform.Y > 120)
                {
                    AnimateAboutBottomSheet(false);
                }
                else
                {
                    AnimateAboutBottomSheet(true);
                }
                e.Handled = true;
            }
        }
    }
}
