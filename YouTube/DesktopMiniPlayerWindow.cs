using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Media.Playback;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    // A true secondary UWP view used by supported VS2017+ desktop builds.
    // The existing MediaPlayer instance is reused; no second stream/decoder is created.
    internal static class DesktopMiniPlayerWindow
    {
        private const double DefaultLongEdge = 360.0;
        private const double MaximumLongEdge = 520.0;
        private const double MinimumWidth = 180.0;
        private const double MinimumHeight = 100.0;
        private const double DefaultAspectRatio = 16.0 / 9.0;

        private static bool _isOpening;
        private static bool _isActive;
        private static bool _suppressConsolidatedCallback;
        private static bool _restoreInProgress;
        private static bool _keepPageOpen;
        private static Task _openingTask;
        private static Task<bool> _closingTask;
        private static int _windowGeneration;
        private static int _viewId;
        private static int _mainViewId;
        private static int _aspectResizeVersion;
        private static double _videoAspectRatio = DefaultAspectRatio;
        private static CoreApplicationView _view;
        private static CoreDispatcher _secondaryDispatcher;
        private static CoreDispatcher _mainDispatcher;
        private static Window _secondaryWindow;
        private static DispatcherTimer _aspectResizeTimer;
        private static Size _lastWindowSize;
        private static Size _pendingWindowSize;
        private static Size _lastResizeSourceSize;
        private static Size _lastRequestedWindowSize;
        private static bool _resizingWindow;
        private static MediaPlayerElement _mediaElement;
        private static Grid _controlsOverlay;
        private static Button _playPauseButton;
        private static Image _playPauseIcon;
        private static Slider _progressSlider;
        private static Border _progressHitArea;
        private static DispatcherTimer _controlsHideTimer;
        private static DispatcherTimer _progressTimer;
        private static bool _progressPointerDown;
        private static bool _controlsVisible;
        private static MediaPlayer _sharedPlayer;
        // Keep the original main-view surface so the shared MediaPlayer can be explicitly
        // rebound after the secondary view releases its MediaPlayerElement. Some UWP builds leave
        // the old MediaPlayerElement in a stale video-surface state if we only detach the
        // secondary element, which makes the restored video render at the PiP geometry.
        private static CustomVideoPlayer _ownerPlayer;
        private static MediaPlayerElement _ownerMediaElement;
        private static Action _restoreRequested;
        private static Action _closeRequested;
        private static Action _failed;

        public static bool IsOpening { get { return _isOpening; } }
        public static bool IsActive { get { return _isActive; } }

        public static bool IsPageWindowFor(CustomVideoPlayer owner)
        {
            return _keepPageOpen && (_isActive || _isOpening)
                && ReferenceEquals(owner, _ownerPlayer);
        }

        public static bool TryShow(
            CustomVideoPlayer owner,
            double videoAspectRatio,
            Action restoreRequested,
            Action closeRequested,
            Action failed,
            bool keepPageOpen = false)
        {
#if VS2017_DESKTOP_PIP
            if (owner == null || !(keepPageOpen ? DesktopMiniPlayerSetting.IsAvailable()
                : DesktopMiniPlayerSetting.IsEnabled()) || _isOpening || _isActive || _restoreInProgress
                || (_closingTask != null && !_closingTask.IsCompleted))
            {
                return false;
            }

            try
            {
                var ownerElement = owner.GetMediaPlayer();
                var shared = ownerElement == null ? null : ownerElement.MediaPlayer;
                if (shared == null)
                {
                    return false;
                }

                _sharedPlayer = shared;
                _ownerPlayer = owner;
                _ownerMediaElement = ownerElement;
                _videoAspectRatio = NormalizeRatio(videoAspectRatio);
                _restoreRequested = restoreRequested;
                _closeRequested = closeRequested;
                _failed = failed;
                _mainDispatcher = Window.Current.Dispatcher;
                _mainViewId = ApplicationView.GetForCurrentView().Id;
                _restoreInProgress = false;
                _keepPageOpen = keepPageOpen;
                _closingTask = null;
                _isOpening = true;
                if (keepPageOpen) owner.SetDesktopMiniPlayerMode(true);
                _openingTask = ShowAsync(++_windowGeneration);
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] TryShow failed: " + ex.Message);
                if (keepPageOpen) owner.SetDesktopMiniPlayerMode(false);
                ResetState(false);
                return false;
            }
#else
            return false;
#endif
        }

        // The stream can publish a more accurate aspect ratio after the PiP window is already open.
        // Re-fit only the secondary window; the shared MediaPlayer itself is not recreated.
        public static void UpdateAspectRatio(double videoAspectRatio)
        {
#if VS2017_DESKTOP_PIP
            var ratio = NormalizeRatio(videoAspectRatio);
            if (Math.Abs(ratio - _videoAspectRatio) < 0.001) return;
            _videoAspectRatio = ratio;
            if (_isActive || _isOpening)
            {
                FitWindowToAspect(_videoAspectRatio, false);
            }
#endif
        }

#if VS2017_DESKTOP_PIP
        private static async Task ShowAsync(int generation)
        {
            try
            {
                _view = CoreApplication.CreateNewView();
                await _view.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    _secondaryDispatcher = Window.Current.Dispatcher;
                    BuildSecondaryWindow();
                    var appView = ApplicationView.GetForCurrentView();
                    _viewId = appView.Id;
                    appView.Consolidated += SecondaryView_Consolidated;
                    try
                    {
                        appView.SetPreferredMinSize(GetAspectWindowSize(
                            new Size(MinimumWidth, MinimumHeight), _videoAspectRatio, true));
                    }
                    catch
                    {
                    }
                    Window.Current.Activate();
                });
                if (generation != _windowGeneration) return;

                var mode = ApplicationViewMode.CompactOverlay;
                var preferences = ViewModePreferences.CreateDefault(mode);
                preferences.ViewSizePreference = ViewSizePreference.Custom;
                preferences.CustomSize = GetPreferredWindowSize(_videoAspectRatio);
                var shown = await ApplicationViewSwitcher.TryShowAsViewModeAsync(
                    _viewId, mode, preferences);
                if (generation != _windowGeneration) return;

                if (!shown)
                {
                    throw new InvalidOperationException("Windows refused to show the secondary player view.");
                }

                _isOpening = false;
                _isActive = true;

                // Both manual opening and automatic handoff remain above other windows.
                // Re-fit after the shell applies CompactOverlay's initial bounds.
                FitWindowToAspect(_videoAspectRatio, true);
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Secondary CompactOverlay opened.");
            }
            catch (Exception ex)
            {
                if (generation != _windowGeneration) return;
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Open failed: " + ex.Message);
                await CleanupFailedOpenAsync();
                var failed = _failed;
                var mainDispatcher = _mainDispatcher;
                ResetState(false);
                if (failed != null && mainDispatcher != null)
                {
                    await mainDispatcher.RunAsync(CoreDispatcherPriority.Normal, () => failed());
                }
            }
        }

        private static double NormalizeRatio(double ratio)
        {
            if (ratio <= 0
                || double.IsNaN(ratio)
                || double.IsInfinity(ratio)
                || ratio < 0.45
                || ratio > 3.5)
            {
                return DefaultAspectRatio;
            }
            return ratio;
        }

        private static Size GetPreferredWindowSize(double ratio)
        {
            ratio = NormalizeRatio(ratio);
            return GetAspectWindowSize(new Size(DefaultLongEdge, DefaultLongEdge), ratio, ratio >= 1.0);
        }

        private static Size GetAspectWindowSize(Size size, double ratio, bool useWidth)
        {
            ratio = NormalizeRatio(ratio);
            var width = useWidth ? size.Width : size.Height * ratio;
            width = Math.Max(width, Math.Max(MinimumWidth, MinimumHeight * ratio));
            var height = width / ratio;
            var longEdge = Math.Max(width, height);
            if (longEdge > MaximumLongEdge)
            {
                var scale = MaximumLongEdge / longEdge;
                width *= scale;
                height *= scale;
            }
            return new Size(Math.Round(width), Math.Round(height));
        }

        private static bool SameWindowSize(Size first, Size second)
        {
            return Math.Abs(first.Width - second.Width) <= 1.0
                && Math.Abs(first.Height - second.Height) <= 1.0;
        }

        private static void SecondaryWindow_SizeChanged(object sender, WindowSizeChangedEventArgs e)
        {
            var previous = _lastWindowSize;
            var size = e.Size;
            _lastWindowSize = size;
            if (_resizingWindow || !_isActive || _restoreInProgress || _aspectResizeTimer == null)
                return;

            // Resize notifications caused by our correction must not start another correction.
            if (SameWindowSize(size, _lastRequestedWindowSize)
                || Math.Abs(size.Width - size.Height * _videoAspectRatio) <= 1.5)
            {
                _aspectResizeTimer.Stop();
                return;
            }

            var widthChange = Math.Abs(size.Width - previous.Width);
            var heightChange = Math.Abs(size.Height - previous.Height) * _videoAspectRatio;
            _pendingWindowSize = GetAspectWindowSize(size, _videoAspectRatio, widthChange >= heightChange);
            if (SameWindowSize(size, _lastResizeSourceSize)
                && SameWindowSize(_pendingWindowSize, _lastRequestedWindowSize))
            {
                _aspectResizeTimer.Stop();
                return;
            }

            _aspectResizeTimer.Stop();
            _aspectResizeTimer.Start();
        }

        private static void AspectResizeTimer_Tick(object sender, object e)
        {
            if (_aspectResizeTimer != null) _aspectResizeTimer.Stop();
            if (!_isActive || _restoreInProgress || _secondaryWindow == null) return;
            RequestSecondaryWindowSize(_pendingWindowSize);
        }

        private static void RequestSecondaryWindowSize(Size requestedSize)
        {
            if (_secondaryWindow == null) return;
            var bounds = _secondaryWindow.Bounds;
            var size = new Size(bounds.Width, bounds.Height);
            if (SameWindowSize(size, requestedSize)) return;
            if (SameWindowSize(size, _lastResizeSourceSize)
                && SameWindowSize(requestedSize, _lastRequestedWindowSize)) return;

            _lastResizeSourceSize = size;
            _lastRequestedWindowSize = requestedSize;
            _resizingWindow = true;
            try
            {
                var view = ApplicationView.GetForCurrentView();
                view.SetPreferredMinSize(GetAspectWindowSize(
                    new Size(MinimumWidth, MinimumHeight), _videoAspectRatio, true));
                var resized = view.TryResizeView(requestedSize);
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Aspect resize "
                    + requestedSize.Width.ToString("0") + "x"
                    + requestedSize.Height.ToString("0") + ": " + resized);
                if (!resized)
                {
                    // At a screen or shell limit, fit within the size Windows already allowed
                    // instead of repeatedly requesting a height/width outside the work area.
                    bounds = _secondaryWindow.Bounds;
                    size = new Size(bounds.Width, bounds.Height);
                    var fitted = GetAspectWindowSize(size, _videoAspectRatio,
                        size.Width <= size.Height * _videoAspectRatio);
                    var fittedResized = false;
                    if (!SameWindowSize(size, fitted) && !SameWindowSize(requestedSize, fitted))
                        fittedResized = view.TryResizeView(fitted);
                    if (!fittedResized)
                    {
                        // At the shell's minimum size, preserving the ratio may require growing
                        // the other dimension rather than shrinking it below the allowed minimum.
                        var expanded = GetAspectWindowSize(size, _videoAspectRatio,
                            size.Width >= size.Height * _videoAspectRatio);
                        if (!SameWindowSize(size, expanded) && !SameWindowSize(requestedSize, expanded)
                            && !SameWindowSize(fitted, expanded))
                            view.TryResizeView(expanded);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] TryResizeView failed: " + ex.Message);
            }
            finally { _resizingWindow = false; }
        }

        private static async void FitWindowToAspect(double ratio, bool settleAfterModeSwitch)
        {
            var dispatcher = _secondaryDispatcher;
            if (dispatcher == null)
            {
                return;
            }

            var version = ++_aspectResizeVersion;
            try
            {
                await ResizeSecondaryViewOnce(dispatcher, ratio, settleAfterModeSwitch, version);
                if (!settleAfterModeSwitch)
                {
                    return;
                }

                // The shell can apply initial bounds after the new view has been shown.
                await Task.Delay(180);
                await ResizeSecondaryViewOnce(dispatcher, ratio, false, version);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Aspect resize failed: " + ex.Message);
            }
        }

        private static async Task ResizeSecondaryViewOnce(
            CoreDispatcher dispatcher,
            double ratio,
            bool useDefaultSize,
            int version)
        {
            if (dispatcher == null || version != _aspectResizeVersion)
            {
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (version != _aspectResizeVersion)
                {
                    return;
                }

                if (_secondaryWindow != null)
                {
                    if (_aspectResizeTimer != null) _aspectResizeTimer.Stop();
                    _lastResizeSourceSize = new Size();
                    _lastRequestedWindowSize = new Size();
                    var bounds = _secondaryWindow.Bounds;
                    var size = new Size(bounds.Width, bounds.Height);
                    var requestedSize = useDefaultSize ? GetPreferredWindowSize(ratio)
                        : GetAspectWindowSize(size, ratio, ratio >= 1.0);
                    RequestSecondaryWindowSize(requestedSize);
                }
            });
        }

        private static void BuildSecondaryWindow()
        {
            // The native window frame handles dragging and resizing. Controls overlay the video.
            ConfigureTransparentTitleBar();
            _secondaryWindow = Window.Current;
            var bounds = _secondaryWindow.Bounds;
            _lastWindowSize = new Size(bounds.Width, bounds.Height);
            _aspectResizeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _aspectResizeTimer.Tick += AspectResizeTimer_Tick;
            _secondaryWindow.SizeChanged += SecondaryWindow_SizeChanged;

            var root = new Grid
            {
                Background = new SolidColorBrush(Colors.Black)
            };
            root.PointerEntered += SecondaryRoot_PointerActivity;
            root.PointerMoved += SecondaryRoot_PointerActivity;
            root.PointerExited += SecondaryRoot_PointerExited;

            _mediaElement = new MediaPlayerElement
            {
                AreTransportControlsEnabled = false,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _mediaElement.SetMediaPlayer(_sharedPlayer);
            root.Children.Add(_mediaElement);

            // Free video area restores the main app. The actual controls are added after this hit
            // target, so Play/Pause and the seek bar receive the pointer instead of restoring.
            var restoreHitTarget = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            restoreHitTarget.Tapped += RestoreHitTarget_Tapped;
            restoreHitTarget.PointerMoved += SecondaryRoot_PointerActivity;
            root.Children.Add(restoreHitTarget);

            _controlsOverlay = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                Opacity = 1.0,
                IsHitTestVisible = true
            };

            _playPauseButton = new Button
            {
                Width = 52,
                Height = 52,
                Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["MiniPlayerControlButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            _playPauseIcon = new Image
            {
                Width = 26,
                Height = 26
            };
            _playPauseButton.Content = _playPauseIcon;
            _playPauseButton.Click += PlayPauseButton_Click;
            _playPauseButton.PointerMoved += SecondaryRoot_PointerActivity;
            _controlsOverlay.Children.Add(_playPauseButton);

            _progressSlider = new Slider
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Height = 24,
                Margin = new Thickness(10, 0, 10, 7),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom,
                IsTabStop = false,
                IsEnabled = false,
                IsHitTestVisible = false
            };
            try
            {
                var sliderStyle = Application.Current.Resources["DesktopMiniPlayerSliderStyle"] as Style;
                if (sliderStyle != null)
                {
                    _progressSlider.Style = sliderStyle;
                }
            }
            catch
            {
                // The slider still works with the platform default style if the app resource was
                // not present for some reason.
            }
            _controlsOverlay.Children.Add(_progressSlider);

            // Match the normal player's approach: the visible Slider does no pointer work. A
            // transparent full-height hit area above it gives a much easier mouse/touch target and
            // maps the pointer to the timeline. The expensive media seek happens only on release.
            _progressHitArea = new Border
            {
                Height = 28,
                Margin = new Thickness(6, 0, 6, 5),
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Bottom
            };
            _progressHitArea.PointerPressed += ProgressHitArea_PointerPressed;
            _progressHitArea.PointerMoved += ProgressHitArea_PointerMoved;
            _progressHitArea.PointerReleased += ProgressHitArea_PointerReleased;
            _progressHitArea.PointerCanceled += ProgressHitArea_PointerCanceled;
            _progressHitArea.PointerCaptureLost += ProgressHitArea_PointerCaptureLost;
            _controlsOverlay.Children.Add(_progressHitArea);

            root.Children.Add(_controlsOverlay);

            Window.Current.Content = root;
            UpdatePlayPauseIcon();
            UpdateProgressSlider();
            StartSecondaryUiTimers();

            try
            {
                if (_sharedPlayer != null && _sharedPlayer.PlaybackSession != null)
                {
                    _sharedPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                    _sharedPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
                }
            }
            catch
            {
            }
        }

        private static void ConfigureTransparentTitleBar()
        {
            try
            {
                var coreTitleBar = CoreApplication.GetCurrentView().TitleBar;
                coreTitleBar.ExtendViewIntoTitleBar = true;

                var titleBar = ApplicationView.GetForCurrentView().TitleBar;
                titleBar.ButtonBackgroundColor = Colors.Transparent;
                titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
                titleBar.ButtonHoverBackgroundColor = Colors.Transparent;
                titleBar.ButtonPressedBackgroundColor = Colors.Transparent;
                titleBar.ButtonForegroundColor = Colors.White;
                titleBar.ButtonHoverForegroundColor = Colors.White;
                titleBar.ButtonPressedForegroundColor = Colors.White;
                titleBar.ButtonInactiveForegroundColor = Color.FromArgb(170, 255, 255, 255);

                // Leave SetTitleBar(null): Windows keeps its default drag region at the top while
                // our video is painted underneath it. The system caption buttons stay functional.
                Window.Current.SetTitleBar(null);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Title bar customization failed: " + ex.Message);
            }
        }

        private static void RestoreHitTarget_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            RestoreToMainAsync();
        }

        private static async void RestoreToMainAsync()
        {
            if (_restoreInProgress)
            {
                return;
            }

            var action = _restoreRequested;
            var dispatcher = _mainDispatcher;
            var mainViewId = _mainViewId;
            var secondaryViewId = _viewId;
            if (action == null || dispatcher == null || mainViewId == 0 || secondaryViewId == 0)
            {
                return;
            }

            _restoreInProgress = true;
            _suppressConsolidatedCallback = true;

            try
            {
                // Release the secondary video surface FIRST. The old v5.4 path called the Video
                // page restore callback while this MediaPlayerElement was still attached. That
                // let two visual trees race around one MediaPlayer and the restored player could
                // keep the tiny PiP measure. Normal in-app mini-player restore never has this race.
                StopSecondaryUiTimers();
                DetachSecondarySurface();

                // Switch the shell back to the real main view and consolidate this PiP window.
                // This also brings the application to the foreground if another desktop app was
                // covering the main window.
                await ApplicationViewSwitcher.SwitchAsync(
                    mainViewId,
                    secondaryViewId,
                    ApplicationViewSwitchingOptions.ConsolidateViews);

                // Mark the secondary view gone before the Video page callback runs. That prevents
                // MiniPlayer.ReleasePlayerForRestore from starting a second async close operation.
                _isActive = false;
                _isOpening = false;

                // The secondary surface is gone and the shell is back on the main view. Rebind the
                // exact same MediaPlayer to the original MediaPlayerElement BEFORE the Video page
                // is navigated back into the visual tree.
                await RestoreMainVideoSurfaceAsync();

                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        Window.Current.Activate();
                    }
                    catch
                    {
                    }

                    // The callback belongs to the cached Video page. It navigates the main Frame
                    // back/forward to that player page and reattaches the exact CustomVideoPlayer.
                    action();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Restore to main view failed: " + ex.Message);

                // Even if shell switching fails, let the page restore its player rather than
                // leaving playback trapped in a detached secondary surface.
                _isActive = false;
                _isOpening = false;
                try
                {
                    await RestoreMainVideoSurfaceAsync();
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    {
                        try { Window.Current.Activate(); } catch { }
                        action();
                    });
                }
                catch
                {
                }
            }
            finally
            {
                ResetState(false);
            }
        }

        private static void SecondaryRoot_PointerActivity(object sender, PointerRoutedEventArgs e)
        {
            ShowControlsAndRestartTimer();
        }

        private static void SecondaryRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            RestartControlsHideTimer();
        }

        private static void StartSecondaryUiTimers()
        {
            StopSecondaryUiTimers();

            _controlsHideTimer = new DispatcherTimer();
            _controlsHideTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _controlsHideTimer.Tick += ControlsHideTimer_Tick;

            _progressTimer = new DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(500);
            _progressTimer.Tick += ProgressTimer_Tick;
            _progressTimer.Start();

            SetControlsVisible(true);
            RestartControlsHideTimer();
        }

        private static void StopSecondaryUiTimers()
        {
            if (_controlsHideTimer != null)
            {
                _controlsHideTimer.Stop();
                _controlsHideTimer.Tick -= ControlsHideTimer_Tick;
                _controlsHideTimer = null;
            }
            if (_progressTimer != null)
            {
                _progressTimer.Stop();
                _progressTimer.Tick -= ProgressTimer_Tick;
                _progressTimer = null;
            }
            _progressPointerDown = false;
        }

        private static void ControlsHideTimer_Tick(object sender, object e)
        {
            if (_controlsHideTimer != null)
            {
                _controlsHideTimer.Stop();
            }

            if (_progressPointerDown)
            {
                RestartControlsHideTimer();
                return;
            }

            SetControlsVisible(false);
        }

        private static void ProgressTimer_Tick(object sender, object e)
        {
            UpdateProgressSlider();
        }

        private static void ShowControlsAndRestartTimer()
        {
            SetControlsVisible(true);
            RestartControlsHideTimer();
        }

        private static void RestartControlsHideTimer()
        {
            if (_controlsHideTimer == null)
            {
                return;
            }
            _controlsHideTimer.Stop();
            _controlsHideTimer.Interval = TimeSpan.FromMilliseconds(2200);
            _controlsHideTimer.Start();
        }

        private static void SetControlsVisible(bool visible)
        {
            _controlsVisible = visible;
            if (_controlsOverlay == null)
            {
                return;
            }

            // Opacity is compositor-cheap and avoids rebuilding the visual tree. Disable hit testing
            // while hidden so a click on the video still restores the main player.
            _controlsOverlay.Opacity = visible ? 1.0 : 0.0;
            _controlsOverlay.IsHitTestVisible = visible;
        }

        private static void UpdateProgressSlider()
        {
            if (_progressSlider == null || _progressPointerDown || _sharedPlayer == null)
            {
                return;
            }

            try
            {
                var session = _sharedPlayer.PlaybackSession;
                if (session == null)
                {
                    _progressSlider.IsEnabled = false;
                    return;
                }

                var duration = session.NaturalDuration;
                var totalSeconds = duration.TotalSeconds;
                if (duration <= TimeSpan.Zero
                    || double.IsNaN(totalSeconds)
                    || double.IsInfinity(totalSeconds))
                {
                    _progressSlider.IsEnabled = false;
                    return;
                }

                var position = session.Position.TotalSeconds;
                if (double.IsNaN(position) || double.IsInfinity(position))
                {
                    position = 0;
                }

                _progressSlider.Maximum = Math.Max(1.0, totalSeconds);
                _progressSlider.Value = Math.Max(
                    _progressSlider.Minimum,
                    Math.Min(position, _progressSlider.Maximum));
                _progressSlider.IsEnabled = true;
            }
            catch
            {
                if (_progressSlider != null)
                {
                    _progressSlider.IsEnabled = false;
                }
            }
        }

        private static void ProgressHitArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            UpdateProgressSlider();
            if (_progressSlider == null || !_progressSlider.IsEnabled || _progressHitArea == null)
            {
                return;
            }

            _progressPointerDown = true;
            SetControlsVisible(true);
            if (_controlsHideTimer != null)
            {
                _controlsHideTimer.Stop();
            }

            try { _progressHitArea.CapturePointer(e.Pointer); } catch { }
            SetProgressFromPointer(e);
            e.Handled = true;
        }

        private static void ProgressHitArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            ShowControlsAndRestartTimer();
            if (!_progressPointerDown)
            {
                return;
            }

            SetProgressFromPointer(e);
            e.Handled = true;
        }

        private static void ProgressHitArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_progressPointerDown)
            {
                return;
            }

            SetProgressFromPointer(e);
            try { _progressHitArea.ReleasePointerCapture(e.Pointer); } catch { }
            CompleteProgressSeek(true);
            e.Handled = true;
        }

        private static void ProgressHitArea_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            if (!_progressPointerDown)
            {
                return;
            }

            try { _progressHitArea.ReleasePointerCapture(e.Pointer); } catch { }
            CompleteProgressSeek(false);
            e.Handled = true;
        }

        private static void ProgressHitArea_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            if (_progressPointerDown)
            {
                CompleteProgressSeek(true);
            }
        }

        private static void SetProgressFromPointer(PointerRoutedEventArgs e)
        {
            if (_progressSlider == null || _progressHitArea == null || e == null)
            {
                return;
            }

            try
            {
                var width = _progressHitArea.ActualWidth;
                if (width <= 1)
                {
                    return;
                }

                var point = e.GetCurrentPoint(_progressHitArea).Position;
                var ratio = Math.Max(0.0, Math.Min(1.0, point.X / width));
                _progressSlider.Value = _progressSlider.Minimum
                    + ((_progressSlider.Maximum - _progressSlider.Minimum) * ratio);
            }
            catch
            {
            }
        }

        private static void CompleteProgressSeek(bool commit)
        {
            if (!_progressPointerDown)
            {
                return;
            }

            _progressPointerDown = false;
            if (commit)
            {
                CommitProgressSeek();
            }
            else
            {
                UpdateProgressSlider();
            }
            ShowControlsAndRestartTimer();
        }

        private static void CommitProgressSeek()
        {
            if (_progressSlider == null || _sharedPlayer == null)
            {
                return;
            }

            try
            {
                var session = _sharedPlayer.PlaybackSession;
                if (session == null)
                {
                    return;
                }

                var total = session.NaturalDuration;
                var seconds = _progressSlider.Value;
                if (DispatchPageCommand(owner => owner.SeekTo(TimeSpan.FromSeconds(Math.Max(0, seconds))))) return;
                if (seconds < 0)
                {
                    seconds = 0;
                }
                if (total > TimeSpan.Zero && seconds > total.TotalSeconds)
                {
                    seconds = total.TotalSeconds;
                }

                var wasPlaying = session.PlaybackState == MediaPlaybackState.Playing
                    || session.PlaybackState == MediaPlaybackState.Buffering;

                // Mini-player handoff is allowed only for the single-player path, so one settled
                // seek on release is safe and avoids the heavy seek-per-pixel behavior that hurts
                // demuxed playback on slower PCs.
                session.Position = TimeSpan.FromSeconds(seconds);
                if (wasPlaying)
                {
                    _sharedPlayer.Play();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Seek failed: " + ex.Message);
            }
        }

        private static void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            ShowControlsAndRestartTimer();
            try
            {
                if (_sharedPlayer == null || _sharedPlayer.PlaybackSession == null)
                {
                    return;
                }

                var state = _sharedPlayer.PlaybackSession.PlaybackState;
                if (state == MediaPlaybackState.Playing || state == MediaPlaybackState.Buffering)
                {
                    if (DispatchPageCommand(owner => owner.Pause())) return;
                    _sharedPlayer.Pause();
                }
                else
                {
                    if (DispatchPageCommand(owner => owner.Play())) return;
                    _sharedPlayer.Play();
                }
                UpdatePlayPauseIcon();
                UpdateProgressSlider();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Play/Pause failed: " + ex.Message);
            }
        }

        private static void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            var dispatcher = _secondaryDispatcher;
            if (dispatcher == null)
            {
                return;
            }
            dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                UpdatePlayPauseIcon();
                UpdateProgressSlider();
            });
        }

        private static void UpdatePlayPauseIcon()
        {
            if (_playPauseIcon == null || _sharedPlayer == null || _sharedPlayer.PlaybackSession == null)
            {
                return;
            }

            try
            {
                var state = _sharedPlayer.PlaybackSession.PlaybackState;
                var path = state == MediaPlaybackState.Playing || state == MediaPlaybackState.Buffering
                    ? "Assets/Dark/player/pause.png"
                    : "Assets/Dark/player/play.png";
                App.SetThemeImageSource(_playPauseIcon, path);
            }
            catch
            {
            }
        }

        private static bool DispatchPageCommand(Action<CustomVideoPlayer> command)
        {
            if (!_keepPageOpen) return false;
            var owner = _ownerPlayer;
            var shared = _sharedPlayer;
            var dispatcher = _mainDispatcher;
            if (owner != null && dispatcher != null)
            {
                var pending = dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    if (!IsPageWindowFor(owner) || !owner.CanRestoreDesktopSurface(shared) || owner.IsSourceLoading) return;
                    command(owner);
                });
            }
            return true;
        }

        private static async Task RestoreMainVideoSurfaceAsync()
        {
            var dispatcher = _mainDispatcher;
            var ownerElement = _ownerMediaElement;
            var owner = _ownerPlayer;
            var shared = _sharedPlayer;
            var keepPageOpen = _keepPageOpen;

            if (dispatcher == null || ownerElement == null || shared == null)
            {
                return;
            }

            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.High, () =>
                {
                    try
                    {
                        // Navigation can release this player while the secondary
                        // window is closing. Never reattach a disposed or replaced player.
                        if (owner == null || !owner.CanRestoreDesktopSurface(shared)) return;
                        // Force a fresh video-surface association. Reassigning the same MediaPlayer
                        // without the null step is not sufficient on some Windows 10 builds after
                        // the player was also attached to a MediaPlayerElement in a secondary view.
                        ownerElement.SetMediaPlayer(null);
                        ownerElement.SetMediaPlayer(shared);
                        ownerElement.Stretch = Stretch.Uniform;
                        ownerElement.HorizontalAlignment = HorizontalAlignment.Stretch;
                        ownerElement.VerticalAlignment = VerticalAlignment.Stretch;

                        // Restore only the CustomVideoPlayer's visual mode here. The Video page
                        // remains the authority for Width/Height and performs a full layout reset
                        // after it is back in the main visual tree.
                        if (owner != null)
                        {
                            if (keepPageOpen) owner.SetDesktopMiniPlayerMode(false);
                            else owner.SetMiniMode(false);
                            owner.InvalidateMeasure();
                            owner.InvalidateArrange();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[DesktopMiniPlayer] Main video surface rebind failed: " + ex.Message);
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[DesktopMiniPlayer] Main dispatcher rebind failed: " + ex.Message);
            }
        }

        private static async void SecondaryView_Consolidated(
            ApplicationView sender,
            ApplicationViewConsolidatedEventArgs args)
        {
            if (_suppressConsolidatedCallback)
            {
                return;
            }

            try
            {
                DetachSecondarySurface();
            }
            catch
            {
            }

            _isActive = false;
            _isOpening = false;
            _restoreInProgress = true;
            var close = _closeRequested;
            var dispatcher = _mainDispatcher;

            // System Close on the secondary window still needs to return the MediaPlayer to
            // the cached CustomVideoPlayer before MiniPlayer.Close releases playback.
            await RestoreMainVideoSurfaceAsync();
            ResetState(false);
            if (close != null && dispatcher != null)
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => close());
            }
        }

        private static void DetachSecondarySurface()
        {
            StopSecondaryUiTimers();
            if (_aspectResizeTimer != null)
            {
                _aspectResizeTimer.Stop();
                _aspectResizeTimer.Tick -= AspectResizeTimer_Tick;
                _aspectResizeTimer = null;
            }
            if (_secondaryWindow != null)
            {
                _secondaryWindow.SizeChanged -= SecondaryWindow_SizeChanged;
                _secondaryWindow = null;
            }
            _aspectResizeVersion++;

            try
            {
                if (_sharedPlayer != null && _sharedPlayer.PlaybackSession != null)
                {
                    _sharedPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                }
            }
            catch
            {
            }

            try
            {
                if (_mediaElement != null)
                {
                    _mediaElement.SetMediaPlayer(null);
                }
            }
            catch
            {
            }

            _mediaElement = null;
            _controlsOverlay = null;
            _playPauseButton = null;
            _playPauseIcon = null;
            _progressSlider = null;
            _progressHitArea = null;
            _controlsVisible = false;
            _progressPointerDown = false;
        }

        private static async Task CleanupFailedOpenAsync()
        {
            var dispatcher = _secondaryDispatcher;
            if (dispatcher == null)
            {
                return;
            }
            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    DetachSecondarySurface();
                    try
                    {
                        Window.Current.Content = null;
                    }
                    catch
                    {
                    }
                });
            }
            catch
            {
            }

            // If secondary view creation fails, the in-app fallback will immediately reuse the
            // original CustomVideoPlayer, so restore its video surface first.
            await RestoreMainVideoSurfaceAsync();
        }

        public static async Task<bool> CloseForRestoreAsync()
        {
            return await CloseViewAsync();
        }

        public static async Task<bool> CloseForFinalAsync()
        {
            return await CloseViewAsync();
        }

        private static async Task<bool> CloseViewAsync()
        {
            if (_closingTask != null && !_closingTask.IsCompleted) return await _closingTask;
            _closingTask = CloseViewCoreAsync();
            return await _closingTask;
        }

        private static async Task<bool> CloseViewCoreAsync()
        {
            // A second click or navigation may happen before CreateNewView finishes.
            // Let its setup settle before detaching and closing that exact view.
            var opening = _openingTask;
            if (_isOpening && opening != null) await opening;
            if (!_isActive && !_isOpening)
            {
                return true;
            }

            var dispatcher = _secondaryDispatcher;
            if (dispatcher == null)
            {
                ResetState(false);
                return true;
            }

            var completion = new TaskCompletionSource<bool>();
            try
            {
                await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try
                    {
                        _suppressConsolidatedCallback = true;
                        DetachSecondarySurface();
                        completion.TrySetResult(true);
                        var coreWindow = CoreWindow.GetForCurrentThread();
                        if (coreWindow != null)
                        {
                            coreWindow.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Close failed inside secondary view: " + ex.Message);
                        completion.TrySetResult(false);
                    }
                });
                var result = await completion.Task;
                await RestoreMainVideoSurfaceAsync();
                ResetState(false);
                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Close failed: " + ex.Message);
                await RestoreMainVideoSurfaceAsync();
                ResetState(false);
                return false;
            }
        }

        private static void ClearSecondaryOnly()
        {
            _viewId = 0;
            _view = null;
            _secondaryDispatcher = null;
            _secondaryWindow = null;
            _aspectResizeTimer = null;
            _lastWindowSize = new Size();
            _pendingWindowSize = new Size();
            _lastResizeSourceSize = new Size();
            _lastRequestedWindowSize = new Size();
            _resizingWindow = false;
            _suppressConsolidatedCallback = false;
            _restoreInProgress = false;
            _controlsOverlay = null;
            _playPauseButton = null;
            _playPauseIcon = null;
            _progressSlider = null;
            _progressHitArea = null;
            _controlsHideTimer = null;
            _progressTimer = null;
            _progressPointerDown = false;
            _controlsVisible = false;
        }
#endif

#if !VS2017_DESKTOP_PIP
        // VS2015 has no external mini-player view, but MiniPlayer calls both
        // cleanup methods regardless of the SDK used to build the app.
        public static Task<bool> CloseForRestoreAsync() { return Task.FromResult(true); }
        public static Task<bool> CloseForFinalAsync() { return Task.FromResult(true); }
#endif

        private static void ResetState(bool keepCallbacks)
        {
            _windowGeneration++;
            _isOpening = false;
            _isActive = false;
            _viewId = 0;
            _mainViewId = 0;
            _view = null;
            _secondaryDispatcher = null;
            _secondaryWindow = null;
            _aspectResizeTimer = null;
            _lastWindowSize = new Size();
            _pendingWindowSize = new Size();
            _lastResizeSourceSize = new Size();
            _lastRequestedWindowSize = new Size();
            _resizingWindow = false;
            _mediaElement = null;
            _controlsOverlay = null;
            _playPauseButton = null;
            _playPauseIcon = null;
            _progressSlider = null;
            _progressHitArea = null;
            _controlsHideTimer = null;
            _progressTimer = null;
            _progressPointerDown = false;
            _controlsVisible = false;
            _sharedPlayer = null;
            _ownerPlayer = null;
            _ownerMediaElement = null;
            _suppressConsolidatedCallback = false;
            _restoreInProgress = false;
            _keepPageOpen = false;
            _openingTask = null;
            _aspectResizeVersion++;
            _videoAspectRatio = DefaultAspectRatio;
            if (!keepCallbacks)
            {
                _restoreRequested = null;
                _closeRequested = null;
                _failed = null;
                _mainDispatcher = null;
            }
        }
    }
}
