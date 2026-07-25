using System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.Media.Playback;
using Windows.UI.Xaml.Data;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Core;

namespace YouTube
{
    public class CustomMediaTransportControls : MediaTransportControls
    {
        private Button _settingsButton;
        private Border _settingsBorder;
        private bool _isFullscreen;
        private DispatcherTimer _hideTimer;
        private TimeSpan _apiDuration = TimeSpan.Zero;
        private Slider _progressSlider;
        private TextBlock _positionText;
        private TextBlock _durationText;
        private MediaPlayerElement _mediaPlayerElement;
        private bool _isSeeking = false;
        private DispatcherTimer _updateTimer;

        public event EventHandler SettingsClicked;

        public bool IsFullscreen
        {
            get { return _isFullscreen; }
            set
            {
                _isFullscreen = value;
                ApplyButtonStyle();
            }
        }

        public CustomMediaTransportControls()
        {
            this.DefaultStyleKey = typeof(MediaTransportControls);
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            // Находим стандартные элементы управления
            _progressSlider = GetTemplateChild("ProgressSlider") as Slider;
            _positionText = GetTemplateChild("PositionText") as TextBlock;
            _durationText = GetTemplateChild("DurationText") as TextBlock;

            // Находим родительский MediaPlayerElement
            _mediaPlayerElement = FindParentMediaPlayerElement();

            if (_progressSlider != null)
            {
                _progressSlider.ValueChanged -= ProgressSlider_ValueChanged;
                _progressSlider.ValueChanged += ProgressSlider_ValueChanged;

                _progressSlider.PointerPressed -= ProgressSlider_PointerPressed;
                _progressSlider.PointerPressed += ProgressSlider_PointerPressed;

                _progressSlider.PointerReleased -= ProgressSlider_PointerReleased;
                _progressSlider.PointerReleased += ProgressSlider_PointerReleased;
            }

            // Создаем кнопку настроек
            var root = GetTemplateChild("RootGrid") as Grid;
            if (root != null)
            {
                _settingsBorder = new Border
                {
                    Width = 36,
                    Height = 36,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(10),
                    Background = new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00)),
                    CornerRadius = new CornerRadius(18)
                };

                _settingsButton = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    Background = new SolidColorBrush(Colors.Transparent),
                    BorderBrush = null,
                    BorderThickness = new Thickness(0)
                };
                var icon = new SymbolIcon(Symbol.Setting) { Foreground = new SolidColorBrush(Colors.White) };
                _settingsButton.Content = icon;
                _settingsButton.Click += SettingsButton_Click;
                _settingsBorder.Child = _settingsButton;
                root.Children.Add(_settingsBorder);

                // Таймер для скрытия в полноэкранном режиме
                _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _hideTimer.Tick += (s, e) =>
                {
                    _hideTimer.Stop();
                    if (_isFullscreen && _settingsBorder != null)
                    {
                        _settingsBorder.Visibility = Visibility.Collapsed;
                    }
                };

                // Таймер для обновления позиции
                _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _updateTimer.Tick += UpdateTimer_Tick;

                // Обработчики для показа/скрытия кнопки
                this.PointerMoved += (s, e) => ShowAndScheduleHide();
                this.Tapped += (s, e) => ShowAndScheduleHide();

                ApplyButtonStyle();
            }

            // Подписываемся на события медиаплеера
            if (_mediaPlayerElement != null && _mediaPlayerElement.MediaPlayer != null)
            {
                _mediaPlayerElement.MediaPlayer.PlaybackSession.PositionChanged += PlaybackSession_PositionChanged;
                _updateTimer.Start();
            }

            UpdateDurationDisplay();
        }

        private MediaPlayerElement FindParentMediaPlayerElement()
        {
            DependencyObject parent = VisualTreeHelper.GetParent(this);
            while (parent != null)
            {
                if (parent is MediaPlayerElement)
                {
                    return (MediaPlayerElement)parent;
                }
                parent = VisualTreeHelper.GetParent(parent);
            }
            return null;
        }

        private void ProgressSlider_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isSeeking = true;
        }

        private void ProgressSlider_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isSeeking && _mediaPlayerElement != null && _mediaPlayerElement.MediaPlayer != null)
            {
                var newPosition = TimeSpan.FromSeconds(_progressSlider.Value);
                _mediaPlayerElement.MediaPlayer.PlaybackSession.Position = newPosition;
            }
            _isSeeking = false;
        }

        private void ProgressSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_isSeeking && _mediaPlayerElement != null && _mediaPlayerElement.MediaPlayer != null)
            {
                var newPosition = TimeSpan.FromSeconds(e.NewValue);
                UpdatePositionDisplay(newPosition);
            }
        }

        private void UpdateTimer_Tick(object sender, object e)
        {
            if (!_isSeeking && _mediaPlayerElement != null && _mediaPlayerElement.MediaPlayer != null)
            {
                var position = _mediaPlayerElement.MediaPlayer.PlaybackSession.Position;
                UpdateProgressBar(position);
            }
        }

        private void PlaybackSession_PositionChanged(MediaPlaybackSession sender, object args)
        {
            if (!_isSeeking)
            {
                UpdateProgressBar(sender.Position);
            }
        }

        private void UpdateProgressBar(TimeSpan position)
        {
            if (_progressSlider != null && _apiDuration > TimeSpan.Zero)
            {
                _progressSlider.Maximum = _apiDuration.TotalSeconds;
                _progressSlider.Value = position.TotalSeconds;
            }
            UpdatePositionDisplay(position);
        }

        private void UpdatePositionDisplay(TimeSpan position)
        {
            if (_positionText != null)
            {
                _positionText.Text = FormatTimeSpan(position);
            }
        }

        public void SetDurationFromApi(TimeSpan duration)
        {
            _apiDuration = duration;
            UpdateDurationDisplay();

            // Обновляем прогресс-бар с правильной длительностью
            if (_progressSlider != null && duration > TimeSpan.Zero)
            {
                _progressSlider.Maximum = duration.TotalSeconds;

                // Обновляем текущую позицию
                if (_mediaPlayerElement != null && _mediaPlayerElement.MediaPlayer != null)
                {
                    var position = _mediaPlayerElement.MediaPlayer.PlaybackSession.Position;
                    _progressSlider.Value = position.TotalSeconds;
                    UpdatePositionDisplay(position);
                }
            }
        }

        private void UpdateDurationDisplay()
        {
            if (_durationText != null)
            {
                _durationText.Text = FormatTimeSpan(_apiDuration);
            }
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
            }
            else
            {
                return $"{timeSpan.Minutes:00}:{timeSpan.Seconds:00}";
            }
        }

        private void ShowAndScheduleHide()
        {
            if (_settingsButton == null || _settingsBorder != null) return;
            if (_isFullscreen)
            {
                _settingsBorder.Visibility = Visibility.Visible;
                _settingsBorder.Opacity = 1;
                _hideTimer?.Stop();
                _hideTimer?.Start();
            }
        }

        private void ApplyButtonStyle()
        {
            if (_settingsButton == null || _settingsBorder == null) return;

            _settingsBorder.Background = _isFullscreen
                ? new SolidColorBrush(Colors.Transparent)
                : new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

            if (_isFullscreen)
            {
                _settingsBorder.Visibility = Visibility.Collapsed;
                _settingsBorder.Opacity = 1;
            }
            else
            {
                _settingsBorder.Visibility = Visibility.Visible;
                _settingsBorder.Opacity = 1;
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsClicked?.Invoke(_settingsButton, EventArgs.Empty);
        }
    }
}