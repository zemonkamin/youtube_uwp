using System;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.Storage.Pickers;
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
        private bool _editingThumbnailQuality;
        private bool _isQualitySheetOpen;
        private bool _isLanguageSheetOpen;
        private bool _isThemeSheetOpen;
        private bool _isAboutSheetOpen;
        private bool _isNotificationsSheetOpen;
        private double _qualityInitialY;
        private double _qualityInitialTransformY;
        private bool _qualityIsDragging;
        private double _languageInitialY;
        private double _languageInitialTransformY;
        private bool _languageIsDragging;
        private double _themeInitialY;
        private double _themeInitialTransformY;
        private bool _themeIsDragging;
        private double _aboutInitialY;
        private double _aboutInitialTransformY;
        private bool _aboutIsDragging;
        private double _notificationsInitialY;
        private double _notificationsInitialTransformY;
        private bool _notificationsIsDragging;
        private Storyboard _notificationsToggleStoryboard;
        private Storyboard _scrubPreviewToggleStoryboard;
        private Storyboard _sponsorMarkersToggleStoryboard;
        private Storyboard _autoFullscreenLandscapeToggleStoryboard;
        private Storyboard _liveTileToggleStoryboard;
        private Storyboard _channelIconsToggleStoryboard;
        private Storyboard _glassEffectToggleStoryboard;
        private Storyboard _landscapeNavbarSearchModeToggleStoryboard;
        private Storyboard _videoAmbientEffectToggleStoryboard;
        private Storyboard _shortsFeatureToggleStoryboard;
        private Storyboard _discordPresenceToggleStoryboard;

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
                    tabbar.SetActiveTab(Tabbar.ActiveTab.None);
                }
            }
            catch
            {
            }

            EnsurePreferredVideoQualityDefault();
            UpdatePreferredQualityText();
            UpdateThumbnailQualityText();
            UpdateChannelIconsToggleVisual(ChannelIconController.IsEnabled(), false);
            UpdateDownloadFolderText();
            BuildQualityOptions();
            EnsureScrubPreviewDefault();
            UpdateScrubPreviewToggleVisual(IsScrubPreviewEnabled(), false);
            UpdateSponsorMarkersToggleVisual(SponsorBlock.AreTimelineMarkersEnabled(), false);
            EnsureAutoFullscreenLandscapeDefault();
            UpdateAutoFullscreenLandscapeToggleVisual(IsAutoFullscreenLandscapeEnabled(), false);
            UpdateNotificationFrequencyText();
            BuildNotificationOptions();
            UpdateNotificationToggleVisual(YouTubeNotificationService.AreNotificationsEnabled(), false);
            UpdateAboutText();
            UpdateLanguageText();
            BuildLanguageOptions();
            YouTube.Discord.DiscordPresenceService.EnsureDefault();
            UpdateAppearanceTexts();
            UpdateThemeText();
            BuildThemeOptions();
            UpdateGlassEffectToggleVisual(FluentGlassEffectHelper.IsEnabled(), false);
            LandscapeNavbarSearchModeController.EnsureDefault();
            UpdateLandscapeNavbarSearchModeToggleVisual(
                LandscapeNavbarSearchModeController.IsEnabled(),
                false);
            VideoAmbientEffectController.EnsureDefault();
            UpdateVideoAmbientEffectToggleVisual(VideoAmbientEffectController.IsEnabled(), false);
            UpdateShortsFeatureToggleVisual(ShortsFeatureController.IsEnabled(), false);
            var discordAvailable = YouTube.Discord.DiscordPresenceService.IsAvailable();
            if (DiscordPresenceToggleButton != null)
            {
                DiscordPresenceToggleButton.IsEnabled = discordAvailable;
                DiscordPresenceToggleButton.Opacity = discordAvailable ? 1.0 : 0.45;
            }
            UpdateDiscordPresenceToggleVisual(YouTube.Discord.DiscordPresenceService.IsEnabled(), false);
            UpdateLiveTileToggleVisual(App.IsLiveTileEnabled(), false);
        }

        private void UpdateLanguageText()
        {
            if (LanguageValueText == null)
                return;

            var savedLanguage = Localization.GetSavedLanguage();
            LanguageValueText.Text = string.IsNullOrEmpty(savedLanguage)
                ? Localization.GetString("LanguageSystem")
                : Localization.GetLanguageDisplayName(savedLanguage);
        }

        private void LanguageButton_Click(object sender, RoutedEventArgs e)
        {
            ShowLanguageBottomSheet();
        }

        private void BuildLanguageOptions()
        {
            if (LanguageOptionsPanel == null)
            {
                return;
            }

            LanguageOptionsPanel.Children.Clear();
            var savedLanguage = Localization.GetSavedLanguage();
            var options = Localization.SupportedLanguages;

            LanguageOptionsPanel.Children.Add(CreateLanguageOptionButton(
                Localization.GetString("LanguageSystem"),
                string.Empty,
                string.IsNullOrEmpty(savedLanguage)));

            for (int i = 0; i < options.Length; i++)
            {
                var option = options[i];
                LanguageOptionsPanel.Children.Add(CreateLanguageOptionButton(
                    option.DisplayName,
                    option.AppTag,
                    string.Equals(savedLanguage, option.AppTag, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private Button CreateLanguageOptionButton(string displayName, string appTag, bool isSelected)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 4, 0, 4),
                MinHeight = 38,
                Margin = new Thickness(0, 0, 0, 2),
                Tag = appTag ?? string.Empty
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = displayName,
                Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 0);
            grid.Children.Add(text);

            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);

            button.Content = grid;
            button.Click += LanguageOptionButton_Click;
            return button;
        }

        private void LanguageOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var selectedLanguage = button != null && button.Tag != null ? button.Tag.ToString() : string.Empty;

            if (string.Equals(Localization.GetSavedLanguage(), selectedLanguage, StringComparison.OrdinalIgnoreCase))
            {
                AnimateLanguageBottomSheet(false);
                return;
            }

            Localization.SetLanguage(selectedLanguage);
            UpdateLanguageText();
            BuildLanguageOptions();
            AnimateLanguageBottomSheet(false);

            // Recreate this page so x:Uid resources are applied with the new language immediately.
            if (Frame != null)
            {
                Frame.Navigate(typeof(Settings), null, new SuppressNavigationTransitionInfo());
                if (Frame.BackStack.Count > 0)
                    Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
            }
        }

        private void UpdateAppearanceTexts()
        {
            if (AppearanceSectionText != null)
                AppearanceSectionText.Text = Localization.GetString("Appearance");
            if (ThemeLabelText != null)
                ThemeLabelText.Text = Localization.GetString("Theme");
            if (ThemeSheetTitleText != null)
                ThemeSheetTitleText.Text = Localization.GetString("Theme");
            if (LiveTileLabelText != null)
                LiveTileLabelText.Text = Localization.GetString("LiveTile");
            if (LiveTileDescriptionText != null)
                LiveTileDescriptionText.Text = Localization.GetString("LiveTileDescription");
            if (GlassEffectLabelText != null)
                GlassEffectLabelText.Text = Localization.GetString("GlassEffect");
            if (GlassEffectDescriptionText != null)
                GlassEffectDescriptionText.Text = Localization.GetString("GlassEffectDescription");
            if (LandscapeNavbarSearchModeLabelText != null)
                LandscapeNavbarSearchModeLabelText.Text = Localization.GetString("LandscapeNavbarSearchMode");
            if (LandscapeNavbarSearchModeDescriptionText != null)
                LandscapeNavbarSearchModeDescriptionText.Text = Localization.GetString(
                    "LandscapeNavbarSearchModeDescription");
            if (VideoAmbientEffectLabelText != null)
                VideoAmbientEffectLabelText.Text = Localization.GetString("VideoAmbientEffect");
            if (VideoAmbientEffectDescriptionText != null)
                VideoAmbientEffectDescriptionText.Text = Localization.GetString("VideoAmbientEffectDescription");
            if (ShortsFeatureLabelText != null)
                ShortsFeatureLabelText.Text = Localization.GetString("ShortsFeature");
            if (ShortsFeatureDescriptionText != null)
                ShortsFeatureDescriptionText.Text = Localization.GetString("ShortsFeatureDescription");
            if (DiscordPresenceLabelText != null)
                DiscordPresenceLabelText.Text = Localization.GetString("DiscordPresence");
            if (DiscordPresenceDescriptionText != null)
                DiscordPresenceDescriptionText.Text = Localization.GetString(
                    YouTube.Discord.DiscordPresenceService.IsAvailable()
                        ? "DiscordPresenceDescription"
                        : "DiscordPresenceUnavailableMobile");
            if (ChannelIconsLabelText != null)
                ChannelIconsLabelText.Text = Localization.GetString("ChannelIcons");
            if (ChannelIconsDescriptionText != null)
                ChannelIconsDescriptionText.Text = Localization.GetString("ChannelIconsDescription");
            if (DownloadFolderLabelText != null)
                DownloadFolderLabelText.Text = Localization.GetString("DownloadFolder");
            if (DownloadFolderDescriptionText != null)
                DownloadFolderDescriptionText.Text = Localization.GetString("DownloadFolderDescription");
            if (ClearDownloadedVideosLabelText != null)
                ClearDownloadedVideosLabelText.Text = Localization.GetString("ClearDownloadedVideos");
            if (ClearDownloadedVideosDescriptionText != null)
                ClearDownloadedVideosDescriptionText.Text = Localization.GetString("ClearDownloadedVideosDescription");
            if (SponsorMarkersLabelText != null)
            {
                var text = Localization.GetString("SponsorMarkers");
                SponsorMarkersLabelText.Text = string.Equals(text, "SponsorMarkers", StringComparison.Ordinal)
                    ? "Advertising segments on seek bar"
                    : text;
            }
            if (SponsorMarkersDescriptionText != null)
            {
                var text = Localization.GetString("SponsorMarkersDescription");
                SponsorMarkersDescriptionText.Text = string.Equals(text, "SponsorMarkersDescription", StringComparison.Ordinal)
                    ? "Show colored SponsorBlock ranges"
                    : text;
            }
        }

        private void UpdateDownloadFolderText()
        {
            if (DownloadFolderValueText != null)
                DownloadFolderValueText.Text = DownloadManager.GetDestinationDisplayName();
        }

        private async void DownloadFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var picker = new FolderPicker();
                picker.SuggestedStartLocation = PickerLocationId.Downloads;
                picker.FileTypeFilter.Add("*");
                var folder = await picker.PickSingleFolderAsync();
                if (folder == null) return;
                DownloadManager.SetDestinationFolder(folder);
                UpdateDownloadFolderText();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Folder picker failed: " + ex.Message);
            }
        }

        private async void ClearDownloadedVideosButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClearDownloadedVideosButton == null) return;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = Localization.GetString("ClearDownloadedVideos"),
                    Content = Localization.GetString("ClearDownloadedVideosConfirmation"),
                    PrimaryButtonText = Localization.GetString("ClearDownloadedVideosAction"),
                    SecondaryButtonText = Localization.GetString("Cancel")
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

                ClearDownloadedVideosButton.IsEnabled = false;
                await DownloadManager.ClearAllAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Clear failed: " + ex.Message);
            }
            finally
            {
                ClearDownloadedVideosButton.IsEnabled = true;
            }
        }

        private void UpdateThemeText()
        {
            if (ThemeValueText == null)
                return;

            var mode = App.GetSavedThemeMode();
            if (string.Equals(mode, App.ThemeModeLight, StringComparison.OrdinalIgnoreCase))
                ThemeValueText.Text = Localization.GetString("ThemeLight");
            else if (string.Equals(mode, App.ThemeModeDark, StringComparison.OrdinalIgnoreCase))
                ThemeValueText.Text = Localization.GetString("ThemeDark");
            else
                ThemeValueText.Text = Localization.GetString("LanguageSystem");
        }

        private void ThemeButton_Click(object sender, RoutedEventArgs e)
        {
            ShowThemeBottomSheet();
        }

        private void BuildThemeOptions()
        {
            if (ThemeOptionsPanel == null)
                return;

            ThemeOptionsPanel.Children.Clear();
            var current = App.GetSavedThemeMode();
            ThemeOptionsPanel.Children.Add(CreateThemeOptionButton(
                Localization.GetString("LanguageSystem"),
                App.ThemeModeSystem,
                string.Equals(current, App.ThemeModeSystem, StringComparison.OrdinalIgnoreCase)));
            ThemeOptionsPanel.Children.Add(CreateThemeOptionButton(
                Localization.GetString("ThemeLight"),
                App.ThemeModeLight,
                string.Equals(current, App.ThemeModeLight, StringComparison.OrdinalIgnoreCase)));
            ThemeOptionsPanel.Children.Add(CreateThemeOptionButton(
                Localization.GetString("ThemeDark"),
                App.ThemeModeDark,
                string.Equals(current, App.ThemeModeDark, StringComparison.OrdinalIgnoreCase)));
        }

        private Button CreateThemeOptionButton(string title, string mode, bool isSelected)
        {
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 4, 0, 4),
                MinHeight = 42,
                Margin = new Thickness(0, 0, 0, 2),
                Tag = mode
            };

            try
            {
                var style = Resources["SettingsIconButtonStyle"] as Style;
                if (style != null)
                    button.Style = style;
            }
            catch
            {
                button.Background = new SolidColorBrush(Colors.Transparent);
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = title,
                Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(text);

            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 18,
                Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(check, 1);
            grid.Children.Add(check);

            button.Content = grid;
            button.Click += ThemeOptionButton_Click;
            return button;
        }

        private void ThemeOptionButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var mode = button != null && button.Tag != null ? button.Tag.ToString() : App.ThemeModeSystem;
            if (string.Equals(App.GetSavedThemeMode(), mode, StringComparison.OrdinalIgnoreCase))
            {
                AnimateThemeBottomSheet(false);
                return;
            }

            App.SetThemeMode(mode);
            UpdateThemeText();
            BuildThemeOptions();
            AnimateThemeBottomSheet(false);

            // Recreate the page so every code-created brush is rebuilt for the selected theme.
            if (Frame != null)
            {
                Frame.Navigate(typeof(Settings), null, new SuppressNavigationTransitionInfo());
                if (Frame.BackStack.Count > 0)
                    Frame.BackStack.RemoveAt(Frame.BackStack.Count - 1);
            }
        }


        private void ChannelIconsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !ChannelIconController.IsEnabled();
            ChannelIconController.SetEnabled(enabled);
            App.RefreshThemeAssets();
            UpdateChannelIconsToggleVisual(enabled, true);
        }

        private void UpdateChannelIconsToggleVisual(bool isOn, bool animate)
        {
            if (ChannelIconsToggleTrack == null || ChannelIconsToggleThumbTransform == null)
                return;

            if (_channelIconsToggleStoryboard != null)
            {
                _channelIconsToggleStoryboard.Stop();
                _channelIconsToggleStoryboard = null;
            }

            var brush = ChannelIconsToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                ChannelIconsToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                ChannelIconsToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, ChannelIconsToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _channelIconsToggleStoryboard = new Storyboard();
            _channelIconsToggleStoryboard.Children.Add(thumbAnimation);
            _channelIconsToggleStoryboard.Children.Add(colorAnimation);
            _channelIconsToggleStoryboard.Begin();
        }

        private void LiveTileToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !App.IsLiveTileEnabled();
            App.SetLiveTileEnabled(enabled);
            UpdateLiveTileToggleVisual(enabled, true);
        }

        private void GlassEffectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !FluentGlassEffectHelper.IsEnabled();
            FluentGlassEffectHelper.SetEnabled(enabled);
            UpdateGlassEffectToggleVisual(enabled, true);
        }

        private void LandscapeNavbarSearchModeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !LandscapeNavbarSearchModeController.IsEnabled();
            LandscapeNavbarSearchModeController.SetEnabled(enabled);
            UpdateLandscapeNavbarSearchModeToggleVisual(enabled, true);
        }

        private void UpdateLandscapeNavbarSearchModeToggleVisual(bool isOn, bool animate)
        {
            if (LandscapeNavbarSearchModeToggleTrack == null
                || LandscapeNavbarSearchModeToggleThumbTransform == null)
                return;

            if (_landscapeNavbarSearchModeToggleStoryboard != null)
            {
                _landscapeNavbarSearchModeToggleStoryboard.Stop();
                _landscapeNavbarSearchModeToggleStoryboard = null;
            }

            var onColor = App.GetThemeColor("AppPrimaryTextBrush", Colors.White);
            var offColor = App.GetThemeColor(
                "AppMutedTextBrush",
                Color.FromArgb(255, 155, 155, 155));
            var brush = LandscapeNavbarSearchModeToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(isOn ? onColor : offColor);
                LandscapeNavbarSearchModeToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? onColor : offColor;
                LandscapeNavbarSearchModeToggleThumbTransform.X = isOn ? OnOffset : 0;
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
                To = isOn ? onColor : offColor
            };

            Storyboard.SetTarget(thumbAnimation, LandscapeNavbarSearchModeToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _landscapeNavbarSearchModeToggleStoryboard = new Storyboard();
            _landscapeNavbarSearchModeToggleStoryboard.Children.Add(thumbAnimation);
            _landscapeNavbarSearchModeToggleStoryboard.Children.Add(colorAnimation);
            _landscapeNavbarSearchModeToggleStoryboard.Begin();
        }

        private void VideoAmbientEffectToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !VideoAmbientEffectController.IsEnabled();
            VideoAmbientEffectController.SetEnabled(enabled);
            UpdateVideoAmbientEffectToggleVisual(enabled, true);
        }

        private void ShortsFeatureToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !ShortsFeatureController.IsEnabled();
            ShortsFeatureController.SetEnabled(enabled);
            UpdateShortsFeatureToggleVisual(enabled, true);
        }

        private void DiscordPresenceToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!YouTube.Discord.DiscordPresenceService.IsAvailable())
                return;
            var enabled = !YouTube.Discord.DiscordPresenceService.IsEnabled();
            YouTube.Discord.DiscordPresenceService.SetEnabled(enabled);
            UpdateDiscordPresenceToggleVisual(enabled, true);
        }

        private void UpdateDiscordPresenceToggleVisual(bool isOn, bool animate)
        {
            if (DiscordPresenceToggleTrack == null || DiscordPresenceToggleThumbTransform == null)
                return;

            if (_discordPresenceToggleStoryboard != null)
            {
                _discordPresenceToggleStoryboard.Stop();
                _discordPresenceToggleStoryboard = null;
            }

            var brush = DiscordPresenceToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White)
                        : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                DiscordPresenceToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White)
                    : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                DiscordPresenceToggleThumbTransform.X = isOn ? OnOffset : 0;
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
                To = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White)
                    : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155))
            };

            Storyboard.SetTarget(thumbAnimation, DiscordPresenceToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _discordPresenceToggleStoryboard = new Storyboard();
            _discordPresenceToggleStoryboard.Children.Add(thumbAnimation);
            _discordPresenceToggleStoryboard.Children.Add(colorAnimation);
            _discordPresenceToggleStoryboard.Begin();
        }

        private void UpdateShortsFeatureToggleVisual(bool isOn, bool animate)
        {
            if (ShortsFeatureToggleTrack == null || ShortsFeatureToggleThumbTransform == null)
                return;

            if (_shortsFeatureToggleStoryboard != null)
            {
                _shortsFeatureToggleStoryboard.Stop();
                _shortsFeatureToggleStoryboard = null;
            }

            var brush = ShortsFeatureToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                ShortsFeatureToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                ShortsFeatureToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, ShortsFeatureToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _shortsFeatureToggleStoryboard = new Storyboard();
            _shortsFeatureToggleStoryboard.Children.Add(thumbAnimation);
            _shortsFeatureToggleStoryboard.Children.Add(colorAnimation);
            _shortsFeatureToggleStoryboard.Begin();
        }

        private void UpdateGlassEffectToggleVisual(bool isOn, bool animate)
        {
            if (GlassEffectToggleTrack == null || GlassEffectToggleThumbTransform == null)
                return;

            if (_glassEffectToggleStoryboard != null)
            {
                _glassEffectToggleStoryboard.Stop();
                _glassEffectToggleStoryboard = null;
            }

            var brush = GlassEffectToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                GlassEffectToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                GlassEffectToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, GlassEffectToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _glassEffectToggleStoryboard = new Storyboard();
            _glassEffectToggleStoryboard.Children.Add(thumbAnimation);
            _glassEffectToggleStoryboard.Children.Add(colorAnimation);
            _glassEffectToggleStoryboard.Begin();
        }

        private void UpdateVideoAmbientEffectToggleVisual(bool isOn, bool animate)
        {
            if (VideoAmbientEffectToggleTrack == null || VideoAmbientEffectToggleThumbTransform == null)
                return;

            if (_videoAmbientEffectToggleStoryboard != null)
            {
                _videoAmbientEffectToggleStoryboard.Stop();
                _videoAmbientEffectToggleStoryboard = null;
            }

            var brush = VideoAmbientEffectToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                VideoAmbientEffectToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                VideoAmbientEffectToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, VideoAmbientEffectToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _videoAmbientEffectToggleStoryboard = new Storyboard();
            _videoAmbientEffectToggleStoryboard.Children.Add(thumbAnimation);
            _videoAmbientEffectToggleStoryboard.Children.Add(colorAnimation);
            _videoAmbientEffectToggleStoryboard.Begin();
        }

        private void UpdateLiveTileToggleVisual(bool isOn, bool animate)
        {
            if (LiveTileToggleTrack == null || LiveTileToggleThumbTransform == null)
                return;

            if (_liveTileToggleStoryboard != null)
            {
                _liveTileToggleStoryboard.Stop();
                _liveTileToggleStoryboard = null;
            }

            var brush = LiveTileToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                LiveTileToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                LiveTileToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, LiveTileToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _liveTileToggleStoryboard = new Storyboard();
            _liveTileToggleStoryboard.Children.Add(thumbAnimation);
            _liveTileToggleStoryboard.Children.Add(colorAnimation);
            _liveTileToggleStoryboard.Begin();
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

            if (_isLanguageSheetOpen)
            {
                AnimateLanguageBottomSheet(false);
                return;
            }

            if (_isThemeSheetOpen)
            {
                AnimateThemeBottomSheet(false);
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
            _editingThumbnailQuality = false;
            ShowQualityBottomSheet();
        }

        private void PreferredShortsQualityButton_Click(object sender, RoutedEventArgs e)
        {
            _editingShortsQuality = true;
            _editingThumbnailQuality = false;
            ShowQualityBottomSheet();
        }

        private void ThumbnailQualityButton_Click(object sender, RoutedEventArgs e)
        {
            _editingShortsQuality = false;
            _editingThumbnailQuality = true;
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

        private void SponsorMarkersToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var enabled = !SponsorBlock.AreTimelineMarkersEnabled();
            SponsorBlock.SetTimelineMarkersEnabled(enabled);
            UpdateSponsorMarkersToggleVisual(enabled, true);
        }

        private void UpdateSponsorMarkersToggleVisual(bool isOn, bool animate)
        {
            if (SponsorMarkersToggleTrack == null || SponsorMarkersToggleThumbTransform == null)
            {
                return;
            }

            if (_sponsorMarkersToggleStoryboard != null)
            {
                _sponsorMarkersToggleStoryboard.Stop();
                _sponsorMarkersToggleStoryboard = null;
            }

            var brush = SponsorMarkersToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(
                    isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155)));
                SponsorMarkersToggleTrack.Background = brush;
            }

            const double OnOffset = 20.0;
            if (!animate)
            {
                brush.Color = isOn ? App.GetThemeColor("AppPrimaryTextBrush", Colors.White) : App.GetThemeColor("AppMutedTextBrush", Color.FromArgb(255, 155, 155, 155));
                SponsorMarkersToggleThumbTransform.X = isOn ? OnOffset : 0;
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

            Storyboard.SetTarget(thumbAnimation, SponsorMarkersToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _sponsorMarkersToggleStoryboard = new Storyboard();
            _sponsorMarkersToggleStoryboard.Children.Add(thumbAnimation);
            _sponsorMarkersToggleStoryboard.Children.Add(colorAnimation);
            _sponsorMarkersToggleStoryboard.Begin();
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

            if (_editingThumbnailQuality)
            {
                var currentThumbnailQuality = VideoThumbnailController.GetSelectedQuality();
                var thumbnailOptions = VideoThumbnailController.Options;
                for (var i = 0; i < thumbnailOptions.Length; i++)
                {
                    var option = thumbnailOptions[i];
                    var button = new Button
                    {
                        Background = new SolidColorBrush(Colors.Transparent),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Padding = new Thickness(0, 4, 0, 4),
                        MinHeight = 42,
                        Margin = new Thickness(0, 0, 0, 2),
                        Tag = option.Key
                    };

                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var text = new TextBlock
                    {
                        Text = option.Key + "  (" + option.FileName + ")",
                        Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                        FontSize = 14,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    grid.Children.Add(text);

                    var check = new FontIcon
                    {
                        Glyph = "\uE73E",
                        FontFamily = new FontFamily("Segoe MDL2 Assets"),
                        FontSize = 18,
                        Foreground = new SolidColorBrush(App.GetThemeColor("AppPrimaryTextBrush", Colors.White)),
                        Visibility = string.Equals(currentThumbnailQuality, option.Key, StringComparison.OrdinalIgnoreCase)
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
                return;
            }

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
                    Text = string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase)
                        ? Localization.GetString("Auto")
                        : quality,
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

            if (_editingThumbnailQuality)
            {
                VideoThumbnailController.SetSelectedQuality(quality);
                UpdateThumbnailQualityText();
                BuildQualityOptions();
                AnimateQualityBottomSheet(false);
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
                var value = GetPreferredQualityDisplayText(PreferredVideoQualitySettingKey);
                PreferredQualityValueText.Text = string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase)
                    ? Localization.GetString("Auto")
                    : value;
            }
            if (PreferredShortsQualityValueText != null)
            {
                var value = GetPreferredQualityDisplayText(PreferredShortsQualitySettingKey);
                PreferredShortsQualityValueText.Text = string.Equals(value, "Auto", StringComparison.OrdinalIgnoreCase)
                    ? Localization.GetString("Auto")
                    : value;
            }
        }

        private void UpdateThumbnailQualityText()
        {
            if (ThumbnailQualityLabelText != null)
                ThumbnailQualityLabelText.Text = Localization.GetString("ThumbnailQuality");

            if (ThumbnailQualityValueText != null)
            {
                var option = VideoThumbnailController.GetSelectedOption();
                ThumbnailQualityValueText.Text = option != null ? option.FileName : "hqdefault.jpg";
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
                new NotificationFrequencyOption { Title = Localization.GetString("Every15Min"), Minutes = 15 },
                new NotificationFrequencyOption { Title = Localization.GetString("Every30Min"), Minutes = 30 },
                new NotificationFrequencyOption { Title = Localization.GetString("EveryHour"), Minutes = 60 },
                new NotificationFrequencyOption { Title = Localization.GetString("Every2Hours"), Minutes = 120 },
                new NotificationFrequencyOption { Title = Localization.GetString("Every6Hours"), Minutes = 360 }
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

            AboutText.Text = Localization.Format("AboutText", versionText);
        }

        private void ShowQualityBottomSheet()
        {
            if (QualitySheetTitleText != null)
            {
                QualitySheetTitleText.Text = _editingThumbnailQuality
                    ? Localization.GetString("ThumbnailQuality")
                    : Localization.GetString("Quality");
            }
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

        private void ShowLanguageBottomSheet()
        {
            UpdateLanguageText();
            BuildLanguageOptions();
            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }
            if (LanguageBottomSheetPanel != null)
            {
                LanguageBottomSheetPanel.Visibility = Visibility.Visible;
            }
            AnimateLanguageBottomSheet(true);
        }

        private void ShowThemeBottomSheet()
        {
            UpdateAppearanceTexts();
            UpdateThemeText();
            BuildThemeOptions();
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Visible;
            if (ThemeBottomSheetPanel != null)
                ThemeBottomSheetPanel.Visibility = Visibility.Visible;
            AnimateThemeBottomSheet(true);
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

            if (show && LanguageBottomSheetPanel != null && LanguageBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateLanguageBottomSheet(false);
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
                To = show ? 0 : QualityBottomSheetPanel.DismissDistance
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

        private void AnimateLanguageBottomSheet(bool show)
        {
            _isLanguageSheetOpen = show;
            if (LanguageBottomSheetTransform == null)
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
            if (show && NotificationsBottomSheetPanel != null && NotificationsBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateNotificationsBottomSheet(false);
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                To = show ? 0 : LanguageBottomSheetPanel.DismissDistance
            };

            Storyboard.SetTarget(animation, LanguageBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (LanguageBottomSheetPanel != null)
                    {
                        LanguageBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    HideOverlayIfNoSheetOpen();
                };
            }

            storyboard.Begin();
        }

        private void AnimateThemeBottomSheet(bool show)
        {
            _isThemeSheetOpen = show;
            if (ThemeBottomSheetTransform == null)
                return;

            if (show)
            {
                if (_isQualitySheetOpen) AnimateQualityBottomSheet(false);
                if (_isLanguageSheetOpen) AnimateLanguageBottomSheet(false);
                if (_isAboutSheetOpen) AnimateAboutBottomSheet(false);
                if (_isNotificationsSheetOpen) AnimateNotificationsBottomSheet(false);
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation
            {
                Duration = TimeSpan.FromMilliseconds(250),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                To = show ? 0 : ThemeBottomSheetPanel.DismissDistance
            };
            Storyboard.SetTarget(animation, ThemeBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (ThemeBottomSheetPanel != null)
                        ThemeBottomSheetPanel.Visibility = Visibility.Collapsed;
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
            if (show && LanguageBottomSheetPanel != null && LanguageBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateLanguageBottomSheet(false);
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
                To = show ? 0 : AboutBottomSheetPanel.DismissDistance
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
            if (show && LanguageBottomSheetPanel != null && LanguageBottomSheetPanel.Visibility == Visibility.Visible)
            {
                AnimateLanguageBottomSheet(false);
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
                To = show ? 0 : NotificationsBottomSheetPanel.DismissDistance
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
            if (OverlayGrid != null && !_isQualitySheetOpen && !_isLanguageSheetOpen && !_isThemeSheetOpen && !_isAboutSheetOpen && !_isNotificationsSheetOpen)
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
            if (_isLanguageSheetOpen)
            {
                AnimateLanguageBottomSheet(false);
            }
            if (_isThemeSheetOpen)
            {
                AnimateThemeBottomSheet(false);
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
                if (newY >= 0 && newY <= QualityBottomSheetPanel.DismissDistance)
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

                if (QualityBottomSheetTransform != null && QualityBottomSheetTransform.Y > QualityBottomSheetPanel.DragDismissThreshold)
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

        private void LanguageDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateLanguageBottomSheet(false);
        }

        private void LanguageDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _languageInitialY = e.GetCurrentPoint(element).Position.Y;
                _languageInitialTransformY = LanguageBottomSheetTransform != null ? LanguageBottomSheetTransform.Y : 0;
                _languageIsDragging = true;
                e.Handled = true;
            }
        }

        private void LanguageDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_languageIsDragging && LanguageBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double newY = _languageInitialTransformY + currentPoint.Position.Y - _languageInitialY;
                if (newY >= 0 && newY <= LanguageBottomSheetPanel.DismissDistance)
                {
                    LanguageBottomSheetTransform.Y = newY;
                }
                e.Handled = true;
            }
        }

        private void LanguageDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_languageIsDragging)
            {
                _languageIsDragging = false;
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (LanguageBottomSheetTransform != null && LanguageBottomSheetTransform.Y > LanguageBottomSheetPanel.DragDismissThreshold)
                {
                    AnimateLanguageBottomSheet(false);
                }
                else
                {
                    AnimateLanguageBottomSheet(true);
                }
                e.Handled = true;
            }
        }

        private void ThemeDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateThemeBottomSheet(false);
        }

        private void ThemeDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _themeInitialY = e.GetCurrentPoint(element).Position.Y;
                _themeInitialTransformY = ThemeBottomSheetTransform != null ? ThemeBottomSheetTransform.Y : 0;
                _themeIsDragging = true;
                e.Handled = true;
            }
        }

        private void ThemeDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_themeIsDragging && ThemeBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double newY = _themeInitialTransformY + currentPoint.Position.Y - _themeInitialY;
                if (newY >= 0 && newY <= ThemeBottomSheetPanel.DismissDistance)
                    ThemeBottomSheetTransform.Y = newY;
                e.Handled = true;
            }
        }

        private void ThemeDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_themeIsDragging)
            {
                _themeIsDragging = false;
                var element = sender as UIElement;
                if (element != null)
                    element.ReleasePointerCapture(e.Pointer);

                if (ThemeBottomSheetTransform != null && ThemeBottomSheetTransform.Y > ThemeBottomSheetPanel.DragDismissThreshold)
                    AnimateThemeBottomSheet(false);
                else
                    AnimateThemeBottomSheet(true);
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
                if (newY >= 0 && newY <= NotificationsBottomSheetPanel.DismissDistance)
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

                if (NotificationsBottomSheetTransform != null && NotificationsBottomSheetTransform.Y > NotificationsBottomSheetPanel.DragDismissThreshold)
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
                if (newY >= 0 && newY <= AboutBottomSheetPanel.DismissDistance)
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

                if (AboutBottomSheetTransform != null && AboutBottomSheetTransform.Y > AboutBottomSheetPanel.DragDismissThreshold)
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
