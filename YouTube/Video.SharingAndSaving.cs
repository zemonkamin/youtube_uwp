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
        private TimeSpan GetShareCurrentPosition()
        {
            try
            {
                if (CustomVideoPlayer == null)
                {
                    return TimeSpan.Zero;
                }

                var field = typeof(CustomVideoPlayer).GetField("MediaPlayer", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var mediaPlayerElement = field != null ? field.GetValue(CustomVideoPlayer) as MediaPlayerElement : null;
                if (mediaPlayerElement != null && mediaPlayerElement.MediaPlayer != null && mediaPlayerElement.MediaPlayer.PlaybackSession != null)
                {
                    return mediaPlayerElement.MediaPlayer.PlaybackSession.Position;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Share] Failed to get playback position: " + ex.Message);
            }

            return TimeSpan.Zero;
        }

        private string FormatShareTimestamp(TimeSpan value)
        {
            if (value.TotalHours >= 1)
            {
                return value.ToString(@"h\:mm\:ss");
            }

            return value.ToString(@"m\:ss");
        }

        private string BuildShareUrl()
        {
            var baseUrl = string.IsNullOrEmpty(currentVideoId) ? string.Empty : ("https://youtu.be/" + currentVideoId);
            if (string.IsNullOrEmpty(baseUrl))
            {
                return string.Empty;
            }

            if (!_shareWithTimestamp)
            {
                return baseUrl;
            }

            var seconds = Math.Max(0, (int)Math.Floor(GetShareCurrentPosition().TotalSeconds));
            return seconds > 0 ? (baseUrl + "?t=" + seconds) : baseUrl;
        }

        private void UpdateShareSheetState()
        {
            if (ShareTimestampText != null)
            {
                ShareTimestampText.Text = FormatShareTimestamp(GetShareCurrentPosition());
            }

            if (ShareUrlTextBox != null)
            {
                ShareUrlTextBox.Text = BuildShareUrl();
            }
        }

        private void ShowSharePopup(string title, string message, BitmapImage imageSource = null)
        {
            if (SharePopupTitleText != null)
            {
                SharePopupTitleText.Text = title ?? string.Empty;
            }

            if (SharePopupMessageText != null)
            {
                SharePopupMessageText.Text = message ?? string.Empty;
            }

            if (SharePopupImage != null)
            {
                SharePopupImage.Source = imageSource;
            }

            if (SharePopupImageHolder != null)
            {
                SharePopupImageHolder.Visibility = imageSource != null ? Visibility.Visible : Visibility.Collapsed;
            }

            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }

            if (SharePopupOverlay != null)
            {
                SharePopupOverlay.Visibility = Visibility.Visible;
            }

            if (SharePopupPanel != null)
            {
                // BottomSheet starts Collapsed. The legacy popup animation only moved its
                // transform and therefore animated an invisible control after Copy/Save.
                SharePopupPanel.Visibility = Visibility.Visible;
                SharePopupPanel.UpdateLayout();
            }
            if (SharePopupTransform != null)
            {
                SharePopupTransform.Y = GetSharePopupDismissDistance();
            }
            AnimateSharePopup(true);
        }

        private double GetSharePopupDismissDistance()
        {
            return SharePopupPanel != null ? SharePopupPanel.DismissDistance : 400;
        }

        private void AnimateSharePopup(bool show)
        {
            if (SharePopupTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation
            {
                To = show ? 0 : GetSharePopupDismissDistance(),
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase
                {
                    EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
                }
            };

            Storyboard.SetTarget(animation, SharePopupTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            if (!show)
            {
                storyboard.Completed += (sender, args) =>
                {
                    if (SharePopupPanel != null)
                    {
                        SharePopupPanel.Visibility = Visibility.Collapsed;
                    }
                    if (SharePopupOverlay != null)
                    {
                        SharePopupOverlay.Visibility = Visibility.Collapsed;
                    }
                    CollapseVideoOverlayIfNoSheetOpen();
                };
            }
            storyboard.Begin();
        }

        private void HideSharePopup()
        {
            if (SharePopupOverlay != null && SharePopupOverlay.Visibility == Visibility.Visible)
            {
                AnimateSharePopup(false);
            }
        }

        private void CollapseVideoOverlayIfNoSheetOpen()
        {
            if (OverlayGrid == null)
            {
                return;
            }

            var anyOpen =
                (SharePopupOverlay != null && SharePopupOverlay.Visibility == Visibility.Visible)
                || (SaveBottomSheetPanel != null && SaveBottomSheetPanel.Visibility == Visibility.Visible)
                || (ShareBottomSheetPanel != null && ShareBottomSheetPanel.Visibility == Visibility.Visible)
                || (CommentsBottomSheetPanel != null && CommentsBottomSheetPanel.Visibility == Visibility.Visible)
                || (SettingsBottomSheetPanel != null && SettingsBottomSheetPanel.Visibility == Visibility.Visible)
                || (SubscriptionMenuBottomSheetPanel != null && SubscriptionMenuBottomSheetPanel.Visibility == Visibility.Visible);

            if (!anyOpen)
            {
                OverlayGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetShareTimeToggleState()
        {
            _shareWithTimestamp = false;
            _shareTimeToggleAnimationGeneration++;

            if (_shareTimeToggleStoryboard != null)
            {
                try { _shareTimeToggleStoryboard.Stop(); } catch { }
                _shareTimeToggleStoryboard = null;
            }

            ForceShareTimeToggleVisual(false);
            UpdateShareSheetState();
            ResetShareTimeToggleVisualSoon();
        }

        private void ForceShareTimeToggleVisual(bool isOn)
        {
            try
            {
                var targetColor = isOn
                    ? App.GetThemeColor("PrimaryActionBackgroundBrush", Windows.UI.Colors.White)
                    : App.GetThemeColor("AppMutedTextBrush", Windows.UI.Color.FromArgb(255, 168, 168, 168));

                if (ShareTimeToggleKnob != null)
                {
                    ShareTimeToggleKnob.Fill = App.GetThemeBrush("ToggleKnobBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 33, 36));
                }

                if (ShareTimeToggleTrackBrush != null)
                {
                    ShareTimeToggleTrackBrush.Color = targetColor;
                }

                if (ShareTimeToggleTrack != null)
                {
                    ShareTimeToggleTrack.Background = ShareTimeToggleTrackBrush ?? new SolidColorBrush(targetColor);
                    ShareTimeToggleTrack.Opacity = 1.0;
                }

                if (ShareTimeToggleButton != null)
                {
                    ShareTimeToggleButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                    ShareTimeToggleButton.Opacity = 1.0;
                }

                if (ShareTimeToggleKnobTransform != null)
                {
                    ShareTimeToggleKnobTransform.X = isOn ? 22.0 : 0.0;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Share] Failed to force time toggle visual: " + ex.Message);
            }
        }

        private async void ResetShareTimeToggleVisualSoon()
        {
            var generation = _shareTimeToggleAnimationGeneration;
            try
            {
                await Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        if (generation == _shareTimeToggleAnimationGeneration && !_shareWithTimestamp)
                        {
                            ForceShareTimeToggleVisual(false);
                            UpdateShareSheetState();
                        }
                    });

                await Task.Delay(120);

                await Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        if (generation == _shareTimeToggleAnimationGeneration && !_shareWithTimestamp)
                        {
                            ForceShareTimeToggleVisual(false);
                            UpdateShareSheetState();
                        }
                    });
            }
            catch { }
        }

        private void UpdateShareTimeToggleVisual(bool animate = true)
        {
            try
            {
                _shareTimeToggleAnimationGeneration++;
                var generation = _shareTimeToggleAnimationGeneration;

                if (_shareTimeToggleStoryboard != null)
                {
                    try { _shareTimeToggleStoryboard.Stop(); } catch { }
                    _shareTimeToggleStoryboard = null;
                }

                var isOn = _shareWithTimestamp;
                var targetColor = isOn
                    ? App.GetThemeColor("PrimaryActionBackgroundBrush", Windows.UI.Colors.White)
                    : App.GetThemeColor("AppMutedTextBrush", Windows.UI.Color.FromArgb(255, 168, 168, 168));
                var targetX = isOn ? 22.0 : 0.0;

                if (ShareTimeToggleKnob != null)
                {
                    ShareTimeToggleKnob.Fill = App.GetThemeBrush("ToggleKnobBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 32, 33, 36));
                }

                if (!animate)
                {
                    ForceShareTimeToggleVisual(isOn);
                    return;
                }

                var storyboard = new Storyboard();
                var duration = new Duration(TimeSpan.FromMilliseconds(220));
                var easing = new CubicEase { EasingMode = EasingMode.EaseInOut };

                if (ShareTimeToggleKnobTransform != null)
                {
                    var moveAnimation = new DoubleAnimation
                    {
                        To = targetX,
                        Duration = duration,
                        EasingFunction = easing,
                        EnableDependentAnimation = true
                    };

                    Storyboard.SetTarget(moveAnimation, ShareTimeToggleKnobTransform);
                    Storyboard.SetTargetProperty(moveAnimation, "X");
                    storyboard.Children.Add(moveAnimation);
                }

                if (ShareTimeToggleTrackBrush != null)
                {
                    var trackColorAnimation = new ColorAnimation
                    {
                        To = targetColor,
                        Duration = duration,
                        EnableDependentAnimation = true
                    };

                    Storyboard.SetTarget(trackColorAnimation, ShareTimeToggleTrackBrush);
                    Storyboard.SetTargetProperty(trackColorAnimation, "Color");
                    storyboard.Children.Add(trackColorAnimation);
                }

                if (storyboard.Children.Count > 0)
                {
                    _shareTimeToggleStoryboard = storyboard;
                    storyboard.Completed += (sender, args) =>
                    {
                        if (generation != _shareTimeToggleAnimationGeneration)
                        {
                            return;
                        }

                        var finalState = _shareSheetIsOpen && _shareWithTimestamp && isOn;
                        ForceShareTimeToggleVisual(finalState);

                        if (ReferenceEquals(_shareTimeToggleStoryboard, storyboard))
                        {
                            _shareTimeToggleStoryboard = null;
                        }
                    };
                    storyboard.Begin();
                }
                else
                {
                    ForceShareTimeToggleVisual(isOn);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Share] Failed to update time toggle visual: " + ex.Message);
            }
        }

        private void ShareTimeToggleButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_shareWithTimestamp)
            {
                ForceShareTimeToggleVisual(false);
            }
        }

        private void ShareTimeToggleButton_Unloaded(object sender, RoutedEventArgs e)
        {
            ResetShareTimeToggleState();
        }

        private async void ShowQrCodeButton_Click(object sender, RoutedEventArgs e)
        {
            var shareUrl = BuildShareUrl();
            if (string.IsNullOrEmpty(shareUrl))
            {
                return;
            }

            try
            {
                var encoded = Uri.EscapeDataString(shareUrl);
                var qrUrl = "https://api.qrserver.com/v1/create-qr-code/?size=448x448&margin=0&data=" + encoded;
                var bitmap = new BitmapImage(new Uri(qrUrl));
                AnimateShareBottomSheet(false);
                ShowSharePopup(Localization.GetString("QrCode"), shareUrl, bitmap);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to show QR code: " + ex.Message);
                AnimateShareBottomSheet(false);
                ShowSharePopup(Localization.GetString("QrCode"), shareUrl);
            }
        }

        private void ShareTimeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _shareWithTimestamp = !_shareWithTimestamp;
            UpdateShareTimeToggleVisual(true);
            UpdateShareSheetState();
        }

        private void ShareButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShareBottomSheet();
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            await ShowSaveBottomSheetAsync();
        }

        private async Task ShowSaveBottomSheetAsync()
        {
            if (string.IsNullOrWhiteSpace(currentVideoId))
            {
                return;
            }

            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                ShowSharePopup(Localization.GetString("Save"), Localization.GetString("NotSignedIn"));
                return;
            }

            var videoId = currentVideoId;
            var loadGeneration = ++_savePlaylistLoadGeneration;
            _saveSheetIsOpen = true;
            _savePlaylistItems.Clear();
            SaveSheetTitleText.Text = Localization.GetString("SelectPlaylist");
            SavePlaylistsEmptyText.Text = Localization.GetString("NoData");
            SavePlaylistsEmptyText.Visibility = Visibility.Collapsed;
            SavePlaylistsScrollViewer.Visibility = Visibility.Collapsed;
            SavePlaylistsLoadingPanel.Visibility = Visibility.Visible;
            SavePlaylistsLoadingRing.IsActive = true;

            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }
            SaveBottomSheetPanel.Visibility = Visibility.Visible;
            SaveBottomSheetPanel.UpdateLayout();
            SaveBottomSheetTransform.Y = GetSaveSheetDismissDistance();
            AnimateSaveBottomSheet(true);

            List<PlaylistSaveState> states = null;
            try
            {
                states = await global::Config.GetSavePlaylistStatesAsync(refreshToken, videoId, 30);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SavePlaylist] Load failed: " + ex.Message);
            }

            if (!_saveSheetIsOpen
                || loadGeneration != _savePlaylistLoadGeneration
                || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            SavePlaylistsLoadingRing.IsActive = false;
            SavePlaylistsLoadingPanel.Visibility = Visibility.Collapsed;
            if (states != null)
            {
                foreach (var state in states)
                {
                    if (state != null && state.Playlist != null)
                    {
                        _savePlaylistItems.Add(new SavePlaylistItemViewModel(
                            state.Playlist,
                            state.ContainsVideo));
                    }
                }
            }

            SavePlaylistsScrollViewer.Visibility = _savePlaylistItems.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SavePlaylistsEmptyText.Visibility = _savePlaylistItems.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            SaveBottomSheetPanel.UpdateLayout();
        }

        private double GetSaveSheetDismissDistance()
        {
            return SaveBottomSheetPanel != null ? SaveBottomSheetPanel.DismissDistance : 560;
        }

        private void AnimateSaveBottomSheet(bool show)
        {
            if (SaveBottomSheetTransform == null)
            {
                return;
            }

            if (show)
            {
                _saveSheetIsOpen = true;
                SaveBottomSheetPanel.Visibility = Visibility.Visible;
            }
            else
            {
                _saveSheetIsOpen = false;
                _savePlaylistLoadGeneration++;
                if (SavePlaylistsLoadingRing != null)
                {
                    SavePlaylistsLoadingRing.IsActive = false;
                }
            }

            var animation = new DoubleAnimation
            {
                To = show ? 0 : GetSaveSheetDismissDistance(),
                Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                EasingFunction = new CircleEase()
            };
            if (!show)
            {
                animation.Completed += (s, e) =>
                {
                    if (SaveBottomSheetPanel != null)
                    {
                        SaveBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    _savePlaylistItems.Clear();
                    CollapseVideoOverlayIfNoSheetOpen();
                };
            }

            Storyboard.SetTarget(animation, SaveBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private async void SavePlaylistRow_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as SavePlaylistItemViewModel : null;
            if (item == null || string.IsNullOrWhiteSpace(item.PlaylistId) || string.IsNullOrWhiteSpace(currentVideoId))
            {
                return;
            }

            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;
            var videoId = currentVideoId;
            var shouldSave = !item.IsSaved;
            item.SetBusy(true);

            var success = false;
            try
            {
                success = await global::Config.SetVideoSavedToPlaylistAsync(
                    refreshToken,
                    item.PlaylistId,
                    videoId,
                    shouldSave);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SavePlaylist] Update failed: " + ex.Message);
            }

            item.SetBusy(false);
            if (!string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            AnimateSaveBottomSheet(false);
            if (success)
            {
                item.SetSaved(shouldSave);
                if (_mainSaveStateSupportedByNext)
                {
                    SetImageSource(
                        SaveActionIcon,
                        _savePlaylistItems.Any(playlist => playlist != null && playlist.IsSaved)
                            ? "Assets/save_clicked.png"
                            : "Assets/save.png");
                }
                var message = Localization.Format(
                    shouldSave ? "VideoAddedToPlaylistFormat" : "VideoRemovedFromPlaylistFormat",
                    item.Title);
                ShowSharePopup(Localization.GetString("Save"), message);
            }
            else
            {
                ShowSharePopup(Localization.GetString("Save"), Localization.GetString("PlaylistUpdateFailed"));
            }
        }

        private void ShowShareBottomSheet()
        {
            if (!string.IsNullOrEmpty(currentVideoId))
            {
                if (ShareVideoTitleText != null)
                    ShareVideoTitleText.Text = VideoTitleText?.Text ?? string.Empty;

                if (ShareChannelText != null)
                    ShareChannelText.Text = VideoAuthorText?.Text ?? string.Empty;

                _shareSheetIsOpen = true;
                ResetShareTimeToggleState();

                if (OverlayGrid != null)
                    OverlayGrid.Visibility = Visibility.Visible;

                if (ShareBottomSheetPanel != null)
                {
                    ShareBottomSheetPanel.Visibility = Visibility.Visible;
                    ShareBottomSheetPanel.UpdateLayout();
                    if (ShareBottomSheetTransform != null)
                        ShareBottomSheetTransform.Y = GetShareSheetDismissDistance();

                    ResetShareTimeToggleState();
                    AnimateShareBottomSheet(true);
                    ResetShareTimeToggleVisualSoon();
                }
            }
        }

        private double GetShareSheetDismissDistance()
        {
            return ShareBottomSheetPanel != null ? ShareBottomSheetPanel.DismissDistance : 400;
        }

        private void AnimateShareBottomSheet(bool show)
        {
            if (ShareBottomSheetTransform == null)
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
                animation.To = GetShareSheetDismissDistance();
                _shareSheetIsOpen = false;
                ResetShareTimeToggleState();

                animation.Completed += (s, args) =>
                {
                    if (ShareBottomSheetPanel != null)
                        ShareBottomSheetPanel.Visibility = Visibility.Collapsed;
                    _shareSheetIsOpen = false;
                    ResetShareTimeToggleState();
                    CollapseVideoOverlayIfNoSheetOpen();
                };
            }

            Storyboard.SetTarget(animation, ShareBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private async void VideoCard_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var videoItem = button?.DataContext as RelatedVideoCardItem;

            if (videoItem != null && !string.IsNullOrEmpty(videoItem.video_id))
            {
                var videoId = videoItem.video_id;
                System.Diagnostics.Debug.WriteLine($"[Video] Switching to video in place: {videoId}");

                // Update this page in place instead of pushing a new one — carrying the card's
                // playlist (a mix / "jam" card has one) so the queue keeps working.
                var ignored = SwitchToVideoAsync(videoId, videoItem.playlist_id, null);
            }
        }

        private async Task<List<RelatedVideoCardItem>> LoadRelatedVideosAsync(string videoId)
        {
            var relatedVideos = new List<RelatedVideoCardItem>();

            try
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RelatedVideos] Loading related videos for: {videoId}"
                );

                JsonObject nextRoot = null;
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;
                if (!string.IsNullOrWhiteSpace(refreshToken))
                {
                    var accessToken = await Config.RefreshAccessTokenAsync(refreshToken)
                        .ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        var response = await GetAuthenticatedNextResponseAsync(
                            videoId, accessToken, false).ConfigureAwait(false);
                        if (response != null) nextRoot = response.Root;
                    }
                }

                if (nextRoot == null)
                {
                    var response = await PostInnertubeJsonAsync(
                        "next", BuildNextPayload(videoId)).ConfigureAwait(false);
                    nextRoot = response.Root;
                }

                // Extract related videos by walking the JSON
                ExtractRelatedVideosFromJson(nextRoot, relatedVideos);

                // A successful TV response can still omit the recommendation shelf for a
                // particular account/video. Keep playback-page recommendations working by
                // falling back to the ordinary WEB shape only when TV yielded no cards at all.
                if (relatedVideos.Count == 0)
                {
                    var webResponse = await PostInnertubeJsonAsync(
                        "next", BuildNextPayload(videoId)).ConfigureAwait(false);
                    if (!ReferenceEquals(nextRoot, webResponse.Root))
                        ExtractRelatedVideosFromJson(webResponse.Root, relatedVideos);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[RelatedVideos] Extracted {relatedVideos.Count} videos"
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RelatedVideos] Error: {ex.Message}");
            }

            return relatedVideos;
        }

    }
}
