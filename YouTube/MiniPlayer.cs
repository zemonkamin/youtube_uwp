using System;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
namespace YouTube
{
    // A floating, always-on-top mini-player, like the official app. The SAME CustomVideoPlayer
    // control is reparented into it from the video page, so playback (and its demuxer) is never
    // interrupted. It lives at the window/view level, so it survives page navigations, and stays
    // up until the user taps its close button.
    internal static class MiniPlayer
    {
        private static Popup _popup;
        private static Border _frame;
        private static Grid _root;
        private static Grid _videoHost;
        private static Image _playPauseIcon;
        private static Button _playPauseButton;
        private static Button _closeButton;
        private static CustomVideoPlayer _player;
        private static Action _onRestore;
        private static Action _onClosed;
        // Drag state.
        private static bool _pointerDown;
        private static bool _dragging;
        private static Point _dragStart;
        private static double _startOffsetX;
        private static double _startOffsetY;
        // Resize state. During the gesture we only scale the already-rendered card with a
        // RenderTransform; the real video surface is resized once, on release. That avoids
        // rebuilding the MediaElement/SwapChain on every pointer move on slower devices.
        [Flags]
        private enum ResizeEdges
        {
            None = 0,
            Left = 1,
            Top = 2,
            Right = 4,
            Bottom = 8
        }
        private static bool _resizing;
        private static ResizeEdges _resizeEdges;
        private static Point _resizeStartPointer;
        private static double _resizeStartWidth;
        private static double _resizeStartHeight;
        private static double _resizeStartOffsetX;
        private static double _resizeStartOffsetY;
        private static double _resizePreviewScale = 1.0;
        private static CompositeTransform _resizeTransform;
        // Cached system cursors: changing the cursor must not allocate a new CoreCursor on every
        // PointerMoved event. These cursor types exist from the first Windows 10 UWP contract.
        private static CoreCursor _cursorArrow;
        private static CoreCursor _cursorSizeWE;
        private static CoreCursor _cursorSizeNS;
        private static CoreCursor _cursorSizeNWSE;
        private static CoreCursor _cursorSizeNESW;
        // Edge-snap animation. Popup offsets are updated on a small DispatcherTimer instead of
        // teleporting to the side, which keeps this compatible with the old VS2015 UWP target
        // (including Windows 10 Mobile) without relying on newer Composition APIs.
        private static DispatcherTimer _snapTimer;
        private static DateTime _snapStartedAt;
        private static double _snapStartX;
        private static double _snapStartY;
        private static double _snapTargetX;
        private static double _snapTargetY;
        // True while the whole app window is in CompactOverlay ("always on top") mode. Desktop
        // only — Windows 10 Mobile has no windowed mode and reports it as unsupported.
        private static bool _compactOverlay;
        private static DispatcherTimer _compactResizeTimer;
        private static int _compactRefitAttempts;
        // Size we ask the shell for when going always-on-top, and the upper bound the shell allows
        // for that mode. Anything wider means the window has not actually been resized yet.
        private const double CompactWidth = 360;
        private const double CompactHeight = 202; // 16:9
        private const double CompactMaxEdge = 520;
        private const double MiniDefaultLongEdge = 220;
        private const double MiniMinimumLongEdge = 150;
        private const double MiniMaximumLongEdge = 420;
        // Deliberately generous mouse hit targets. The side strips stay narrow enough to leave
        // a large drag area, while corners are wider because diagonal resize is harder to hit.
        private const double ResizeHandleThickness = 22;
        private const double ResizeCornerSize = 36;
        // Rounded card radius close to the official YouTube mini-player. Border.CornerRadius
        // clips its child in UWP, so this stays a cheap compositor operation instead of a blur/mask.
        private const double MiniCornerRadius = 12;
        private const double DefaultMiniAspectRatio = 16.0 / 9.0;
        private static double _miniAspectRatio = DefaultMiniAspectRatio;
        private static double _miniLongEdge = MiniDefaultLongEdge;
        private static double _miniWidth = MiniDefaultLongEdge;
        private static double _miniHeight = MiniDefaultLongEdge / DefaultMiniAspectRatio;
        private const double EdgeMargin = 10;
        private const double BottomInset = 68; // clear a bottom tab bar on the page behind it
        private const double DragThreshold = 8;
        private const double SnapAnimationMilliseconds = 220;
        public static bool IsActive
        {
            get
            {
                return (_popup != null && _popup.IsOpen && _player != null)
                    || (_player != null && (DesktopMiniPlayerWindow.IsActive
                        || DesktopMiniPlayerWindow.IsOpening));
            }
        }

        public static CustomVideoPlayer ActivePlayer { get { return _player; } }
        // Reparents the given player into the mini window and shows it. onRestore is invoked when
        // the user taps the free area (expand back); onClosed when the user taps the X.
        public static void Show(
            CustomVideoPlayer player,
            double videoAspectRatio,
            Action onRestore,
            Action onClosed)
        {
            if (player == null)
            {
                return;
            }

            _pointerDown = false;
            _dragging = false;
            _resizing = false;
            _resizeEdges = ResizeEdges.None;
            ResetResizePreview();
            ApplyVideoAspectRatio(videoAspectRatio);
            _player = player;
            _onRestore = onRestore;
            _onClosed = onClosed;

            player.HorizontalAlignment = HorizontalAlignment.Stretch;
            player.VerticalAlignment = VerticalAlignment.Stretch;
            player.Width = double.NaN;
            player.Height = double.NaN;
            player.MinHeight = 0;
            player.Margin = new Thickness(0);
            try { player.SuspendNonPlaybackVisuals(); } catch { }
            player.SetMiniMode(true);

            // VS2017 + SDK 15063 desktop builds can use a real secondary CompactOverlay view.
            // The SAME MediaPlayer instance is attached to that view, so there is no second
            // network stream or decoder. On VS2015, Mobile, or unsupported Windows builds this
            // returns false and we keep the lightweight in-app popup.
            if (DesktopMiniPlayerSetting.IsEnabled()
                && DesktopMiniPlayerWindow.TryShow(
                    player,
                    videoAspectRatio,
                    HandleExternalRestoreRequested,
                    HandleExternalCloseRequested,
                    ShowInAppFallbackFromExternal))
            {
                StopSnapAnimation(false);
                return;
            }

            ShowInAppFallbackFromExternal();
        }

        private static void ShowInAppFallbackFromExternal()
        {
            if (_player == null)
            {
                return;
            }

            EnsureUi();
            _videoHost.Children.Clear();
            _videoHost.Children.Add(_player);
            UpdatePlayPauseIcon();
            StopSnapAnimation(false);
            PositionPopup();
            _popup.IsOpen = true;
        }

        private static void HandleExternalRestoreRequested()
        {
            var restore = _onRestore;
            if (restore != null)
            {
                try { restore(); } catch { }
            }
        }

        private static void HandleExternalCloseRequested()
        {
            Close();
        }
        // Keep the popup bounds in the video's aspect ratio. A fixed 16:9 host caused black
        // letterbox bars for square, 4:3 and vertical videos. The user's chosen long-edge size is
        // retained instead of snapping back to the default after every metadata update.
        public static void UpdateAspectRatio(double videoAspectRatio)
        {
            ApplyVideoAspectRatio(videoAspectRatio);

            if (DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening)
            {
                DesktopMiniPlayerWindow.UpdateAspectRatio(videoAspectRatio);
                return;
            }

            if (IsActive && !_compactOverlay)
            {
                PositionPopup();
            }
        }
        private static void ApplyVideoAspectRatio(double videoAspectRatio)
        {
            if (videoAspectRatio <= 0
                || double.IsNaN(videoAspectRatio)
                || double.IsInfinity(videoAspectRatio)
                || videoAspectRatio < 0.45
                || videoAspectRatio > 3.5)
            {
                videoAspectRatio = DefaultMiniAspectRatio;
            }
            _miniAspectRatio = videoAspectRatio;
            // Preserve the user's chosen mini-player size when the stream/aspect ratio changes.
            // Clamp again because a very tall/wide video can have a different screen-fit limit.
            _miniLongEdge = ClampLongEdge(_miniLongEdge);
            UpdateMiniDimensionsFromLongEdge();

            if (_root != null && !_compactOverlay)
            {
                _root.Width = _miniWidth;
                _root.Height = _miniHeight;
            }
            System.Diagnostics.Debug.WriteLine(
                "[MiniPlayer] Video aspect " + videoAspectRatio.ToString("0.###")
                + ", popup " + _miniWidth.ToString("0.#") + "x" + _miniHeight.ToString("0.#")
            );
        }
        private static void UpdateMiniDimensionsFromLongEdge()
        {
            if (_miniAspectRatio >= 1.0)
            {
                _miniWidth = _miniLongEdge;
                _miniHeight = _miniLongEdge / _miniAspectRatio;
            }
            else
            {
                _miniHeight = _miniLongEdge;
                _miniWidth = _miniLongEdge * _miniAspectRatio;
            }
        }
        private static double ClampLongEdge(double value)
        {
            var min = MiniMinimumLongEdge;
            // Very narrow portrait videos still need enough horizontal room for both 32px buttons.
            if (_miniAspectRatio < 1.0)
            {
                min = Math.Max(min, 84.0 / _miniAspectRatio);
            }

            var max = MiniMaximumLongEdge;
            try
            {
                var bounds = Window.Current.Bounds;
                var maxWidth = Math.Max(80.0, bounds.Width - (EdgeMargin * 2.0));
                var maxHeight = Math.Max(80.0, bounds.Height - (EdgeMargin * 2.0));
                if (_miniAspectRatio >= 1.0)
                {
                    max = Math.Min(max, Math.Min(maxWidth, maxHeight * _miniAspectRatio));
                }
                else
                {
                    max = Math.Min(max, Math.Min(maxHeight, maxWidth / _miniAspectRatio));
                }
            }
            catch
            {
            }

            // Tiny app windows can make the screen-fit maximum smaller than our normal minimum.
            if (max < min)
            {
                min = max;
            }
            return Math.Max(min, Math.Min(value, max));
        }
        // Removes a player from its current parent panel so it can be handed to the mini-player.
        public static void DetachFromParent(CustomVideoPlayer player)
        {
            if (player == null)
            {
                return;
            }

            var parent = player.Parent as Panel;
            if (parent != null)
            {
                parent.Children.Remove(player);
            }
        }
        // Hands the live player back to the page WITHOUT disposing it — used when expanding the
        // mini-player back to full screen. The caller reattaches it into its own layout.
        public static CustomVideoPlayer ReleasePlayerForRestore()
        {
            if (DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening)
            {
                ExitCompactOverlay();
            }
            StopSnapAnimation(false);
            _pointerDown = false;
            _dragging = false;
            _resizing = false;
            _resizeEdges = ResizeEdges.None;
            ResetResizePreview();
            SetResizeCursor(ResizeEdges.None);

            var player = _player;
            _player = null;
            _onRestore = null;
            _onClosed = null;
            if (_videoHost != null)
            {
                _videoHost.Children.Clear();
            }
            if (_popup != null)
            {
                _popup.IsOpen = false;
            }
            if (player != null)
            {
                // Restore the normal player visuals only after it leaves the tiny popup.
                try { player.SetMiniMode(false); } catch { }
                try { player.ResumeNonPlaybackVisuals(); } catch { }
            }

            return player;
        }

        // Tears the mini-player down and releases the shared player for good.
        public static void Close()
        {
            if (DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening)
            {
                CloseExternalThenFinalize();
                return;
            }
            FinalizeClose();
        }

        private static async void CloseExternalThenFinalize()
        {
            try { await DesktopMiniPlayerWindow.CloseForFinalAsync(); } catch { }
            FinalizeClose();
        }

        private static void FinalizeClose()
        {
            StopSnapAnimation(false);
            _pointerDown = false;
            _dragging = false;
            _resizing = false;
            _resizeEdges = ResizeEdges.None;
            ResetResizePreview();
            SetResizeCursor(ResizeEdges.None);
            var player = _player;
            var onClosed = _onClosed;
            _player = null;
            _onRestore = null;
            _onClosed = null;

            if (_videoHost != null)
            {
                _videoHost.Children.Clear();
            }
            if (player != null)
            {
                // Release the media, but do NOT dispose the control: the video page that owns it is
                // cached and may be navigated to again, and it would then hold a dead player.
                // ResetForReuse (called by the page on its next load) makes it playable again.
                try { player.SetMiniMode(false); } catch { }
                try { player.StopAndReleasePlayback(); } catch { }
            }
            if (_popup != null)
            {
                _popup.IsOpen = false;
            }

            if (onClosed != null)
            {
                try { onClosed(); } catch { }
            }
        }

        private static void EnsureUi()
        {
            if (_popup != null)
            {
                return;
            }

            _videoHost = new Grid { Background = new SolidColorBrush(Colors.Black) };
            // Close (top-right).
            var closeButton = MakeGlyphButton(""); // Segoe MDL2 "Cancel" (X)
            closeButton.HorizontalAlignment = HorizontalAlignment.Right;
            closeButton.VerticalAlignment = VerticalAlignment.Top;
            closeButton.Margin = new Thickness(0, 4, 4, 0);
            closeButton.Click += (s, e) => Close();
            _closeButton = closeButton;
            // Play/pause (top-left). Use the same circular dimmed chrome as the normal player.
            var playPauseButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["MiniPlayerControlButtonStyle"],
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0)
            };
            _playPauseIcon = new Image
            {
                Width = 18,
                Height = 18
            };
            App.SetThemeImageSource(_playPauseIcon, "Assets/Dark/player/pause.png");
            playPauseButton.Content = _playPauseIcon;
            playPauseButton.Click += (s, e) => TogglePlayPause();
            _playPauseButton = playPauseButton;
            _root = new Grid
            {
                Width = _miniWidth,
                Height = _miniHeight,
                Background = new SolidColorBrush(Colors.Black)
            };
            _root.Children.Add(_videoHost);
            // Transparent hit zones make the outer edge easy to grab even when the live video
            // control consumes pointer input. Buttons are added after these zones, so they remain
            // fully clickable in the two top corners.
            AddResizeHitZones();
            _root.Children.Add(playPauseButton);
            _root.Children.Add(closeButton);
            // Drag to move (with edge snap) / tap the free area to expand back.
            _root.PointerPressed += Root_PointerPressed;
            _root.PointerMoved += Root_PointerMoved;
            _root.PointerReleased += Root_PointerReleased;
            _root.PointerCanceled += Root_PointerReleased;
            _root.PointerCaptureLost += Root_PointerReleased;
            // One rounded Border clips the whole live player (video + mini controls). This is
            // intentionally simpler than a Composition/Win2D mask: no extra frame copy, blur or
            // per-frame geometry updates, so it remains smooth on weak Windows 10 Mobile devices.
            _resizeTransform = new CompositeTransform();
            _frame = new Border
            {
                Background = new SolidColorBrush(Colors.Black),
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(MiniCornerRadius),
                Child = _root,
                RenderTransform = _resizeTransform
            };

            _popup = new Popup { Child = _frame, IsLightDismissEnabled = false };
        }

        private static void AddResizeHitZones()
        {
            // Four side strips + four larger corners. Keeping them as simple transparent Borders
            // is cheaper than continuously doing geometry hit-testing and also makes pointer hover
            // reliable even when the live video control underneath consumes pointer input.
            AddResizeHitZone(
                ResizeEdges.Left,
                ResizeHandleThickness, double.NaN,
                HorizontalAlignment.Left, VerticalAlignment.Stretch,
                new Thickness(0, ResizeCornerSize, 0, ResizeCornerSize));
            AddResizeHitZone(
                ResizeEdges.Right,
                ResizeHandleThickness, double.NaN,
                HorizontalAlignment.Right, VerticalAlignment.Stretch,
                new Thickness(0, ResizeCornerSize, 0, ResizeCornerSize));
            AddResizeHitZone(
                ResizeEdges.Top,
                double.NaN, ResizeHandleThickness,
                HorizontalAlignment.Stretch, VerticalAlignment.Top,
                new Thickness(ResizeCornerSize, 0, ResizeCornerSize, 0));
            AddResizeHitZone(
                ResizeEdges.Bottom,
                double.NaN, ResizeHandleThickness,
                HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
                new Thickness(ResizeCornerSize, 0, ResizeCornerSize, 0));

            // Corners go after the side strips, so the diagonal cursor/gesture wins in overlaps.
            AddResizeHitZone(
                ResizeEdges.Left | ResizeEdges.Top,
                ResizeCornerSize, ResizeCornerSize,
                HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(0));
            AddResizeHitZone(
                ResizeEdges.Right | ResizeEdges.Top,
                ResizeCornerSize, ResizeCornerSize,
                HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0));
            AddResizeHitZone(
                ResizeEdges.Left | ResizeEdges.Bottom,
                ResizeCornerSize, ResizeCornerSize,
                HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(0));
            AddResizeHitZone(
                ResizeEdges.Right | ResizeEdges.Bottom,
                ResizeCornerSize, ResizeCornerSize,
                HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0));
        }

        private static void AddResizeHitZone(
            ResizeEdges edges,
            double width,
            double height,
            HorizontalAlignment horizontalAlignment,
            VerticalAlignment verticalAlignment,
            Thickness margin)
        {
            var zone = new Border
            {
                Background = new SolidColorBrush(Colors.Transparent),
                HorizontalAlignment = horizontalAlignment,
                VerticalAlignment = verticalAlignment,
                Margin = margin,
                Tag = edges
            };
            if (!double.IsNaN(width)) zone.Width = width;
            if (!double.IsNaN(height)) zone.Height = height;

            zone.PointerEntered += ResizeZone_PointerEntered;
            zone.PointerMoved += ResizeZone_PointerMoved;
            zone.PointerExited += ResizeZone_PointerExited;
            _root.Children.Add(zone);
        }

        private static void ResizeZone_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_compactOverlay)
            {
                SetResizeCursor(ResizeEdges.None);
                return;
            }
            SetResizeCursor(GetTaggedResizeEdges(sender as FrameworkElement));
        }

        private static void ResizeZone_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_compactOverlay)
            {
                return;
            }
            // Keep the resize cursor stable while captured, even when the pointer briefly leaves
            // the original transparent Border during a fast drag.
            SetResizeCursor(_resizing ? _resizeEdges : GetTaggedResizeEdges(sender as FrameworkElement));
        }

        private static void ResizeZone_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (!_resizing)
            {
                SetResizeCursor(ResizeEdges.None);
            }
        }

        private static ResizeEdges GetTaggedResizeEdges(FrameworkElement element)
        {
            if (element != null && element.Tag is ResizeEdges)
            {
                return (ResizeEdges)element.Tag;
            }
            return ResizeEdges.None;
        }

        private static void EnsureResizeCursors()
        {
            if (_cursorArrow != null)
            {
                return;
            }
            _cursorArrow = new CoreCursor(CoreCursorType.Arrow, 0);
            _cursorSizeWE = new CoreCursor(CoreCursorType.SizeWestEast, 0);
            _cursorSizeNS = new CoreCursor(CoreCursorType.SizeNorthSouth, 0);
            _cursorSizeNWSE = new CoreCursor(CoreCursorType.SizeNorthwestSoutheast, 0);
            _cursorSizeNESW = new CoreCursor(CoreCursorType.SizeNortheastSouthwest, 0);
        }

        private static void SetResizeCursor(ResizeEdges edges)
        {
            try
            {
                EnsureResizeCursors();
                CoreCursor cursor = _cursorArrow;
                var horizontal = (edges & (ResizeEdges.Left | ResizeEdges.Right)) != 0;
                var vertical = (edges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;

                if (horizontal && vertical)
                {
                    var nwse = ((edges & ResizeEdges.Left) != 0 && (edges & ResizeEdges.Top) != 0)
                        || ((edges & ResizeEdges.Right) != 0 && (edges & ResizeEdges.Bottom) != 0);
                    cursor = nwse ? _cursorSizeNWSE : _cursorSizeNESW;
                }
                else if (horizontal)
                {
                    cursor = _cursorSizeWE;
                }
                else if (vertical)
                {
                    cursor = _cursorSizeNS;
                }

                Window.Current.CoreWindow.PointerCursor = cursor;
            }
            catch
            {
                // Cursor feedback is desktop-only polish. Never let it affect touch/mobile resize.
            }
        }

        private static Button MakeGlyphButton(string glyph)
        {
            return new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Style = (Style)Application.Current.Resources["MiniPlayerControlButtonStyle"],
                Foreground = new SolidColorBrush(Colors.White),
                Content = new TextBlock
                {
                    Text = glyph,
                    FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15,
                    Foreground = new SolidColorBrush(Colors.White),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
        private static void PositionPopup()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                _popup.HorizontalOffset = Math.Max(EdgeMargin, bounds.Width - _miniWidth - EdgeMargin);
                _popup.VerticalOffset = Math.Max(EdgeMargin, bounds.Height - _miniHeight - BottomInset);
            }
            catch
            {
            }
        }
        private static void TogglePlayPause()
        {
            if (_player == null)
            {
                return;
            }
            try
            {
                if (_player.IsPlaying)
                {
                    _player.Pause();
                }
                else
                {
                    _player.Play();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Toggle failed: " + ex.Message);
            }

            UpdatePlayPauseIcon();
        }
        // --- CompactOverlay ("always on top") ------------------------------------------------
        // On desktop the whole app window can become a small always-on-top window, which is the
        // only way a mini-player can float above OTHER apps. Windows 10 Mobile has no windowed
        // mode and reports this as unsupported, so the phone keeps the in-app popup unchanged.
        private static bool IsCompactOverlaySupported()
        {
            return DesktopMiniPlayerSetting.IsAvailable();
        }
        private static void TryEnterCompactOverlay()
        {
            // Kept for source compatibility with the previous mini-player implementation.
            // The main app window is never converted to CompactOverlay anymore; the VS2017
            // path uses DesktopMiniPlayerWindow, a true secondary view.
            _compactOverlay = false;
        }

        public static bool IsCompactOverlayActive
        {
            get { return DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening; }
        }
        public static async System.Threading.Tasks.Task<bool> LeaveCompactOverlayAsync()
        {
            if (DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening)
            {
                try { return await DesktopMiniPlayerWindow.CloseForRestoreAsync(); }
                catch { return false; }
            }
            return true;
        }

        private static async void ExitCompactOverlay()
        {
            await LeaveCompactOverlayAsync();
        }
        // Resizing the video surface tears down and rebuilds the swap chain, and doing that on every
        // intermediate size a window drag produces is what makes the demuxed source fail. Wait for
        // the drag to settle and apply the final size once.
        private static void CompactOverlay_WindowSizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            if (!_compactOverlay)
            {
                return;
            }
            if (e != null)
            {
                // A genuine window resize — the retry budget is only for our own re-measurements.
                _compactRefitAttempts = 0;
            }

            if (_compactResizeTimer == null)
            {
                _compactResizeTimer = new DispatcherTimer();
                _compactResizeTimer.Interval = TimeSpan.FromMilliseconds(120);
                _compactResizeTimer.Tick += CompactResizeTimer_Tick;
            }
            _compactResizeTimer.Stop();
            _compactResizeTimer.Start();
        }
        // In compact overlay Windows draws its own title bar ON TOP of the client area, which
        // covered our pause/close buttons. Move them to the bottom corners for that mode — the
        // title bar only ever occupies the top edge.
        private static void ApplyCompactChrome(bool compact)
        {
            try
            {
                if (_closeButton != null)
                {
                    _closeButton.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    _closeButton.Margin = compact ? new Thickness(0, 0, 4, 4) : new Thickness(0, 4, 4, 0);
                }
                if (_playPauseButton != null)
                {
                    _playPauseButton.VerticalAlignment = compact ? VerticalAlignment.Bottom : VerticalAlignment.Top;
                    _playPauseButton.Margin = compact ? new Thickness(4, 0, 0, 4) : new Thickness(4, 4, 0, 0);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Chrome layout failed: " + ex.Message);
            }
        }
        private static void CompactResizeTimer_Tick(object sender, object e)
        {
            _compactResizeTimer.Stop();
            FillWindowForCompactOverlay();
        }
        // The shell can apply the window resize before, during or after TryEnterViewModeAsync
        // returns, so a SizeChanged we can observe is not guaranteed. This re-measures shortly
        // after, whatever the ordering was.
        private static void ScheduleCompactRefit()
        {
            // Bounded, so an overlay that never actually shrinks cannot spin forever.
            if (_compactRefitAttempts >= 16)
            {
                return;
            }
            _compactRefitAttempts++;
            CompactOverlay_WindowSizeChanged(null, null);
        }

        // In compact overlay the tiny window IS the mini-player, so the video fills it edge to edge
        // and the popup sits at the origin.
        private static void FillWindowForCompactOverlay()
        {
            if (!_compactOverlay || _root == null || _popup == null)
            {
                return;
            }
            try
            {
                var bounds = Window.Current.Bounds;
                // The compact overlay window is small by definition. A larger size means the
                // resize has not been applied yet (or we are already on the way out), and
                // stretching to it would cover the whole screen — ignore it and wait for the
                // settled size instead.
                // Both edges have to be small: a tall, narrow main window slips past a width-only
                // check and the mini-player ends up stretched down the whole window.
                if (bounds.Width > CompactMaxEdge || bounds.Height > CompactMaxEdge
                    || bounds.Width <= 0 || bounds.Height <= 0)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[MiniPlayer] Ignoring unsettled compact size " + bounds.Width + "x" + bounds.Height);
                    ScheduleCompactRefit();
                    return;
                }
                // Re-applying the same size still rebuilds the video surface, so only touch it when
                // it actually changed.
                if (Math.Abs(_root.Width - bounds.Width) < 0.5 && Math.Abs(_root.Height - bounds.Height) < 0.5)
                {
                    return;
                }
                _root.Width = bounds.Width;
                _root.Height = bounds.Height;
                _popup.HorizontalOffset = 0;
                _popup.VerticalOffset = 0;
                System.Diagnostics.Debug.WriteLine(
                    "[MiniPlayer] Compact overlay fitted to " + bounds.Width + "x" + bounds.Height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Compact resize failed: " + ex.Message);
            }
        }
        private static void Root_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // If the user grabs the card while it is still gliding to an edge, continue the drag
            // from the exact current position instead of waiting for the snap to finish.
            StopSnapAnimation(false);
            if (IsButtonSource(e.OriginalSource as DependencyObject))
            {
                // Let play/pause and close own their gesture completely; otherwise a bubbled
                // PointerReleased could be mistaken for a tap-to-restore.
                _pointerDown = false;
                return;
            }
            // In compact overlay the OS moves the window itself, so in-popup dragging is off —
            // only the tap-to-expand gesture stays.
            if (_compactOverlay)
            {
                _pointerDown = true;
                _dragging = false;
                return;
            }
            _pointerDown = true;
            _dragging = false;
            _resizing = false;

            // Resize takes priority only in the thin outer hit zone. A normal press in the body
            // keeps the existing drag/tap behaviour. Button presses are left to the buttons.
            {
                var local = e.GetCurrentPoint(_root).Position;
                var edges = GetResizeEdges(local);
                if (edges != ResizeEdges.None)
                {
                    BeginResize(e, edges);
                    return;
                }
            }

            SetResizeCursor(ResizeEdges.None);
            _dragStart = e.GetCurrentPoint(null).Position;
            _startOffsetX = _popup.HorizontalOffset;
            _startOffsetY = _popup.VerticalOffset;
            try { _root.CapturePointer(e.Pointer); } catch { }
        }

        private static void Root_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown || _compactOverlay)
            {
                return;
            }
            var p = e.GetCurrentPoint(null).Position;

            if (_resizing)
            {
                UpdateResizePreview(p);
                e.Handled = true;
                return;
            }

            var dx = p.X - _dragStart.X;
            var dy = p.Y - _dragStart.Y;

            if (!_dragging && (Math.Abs(dx) > DragThreshold || Math.Abs(dy) > DragThreshold))
            {
                _dragging = true;
            }
            if (_dragging)
            {
                var bounds = Window.Current.Bounds;
                var x = _startOffsetX + dx;
                var y = _startOffsetY + dy;
                // Keep it fully on screen while dragging.
                x = Math.Max(0, Math.Min(x, bounds.Width - _miniWidth));
                y = Math.Max(0, Math.Min(y, bounds.Height - _miniHeight));
                _popup.HorizontalOffset = x;
                _popup.VerticalOffset = y;
            }
        }
        private static void Root_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerDown)
            {
                return;
            }

            var wasResizing = _resizing;
            _pointerDown = false;
            try { _root.ReleasePointerCapture(e.Pointer); } catch { }
            if (wasResizing)
            {
                CommitResizePreview();
                _resizing = false;
                _resizeEdges = ResizeEdges.None;
                try { SetResizeCursor(GetResizeEdges(e.GetCurrentPoint(_root).Position)); }
                catch { SetResizeCursor(ResizeEdges.None); }
                e.Handled = true;
                return;
            }
            if (_dragging)
            {
                _dragging = false;
                SnapToEdge();
            }
            else
            {
                // A tap on the free area expands the page back.
                var restore = _onRestore;
                if (restore != null)
                {
                    restore();
                }
            }
        }

        private static bool IsButtonSource(DependencyObject source)
        {
            var current = source;
            while (current != null && current != _root)
            {
                if (current is Button)
                {
                    return true;
                }
                try { current = VisualTreeHelper.GetParent(current); }
                catch { break; }
            }
            return false;
        }

        private static ResizeEdges GetResizeEdges(Point point)
        {
            if (_root == null)
            {
                return ResizeEdges.None;
            }

            var width = _root.ActualWidth > 0 ? _root.ActualWidth : _miniWidth;
            var height = _root.ActualHeight > 0 ? _root.ActualHeight : _miniHeight;

            // Larger square corner targets make diagonal resizing much easier than requiring the
            // pointer to land inside the tiny intersection of two side strips.
            if (point.X <= ResizeCornerSize && point.Y <= ResizeCornerSize)
                return ResizeEdges.Left | ResizeEdges.Top;
            if (point.X >= width - ResizeCornerSize && point.Y <= ResizeCornerSize)
                return ResizeEdges.Right | ResizeEdges.Top;
            if (point.X <= ResizeCornerSize && point.Y >= height - ResizeCornerSize)
                return ResizeEdges.Left | ResizeEdges.Bottom;
            if (point.X >= width - ResizeCornerSize && point.Y >= height - ResizeCornerSize)
                return ResizeEdges.Right | ResizeEdges.Bottom;

            if (point.X <= ResizeHandleThickness) return ResizeEdges.Left;
            if (point.X >= width - ResizeHandleThickness) return ResizeEdges.Right;
            if (point.Y <= ResizeHandleThickness) return ResizeEdges.Top;
            if (point.Y >= height - ResizeHandleThickness) return ResizeEdges.Bottom;
            return ResizeEdges.None;
        }

        private static void BeginResize(PointerRoutedEventArgs e, ResizeEdges edges)
        {
            _resizing = true;
            _resizeEdges = edges;
            _resizeStartPointer = e.GetCurrentPoint(null).Position;
            _resizeStartWidth = _miniWidth;
            _resizeStartHeight = _miniHeight;
            _resizeStartOffsetX = _popup.HorizontalOffset;
            _resizeStartOffsetY = _popup.VerticalOffset;
            _resizePreviewScale = 1.0;
            SetResizeCursor(edges);

            if (_frame != null)
            {
                var originX = (edges & ResizeEdges.Left) != 0 ? 1.0
                    : (edges & ResizeEdges.Right) != 0 ? 0.0 : 0.5;
                var originY = (edges & ResizeEdges.Top) != 0 ? 1.0
                    : (edges & ResizeEdges.Bottom) != 0 ? 0.0 : 0.5;
                _frame.RenderTransformOrigin = new Point(originX, originY);
            }
            if (_resizeTransform != null)
            {
                _resizeTransform.ScaleX = 1.0;
                _resizeTransform.ScaleY = 1.0;
            }

            try { _root.CapturePointer(e.Pointer); } catch { }
            e.Handled = true;
        }

        private static void UpdateResizePreview(Point pointer)
        {
            if (!_resizing || _resizeTransform == null || _resizeStartWidth <= 0 || _resizeStartHeight <= 0)
            {
                return;
            }

            var dx = pointer.X - _resizeStartPointer.X;
            var dy = pointer.Y - _resizeStartPointer.Y;
            var hasHorizontal = (_resizeEdges & (ResizeEdges.Left | ResizeEdges.Right)) != 0;
            var hasVertical = (_resizeEdges & (ResizeEdges.Top | ResizeEdges.Bottom)) != 0;

            var horizontalScale = 1.0;
            var verticalScale = 1.0;
            if (hasHorizontal)
            {
                var deltaWidth = (_resizeEdges & ResizeEdges.Left) != 0 ? -dx : dx;
                horizontalScale = (_resizeStartWidth + deltaWidth) / _resizeStartWidth;
            }
            if (hasVertical)
            {
                var deltaHeight = (_resizeEdges & ResizeEdges.Top) != 0 ? -dy : dy;
                verticalScale = (_resizeStartHeight + deltaHeight) / _resizeStartHeight;
            }

            double scale;
            if (hasHorizontal && hasVertical)
            {
                // On a corner use whichever axis the user moved proportionally farther. The card
                // still scales uniformly, so the video aspect ratio never changes.
                scale = Math.Abs(horizontalScale - 1.0) >= Math.Abs(verticalScale - 1.0)
                    ? horizontalScale : verticalScale;
            }
            else
            {
                scale = hasHorizontal ? horizontalScale : verticalScale;
            }

            var startLongEdge = _miniAspectRatio >= 1.0 ? _resizeStartWidth : _resizeStartHeight;
            var minScale = ClampLongEdge(0) / startLongEdge;
            var maxScale = ClampLongEdge(double.MaxValue) / startLongEdge;
            maxScale = Math.Min(maxScale, GetAnchoredScreenFitScale());
            if (maxScale < minScale) maxScale = minScale;
            scale = Math.Max(minScale, Math.Min(scale, maxScale));

            // Avoid tiny floating point updates that would just wake up the UI thread for no
            // visible change.
            if (Math.Abs(scale - _resizePreviewScale) < 0.002)
            {
                return;
            }

            _resizePreviewScale = scale;
            _resizeTransform.ScaleX = scale;
            _resizeTransform.ScaleY = scale;
        }

        private static double GetAnchoredScreenFitScale()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                var originX = (_resizeEdges & ResizeEdges.Left) != 0 ? 1.0
                    : (_resizeEdges & ResizeEdges.Right) != 0 ? 0.0 : 0.5;
                var originY = (_resizeEdges & ResizeEdges.Top) != 0 ? 1.0
                    : (_resizeEdges & ResizeEdges.Bottom) != 0 ? 0.0 : 0.5;

                var anchorX = _resizeStartOffsetX + (_resizeStartWidth * originX);
                var anchorY = _resizeStartOffsetY + (_resizeStartHeight * originY);

                double availableWidth;
                if (originX <= 0.001)
                {
                    availableWidth = bounds.Width - EdgeMargin - anchorX;
                }
                else if (originX >= 0.999)
                {
                    availableWidth = anchorX - EdgeMargin;
                }
                else
                {
                    availableWidth = 2.0 * Math.Min(anchorX - EdgeMargin, bounds.Width - EdgeMargin - anchorX);
                }

                double availableHeight;
                if (originY <= 0.001)
                {
                    availableHeight = bounds.Height - EdgeMargin - anchorY;
                }
                else if (originY >= 0.999)
                {
                    availableHeight = anchorY - EdgeMargin;
                }
                else
                {
                    availableHeight = 2.0 * Math.Min(anchorY - EdgeMargin, bounds.Height - EdgeMargin - anchorY);
                }

                var scaleX = availableWidth / _resizeStartWidth;
                var scaleY = availableHeight / _resizeStartHeight;
                return Math.Max(0.1, Math.Min(scaleX, scaleY));
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static void CommitResizePreview()
        {
            if (_root == null || _popup == null)
            {
                ResetResizePreview();
                return;
            }

            var scale = _resizePreviewScale;
            if (scale <= 0 || double.IsNaN(scale) || double.IsInfinity(scale))
            {
                scale = 1.0;
            }

            var newWidth = _resizeStartWidth * scale;
            var newHeight = _resizeStartHeight * scale;
            var originX = (_resizeEdges & ResizeEdges.Left) != 0 ? 1.0
                : (_resizeEdges & ResizeEdges.Right) != 0 ? 0.0 : 0.5;
            var originY = (_resizeEdges & ResizeEdges.Top) != 0 ? 1.0
                : (_resizeEdges & ResizeEdges.Bottom) != 0 ? 0.0 : 0.5;

            var newX = _resizeStartOffsetX + ((_resizeStartWidth - newWidth) * originX);
            var newY = _resizeStartOffsetY + ((_resizeStartHeight - newHeight) * originY);

            _miniLongEdge = _miniAspectRatio >= 1.0 ? newWidth : newHeight;
            _miniLongEdge = ClampLongEdge(_miniLongEdge);
            UpdateMiniDimensionsFromLongEdge();

            try
            {
                var bounds = Window.Current.Bounds;
                newX = Math.Max(0, Math.Min(newX, Math.Max(0, bounds.Width - _miniWidth)));
                newY = Math.Max(0, Math.Min(newY, Math.Max(0, bounds.Height - _miniHeight)));
            }
            catch
            {
            }

            // Commit once. This is the only point in the whole resize gesture where the live video
            // gets a new layout size; the drag preview above is compositor scaling only.
            _root.Width = _miniWidth;
            _root.Height = _miniHeight;
            _popup.HorizontalOffset = newX;
            _popup.VerticalOffset = newY;
            ResetResizePreview();

            System.Diagnostics.Debug.WriteLine(
                "[MiniPlayer] Resized to " + _miniWidth.ToString("0.#") + "x" + _miniHeight.ToString("0.#"));
        }

        private static void ResetResizePreview()
        {
            _resizePreviewScale = 1.0;
            if (_resizeTransform != null)
            {
                _resizeTransform.ScaleX = 1.0;
                _resizeTransform.ScaleY = 1.0;
            }
            if (_frame != null)
            {
                _frame.RenderTransformOrigin = new Point(0.5, 0.5);
            }
        }
        // Magnetise horizontally to whichever side edge is nearer, and clamp vertically inside the
        // screen. Instead of assigning the final Popup offsets immediately, glide there with a
        // short ease-out animation like the modern YouTube mini-player.
        private static void SnapToEdge()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                var x = _popup.HorizontalOffset;
                var y = _popup.VerticalOffset;
                var centerX = x + _miniWidth / 2.0;
                var snapLeft = centerX < bounds.Width / 2.0;
                var targetX = snapLeft ? EdgeMargin : bounds.Width - _miniWidth - EdgeMargin;
                var targetY = y;

                if (targetY < EdgeMargin) targetY = EdgeMargin;
                if (targetY > bounds.Height - _miniHeight - EdgeMargin)
                {
                    targetY = bounds.Height - _miniHeight - EdgeMargin;
                }
                StartSnapAnimation(targetX, targetY);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Snap failed: " + ex.Message);
            }
        }

        private static void StartSnapAnimation(double targetX, double targetY)
        {
            if (_popup == null)
            {
                return;
            }

            StopSnapAnimation(false);
            _snapStartX = _popup.HorizontalOffset;
            _snapStartY = _popup.VerticalOffset;
            _snapTargetX = targetX;
            _snapTargetY = targetY;
            // Nothing meaningful to animate. Keep the exact final value so repeated drags do not
            // accumulate sub-pixel offsets.
            if (Math.Abs(_snapTargetX - _snapStartX) < 0.5
                && Math.Abs(_snapTargetY - _snapStartY) < 0.5)
            {
                _popup.HorizontalOffset = _snapTargetX;
                _popup.VerticalOffset = _snapTargetY;
                return;
            }
            if (_snapTimer == null)
            {
                _snapTimer = new DispatcherTimer();
                _snapTimer.Interval = TimeSpan.FromMilliseconds(16);
                _snapTimer.Tick += SnapTimer_Tick;
            }

            _snapStartedAt = DateTime.UtcNow;
            _snapTimer.Start();
        }
        private static void SnapTimer_Tick(object sender, object e)
        {
            if (_popup == null || !_popup.IsOpen || _pointerDown || _compactOverlay)
            {
                StopSnapAnimation(false);
                return;
            }
            var elapsed = (DateTime.UtcNow - _snapStartedAt).TotalMilliseconds;
            var progress = elapsed / SnapAnimationMilliseconds;
            if (progress >= 1.0)
            {
                _popup.HorizontalOffset = _snapTargetX;
                _popup.VerticalOffset = _snapTargetY;
                StopSnapAnimation(false);
                return;
            }

            if (progress < 0) progress = 0;
            // Cubic ease-out: quick response right after release and a soft finish at the edge.
            var remaining = 1.0 - progress;
            var eased = 1.0 - (remaining * remaining * remaining);

            _popup.HorizontalOffset = _snapStartX + ((_snapTargetX - _snapStartX) * eased);
            _popup.VerticalOffset = _snapStartY + ((_snapTargetY - _snapStartY) * eased);
        }
        private static void StopSnapAnimation(bool complete)
        {
            if (_snapTimer != null)
            {
                _snapTimer.Stop();
            }

            if (complete && _popup != null)
            {
                _popup.HorizontalOffset = _snapTargetX;
                _popup.VerticalOffset = _snapTargetY;
            }
        }
        private static void UpdatePlayPauseIcon()
        {
            if (_playPauseIcon == null || _player == null)
            {
                return;
            }

            var path = _player.IsPlaying ? "Assets/Dark/player/pause.png" : "Assets/Dark/player/play.png";
            App.SetThemeImageSource(_playPauseIcon, path);
        }
    }
}
