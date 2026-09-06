using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Data.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Video
    {
        private void SettingsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Close settings bottom sheet when tapping drag area
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void ShowSettingsBottomSheet()
        {
            // Reset to the Video page's single settings UI. The same live controls are moved
            // into a top-level popup when the player is fullscreen; CustomVideoPlayer no longer
            // owns a second settings implementation.
            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Visible;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null)
                AudioTrackSettingsPanel.Visibility = Visibility.Collapsed;
            if (DownloadSettingsPanel != null)
                DownloadSettingsPanel.Visibility = Visibility.Collapsed;

            UpdateSettingsRowValues();

            if (SubtitlesButton != null)
                SubtitlesButton.Visibility = (_subtitleTracks != null && _subtitleTracks.HasAny)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (_offlineMode)
            {
                if (QualityButton != null) QualityButton.Visibility = Visibility.Collapsed;
                if (AudioTrackButton != null) AudioTrackButton.Visibility = Visibility.Collapsed;
                if (SubtitlesButton != null) SubtitlesButton.Visibility = Visibility.Collapsed;
            }

            if (AudioTrackButton != null)
                AudioTrackButton.Visibility = (!_offlineMode
                    && _availableAudioTracks != null && _availableAudioTracks.Count > 1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            UpdateSettingsBottomSheetHeight();
            if (!_offlineMode)
                UpdateAudioTrackButtonVisibilityAsync();

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                ShowSettingsBottomSheetInFullscreenPopup();
                return;
            }

            RestoreSettingsBottomSheetFromFullscreenPopup();

            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Visible;

            if (SettingsBottomSheetPanel != null)
            {
                SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                AnimateSettingsBottomSheet(true);
            }
        }

        private void ShowSettingsBottomSheetInFullscreenPopup()
        {
            if (SettingsBottomSheetPanel == null)
            {
                return;
            }

            if (_fullscreenSettingsPopup != null && _fullscreenSettingsPopup.IsOpen)
            {
                var currentBounds = Window.Current.Bounds;
                UpdateFullscreenSettingsPopupBounds(new Windows.Foundation.Size(currentBounds.Width, currentBounds.Height));
                SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                AnimateSettingsBottomSheet(true);
                return;
            }

            var currentParent = SettingsBottomSheetPanel.Parent as Panel;
            if (currentParent == null)
            {
                return;
            }

            _settingsBottomSheetOriginalParent = currentParent;
            _settingsBottomSheetOriginalIndex = currentParent.Children.IndexOf(SettingsBottomSheetPanel);
            currentParent.Children.Remove(SettingsBottomSheetPanel);

            var bounds = Window.Current.Bounds;
            _fullscreenSettingsPopupRoot = new Grid
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                RequestedTheme = App.GetCurrentElementTheme()
            };

            var dimOverlay = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0))
            };
            dimOverlay.Tapped += FullscreenSettingsOverlay_Tapped;
            _fullscreenSettingsPopupRoot.Children.Add(dimOverlay);

            SettingsBottomSheetPanel.Visibility = Visibility.Visible;
            SettingsBottomSheetPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SettingsBottomSheetPanel.VerticalAlignment = VerticalAlignment.Bottom;
            UpdateSettingsBottomSheetHeight();
            if (SettingsBottomSheetTransform != null)
            {
                SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
            }
            _fullscreenSettingsPopupRoot.Children.Add(SettingsBottomSheetPanel);

            _fullscreenSettingsPopup = new Popup
            {
                Child = _fullscreenSettingsPopupRoot,
                HorizontalOffset = 0,
                VerticalOffset = 0,
                Width = bounds.Width,
                Height = bounds.Height,
                IsHitTestVisible = true
            };
            _fullscreenSettingsPopup.IsOpen = true;

            AnimateSettingsBottomSheet(true);
        }

        private void FullscreenSettingsOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSettingsBottomSheet(false);
            e.Handled = true;
        }

        private void UpdateFullscreenSettingsPopupBounds(Windows.Foundation.Size size)
        {
            if (_fullscreenSettingsPopup == null || !_fullscreenSettingsPopup.IsOpen)
            {
                return;
            }

            _fullscreenSettingsPopup.Width = size.Width;
            _fullscreenSettingsPopup.Height = size.Height;
            if (_fullscreenSettingsPopupRoot != null)
            {
                _fullscreenSettingsPopupRoot.Width = size.Width;
                _fullscreenSettingsPopupRoot.Height = size.Height;
            }
        }

        private void RestoreSettingsBottomSheetFromFullscreenPopup()
        {
            if (_fullscreenSettingsPopup == null && _settingsBottomSheetOriginalParent == null)
            {
                return;
            }

            try
            {
                var currentParent = SettingsBottomSheetPanel != null
                    ? SettingsBottomSheetPanel.Parent as Panel
                    : null;
                if (currentParent != null)
                {
                    currentParent.Children.Remove(SettingsBottomSheetPanel);
                }

                if (SettingsBottomSheetPanel != null && _settingsBottomSheetOriginalParent != null)
                {
                    var insertIndex = _settingsBottomSheetOriginalIndex;
                    if (insertIndex < 0 || insertIndex > _settingsBottomSheetOriginalParent.Children.Count)
                    {
                        insertIndex = _settingsBottomSheetOriginalParent.Children.Count;
                    }
                    _settingsBottomSheetOriginalParent.Children.Insert(insertIndex, SettingsBottomSheetPanel);
                    SettingsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (SettingsBottomSheetTransform != null)
                    {
                        SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
                    }
                }
            }
            finally
            {
                if (_fullscreenSettingsPopup != null)
                {
                    _fullscreenSettingsPopup.IsOpen = false;
                    _fullscreenSettingsPopup.Child = null;
                }
                _fullscreenSettingsPopup = null;
                _fullscreenSettingsPopupRoot = null;
                _settingsBottomSheetOriginalParent = null;
                _settingsBottomSheetOriginalIndex = -1;
            }
        }

        private double GetSettingsBottomSheetHiddenOffset()
        {
            if (SettingsBottomSheetPanel == null)
            {
                return 214;
            }

            var height = SettingsBottomSheetPanel.Height;
            if (double.IsNaN(height) || height <= 0)
            {
                height = SettingsBottomSheetPanel.ActualHeight;
            }

            return (height > 0 ? height : 204) + 10;
        }

        // Same sizing rule as youtube-ios/YTSettingsSheet: grip + visible page content +
        // bottom padding, capped at 70% of the current window. The page ScrollViewers handle
        // lists that exceed the cap; the main page grows and shrinks with its visible rows.
        private void UpdateSettingsBottomSheetHeight()
        {
            if (SettingsBottomSheetPanel == null || Window.Current == null)
            {
                return;
            }

            FrameworkElement content = null;
            if (MainSettingsPanel != null && MainSettingsPanel.Visibility == Visibility.Visible)
            {
                content = MainSettingsPanel;
            }
            else if (QualitySettingsPanel != null && QualitySettingsPanel.Visibility == Visibility.Visible)
            {
                content = QualitySettingsPanel.Content as FrameworkElement;
            }
            else if (SpeedSettingsPanel != null && SpeedSettingsPanel.Visibility == Visibility.Visible)
            {
                content = SpeedSettingsPanel.Content as FrameworkElement;
            }
            else if (AudioTrackSettingsPanel != null && AudioTrackSettingsPanel.Visibility == Visibility.Visible)
            {
                content = AudioTrackSettingsPanel.Content as FrameworkElement;
            }
            else if (SubtitlesSettingsPanel != null && SubtitlesSettingsPanel.Visibility == Visibility.Visible)
            {
                content = SubtitlesSettingsPanel.Content as FrameworkElement;
            }
            else if (DownloadSettingsPanel != null && DownloadSettingsPanel.Visibility == Visibility.Visible)
            {
                content = DownloadSettingsPanel.Content as FrameworkElement;
            }

            if (content == null)
            {
                return;
            }

            var bounds = Window.Current.Bounds;
            var innerWidth = Math.Max(0, bounds.Width - 60);
            content.Measure(new Windows.Foundation.Size(innerWidth, double.PositiveInfinity));

            // The shared BottomSheet template has a 24px handle row. Keep this in sync
            // with the template: using the old 40px value stretched the content row and
            // left a false 16px gap below both video settings and download qualities.
            var contentHeight = 24 + content.DesiredSize.Height + 20;
            var maxHeight = Math.Max(100, bounds.Height * 0.70);
            var newHeight = Math.Min(contentHeight, maxHeight);
            var wasHidden = SettingsBottomSheetPanel.Visibility != Visibility.Visible;

            SettingsBottomSheetPanel.Height = Math.Max(60, Math.Ceiling(newHeight));
            if (wasHidden && SettingsBottomSheetTransform != null)
            {
                SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
            }
        }

        private void AnimateSettingsBottomSheet(bool show)
        {
            if (SettingsBottomSheetTransform == null)
                return;

            var storyboard = new Storyboard();
            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0;
            }
            else
            {
                animation.To = GetSettingsBottomSheetHiddenOffset();

                // Hide panel after animation completes
                animation.Completed += (s, args) =>
                {
                    if (SettingsBottomSheetPanel != null)
                        SettingsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    RestoreSettingsBottomSheetFromFullscreenPopup();
                };
            }

            Storyboard.SetTarget(animation, SettingsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void SettingsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var pointer = e.Pointer;
            if (element.CapturePointer(pointer))
            {
                _settingsInitialY = e.GetCurrentPoint(element).Position.Y;
                _settingsInitialTransformY = SettingsBottomSheetTransform != null ? SettingsBottomSheetTransform.Y : 0;
                _settingsIsDragging = true;
                e.Handled = true;
            }
        }

        private void SettingsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_settingsIsDragging || SettingsBottomSheetTransform == null)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentPoint = e.GetCurrentPoint(element);
            double dragOffset = currentPoint.Position.Y - _settingsInitialY;
            double newY = _settingsInitialTransformY + dragOffset;

            var hiddenOffset = GetSettingsBottomSheetHiddenOffset();
            if (newY >= 0 && newY <= hiddenOffset)
            {
                SettingsBottomSheetTransform.Y = newY;
            }

            e.Handled = true;
        }

        private void SettingsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_settingsIsDragging)
            {
                return;
            }

            _settingsIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SettingsBottomSheetTransform != null
                && SettingsBottomSheetTransform.Y > GetSettingsBottomSheetHiddenOffset() / 2)
            {
                AnimateSettingsBottomSheet(false);
                if (OverlayGrid != null)
                {
                    OverlayGrid.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                AnimateSettingsBottomSheet(true);
            }

            e.Handled = true;
        }

        private async void QualityButton_Click(object sender, RoutedEventArgs e)
        {
            if (QualityOptionsPanel == null)
            {
                return;
            }

            QualityOptionsPanel.Children.Clear();

            // Show the quality panel immediately; it is populated below.
            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Visible;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;

            // Highlight what is actually playing, not the raw per-video override. With a preferred
            // quality set in Settings the override is empty yet the video plays at that quality —
            // checkmarking "Auto" then was misleading. The effective tag reflects the real pick.
            var effective = GetEffectiveVideoQualityTag();

            // Auto is always offered (defaults to the reliable muxed/HLS pick).
            AddQualityOptionButton("Auto", string.IsNullOrWhiteSpace(effective));

            // Offer every height this video actually provides in H.264: muxed progressive
            // (itag 18 = 360p, itag 22 = 720p) AND adaptive video-only avc1 (up to 1080p),
            // which the on-the-fly DASH demuxer plays by pairing it with the AAC audio track.
            try
            {
                var heights = await GetAvailableQualityTagsAsync(currentVideoId);
                foreach (var height in heights)
                {
                    AddQualityOptionButton(height + "p", string.Equals(effective, height, StringComparison.Ordinal));
                }
                UpdateSettingsBottomSheetHeight();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to populate quality menu: " + ex.Message);
            }
        }

        // A settings-menu row with a leading checkmark column (like the Settings quality
        // picker): the check shows on the current item, the label sits to its right. Shared by the
        // quality and speed pickers so they look identical.
        private Button MakeCheckableOptionButton(string label, bool isCurrent, Action onClick)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var labelText = new TextBlock
            {
                Text = label,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelText, 1);
            grid.Children.Add(labelText);

            button.Content = grid;
            if (onClick != null)
            {
                button.Click += (s, args) => onClick();
            }

            return button;
        }

        private Grid MakeDownloadOptionRow(
            string quality,
            string label,
            bool isCurrent,
            bool isActive,
            Action onClick,
            Action<Button> onCancel)
        {
            var row = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            row.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = new GridLength(1, GridUnitType.Star)
            });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var optionButton = MakeCheckableOptionButton(label, isCurrent, onClick);
            Grid.SetColumn(optionButton, 0);
            row.Children.Add(optionButton);

            var cancelButton = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush")
                    ?? new SolidColorBrush(Windows.UI.Colors.White)),
                Content = new FontIcon
                {
                    Glyph = "\uE711",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 14
                },
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Stretch,
                MinWidth = 0,
                Width = 44,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 0, 4),
                Visibility = isActive ? Visibility.Visible : Visibility.Collapsed
            };
            ToolTipService.SetToolTip(cancelButton, Localization.GetString("Cancel"));
            if (onCancel != null)
            {
                cancelButton.Click += (sender, args) => onCancel((Button)sender);
            }
            Grid.SetColumn(cancelButton, 1);
            row.Children.Add(cancelButton);

            var contentGrid = optionButton.Content as Grid;
            _downloadOptionRows.Add(new DownloadOptionRowState
            {
                Quality = quality,
                OptionButton = optionButton,
                Label = contentGrid == null
                    ? null
                    : contentGrid.Children.OfType<TextBlock>().FirstOrDefault(),
                Check = contentGrid == null
                    ? null
                    : contentGrid.Children.OfType<FontIcon>().FirstOrDefault(),
                CancelButton = cancelButton
            });

            return row;
        }

        private void AddQualityOptionButton(string quality, bool isCurrent)
        {
            if (QualityOptionsPanel == null)
            {
                return;
            }

            // A leading check column (like the Settings quality picker) marks the current pick;
            // the label sits to its right.
            var button = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var label = new TextBlock
            {
                Text = string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase) ? Localization.GetString("Auto") : quality,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            button.Content = grid;

            string selectedQuality = quality;
            button.Click += (s, args) =>
            {
                ChangeQuality(selectedQuality);

                AnimateSettingsBottomSheet(false);
                if (OverlayGrid != null)
                    OverlayGrid.Visibility = Visibility.Collapsed;
            };

            QualityOptionsPanel.Children.Add(button);
        }

        // Distinct H.264 heights this video offers (from the ANDROID_VR response), ascending:
        // muxed progressive (itag 18/22) plus adaptive video-only avc1 (up to 1080p). The
        // adaptive heights are playable because the DASH demuxer muxes them with AAC audio.
        private async Task<List<string>> GetAvailableQualityTagsAsync(string videoId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return result;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return result;
                }

                var androidRoot = JsonValue.Parse(androidJson).GetObject();
                var formats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, formats);

                var heights = new SortedSet<int>();
                foreach (var f in formats)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.Url) || f.QualityTier <= 0)
                    {
                        continue;
                    }

                    var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                    if (mime.IndexOf("video/mp4") < 0)
                    {
                        continue;
                    }

                    // Muxed progressive (video+audio, H.264/AAC) — plays directly.
                    var isMuxed = !f.IsAdaptive && f.HasAudio && f.HasVideo;
                    // Adaptive video-only H.264 — plays via the DASH demuxer + AAC audio track.
                    var isAvcVideoOnly = f.IsAdaptive && f.HasVideo && !f.HasAudio && mime.IndexOf("avc1") >= 0;

                    if (isMuxed || isAvcVideoOnly)
                    {
                        heights.Add(f.QualityTier);
                    }
                }

                foreach (var h in heights)
                {
                    result.Add(h.ToString());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetAvailableQualityTagsAsync failed: " + ex.Message);
            }

            return result;
        }

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            // Show speed options
            if (SpeedOptionsPanel != null)
            {
                SpeedOptionsPanel.Children.Clear();

                // Add speed options with a leading checkmark on the current one, matching the
                // quality picker.
                var speeds = new[]
                {
                    0.25, 0.5, 0.75, 1.0
                    // Windows 10 Mobile ignores PlaybackRate > 1 for a MediaStreamSource.
                    // youtube-ios can offer these because AVPlayer changes its media clock:
                    // , 1.25, 1.5, 1.75, 2.0
                };
                foreach (var speed in speeds)
                {
                    var label = speed.ToString("0.##",
                        System.Globalization.CultureInfo.InvariantCulture) + "x";
                    var isCurrent = Math.Abs(speed - currentSpeed) < 0.001;

                    double selectedSpeed = speed;
                    var button = MakeCheckableOptionButton(label, isCurrent, () =>
                    {
                        ChangeSpeed(selectedSpeed);
                        AnimateSettingsBottomSheet(false);
                        if (OverlayGrid != null)
                            OverlayGrid.Visibility = Visibility.Collapsed;
                    });

                    SpeedOptionsPanel.Children.Add(button);
                }

                // Show speed panel, hide main panel
                if (MainSettingsPanel != null)
                    MainSettingsPanel.Visibility = Visibility.Collapsed;
                if (QualitySettingsPanel != null)
                    QualitySettingsPanel.Visibility = Visibility.Collapsed;
                if (SpeedSettingsPanel != null)
                    SpeedSettingsPanel.Visibility = Visibility.Visible;
                UpdateSettingsBottomSheetHeight();
            }
        }

        // --- Subtitles ------------------------------------------------------------------------

        private bool _showingSubtitleTranslations;

        // The player holds the list and the selection; the sheet only renders them.
        private Subtitles.TrackList _subtitleTracks
        {
            get
            {
                return CustomVideoPlayer != null
                    ? CustomVideoPlayer.SubtitleTracks
                    : new Subtitles.TrackList();
            }
        }

        private Subtitles.SubtitleTrack _activeSubtitleTrack
        {
            get { return CustomVideoPlayer != null ? CustomVideoPlayer.ActiveSubtitleTrack : null; }
        }

    }
}
