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
        private void SubscribeWindowSizeChanged()
        {
            if (_windowSizeChangedSubscribed || Window.Current == null)
            {
                return;
            }

            Window.Current.SizeChanged += Window_SizeChanged;
            _windowSizeChangedSubscribed = true;
        }

        private void UnsubscribeWindowSizeChanged()
        {
            if (!_windowSizeChangedSubscribed || Window.Current == null)
            {
                return;
            }

            Window.Current.SizeChanged -= Window_SizeChanged;
            _windowSizeChangedSubscribed = false;
        }

        private static bool IsCurrentViewPortrait()
        {
            try
            {
                var view = ApplicationView.GetForCurrentView();
                if (view != null)
                {
                    return view.Orientation == ApplicationViewOrientation.Portrait;
                }
            }
            catch { }

            // Old/mobile fallback if the ApplicationView query is temporarily unavailable.
            if (Window.Current != null)
            {
                var bounds = Window.Current.Bounds;
                return bounds.Height > bounds.Width;
            }

            return true;
        }

        private void ResetOrientationTrackingToCurrentWindow()
        {
            _orientationFullscreenGeneration++;
            _wasPortrait = IsCurrentViewPortrait();
        }

        private void Video_Loaded(object sender, RoutedEventArgs e)
        {
            DownloadManager.Changed -= DownloadManager_Changed;
            DownloadManager.Changed += DownloadManager_Changed;
            App.ThemeChanged -= App_ThemeChanged;
            App.ThemeChanged += App_ThemeChanged;
            VideoAmbientEffectController.EnabledChanged -= VideoAmbientEffect_EnabledChanged;
            VideoAmbientEffectController.EnabledChanged += VideoAmbientEffect_EnabledChanged;
            ApplyCurrentVideoTheme();

            // A cached Video page is loaded again after returning from the mini-player, so
            // restore the window handler every time and baseline orientation without causing an
            // artificial fullscreen transition merely because the page became visible.
            SubscribeWindowSizeChanged();
            ResetOrientationTrackingToCurrentWindow();
            RefreshAuthenticatedVideoActions();
            UpdateVideoPlayerLayout();
            RefreshRelatedThumbnailPresentation();
            if (CustomVideoPlayer != null)
                CustomVideoPlayer.ResumeNonPlaybackVisuals();

            // Reattach once per visual load. The helper first removes existing handlers so a
            // cached Video page cannot accumulate duplicate VideoEnded callbacks after restores.
            AttachPlayerEventHandlers();

            // Cached pages are loaded more than once. De-duplicate the system back handler just
            // like the player/window handlers so a mini-player restore cannot issue two Back actions.
            var navigationManager = SystemNavigationManager.GetForCurrentView();
            navigationManager.BackRequested -= VideoPage_BackRequested;
            navigationManager.BackRequested += VideoPage_BackRequested;

            // Prevent device from going to sleep while watching video
            ActivateDisplayRequest();
        }

        private void CustomVideoPlayer_MediaFailed(
            object sender,
            YouTube.CustomVideoPlayer.CustomVideoPlayerMediaFailedEventArgs e
        )
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Video] MediaFailed: {e.Error} - {e.ErrorMessage}"
            );

            // A local MP4 can briefly report a Media Foundation failure while its seek index is
            // repositioning, especially on Windows 10 Mobile. None of the online recovery paths
            // are meaningful here: there are no signed URLs to refresh and no lower remote itag
            // to request. Mark it handled before decode fallback/error UI can turn an ordinary
            // offline scrub into a repeated "reload video" prompt.
            if (_offlineMode)
            {
                e.Handled = true;
                System.Diagnostics.Debug.WriteLine(
                    "[Video][Offline] Ignored transient MediaFailed during local playback/seek");
                return;
            }

            bool shouldReloadSource;
            bool shouldStopPlayback;
            if (TryHandleVideoOnlyDecodeFailure(e.Error, out shouldReloadSource, out shouldStopPlayback))
            {
                e.Handled = true;

                if (shouldReloadSource)
                {
                    ScheduleReservedVideoOnlyDecodeFallback();
                }
                else if (shouldStopPlayback)
                {
                    ScheduleHandledDecodeFailureStop();
                }

                return;
            }

            // Never silently change the user's configured quality after a playback error.
            // In particular, Auto must not jump to the highest available stream (often 1080p).
            // A failure is surfaced normally; quality changes happen only by explicit user choice.

            // Show error to user
            var playbackError = e.Error;
            var playbackErrorMessage = e.ErrorMessage;
            var _ = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                async () =>
                {
                    if (_playbackErrorDialogOpen)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] Playback error dialog already open; suppressing duplicate");
                        return;
                    }

                    _playbackErrorDialogOpen = true;
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = Localization.GetString("PlaybackError"),
                            Content = Localization.Format("FailedPlayVideoFormat", playbackErrorMessage)
                                + "\n\n" + Localization.Format("ErrorCodeFormat", playbackError),
                            PrimaryButtonText = Localization.GetString("OK"),
                        };
                        await dialog.ShowAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] Playback error dialog failed: " + ex.Message);
                    }
                    finally
                    {
                        _playbackErrorDialogOpen = false;
                    }
                }
            );
        }


        private void ShowMediaErrorDialog(string message, string code)
        {
            var ignored = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                async () =>
                {
                    if (_playbackErrorDialogOpen)
                    {
                        return;
                    }

                    _playbackErrorDialogOpen = true;
                    try
                    {
                        var dialog = new ContentDialog
                        {
                            Title = Localization.GetString("PlaybackError"),
                            Content = message + "\n\n" + Localization.Format("ErrorCodeFormat", code),
                            PrimaryButtonText = Localization.GetString("OK"),
                        };
                        await dialog.ShowAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] Error dialog failed: " + ex.Message);
                    }
                    finally
                    {
                        _playbackErrorDialogOpen = false;
                    }
                });
        }

        private void ScheduleHandledDecodeFailureStop()
        {
            if (_manualDecodeFailureStopScheduled)
            {
                return;
            }

            _manualDecodeFailureStopScheduled = true;

            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Manual decode failure stop could not be scheduled: dispatcher is null");
                    _manualDecodeFailureStopScheduled = false;
                    return;
                }

                var _ = dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () =>
                    {
                        try
                        {
                            if (CustomVideoPlayer != null)
                            {
                                CustomVideoPlayer.Stop();
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("[Video] Manual decode failure stop failed: " + ex.Message);
                        }
                        finally
                        {
                            _manualDecodeFailureStopScheduled = false;
                        }
                    }
                );
            }
            catch (Exception ex)
            {
                _manualDecodeFailureStopScheduled = false;
                System.Diagnostics.Debug.WriteLine("[Video] Manual decode failure stop scheduling failed: " + ex.Message);
            }
        }

        private void ScheduleReservedVideoOnlyDecodeFallback()
        {
            try
            {
                var dispatcher = Dispatcher;
                if (dispatcher == null)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Decode fallback could not be scheduled: dispatcher is null");
                    _decoderFallbackInProgress = false;
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[Video] Scheduling decode fallback on UI thread");
                var _ = dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    async () =>
                    {
                        await RunReservedVideoOnlyDecodeFallbackAsync();
                    }
                );
            }
            catch (Exception ex)
            {
                _decoderFallbackInProgress = false;
                System.Diagnostics.Debug.WriteLine("[Video] Decode fallback scheduling failed: " + ex.Message);
            }
        }

        private bool TryHandleVideoOnlyDecodeFailure(
            Windows.Media.Playback.MediaPlayerError error,
            out bool shouldReloadSource,
            out bool shouldStopPlayback
        )
        {
            shouldReloadSource = false;
            shouldStopPlayback = false;

            if (error != Windows.Media.Playback.MediaPlayerError.DecodingError)
            {
                return false;
            }

            if (ParseInt(currentQualityTag) > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Manual quality decode failure; not re-requesting or switching source"
                );
                shouldStopPlayback = true;
                return true;
            }

            if (_decoderFallbackInProgress)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Decode fallback already running; suppressing repeated decoder failure");
                return true;
            }

            if (_currentH264VideoOnlyItag <= 0)
            {
                return false;
            }

            if (_excludedH264VideoOnlyItags.Contains(_currentH264VideoOnlyItag))
            {
                System.Diagnostics.Debug.WriteLine("[Video] Decode fallback already excluded current itag; suppressing repeated decoder failure");
                return true;
            }

            if (_decoderFallbackAttemptsForCurrentQuality >= MaxVideoOnlyDecodeFallbackAttempts)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Decode fallback limit reached; allowing playback error to surface");
                return false;
            }

            _decoderFallbackInProgress = true;
            _decoderFallbackAttemptsForCurrentQuality++;
            _excludedH264VideoOnlyItags.Add(_currentH264VideoOnlyItag);
            shouldReloadSource = true;

            return true;
        }

        private async Task RunReservedVideoOnlyDecodeFallbackAsync()
        {
            try
            {
                var resumePosition = TimeSpan.Zero;
                if (CustomVideoPlayer != null)
                {
                    resumePosition = CustomVideoPlayer.CurrentPlaybackPosition;
                    CustomVideoPlayer.Stop();
                    CustomVideoPlayer.PrepareResumeAfterSourceReload(resumePosition, true);
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Decoder failed for H.264 video-only itag="
                    + _currentH264VideoOnlyItag
                    + "; falling back once at "
                    + resumePosition
                );

                var playerJson = _lastPlayerJson;
                if (string.IsNullOrWhiteSpace(playerJson) && !string.IsNullOrWhiteSpace(currentVideoId))
                {
                    playerJson = await PostInnertubeAsync("player", BuildPlayerPayload(currentVideoId));
                    _lastPlayerJson = playerJson;
                }

                if (!string.IsNullOrWhiteSpace(playerJson))
                {
                    await BindPlayerSourceAsync(playerJson, true);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Decode fallback failed: " + ex.Message);
            }
            finally
            {
                _decoderFallbackInProgress = false;
            }
        }

        private void ResetVideoOnlyDecodeFallbackState()
        {
            _excludedH264VideoOnlyItags.Clear();
            _currentH264VideoOnlyItag = -1;
            _decoderFallbackInProgress = false;
            _decoderFallbackAttemptsForCurrentQuality = 0;
        }

        private void CustomVideoPlayer_SettingsRequested(object sender, object e)
        {
            ShowSettingsBottomSheet();
        }

        private void CustomVideoPlayer_FullscreenStateChanged(bool isFullscreen)
        {
            if (!isFullscreen)
            {
                RestoreSettingsBottomSheetFromFullscreenPopup();
            }
        }


        private void Video_Unloaded(object sender, RoutedEventArgs e)
        {
            DownloadManager.Changed -= DownloadManager_Changed;
            App.ThemeChanged -= App_ThemeChanged;
            VideoAmbientEffectController.EnabledChanged -= VideoAmbientEffect_EnabledChanged;
            _orientationFullscreenGeneration++;
            UnsubscribeWindowSizeChanged();
            RestoreSettingsBottomSheetFromFullscreenPopup();

            // When minimized, the player was handed to the mini-player — leave it running.
            // Otherwise stop it but keep the control alive: this page is cached and reused, so
            // disposing here would hand the reused page a dead player.
            if (!_minimizedToMiniPlayer && CustomVideoPlayer != null)
            {
                try
                {
                    CustomVideoPlayer.Stop();
                    CustomVideoPlayer.SuspendNonPlaybackVisuals();
                }
                catch { }
            }
            SystemNavigationManager.GetForCurrentView().BackRequested -= VideoPage_BackRequested;
            ReleaseDisplayRequest();
        }

        private static bool IsAutoFullscreenLandscapeEnabled()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                object raw;
                if (!values.TryGetValue(AutoFullscreenLandscapeSettingKey, out raw) || raw == null)
                {
                    // Auto fullscreen is enabled by default.
                    values[AutoFullscreenLandscapeSettingKey] = true;
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

        private void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            if (_minimizedToMiniPlayer)
            {
                return;
            }

            // e.Size is already the new orientation. ApplicationView.Orientation can keep the old
            // value for several frames on low-end phones, which used to make fullscreen feel late.
            var isPortraitAtEvent = e.Size.Width > 0 && e.Size.Height > 0
                ? e.Size.Height >= e.Size.Width
                : IsCurrentViewPortrait();

            // IMPORTANT: decide whether this is a real orientation transition BEFORE
            // UpdateVideoPlayerLayout() updates _wasPortrait. Ordinary SizeChanged events in
            // landscape (fullscreen exit, system bars, popup/layout changes, etc.) must not
            // trigger auto-fullscreen again.
            var orientationChanged = isPortraitAtEvent != _wasPortrait;
            var wasPortraitBeforeChange = _wasPortrait;

            if (orientationChanged && CustomVideoPlayer != null)
            {
                _orientationFullscreenGeneration++;
                try
                {
                    if (wasPortraitBeforeChange
                        && !isPortraitAtEvent
                        && IsAutoFullscreenLandscapeEnabled()
                        && !CustomVideoPlayer.IsFullscreen)
                    {
                        // Mark the new orientation before reparenting. Any additional SizeChanged
                        // notification from the Popup then becomes a cheap geometry update instead
                        // of a second fullscreen transition.
                        _wasPortrait = false;
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] Portrait -> landscape: entering fullscreen immediately");
                        CustomVideoPlayer.ToggleFullscreen(e.Size);

                        // The normal page is completely covered by the fullscreen Popup. Deferring
                        // its expensive panel/card rearrangement until fullscreen is left lets this
                        // SizeChanged return immediately so the compositor can present the player.
                        return;
                    }
                    else if (!wasPortraitBeforeChange
                        && isPortraitAtEvent
                        && CustomVideoPlayer.IsFullscreen)
                    {
                        _wasPortrait = true;
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] Landscape -> portrait: leaving fullscreen immediately");
                        CustomVideoPlayer.ToggleFullscreen(e.Size);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Orientation fullscreen transition failed: " + ex.Message);
                }
            }

            UpdateFullscreenSettingsPopupBounds(e.Size);
            if (SettingsBottomSheetPanel != null
                && SettingsBottomSheetPanel.Visibility == Visibility.Visible)
            {
                UpdateSettingsBottomSheetHeight();
            }

            UpdateVideoPlayerLayout(true, isPortraitAtEvent);
        }

        private void VideoPlayerContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVideoPlayerHeight(e.NewSize.Width);
        }

    }
}
