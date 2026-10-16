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
        private void VideoPage_BackRequested(object sender, BackRequestedEventArgs e)
        {
            if (!Frame.CanGoBack)
            {
                return;
            }

            e.Handled = true;

            // Back closes the video for good. Keeping playback alive is the minimize gesture's job
            // (the button in the player's top-left corner, or a swipe down from its top edge).
            Frame.GoBack();
        }

        private void CustomVideoPlayer_MinimizeRequested(object sender, EventArgs e)
        {
            MinimizeToMiniPlayer();
        }

        // Hands the player to the floating mini-player and leaves the page, so playback continues.
        private void MinimizeToMiniPlayer()
        {
            if (Frame == null || !Frame.CanGoBack)
            {
                return;
            }

            TryMinimizeToMiniPlayer();
            Frame.GoBack();
        }

        // Reparents the live player into the mini-player when eligible, and marks the page so its
        // teardown leaves that player alone.
        private void TryMinimizeToMiniPlayer()
        {
            try
            {
                if (CustomVideoPlayer == null || !CustomVideoPlayer.CanMinimizeToMiniPlayer)
                {
                    return;
                }

                // Keep only playlist navigation callbacks while the cached page is behind the
                // mini-player. They are needed so MediaEnded / SMTC next/previous can load the
                // next full Video state while the same player remains reparented in MiniPlayer.
                DetachPlayerEventHandlers();
                AttachBackgroundPlaylistEventHandlers();
                _minimizedToMiniPlayer = true;
                _orientationFullscreenGeneration++;

                // Video_Unloaded normally drops this, but it runs after the window has already
                // been resized for the mini-player — too late to stop the layout reacting.
                UnsubscribeWindowSizeChanged();

                var frame = this.Frame;
                MiniPlayer.DetachFromParent(CustomVideoPlayer);
                MiniPlayer.Show(
                    CustomVideoPlayer,
                    _currentVideoPlayerAspectRatio,
                    RestoreFromMiniPlayer,
                    () =>
                    {
                        // Closing the mini-player is a real end of this hand-off. Do not leave the
                        // cached Video page latched in mini mode or a later navigation would keep
                        // its player detached from VideoPlayerContainer.
                        _minimizedToMiniPlayer = false;
                        DetachPlayerEventHandlers();
                        try { if (frame != null) frame.ForwardStack.Clear(); } catch { }
                    });
                System.Diagnostics.Debug.WriteLine("[Video] Minimized to mini-player");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Minimize to mini-player failed: " + ex.Message);
                _minimizedToMiniPlayer = false;
            }
        }

        private void AttachPlayerEventHandlers()
        {
            if (CustomVideoPlayer == null)
            {
                return;
            }

            DetachPlayerEventHandlers();
            CustomVideoPlayer.MediaFailed += CustomVideoPlayer_MediaFailed;
            CustomVideoPlayer.SettingsRequested += CustomVideoPlayer_SettingsRequested;
            CustomVideoPlayer.FullscreenStateChanged += CustomVideoPlayer_FullscreenStateChanged;
            CustomVideoPlayer.VideoEnded += CustomVideoPlayer_VideoEnded;
            CustomVideoPlayer.NextRequested += CustomVideoPlayer_NextRequested;
            CustomVideoPlayer.PreviousRequested += CustomVideoPlayer_PreviousRequested;
            CustomVideoPlayer.PlaybackStalling += CustomVideoPlayer_PlaybackStalling;
            CustomVideoPlayer.PlaybackRecoveryRequested += CustomVideoPlayer_PlaybackRecoveryRequested;
            CustomVideoPlayer.MinimizeRequested += CustomVideoPlayer_MinimizeRequested;
            CustomVideoPlayer.RelatedVideoRequested += CustomVideoPlayer_RelatedVideoRequested;
        }

        private void AttachBackgroundPlaylistEventHandlers()
        {
            if (CustomVideoPlayer == null)
            {
                return;
            }

            CustomVideoPlayer.VideoEnded -= CustomVideoPlayer_VideoEnded;
            CustomVideoPlayer.NextRequested -= CustomVideoPlayer_NextRequested;
            CustomVideoPlayer.PreviousRequested -= CustomVideoPlayer_PreviousRequested;
            CustomVideoPlayer.PlaybackRecoveryRequested -= CustomVideoPlayer_PlaybackRecoveryRequested;
            CustomVideoPlayer.VideoEnded += CustomVideoPlayer_VideoEnded;
            CustomVideoPlayer.NextRequested += CustomVideoPlayer_NextRequested;
            CustomVideoPlayer.PreviousRequested += CustomVideoPlayer_PreviousRequested;
            CustomVideoPlayer.PlaybackRecoveryRequested += CustomVideoPlayer_PlaybackRecoveryRequested;
        }

        private void DetachPlayerEventHandlers()
        {
            if (CustomVideoPlayer == null)
            {
                return;
            }

            try
            {
                CustomVideoPlayer.MediaFailed -= CustomVideoPlayer_MediaFailed;
                CustomVideoPlayer.SettingsRequested -= CustomVideoPlayer_SettingsRequested;
                CustomVideoPlayer.FullscreenStateChanged -= CustomVideoPlayer_FullscreenStateChanged;
                CustomVideoPlayer.VideoEnded -= CustomVideoPlayer_VideoEnded;
                CustomVideoPlayer.NextRequested -= CustomVideoPlayer_NextRequested;
                CustomVideoPlayer.PreviousRequested -= CustomVideoPlayer_PreviousRequested;
                CustomVideoPlayer.PlaybackStalling -= CustomVideoPlayer_PlaybackStalling;
                CustomVideoPlayer.PlaybackRecoveryRequested -= CustomVideoPlayer_PlaybackRecoveryRequested;
                CustomVideoPlayer.MinimizeRequested -= CustomVideoPlayer_MinimizeRequested;
                CustomVideoPlayer.RelatedVideoRequested -= CustomVideoPlayer_RelatedVideoRequested;
            }
            catch { }
        }

        private void ActivateDisplayRequest()
        {
            try
            {
                if (_displayRequest == null)
                {
                    _displayRequest = new Windows.System.Display.DisplayRequest();
                    _displayRequest.RequestActive();
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Display request activated - preventing sleep mode"
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Error activating display request: {ex.Message}"
                );
            }
        }

        private void ReleaseDisplayRequest()
        {
            try
            {
                if (_displayRequest != null)
                {
                    _displayRequest.RequestRelease();
                    _displayRequest = null;
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Display request released - sleep mode allowed"
                    );
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Error releasing display request: {ex.Message}"
                );
            }
        }

        // Invoked when the user taps the mini-player's free area. Prefer the original forward entry
        // when it is still intact; otherwise navigate directly to this Required-cached Video page.
        private async void RestoreFromMiniPlayer()
        {
            try
            {
                // Desktop: give the window its normal size back BEFORE navigating, so the page is
                // measured full width instead of at the 360px compact-overlay width.
                if (MiniPlayer.IsCompactOverlayActive)
                {
                    await MiniPlayer.LeaveCompactOverlayAsync();
                }

                if (Frame != null)
                {
                    _restoringFromMiniPlayer = true;

                    // GoForward is only safe when the next entry really is this Video page. Any
                    // navigation performed while the mini-player is visible can replace/clear the
                    // forward stack; blindly calling GoForward could then open a different page.
                    var canReturnThroughForwardStack = false;
                    try
                    {
                        // Immediately after minimizing there is exactly one forward entry: this
                        // Video page. If the user navigated elsewhere while the mini-player was
                        // visible, Frame.Navigate clears/replaces that forward history, so do not
                        // trust GoForward unless this simple shape is still intact.
                        if (Frame.CanGoForward && Frame.ForwardStack != null && Frame.ForwardStack.Count == 1)
                        {
                            var entry = Frame.ForwardStack[0];
                            canReturnThroughForwardStack = entry != null && entry.SourcePageType == typeof(Video);
                        }
                    }
                    catch { }

                    if (canReturnThroughForwardStack)
                    {
                        Frame.GoForward();
                    }
                    else
                    {
                        // NavigationCacheMode.Required reuses this same Video instance, so this is a
                        // deterministic restore even when the forward history has changed.
                        var navigated = Frame.Navigate(typeof(Video), new VideoNavigationArgs
                        {
                            VideoId = currentVideoId,
                            PlaylistId = currentPlaylistId,
                            PlaylistTitle = _playlistQueueTitle
                        });
                        if (!navigated)
                        {
                            _restoringFromMiniPlayer = false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _restoringFromMiniPlayer = false;
                System.Diagnostics.Debug.WriteLine("[Video] Restore from mini-player failed: " + ex.Message);
            }
        }

        // Puts the player control back into this page's container if it is not there — it may have
        // been handed to the mini-player earlier. Restores the full-page sizing.
        private void EnsurePlayerAttached()
        {
            try
            {
                if (CustomVideoPlayer == null || VideoPlayerContainer == null)
                {
                    return;
                }

                if (VideoPlayerContainer.Children.Contains(CustomVideoPlayer))
                {
                    return;
                }

                MiniPlayer.DetachFromParent(CustomVideoPlayer);
                CustomVideoPlayer.SetMiniMode(false);
                CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                CustomVideoPlayer.Width = double.NaN;
                CustomVideoPlayer.Height = double.NaN;
                CustomVideoPlayer.MinHeight = 211;
                CustomVideoPlayer.Margin = new Thickness(0);
                VideoPlayerContainer.Children.Add(CustomVideoPlayer);

                // The handlers were reduced to background playlist callbacks while minimized.
                AttachPlayerEventHandlers();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] EnsurePlayerAttached failed: " + ex.Message);
            }
        }

        // Takes the live player back out of the mini-player and reattaches it into this page's
        // layout, resuming the full-screen video page without reloading anything.
        private void ReattachPlayerFromMiniPlayer()
        {
            try
            {
                // Hands the still-playing control back (without disposing it) and drops it into
                // this page's container again. Playback is never interrupted.
                MiniPlayer.ReleasePlayerForRestore();
                EnsurePlayerAttached();

                _minimizedToMiniPlayer = false;
                ActivateDisplayRequest();
                System.Diagnostics.Debug.WriteLine("[Video] Restored from mini-player");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Reattach from mini-player failed: " + ex.Message);
            }
        }

        // Coming back from the mini-player, the window may still be resizing (on desktop it is
        // leaving the small always-on-top mode). Video_Unloaded dropped the SizeChanged handler
        // when the page left the tree, so a resize landing before Video_Loaded re-subscribes is
        // missed and the page keeps the two-column "landscape" layout it picked at 360px wide —
        // the player ends up as a narrow strip. Re-apply the layout a few times while the window
        // settles.
        private void ScheduleLayoutResync()
        {
            try
            {
                var timer = new DispatcherTimer();
                timer.Interval = TimeSpan.FromMilliseconds(150);
                int ticks = 0;
                double lastWidth = -1;

                timer.Tick += (s, args) =>
                {
                    ticks++;
                    var width = Window.Current.Bounds.Width;

                    // Re-measure the restored page without advancing the orientation baseline.
                    // A real rotation during this settle window must still be seen by Window_SizeChanged.
                    UpdateVideoPlayerLayout(false);

                    // Stop once the size has held steady for one interval, or after ~1.2s.
                    if ((Math.Abs(width - lastWidth) < 0.5 && ticks > 1) || ticks >= 8)
                    {
                        timer.Stop();
                    }
                    lastWidth = width;
                };

                timer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Layout resync failed: " + ex.Message);
            }
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // Expanding back from the mini-player: reattach the still-playing player and keep the
            // page's existing content — do NOT reload the video.
            if (_restoringFromMiniPlayer)
            {
                _restoringFromMiniPlayer = false;
                ReattachPlayerFromMiniPlayer();
                YouTube.Discord.DiscordPresenceService.SetVideo(
                    currentVideoId,
                    VideoTitleText != null ? VideoTitleText.Text : string.Empty,
                    currentChannelName,
                    _currentVideoThumbnailUrl);
                SystemNavigationManager.GetForCurrentView().BackRequested -= VideoPage_BackRequested;
                SystemNavigationManager.GetForCurrentView().BackRequested += VideoPage_BackRequested;
                ScheduleLayoutResync();
                return;
            }

            // Opening a full video page supersedes any floating mini-player — close it so two
            // players never run at once.
            if (MiniPlayer.IsActive)
            {
                MiniPlayer.Close();
            }

            // A cached Video page can be reused for an online or offline item. Force one complete
            // layout pass for the new navigation; subsequent rotation sizes use the fast path.
            _videoLayoutInitialized = false;

            var offlineArgs = e.Parameter as OfflineVideoNavigationArgs;
            if (offlineArgs != null && offlineArgs.Item != null)
            {
                await OpenOfflineVideoAsync(offlineArgs.Item);
                return;
            }

            _offlineMode = false;
            _offlineItem = null;
            ApplyOfflineModeVisuals(false);

            // A video can be opened standalone (plain id) or as part of a playlist / mix,
            // in which case the playlist context travels with it so the queue keeps working.
            var navArgs = e.Parameter as VideoNavigationArgs;
            var videoId = navArgs != null ? navArgs.VideoId : e.Parameter as string;

            // Show back button in title bar (for Windows 10 desktop)
            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                Frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;

            await SwitchToVideoAsync(
                videoId,
                navArgs != null ? navArgs.PlaylistId : null,
                navArgs != null ? navArgs.PlaylistTitle : null);
        }

        // Loads a video into THIS page — reused both for the initial navigation and for switching
        // to another video (a related tap, playlist next/prev) without spinning up a whole new
        // page. Resets the per-video state, stops the old playback, and rebinds the player.
        private async Task SwitchToVideoAsync(string videoId, string playlistId, string playlistTitle)
        {
            currentPlaylistId = playlistId ?? string.Empty;
            _playlistQueueTitle = playlistTitle ?? string.Empty;

            System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Video opened: video=" + videoId
                + ", playlist=" + (string.IsNullOrWhiteSpace(currentPlaylistId) ? "(none)" : currentPlaylistId));

            if (string.IsNullOrWhiteSpace(videoId))
            {
                return;
            }

            // Changing currentVideoId first makes any in-flight work from the previous video bail
            // out at its IsStillCurrentVideo checks. Stopping the player gives a clean hand-off;
            // BindPlayerSourceAsync then replaces the source (same as a quality change does).
            currentVideoId = videoId;
            YouTube.Discord.DiscordPresenceService.SetVideo(currentVideoId, string.Empty, string.Empty);
            UpdatePlaylistTransportControls();

            // When the page is visible, make sure the player is in its normal host. While the
            // mini-player is active the SAME control must stay there; loading a playlist item is
            // allowed to update the cached page and replace the media source without reparenting it.
            if (!_minimizedToMiniPlayer)
            {
                EnsurePlayerAttached();
            }

            if (CustomVideoPlayer != null)
            {
                try
                {
                    CustomVideoPlayer.ResetForReuse();
                    CustomVideoPlayer.Stop();
                }
                catch (Exception ex)
                { System.Diagnostics.Debug.WriteLine("[Video] Stop before switch failed: " + ex.Message); }
            }

            _historyReportedVideoId = null;
            _autoQualityFallbackAttempted = false;
            _currentVideoDescription = string.Empty;
            _currentVideoThumbnailUrl = string.Empty;
            _currentChannelThumbnailUrl = string.Empty;
            if (ChannelImage != null) ChannelImage.ImageSource = null;
            if (DownloadActionText != null) DownloadActionText.Text = Localization.GetString("Download");
            if (DownloadActionIcon != null) DownloadActionIcon.Visibility = Visibility.Visible;
            if (DownloadProgressIcon != null) DownloadProgressIcon.Visibility = Visibility.Collapsed;
            _landscapeDescriptionExpanded = false;
            _landscapeDescriptionNeedsToggle = false;
            UpdateLandscapeDescriptionText();
            UpdateLandscapeDescriptionExpansionState();

            // Materialize the Settings default BEFORE any player/format request can choose a
            // source. Fresh installs become explicit Auto immediately.
            var configuredQuality = GetPreferredVideoQualitySetting();
            System.Diagnostics.Debug.WriteLine(
                "[Video] Opening video with configured quality: " + configuredQuality);

            // A freshly opened video follows the Settings default until the user overrides it.
            currentQualityTag = string.Empty;
            _qualityExplicitlyChosen = false;
            currentChannelId = string.Empty;
            currentChannelName = string.Empty;
            ResetVideoOnlyDecodeFallbackState();
            ResetRatingUiForNewVideo();
            ResetSubscriptionUiForNewVideo();
            ScrollVideoPageToTop();

            await LoadVideoDetailsAsync(videoId);
        }

        private async Task OpenOfflineVideoAsync(DownloadedVideoItem item)
        {
            _offlineMode = true;
            _offlineItem = item;
            currentPlaylistId = string.Empty;
            _playlistQueueTitle = string.Empty;
            currentVideoId = item.VideoId ?? string.Empty;
            currentChannelId = item.ChannelId ?? string.Empty;
            currentChannelName = item.Author ?? string.Empty;
            _currentVideoDescription = item.Description ?? string.Empty;
            _currentVideoThumbnailUrl = !string.IsNullOrWhiteSpace(item.LocalThumbnailUri)
                ? item.LocalThumbnailUri : (item.ThumbnailUrl ?? string.Empty);
            _currentChannelThumbnailUrl = !string.IsNullOrWhiteSpace(item.LocalChannelThumbnailUri)
                ? item.LocalChannelThumbnailUri : (item.ChannelThumbnailUrl ?? string.Empty);
            UpdatePlaylistTransportControls();

            SystemNavigationManager.GetForCurrentView().AppViewBackButtonVisibility =
                Frame != null && Frame.CanGoBack
                    ? AppViewBackButtonVisibility.Visible
                    : AppViewBackButtonVisibility.Collapsed;

            EnsurePlayerAttached();
            ResetSecondaryContentForFastLoad();
            SetSkeletonVisibility(false);

            if (VideoTitleText != null) VideoTitleText.Text = item.Title ?? string.Empty;
            if (VideoAuthorText != null) VideoAuthorText.Text = item.Author ?? string.Empty;
            YouTube.Discord.DiscordPresenceService.SetVideo(currentVideoId, item.Title, item.Author, item.ThumbnailUrl);
            if (SubscriberCountText != null) SubscriberCountText.Text = string.Empty;
            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.ResetForReuse();
                CustomVideoPlayer.SetVideoInfo(item.Title ?? string.Empty, item.Author ?? string.Empty);
                CustomVideoPlayer.SetSystemMediaMetadata(item.VideoId, item.Title, item.Author);
                CustomVideoPlayer.SetSystemMediaNavigationEnabled(false, false);
                CustomVideoPlayer.SetSubtitleTracks(new Subtitles.TrackList());
            }

            if (item.Width > 0 && item.Height > 0)
                SetVideoPlayerAspectRatio((double)item.Width / item.Height);

            if (ChannelImage != null && !string.IsNullOrWhiteSpace(_currentChannelThumbnailUrl))
            {
                try { ChannelImage.ImageSource = new BitmapImage(new Uri(_currentChannelThumbnailUrl)); }
                catch { ChannelImage.ImageSource = null; }
            }

            UpdateLandscapeDescriptionText();
            ApplyOfflineModeVisuals(true);
            UpdateVideoPlayerLayout();

            var file = await DownloadManager.GetFileAsync(item);
            if (file != null && CustomVideoPlayer != null)
                await CustomVideoPlayer.SetSourceFromStorageFileAsync(file, true);
        }

        private void ApplyOfflineModeVisuals(bool offline)
        {
            if (VideoActionsScrollViewer != null)
                VideoActionsScrollViewer.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (SubscribeButtonContainer != null)
                SubscribeButtonContainer.Visibility = offline || !_hasSignedInAccount
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            if (SubscriberCountText != null && offline)
                SubscriberCountText.Visibility = Visibility.Collapsed;
            else if (SubscriberCountText != null)
                SubscriberCountText.Visibility = Visibility.Visible;
            if (PlaylistQueuePanel != null && offline)
                PlaylistQueuePanel.Visibility = Visibility.Collapsed;
            if (CommentsContainerButton != null && offline)
                CommentsContainerButton.Visibility = Visibility.Collapsed;
            if (LandscapeCommentsPanel != null && offline)
                LandscapeCommentsPanel.Visibility = Visibility.Collapsed;
            if (QualityButton != null)
                QualityButton.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (AudioTrackButton != null && offline)
                AudioTrackButton.Visibility = Visibility.Collapsed;
            if (SubtitlesButton != null && offline)
                SubtitlesButton.Visibility = Visibility.Collapsed;
            if (StatLikes != null)
                StatLikes.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (StatViews != null)
                StatViews.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (StatDate != null)
                StatDate.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
            if (LandscapeDescriptionStats != null)
                LandscapeDescriptionStats.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
        }

        // Return the page to the top so a switched-in video does not start scrolled down where the
        // previous one's comments/related were.
        private void ScrollVideoPageToTop()
        {
            try
            {
                if (MainScrollViewer != null)
                {
                    MainScrollViewer.ChangeView(null, 0, null, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Scroll to top failed: " + ex.Message);
            }
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            base.OnNavigatedFrom(e);

            // When handed to MiniPlayer this cached page remains the playlist controller: keep
            // currentVideoId and the lightweight next/previous/end handlers alive so background
            // transitions can run the SAME full SwitchToVideoAsync path and refresh all metadata.
            if (!_minimizedToMiniPlayer)
            {
                currentVideoId = null;
                DetachPlayerEventHandlers();

                try
                {
                    if (CustomVideoPlayer != null)
                    {
                        CustomVideoPlayer.Stop();
                        CustomVideoPlayer.SetSystemMediaNavigationEnabled(false, false);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] OnNavigatedFrom teardown failed: " + ex.Message);
                }
            }

            ReleaseDisplayRequest();
        }

    }
}
