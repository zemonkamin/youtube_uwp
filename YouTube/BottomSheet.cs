using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace YouTube
{
    // Shared surface and interaction model for every modal sheet that slides from the bottom.
    // Pages provide only their sheet-specific Content; chrome, motion and drag-to-dismiss live here.
    public sealed class BottomSheet : ContentControl
    {
        private FrameworkElement _dragArea;
        private TranslateTransform _translate;
        private bool _dragging;
        private bool _externalDragActive;
        private double _dragStartY;
        private double _dragStartOffset;
        private bool _isOpen;
        private bool _layoutInitialized;
        private double _declaredMaxHeight = double.PositiveInfinity;
        private double _declaredWidth = double.NaN;
        private double _declaredMaxWidth = double.PositiveInfinity;
        private HorizontalAlignment _declaredHorizontalAlignment = HorizontalAlignment.Stretch;
        private Page _hostPage;
        private Storyboard _activeStoryboard;

        public BottomSheet()
        {
            DefaultStyleKey = typeof(BottomSheet);
            Visibility = Visibility.Collapsed;
            PointerMoved += BottomSheet_PointerMoved;
            PointerReleased += BottomSheet_PointerReleased;
            PointerCaptureLost += BottomSheet_PointerReleased;
            PointerWheelChanged += BottomSheet_PointerWheelChanged;
            Loaded += BottomSheet_Loaded;
            Unloaded += BottomSheet_Unloaded;
        }

        public static readonly DependencyProperty CornerRadiusProperty =
            DependencyProperty.Register("CornerRadius", typeof(CornerRadius),
                typeof(BottomSheet), new PropertyMetadata(new CornerRadius(15)));

        public CornerRadius CornerRadius
        {
            get { return (CornerRadius)GetValue(CornerRadiusProperty); }
            set { SetValue(CornerRadiusProperty, value); }
        }

        public static readonly DependencyProperty HiddenOffsetProperty =
            DependencyProperty.Register("HiddenOffset", typeof(double),
                typeof(BottomSheet), new PropertyMetadata(400.0));

        public double HiddenOffset
        {
            get { return (double)GetValue(HiddenOffsetProperty); }
            set { SetValue(HiddenOffsetProperty, value); }
        }

        public static readonly DependencyProperty HandleHeightProperty =
            DependencyProperty.Register("HandleHeight", typeof(GridLength),
                typeof(BottomSheet), new PropertyMetadata(new GridLength(36)));

        public GridLength HandleHeight
        {
            get { return (GridLength)GetValue(HandleHeightProperty); }
            set { SetValue(HandleHeightProperty, value); }
        }

        public static readonly DependencyProperty OverlayElementNameProperty =
            DependencyProperty.Register("OverlayElementName", typeof(string),
                typeof(BottomSheet), new PropertyMetadata("OverlayGrid"));

        public string OverlayElementName
        {
            get { return (string)GetValue(OverlayElementNameProperty); }
            set { SetValue(OverlayElementNameProperty, value); }
        }

        public bool IsOpen
        {
            get { return _isOpen; }
        }

        // Page-specific legacy animations use this while they are being folded into
        // the shared control. It is always based on the sheet's real arranged height.
        public double DismissDistance
        {
            get
            {
                if (Visibility == Visibility.Visible) UpdateLayout();
                return GetDismissDistance();
            }
        }

        public double DragDismissThreshold
        {
            get { return Math.Max(72, GetDismissDistance() * 0.35); }
        }

        public double ClampDragOffset(double value)
        {
            return Math.Max(0, Math.Min(GetDismissDistance(), value));
        }

        public event EventHandler Opened;
        public event EventHandler Closed;
        public event EventHandler<TappedRoutedEventArgs> HandleTapped;
        public event EventHandler<PointerRoutedEventArgs> HandlePointerPressed;
        public event EventHandler<PointerRoutedEventArgs> HandlePointerMoved;
        public event EventHandler<PointerRoutedEventArgs> HandlePointerReleased;

        protected override void OnApplyTemplate()
        {
            if (_dragArea != null)
            {
                _dragArea.Tapped -= DragArea_Tapped;
                _dragArea.PointerPressed -= DragArea_PointerPressed;
                _dragArea.PointerMoved -= DragArea_PointerMoved;
                _dragArea.PointerReleased -= DragArea_PointerReleased;
                _dragArea.PointerCaptureLost -= DragArea_PointerReleased;
            }

            base.OnApplyTemplate();
            _dragArea = GetTemplateChild("PART_DragArea") as FrameworkElement;
            if (_dragArea != null)
            {
                _dragArea.Tapped += DragArea_Tapped;
                _dragArea.PointerPressed += DragArea_PointerPressed;
                _dragArea.PointerMoved += DragArea_PointerMoved;
                _dragArea.PointerReleased += DragArea_PointerReleased;
                _dragArea.PointerCaptureLost += DragArea_PointerReleased;
            }
        }

        public void SetOpen(bool show)
        {
            if (show) Open();
            else Close();
        }

        public void Open()
        {
            _isOpen = true;
            UpdateMaximumHeight();
            var overlay = FindOverlay();
            if (overlay != null) overlay.Visibility = Visibility.Visible;
            Visibility = Visibility.Visible;
            UpdateLayout();
            var transform = EnsureTransform();
            if (Math.Abs(transform.Y) < 0.1) transform.Y = GetDismissDistance();
            AnimateTo(0, false);
        }

        public void Close()
        {
            if (Visibility != Visibility.Visible)
            {
                _isOpen = false;
                UpdateOverlayVisibility();
                return;
            }
            _isOpen = false;
            AnimateTo(GetDismissDistance(), true);
        }

        public void CloseImmediately()
        {
            _isOpen = false;
            EnsureTransform().Y = GetDismissDistance();
            Visibility = Visibility.Collapsed;
            UpdateOverlayVisibility();
        }

        private TranslateTransform EnsureTransform()
        {
            if (_translate != null) return _translate;
            _translate = RenderTransform as TranslateTransform;
            if (_translate == null)
            {
                _translate = new TranslateTransform { Y = GetDismissDistance() };
                RenderTransform = _translate;
            }
            return _translate;
        }

        private double GetDismissDistance()
        {
            if (ActualHeight > 0)
            {
                return ActualHeight + Math.Max(12, Margin.Bottom + 2);
            }
            return HiddenOffset;
        }

        private void AnimateTo(double target, bool collapseWhenDone)
        {
            var transform = EnsureTransform();
            if (_activeStoryboard != null)
            {
                _activeStoryboard.Stop();
                _activeStoryboard = null;
            }
            var animation = new DoubleAnimation
            {
                From = transform.Y,
                To = target,
                Duration = new Duration(TimeSpan.FromMilliseconds(240)),
                EnableDependentAnimation = true,
                EasingFunction = new QuadraticEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };
            var storyboard = new Storyboard();
            _activeStoryboard = storyboard;
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, transform);
            Storyboard.SetTargetProperty(animation, "Y");
            if (collapseWhenDone)
            {
                storyboard.Completed += (sender, args) =>
                {
                    if (!ReferenceEquals(_activeStoryboard, storyboard)) return;
                    _activeStoryboard = null;
                    transform.Y = GetDismissDistance();
                    Visibility = Visibility.Collapsed;
                    UpdateOverlayVisibility();
                    var handler = Closed;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
            }
            else
            {
                storyboard.Completed += (sender, args) =>
                {
                    if (!ReferenceEquals(_activeStoryboard, storyboard)) return;
                    _activeStoryboard = null;
                    transform.Y = 0;
                    var handler = Opened;
                    if (handler != null) handler(this, EventArgs.Empty);
                };
            }
            storyboard.Begin();
        }

        private void BottomSheet_Loaded(object sender, RoutedEventArgs e)
        {
            if (!_layoutInitialized)
            {
                _layoutInitialized = true;
                _declaredMaxHeight = MaxHeight;
                _declaredWidth = Width;
                _declaredMaxWidth = MaxWidth;
                _declaredHorizontalAlignment = HorizontalAlignment;
                // One compact handle geometry for every sheet. Old pages supplied
                // different values, which produced inconsistent blank space above it.
                HandleHeight = new GridLength(24);

            }

            _hostPage = FindPage();
            if (_hostPage != null) _hostPage.SizeChanged += HostPage_SizeChanged;
            UpdateMaximumHeight();
        }

        private void BottomSheet_Unloaded(object sender, RoutedEventArgs e)
        {
            if (_hostPage != null) _hostPage.SizeChanged -= HostPage_SizeChanged;
            _hostPage = null;
            if (_activeStoryboard != null)
            {
                _activeStoryboard.Stop();
                _activeStoryboard = null;
            }
        }

        private void HostPage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMaximumHeight();
        }

        private void UpdateMaximumHeight()
        {
            var availableWidth = _hostPage != null && _hostPage.ActualWidth > 0
                ? _hostPage.ActualWidth
                : Window.Current.Bounds.Width;
            var availableHeight = _hostPage != null && _hostPage.ActualHeight > 0
                ? _hostPage.ActualHeight
                : Window.Current.Bounds.Height;
            var windowLimit = Math.Max(160, availableHeight * 0.82);
            MaxHeight = double.IsInfinity(_declaredMaxHeight)
                ? windowLimit
                : Math.Min(_declaredMaxHeight, windowLimit);

            // On a wide screen a full-width phone-style sheet is difficult to scan and leaves
            // actions far apart. Keep the portrait layout unchanged, but make every shared sheet
            // a compact, centered surface in landscape. The small lower bound keeps it usable on
            // Windows 10 Mobile in landscape while desktop widths use 35% of the viewport.
            if (availableWidth > availableHeight)
            {
                var landscapeWidth = Math.Max(260, availableWidth * 0.35);
                Width = landscapeWidth;
                MaxWidth = landscapeWidth;
                HorizontalAlignment = HorizontalAlignment.Center;
            }
            else
            {
                Width = _declaredWidth;
                MaxWidth = _declaredMaxWidth;
                HorizontalAlignment = _declaredHorizontalAlignment;
            }
        }

        private void DragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var external = HandleTapped;
            if (external != null)
            {
                external(this, e);
                return;
            }
            if (HandlePointerPressed != null) return;
            if (!_dragging) Close();
        }

        private void DragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var external = HandlePointerPressed;
            if (external != null)
            {
                _externalDragActive = true;
                external(this, e);
                return;
            }
            if (!_isOpen) return;
            _dragging = true;
            _dragStartY = e.GetCurrentPoint(this).Position.Y;
            _dragStartOffset = EnsureTransform().Y;
            if (_dragArea != null) _dragArea.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void DragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var external = HandlePointerMoved;
            if (external != null)
            {
                external(this, e);
                return;
            }
            if (!_dragging) return;
            var currentY = e.GetCurrentPoint(this).Position.Y;
            EnsureTransform().Y = Math.Max(0, _dragStartOffset + currentY - _dragStartY);
            e.Handled = true;
        }

        private void DragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var external = HandlePointerReleased;
            if (external != null)
            {
                _externalDragActive = false;
                external(this, e);
                return;
            }
            if (!_dragging) return;
            _dragging = false;
            if (_dragArea != null) _dragArea.ReleasePointerCaptures();
            if (EnsureTransform().Y > Math.Max(80, GetDismissDistance() * 0.35)) Close();
            else AnimateTo(0, false);
            e.Handled = true;
        }

        private void BottomSheet_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_externalDragActive || HandlePointerMoved == null) return;
            HandlePointerMoved(this, e);
        }

        private void BottomSheet_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_externalDragActive || HandlePointerReleased == null) return;
            _externalDragActive = false;
            HandlePointerReleased(this, e);
        }

        private void BottomSheet_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            // Nested ScrollViewers consume the wheel first. If it bubbles this far,
            // do not let the page underneath react while the pointer is over a sheet.
            e.Handled = true;
        }

        private FrameworkElement FindOverlay()
        {
            if (string.IsNullOrWhiteSpace(OverlayElementName)) return null;
            DependencyObject current = this;
            while (current != null)
            {
                var page = current as Page;
                if (page != null) return page.FindName(OverlayElementName) as FrameworkElement;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private void UpdateOverlayVisibility()
        {
            var overlay = FindOverlay();
            if (overlay == null) return;
            var page = FindPage();
            overlay.Visibility = page != null && HasVisibleSheetForOverlay(page, OverlayElementName)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private Page FindPage()
        {
            DependencyObject current = this;
            while (current != null)
            {
                var page = current as Page;
                if (page != null) return page;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static bool HasVisibleSheetForOverlay(DependencyObject root, string overlayName)
        {
            var sheet = root as BottomSheet;
            if (sheet != null && sheet.Visibility == Visibility.Visible && sheet._isOpen
                && string.Equals(sheet.OverlayElementName, overlayName, StringComparison.Ordinal))
                return true;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                if (HasVisibleSheetForOverlay(VisualTreeHelper.GetChild(root, i), overlayName))
                    return true;
            }
            return false;
        }
    }
}
