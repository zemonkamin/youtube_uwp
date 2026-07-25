using System;
using Windows.Foundation;
using Windows.Foundation.Metadata;
using Windows.UI;
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

        private const double MiniWidth = 220;
        private const double MiniHeight = 124; // ~16:9
        private const double EdgeMargin = 10;
        private const double BottomInset = 68; // clear a bottom tab bar on the page behind it
        private const double DragThreshold = 8;

        public static bool IsActive { get { return _popup != null && _popup.IsOpen && _player != null; } }

        public static CustomVideoPlayer ActivePlayer { get { return _player; } }

        // Reparents the given player into the mini window and shows it. onRestore is invoked when
        // the user taps the free area (expand back); onClosed when the user taps the X.
        public static void Show(CustomVideoPlayer player, Action onRestore, Action onClosed)
        {
            if (player == null)
            {
                return;
            }

            EnsureUi();

            _player = player;
            _onRestore = onRestore;
            _onClosed = onClosed;
            player.HorizontalAlignment = HorizontalAlignment.Stretch;
            player.VerticalAlignment = VerticalAlignment.Stretch;
            player.Width = double.NaN;
            player.Height = double.NaN;
            player.MinHeight = 0; // XAML sets MinHeight=211 for the full page; the mini window is smaller
            player.Margin = new Thickness(0);

            _videoHost.Children.Clear();
            _videoHost.Children.Add(player);
            player.SetMiniMode(true);

            UpdatePlayPauseIcon();
            PositionPopup();
            _popup.IsOpen = true;

            // Desktop only: turn the app window itself into a small always-on-top window, so the
            // mini-player floats above other applications. No-op on Windows 10 Mobile.
            TryEnterCompactOverlay();
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
            ExitCompactOverlay();

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

            return player;
        }

        // Tears the mini-player down and releases the shared player for good.
        public static void Close()
        {
            ExitCompactOverlay();

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

            // Play/pause (top-left).
            var playPauseButton = new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0)
            };
            _playPauseIcon = new Image
            {
                Width = 18,
                Height = 18,
                Source = new BitmapImage(new Uri("ms-appx:///Assets/player/pause.png"))
            };
            playPauseButton.Content = _playPauseIcon;
            playPauseButton.Click += (s, e) => TogglePlayPause();
            _playPauseButton = playPauseButton;

            _root = new Grid
            {
                Width = MiniWidth,
                Height = MiniHeight,
                Background = new SolidColorBrush(Colors.Black)
            };
            _root.Children.Add(_videoHost);
            _root.Children.Add(playPauseButton);
            _root.Children.Add(closeButton);

            // Drag to move (with edge snap) / tap the free area to expand back.
            _root.PointerPressed += Root_PointerPressed;
            _root.PointerMoved += Root_PointerMoved;
            _root.PointerReleased += Root_PointerReleased;
            _root.PointerCanceled += Root_PointerReleased;
            _root.PointerCaptureLost += Root_PointerReleased;

            // A thin border so it reads as a floating card over the page behind it.
            var frame = new Border
            {
                BorderBrush = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = _root
            };

            _popup = new Popup { Child = frame, IsLightDismissEnabled = false };
        }

        private static Button MakeGlyphButton(string glyph)
        {
            return new Button
            {
                Width = 32,
                Height = 32,
                Padding = new Thickness(0),
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                BorderThickness = new Thickness(0),
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
                _popup.HorizontalOffset = Math.Max(EdgeMargin, bounds.Width - MiniWidth - EdgeMargin);
                _popup.VerticalOffset = Math.Max(EdgeMargin, bounds.Height - MiniHeight - BottomInset);
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
            try
            {
                if (!ApiInformation.IsMethodPresent("Windows.UI.ViewManagement.ApplicationView", "IsViewModeSupported"))
                {
                    return false;
                }

                return ApplicationView.GetForCurrentView().IsViewModeSupported(ApplicationViewMode.CompactOverlay);
            }
            catch
            {
                return false;
            }
        }

        private static async void TryEnterCompactOverlay()
        {
            if (_compactOverlay || !IsCompactOverlaySupported())
            {
                return;
            }

            try
            {
                var preferences = ViewModePreferences.CreateDefault(ApplicationViewMode.CompactOverlay);
                preferences.CustomSize = new Size(CompactWidth, CompactHeight);

                var entered = await ApplicationView.GetForCurrentView()
                    .TryEnterViewModeAsync(ApplicationViewMode.CompactOverlay, preferences);

                if (!entered || _player == null)
                {
                    return;
                }

                _compactOverlay = true;
                _compactRefitAttempts = 0;
                Window.Current.SizeChanged += CompactOverlay_WindowSizeChanged;

                // Do NOT measure the window here: the shell has accepted the mode but the actual
                // resize lands later, so Bounds is still the old (possibly full-screen) size and
                // stretching to it would blow the mini-player up over the whole screen. Start at
                // the size we asked for and let SizeChanged fit it exactly.
                _popup.HorizontalOffset = 0;
                _popup.VerticalOffset = 0;
                _root.Width = CompactWidth;
                _root.Height = CompactHeight;
                ApplyCompactChrome(true);
                FillWindowForCompactOverlay();
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Entered compact overlay");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Compact overlay failed: " + ex.Message);
            }
        }

        public static bool IsCompactOverlayActive { get { return _compactOverlay; } }

        // Returns the window to its normal size. MUST be awaited before navigating back to the
        // video page: otherwise the page is measured while the window is still 360px wide and
        // stays laid out as a narrow strip after the window grows again.
        public static async System.Threading.Tasks.Task LeaveCompactOverlayAsync()
        {
            if (!_compactOverlay)
            {
                return;
            }

            _compactOverlay = false;
            ApplyCompactChrome(false);

            try
            {
                Window.Current.SizeChanged -= CompactOverlay_WindowSizeChanged;
                if (_compactResizeTimer != null)
                {
                    _compactResizeTimer.Stop();
                }
                await ApplicationView.GetForCurrentView().TryEnterViewModeAsync(ApplicationViewMode.Default);
                System.Diagnostics.Debug.WriteLine(
                    "[MiniPlayer] Left compact overlay; window is now " + Window.Current.Bounds.Width
                    + "x" + Window.Current.Bounds.Height);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Leaving compact overlay failed: " + ex.Message);
            }

            // Back to the small floating card inside the app window.
            if (_root != null)
            {
                _root.Width = MiniWidth;
                _root.Height = MiniHeight;
            }
            PositionPopup();

            // The page behind us was measured at the small size; make it re-measure at the new one.
            try { (Window.Current.Content as FrameworkElement)?.InvalidateMeasure(); } catch { }
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
                x = Math.Max(0, Math.Min(x, bounds.Width - MiniWidth));
                y = Math.Max(0, Math.Min(y, bounds.Height - MiniHeight));
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

            _pointerDown = false;
            try { _root.ReleasePointerCapture(e.Pointer); } catch { }

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

        // Magnetise horizontally to whichever side edge is nearer, and clamp vertically inside the
        // screen — the picture-in-picture "sticks" to the border.
        private static void SnapToEdge()
        {
            try
            {
                var bounds = Window.Current.Bounds;
                var x = _popup.HorizontalOffset;
                var y = _popup.VerticalOffset;

                var centerX = x + MiniWidth / 2.0;
                var snapLeft = centerX < bounds.Width / 2.0;
                x = snapLeft ? EdgeMargin : bounds.Width - MiniWidth - EdgeMargin;

                if (y < EdgeMargin) y = EdgeMargin;
                if (y > bounds.Height - MiniHeight - EdgeMargin) y = bounds.Height - MiniHeight - EdgeMargin;

                _popup.HorizontalOffset = x;
                _popup.VerticalOffset = y;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[MiniPlayer] Snap failed: " + ex.Message);
            }
        }

        private static void UpdatePlayPauseIcon()
        {
            if (_playPauseIcon == null || _player == null)
            {
                return;
            }

            var path = _player.IsPlaying ? "ms-appx:///Assets/player/pause.png" : "ms-appx:///Assets/player/play.png";
            _playPauseIcon.Source = new BitmapImage(new Uri(path));
        }
    }
}
