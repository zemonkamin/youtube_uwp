using System;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    public sealed partial class AndroidLoadingRing : UserControl
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            "IsActive",
            typeof(bool),
            typeof(AndroidLoadingRing),
            new PropertyMetadata(false, OnIsActiveChanged));

        private readonly Stopwatch _animationClock = new Stopwatch();
        private bool _isRenderingSubscribed;

        private const double DefaultSize = 32.0;
        private const double ArcStrokeThickness = 2.5;

        // Android-like indeterminate arc: it never reverses, only the tail catches the head.
        private const double MinSweepAngle = 44.0;
        private const double MaxSweepAngle = 278.0;
        private const double SweepCycleSeconds = 1.35;
        private const double RotationDegreesPerSecond = 160.0;
        private const double TailAdvancePerCycle = MaxSweepAngle - MinSweepAngle;

        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
        }

        public AndroidLoadingRing()
        {
            this.InitializeComponent();
            this.Loaded += AndroidLoadingRing_Loaded;
            this.Unloaded += AndroidLoadingRing_Unloaded;
            this.SizeChanged += AndroidLoadingRing_SizeChanged;
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = d as AndroidLoadingRing;
            if (control == null) return;

            if ((bool)e.NewValue)
            {
                control.StartAnimation();
            }
            else
            {
                control.StopAnimation();
            }
        }

        private void AndroidLoadingRing_Loaded(object sender, RoutedEventArgs e)
        {
            if (IsActive)
            {
                StartAnimation();
            }
            else
            {
                UpdateArc(0);
            }
        }

        private void AndroidLoadingRing_Unloaded(object sender, RoutedEventArgs e)
        {
            StopAnimation();
        }

        private void AndroidLoadingRing_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateArc(_animationClock.Elapsed.TotalSeconds);
        }

        private void StartAnimation()
        {
            if (!_animationClock.IsRunning)
            {
                _animationClock.Restart();
            }

            if (!_isRenderingSubscribed)
            {
                CompositionTarget.Rendering += CompositionTarget_Rendering;
                _isRenderingSubscribed = true;
            }

            this.Visibility = Visibility.Visible;
            UpdateArc(_animationClock.Elapsed.TotalSeconds);
        }

        private void StopAnimation()
        {
            if (_isRenderingSubscribed)
            {
                CompositionTarget.Rendering -= CompositionTarget_Rendering;
                _isRenderingSubscribed = false;
            }

            _animationClock.Stop();
            SpinnerPath.Data = null;
        }

        private void CompositionTarget_Rendering(object sender, object e)
        {
            // Если элемент не виден или не активен, не производим тяжёлые геом. вычисления!
            if (this.Visibility != Visibility.Visible || !IsActive)
            {
                return;
            }

            UpdateArc(_animationClock.Elapsed.TotalSeconds);
        }

        private void UpdateArc(double seconds)
        {
            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0 || double.IsNaN(size) || double.IsInfinity(size))
            {
                size = DefaultSize;
            }

            // Keep the whole stroke and the rounded caps inside the control bounds,
            // otherwise the arc gets clipped when Width/Height is small or when the
            // control is placed inside a fixed-size parent.
            double radius = Math.Max(1.0, (size - (ArcStrokeThickness * 2.0)) / 2.0 - 1.0);
            double center = size / 2.0;

            double totalCycles = seconds / SweepCycleSeconds;
            int cycleIndex = (int)Math.Floor(totalCycles);
            double cycle = totalCycles - cycleIndex;

            double sweep;
            double tailAdvanceInsideCycle;

            if (cycle < 0.5)
            {
                // The head runs forward and the arc grows.
                double p = EaseInOut(cycle / 0.5);
                sweep = Lerp(MinSweepAngle, MaxSweepAngle, p);
                tailAdvanceInsideCycle = 0.0;
            }
            else
            {
                // The tail catches the head, so the arc gets shorter.
                // This value ends exactly where the next cycle begins, so there is no jump back.
                double p = EaseInOut((cycle - 0.5) / 0.5);
                sweep = Lerp(MaxSweepAngle, MinSweepAngle, p);
                tailAdvanceInsideCycle = Lerp(0.0, TailAdvancePerCycle, p);
            }

            double continuousTailAdvance = (cycleIndex * TailAdvancePerCycle) + tailAdvanceInsideCycle;
            double continuousRotation = seconds * RotationDegreesPerSecond;
            double startAngle = continuousRotation + continuousTailAdvance;

            SetArcGeometry(center, center, radius, startAngle, sweep);
        }

        private void SetArcGeometry(double centerX, double centerY, double radius, double startAngle, double sweepAngle)
        {
            if (sweepAngle > 359.0)
            {
                sweepAngle = 359.0;
            }
            else if (sweepAngle < 1.0)
            {
                sweepAngle = 1.0;
            }

            Point startPoint = PointOnCircle(centerX, centerY, radius, startAngle);
            Point endPoint = PointOnCircle(centerX, centerY, radius, startAngle + sweepAngle);

            var segment = new ArcSegment();
            segment.Point = endPoint;
            segment.Size = new Size(radius, radius);
            segment.IsLargeArc = sweepAngle > 180.0;
            segment.SweepDirection = SweepDirection.Clockwise;

            var figure = new PathFigure();
            figure.StartPoint = startPoint;
            figure.IsClosed = false;
            figure.Segments.Add(segment);

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);

            SpinnerPath.Data = geometry;
        }

        private static Point PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
        {
            // -90 degrees makes the arc start from the top, like Android's indeterminate spinner.
            double radians = (angleDegrees - 90.0) * Math.PI / 180.0;
            return new Point(
                centerX + radius * Math.Cos(radians),
                centerY + radius * Math.Sin(radians));
        }

        private static double Lerp(double from, double to, double amount)
        {
            return from + (to - from) * amount;
        }

        private static double EaseInOut(double value)
        {
            if (value < 0)
            {
                value = 0;
            }
            else if (value > 1)
            {
                value = 1;
            }

            return value * value * (3.0 - 2.0 * value);
        }
    }
}
