using System;
using System.Collections.Generic; // Added for List<T>
using System.Net.Http;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.Streaming.Adaptive;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging; // BitmapImage for scrub-preview sheets
using Windows.UI.Xaml.Shapes; // Added for Rectangle class
using Windows.Foundation;
using System.Threading.Tasks;
using Windows.UI.Xaml.Media.Animation; // Added for animation classes
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI;
using System.Threading;
using Windows.Storage;
using Windows.Storage.Streams;

namespace YouTube
{
    public sealed partial class CustomVideoPlayer : UserControl, IDisposable
    {
        public sealed class VideoChapterMarker
        {
            public TimeSpan Position { get; set; }
            public string Title { get; set; }
        }

        public sealed class QualityChangeRefreshRequest
        {
            public string QualityTag { get; set; }
            public bool ShouldAutoPlay { get; set; }
            public TimeSpan Position { get; set; }
        }

        public sealed class CustomVideoPlayerMediaFailedEventArgs : EventArgs
        {
            public CustomVideoPlayerMediaFailedEventArgs(MediaPlayerFailedEventArgs originalArgs)
            {
                OriginalArgs = originalArgs;
            }

            public MediaPlayerFailedEventArgs OriginalArgs { get; private set; }

            public MediaPlayerError Error
            {
                get { return OriginalArgs != null ? OriginalArgs.Error : MediaPlayerError.SourceNotSupported; }
            }

            public string ErrorMessage
            {
                get { return OriginalArgs != null ? OriginalArgs.ErrorMessage : string.Empty; }
            }

            public bool Handled { get; set; }
        }

        private const string YouTubeIosUserAgent =
            "com.google.ios.youtube/19.16.3 (iPhone16,2; U; CPU iOS 18_0 like Mac OS X)";
        private const string YouTubeReferer = "https://www.youtube.com/";
        private const string RequiredHlsAvcCodec = "avc1.4D401F,mp4a.40.2";
        private static readonly HttpClient _hlsHttpClient = new HttpClient();
        private bool _usingSeparateAudio = false;
        private CancellationTokenSource _audioInitCts;
        private CancellationTokenSource _audioResumeCts;
        private Windows.Media.Playback.MediaPlayer _separateAudioPlayer;
        private SystemMediaTransportControls _systemMediaControls;
        private string _systemMediaVideoId = string.Empty;
        private string _systemMediaTitle = string.Empty;
        private string _systemMediaAuthor = string.Empty;
        private string _systemMediaThumbnailUrl = string.Empty;
        private bool _systemMediaMetadataDirty = true;
        private readonly List<VideoChapterMarker> _chapterMarkers = new List<VideoChapterMarker>();
        private const double TimelineGapWidth = 4.0;
        private const double TimelineBarHeight = 4.0;
        private const double TimelineMinSegmentSeconds = 0.15;
        private readonly List<TimelineSegmentVisual> _timelineSegmentVisuals = new List<TimelineSegmentVisual>();
        private readonly SolidColorBrush _timelineBackgroundBrush = new SolidColorBrush(Color.FromArgb(255, 102, 102, 102));
        private readonly SolidColorBrush _timelineForegroundBrush = new SolidColorBrush(Color.FromArgb(255, 255, 0, 51));
        private bool _isRebuildingTimelineVisual = false;
        private double _lastRenderedTimelineMaximum = -1;
        private bool _isProgressPointerCaptured = false;

        private sealed class TimelineSegmentRange
        {
            public double StartSeconds;
            public double EndSeconds;
        }

        private sealed class TimelineSegmentVisual
        {
            public double StartSeconds;
            public double EndSeconds;
            public Grid Container;
            public Rectangle Fill;
            public double Left;
            public double Width;
        }

        private TimeSpan _pendingAudioPosition = TimeSpan.Zero;
        private DispatcherTimer _audioSyncTimer;
        private bool _isSyncingSeparateAudio = false;
        private bool _isWindowsMobileAudioMode = false;
        private bool _isSeparateAudioSeekPending = false;
        private DateTime _lastSeparateAudioHardSyncUtc = DateTime.MinValue;
        // Do not poll too often: Windows 10 Mobile starts stuttering when we keep
        // seeking/pausing MediaPlayer every few milliseconds. Sync is now event-based
        // with a rare drift guard while playback is running.
        private static readonly TimeSpan SeparateAudioSyncInterval = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan SeparateAudioMobileSyncInterval = TimeSpan.FromMilliseconds(3500);
        private static readonly TimeSpan SeparateAudioMaxDrift = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan SeparateAudioMobileMaxDrift = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan SeparateAudioDefaultHardSyncCooldown = TimeSpan.FromMilliseconds(1200);
        private static readonly TimeSpan SeparateAudioMobileHardSyncCooldown = TimeSpan.FromMilliseconds(6000);
        private static readonly TimeSpan SeparateAudioMobileResumeDelay = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SeparateAudioDefaultResumeDelay = TimeSpan.FromMilliseconds(120);
        // Start barrier: do not let audio or video run alone after load/seek/resume.
        // We only poll during explicit start/seek and while recovering from buffering.
        private static readonly TimeSpan SeparateAudioDefaultReadyPoll = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan SeparateAudioMobileReadyPoll = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SeparateAudioDefaultReadyTimeout = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan SeparateAudioMobileReadyTimeout = TimeSpan.FromSeconds(14);
        private static readonly TimeSpan SeparateAudioDefaultReadyStable = TimeSpan.FromMilliseconds(160);
        private static readonly TimeSpan SeparateAudioMobileReadyStable = TimeSpan.FromMilliseconds(300);
        private static readonly TimeSpan SeparateAudioDefaultStartupGuardPoll = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan SeparateAudioMobileStartupGuardPoll = TimeSpan.FromMilliseconds(700);
        private static readonly TimeSpan SeparateAudioDefaultStartupGuardDuration = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan SeparateAudioMobileStartupGuardDuration = TimeSpan.FromSeconds(6);
        private DateTime _separateAudioStartupGuardUntilUtc = DateTime.MinValue;
        private bool _visibleVideoMediaOpened = false;
        private bool _separateAudioMediaOpened = false;
        private bool _suppressAutoPlayUntilSeparateAudioReady = false;
        private bool _pendingAutoPlayAfterSeparateAudioReady = false;
        private bool _playImmediatelyAfterSeparateAudioOpened = false;
        private DateTime _separateAudioAutoplayPauseBlockUntilUtc = DateTime.MinValue;
        // After seek/resume a stream can report ready, start, and then immediately enter Buffering.
        // These fields implement an event-driven recovery barrier: pause both sides immediately,
        // then restart both only after they are ready again. The watchdog polls only for a few
        // seconds after start/seek so Windows 10 Mobile is not hammered continuously.
        private CancellationTokenSource _separateAudioBufferRecoveryCts;
        private CancellationTokenSource _separateAudioPostStartWatchCts;
        private bool _isRecoveringSeparateAudioBuffering = false;
        private DateTime _lastSeparateAudioBufferRecoveryUtc = DateTime.MinValue;
        private static readonly TimeSpan SeparateAudioDefaultBufferRecoveryCooldown = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan SeparateAudioMobileBufferRecoveryCooldown = TimeSpan.FromMilliseconds(750);
        private static readonly TimeSpan SeparateAudioDefaultPostStartWatchPoll = TimeSpan.FromMilliseconds(180);
        private static readonly TimeSpan SeparateAudioMobilePostStartWatchPoll = TimeSpan.FromMilliseconds(450);
        private static readonly TimeSpan SeparateAudioDefaultPostStartWatchDuration = TimeSpan.FromSeconds(4);
        private static readonly TimeSpan SeparateAudioMobilePostStartWatchDuration = TimeSpan.FromSeconds(7);
        private static readonly TimeSpan SeparateAudioDefaultStartDrift = TimeSpan.FromMilliseconds(220);
        private static readonly TimeSpan SeparateAudioMobileStartDrift = TimeSpan.FromMilliseconds(650);

        private bool _isPlaying = false;
        private bool _isUserDragging = false;
        // The demuxed source currently playing, kept so playback-speed changes can reach it.
        private Windows.Media.Core.MediaStreamSource _demuxedSource;
        // Set once playback has been torn down for good; blocks late async work from restarting it.
        private bool _playbackReleased;
        // Sampling points for measuring the speed actually achieved (see UpdateTimer_Tick).
        private TimeSpan _rateProbeMedia;
        private DateTime _rateProbeWall = DateTime.MinValue;
        // Seeks are collected here and applied once the user stops; see RequestSeek.
        private TimeSpan? _pendingSeekTarget;
        private DispatcherTimer _seekDebounceTimer;
        private DispatcherTimer _updateTimer;
        private bool _isFullscreen = false;
        private Panel _originalParent;
        private ApplicationView _applicationView;
        private bool _isWindowFullscreen = false;
        private double _originalHeight;
        private HorizontalAlignment _originalHorizontalAlignment;
        private VerticalAlignment _originalVerticalAlignment;
        private Thickness _originalMargin;
        private ScrollViewer _parentScrollViewer;
        private StackPanel _parentStackPanel;
        private Grid _parentGrid;
        private int _originalGridRow = -1;
        private int _originalGridColumn = -1;
        private int _originalGridRowSpan = 1;
        private int _originalGridColumnSpan = 1;
        private Popup _fullscreenPopup; // Popup for true fullscreen
        private Grid _fullscreenGrid; // Reference to fullscreen grid
        private bool _isDisposed = false; // To track disposal
        private Visibility _originalControlsVisibility; // Store original controls visibility
        private DispatcherTimer _controlsTimer; // Timer to auto-hide controls in fullscreen
        private DispatcherTimer _autoHideTimer; // Timer to auto-hide controls after inactivity
        private bool _isMiniMode; // True while docked in the floating mini-player
        private bool _controlsVisible = true; // Track controls visibility state
        private int _fadeCounter = 0; // Counter for fade operations
        private bool _videoLoaded = false; // Track if video is loaded and playing
        private TimeSpan _parsedDuration = TimeSpan.Zero; // Store parsed duration from API
        
        // Add fields for the Image controls
        private Windows.UI.Xaml.Controls.Image _playPauseIcon;
        private Windows.UI.Xaml.Controls.Image _settingsIcon;
        private Windows.UI.Xaml.Controls.Image _fullscreenIcon;
        private Windows.UI.Xaml.Controls.Image _replayIcon;
        
        // Store original MediaPlayer properties
        private Stretch _originalMediaPlayerStretch;
        private HorizontalAlignment _originalMediaPlayerHorizontalAlignment;
        private VerticalAlignment _originalMediaPlayerVerticalAlignment;
        
        // Add field to track current video quality
        private string _currentQuality;
        private bool _resumePlaybackAfterQualityChange = false;
        private TimeSpan _resumePositionAfterQualityChange = TimeSpan.Zero;
        
        // Add fields to track position for end detection
        private TimeSpan _lastPosition = TimeSpan.Zero;
        private int _positionStuckCounter = 0;

        private DispatcherTimer _skipOverlayTimer;

        public event EventHandler<object> FullscreenRequested;
        public event EventHandler<object> SettingsRequested;
        public event EventHandler<CustomVideoPlayerMediaFailedEventArgs> MediaFailed;
        public event EventHandler<bool> SkipOverlayRequested; // New event for skip overlay
        public event EventHandler<object> RefreshRequested; // New event for refresh requests
        // Raised once when playback reaches the end, so the page can auto-advance inside a
        // playlist / mix ("jam") queue.
        public event EventHandler<object> VideoEnded;
        // Raised when playback cannot keep up with real time for several seconds, so the page
        // can drop to a lighter quality instead of grinding to a halt and crashing.
        public event EventHandler<object> PlaybackStalling;
        private int _slowPlaybackCounter;

        // Property to set the parsed duration from API
        public TimeSpan ParsedDuration
        {
            get { return _parsedDuration; }
            set 
            { 
                _parsedDuration = value;
                System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer: ParsedDuration set to {value.TotalSeconds} seconds");
                // Update the time display and chapter marks when duration is set
                UpdateTimeDisplay();
                RebuildSegmentedProgressBar();
            }
        }

        // Public property to check if player is in fullscreen mode
        public bool IsFullscreen
        {
            get { return _isFullscreen; }
        }

        // Public property to get/set current quality
        public string CurrentQuality
        {
            get { return _currentQuality; }
            set { _currentQuality = value; }
        }

        public TimeSpan CurrentPlaybackPosition
        {
            get { return GetCurrentPlaybackPositionSafe(); }
        }

        private bool _sourceLoading;
        private DispatcherTimer _sourceLoadingTimeoutTimer;

        public bool IsSourceLoading { get { return _sourceLoading; } }

        // A new quality stream started loading: show the spinner and block play/seek until the
        // source is actually ready (MediaOpened) or fails.
        public void BeginSourceLoading()
        {
            _sourceLoading = true;

            if (SourceLoadingRing != null)
            {
                SourceLoadingRing.IsActive = true;
                SourceLoadingRing.Visibility = Visibility.Visible;
            }

            if (PlayPauseButton != null)
            {
                PlayPauseButton.IsEnabled = false;
                PlayPauseButton.Opacity = 0.25;
            }

            if (ProgressSlider != null)
            {
                ProgressSlider.IsEnabled = false;
            }

            if (ReplayButton != null)
            {
                ReplayButton.Visibility = Visibility.Collapsed;
                ReplayButton.Opacity = 0;
                ReplayButton.IsHitTestVisible = false;
            }

            // Safety net: never leave the controls locked if neither MediaOpened nor MediaFailed
            // ever arrives (dead source, network stall, ...).
            if (_sourceLoadingTimeoutTimer == null)
            {
                _sourceLoadingTimeoutTimer = new DispatcherTimer();
                _sourceLoadingTimeoutTimer.Interval = TimeSpan.FromSeconds(45);
                _sourceLoadingTimeoutTimer.Tick += SourceLoadingTimeoutTimer_Tick;
            }

            _sourceLoadingTimeoutTimer.Stop();
            _sourceLoadingTimeoutTimer.Start();
        }

        private void SourceLoadingTimeoutTimer_Tick(object sender, object e)
        {
            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: source loading timed out; unlocking controls");
            EndSourceLoading();
        }

        public void EndSourceLoading()
        {
            if (_sourceLoadingTimeoutTimer != null)
            {
                _sourceLoadingTimeoutTimer.Stop();
            }

            if (!_sourceLoading)
            {
                return;
            }

            _sourceLoading = false;

            if (SourceLoadingRing != null)
            {
                SourceLoadingRing.IsActive = false;
                SourceLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (PlayPauseButton != null)
            {
                PlayPauseButton.IsEnabled = true;
                PlayPauseButton.Opacity = 0.8;
            }

            if (ProgressSlider != null)
            {
                ProgressSlider.IsEnabled = true;
            }
        }

        // Heights ("360", "720", "1080", ...) this video actually offers, published by the page
        // so the fullscreen quality menu lists the same options as the portrait one.
        public List<string> AvailableQualities { get; set; }

        private List<string> BuildQualityOptionLabels()
        {
            var labels = new List<string> { "Auto" };
            var heights = AvailableQualities;
            if (heights != null && heights.Count > 0)
            {
                for (int i = 0; i < heights.Count; i++)
                {
                    labels.Add(heights[i] + "p");
                }
            }
            else
            {
                labels.Add("144p");
                labels.Add("360p");
                labels.Add("480p");
                labels.Add("720p");
                labels.Add("1080p");
            }

            return labels;
        }

        private bool IsCurrentQualityLabel(string label)
        {
            if (string.Equals(label, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(_currentQuality);
            }

            return string.Equals(_currentQuality, label.Replace("p", ""), StringComparison.Ordinal);
        }

        private void PopulateFullscreenQualityOptions(StackPanel qualityOptionsPanel)
        {
            if (qualityOptionsPanel == null)
            {
                return;
            }

            qualityOptionsPanel.Children.Clear();

            var qualityOptions = BuildQualityOptionLabels();
            for (int i = 0; i < qualityOptions.Count; i++)
            {
                var quality = qualityOptions[i];
                var isCurrent = IsCurrentQualityLabel(quality);

                var qualityOptionButton = new Button();
                qualityOptionButton.Background = isCurrent
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255))
                    : new SolidColorBrush(Colors.Transparent);
                qualityOptionButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                qualityOptionButton.HorizontalContentAlignment = HorizontalAlignment.Left;
                qualityOptionButton.Padding = new Thickness(16, 8, 16, 8);
                qualityOptionButton.Height = 48;
                qualityOptionButton.Margin = new Thickness(0, 0, 0, 4);
                qualityOptionButton.Content = isCurrent ? (quality + "   ✓") : quality;
                qualityOptionButton.Foreground = new SolidColorBrush(Colors.White);
                qualityOptionButton.FontWeight = isCurrent
                    ? Windows.UI.Text.FontWeights.SemiBold
                    : Windows.UI.Text.FontWeights.Normal;
                qualityOptionButton.Tag = quality;
                qualityOptionButton.Click += (s, e) => {
                    var button = s as Button;
                    var qualityValue = button?.Tag as string;
                    if (qualityValue != null)
                    {
                        // Apply the quality change
                        string newQuality = null;
                        if (qualityValue != "Auto")
                        {
                            newQuality = qualityValue.Replace("p", ""); // Remove 'p' suffix
                        }

                        System.Diagnostics.Debug.WriteLine(string.Format("Quality selected: {0}, internal quality: {1}", qualityValue, newQuality ?? "auto"));

                        // Store current playback state before the parent reloads the stream.
                        // Without this, quality switching replaces MediaPlayer.Source, SetSource()
                        // resets _isPlaying to false, and the new quality stays paused.
                        var resumePosition = GetCurrentPlaybackPositionSafe();
                        // User-selected quality change must resume playback automatically.
                        // Older Windows 10 Mobile MediaPlayer can report Paused/Opening during
                        // the switch even when video was playing, so do not rely only on
                        // PlaybackState here.
                        var shouldAutoPlayAfterQualityChange = true;
                        _resumePlaybackAfterQualityChange = shouldAutoPlayAfterQualityChange;
                        _resumePositionAfterQualityChange = resumePosition;

                        // Store the selected quality
                        _currentQuality = newQuality;

                        // Notify parent to reload the video with the new quality
                        RefreshRequested?.Invoke(this, new QualityChangeRefreshRequest
                        {
                            QualityTag = newQuality,
                            ShouldAutoPlay = shouldAutoPlayAfterQualityChange,
                            Position = resumePosition
                        });

                        // Close settings panel
                        _isSettingsPanelOpen = false;
                        AnimateFullscreenSettingsPanel(false);
                    }
                };
                qualityOptionsPanel.Children.Add(qualityOptionButton);
            }
        }

        public void PrepareResumeAfterSourceReload(TimeSpan position, bool autoPlay)
        {
            if (position < TimeSpan.Zero)
            {
                position = TimeSpan.Zero;
            }

            _resumePlaybackAfterQualityChange = autoPlay;
            _resumePositionAfterQualityChange = position;
        }

        public CustomVideoPlayer()
        {
            this.InitializeComponent();
            InitializePlayer();
            
            // Subscribe to window size changes
            Window.Current.SizeChanged += Current_SizeChanged;
        }

        private void Current_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            // If we're in fullscreen mode, resize the popup
            if (_isFullscreen && _fullscreenPopup != null && _fullscreenGrid != null)
            {
                _fullscreenPopup.Width = e.Size.Width;
                _fullscreenPopup.Height = e.Size.Height;
                
                // Also resize the grid inside the popup
                _fullscreenGrid.Width = e.Size.Width;
                _fullscreenGrid.Height = e.Size.Height;
            }
        }

        private void InitializePlayer()
        {
            // Force-create the internal player so it is guaranteed not to be null
            if (MediaPlayer.MediaPlayer == null)
            {
                MediaPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            }

            ConfigureMediaPlayerForBackground(MediaPlayer.MediaPlayer);
            InitializeSystemMediaControls();

            _isWindowsMobileAudioMode = DetectWindowsMobileDevice();
            _updateTimer = new DispatcherTimer();
            _updateTimer.Interval = TimeSpan.FromMilliseconds(1000);
            _updateTimer.Tick += UpdateTimer_Tick;
            _audioSyncTimer = new DispatcherTimer();
            _audioSyncTimer.Interval = _isWindowsMobileAudioMode ? SeparateAudioMobileSyncInterval : SeparateAudioSyncInterval;
            _audioSyncTimer.Tick += AudioSyncTimer_Tick;
            _applicationView = ApplicationView.GetForCurrentView();
            MediaPlayer.Stretch = Stretch.Uniform;
            MediaPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            MediaPlayer.VerticalAlignment = VerticalAlignment.Stretch;
            _originalMediaPlayerStretch = MediaPlayer.Stretch;
            _originalMediaPlayerHorizontalAlignment = MediaPlayer.HorizontalAlignment;
            _originalMediaPlayerVerticalAlignment = MediaPlayer.VerticalAlignment;
            _autoHideTimer = new DispatcherTimer();
            _autoHideTimer.Interval = TimeSpan.FromSeconds(3);
            _autoHideTimer.Tick += AutoHideTimer_Tick;
            _skipOverlayTimer = new DispatcherTimer();
            _skipOverlayTimer.Interval = TimeSpan.FromSeconds(1);
            _skipOverlayTimer.Tick += SkipOverlayTimer_Tick;
            PlayPauseButton.Opacity = 0.8;
            SettingsButton.Opacity = 0.8;
            this.Loaded += CustomVideoPlayer_Loaded;
        }

        private void CustomVideoPlayer_Loaded(object sender, RoutedEventArgs e)
        {
            // Get references to the Image controls directly
            // Since the Image controls have x:Name attributes, they should be accessible directly
            _playPauseIcon = PlayPauseIcon;
            _settingsIcon = SettingsIcon;
            _fullscreenIcon = FullscreenIcon;
            _replayIcon = ReplayIcon;
            
            // Debug information
            System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer - _playPauseIcon found: {_playPauseIcon != null}");
            System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer - _fullscreenIcon found: {_fullscreenIcon != null}");
            System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer - _settingsIcon found: {_settingsIcon != null}");
            System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer - _replayIcon found: {_replayIcon != null}");
            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer - separate audio core player mode enabled");
            
            // Set initial play icon based on current play state
            if (_playPauseIcon != null)
            {
                // Determine the correct icon based on current play state
                string iconPath = _isPlaying ? "ms-appx:///Assets/player/pause.png" : "ms-appx:///Assets/player/play.png";
                _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
            }
            
            // Ensure controls overlay is hit-testable by default
            ControlsOverlay.IsHitTestVisible = true;
            
            // Initialize replay button as not hit-testable since it's initially hidden
            ReplayButton.IsHitTestVisible = false;
            
            // Mobile-specific optimizations
            OptimizeForMobile();
            
            // Show controls initially but don't start auto-hide timer until video is loaded
            FadeInControls();
        }
        
        // Mobile-specific optimizations for video playback
        private void OptimizeForMobile()
        {
            try
            {
                // Check if we're running on a mobile device
                var deviceFamily = Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily;
                bool isMobile = deviceFamily == "Windows.Mobile";
                
                if (isMobile && MediaPlayer?.MediaPlayer != null)
                {
                    System.Diagnostics.Debug.WriteLine("Optimizing for mobile device");
                    
                    // Enable hardware acceleration if available
                    // Note: This is handled automatically by the UWP MediaPlayer, 
                    // but we can ensure optimal settings
                    
                    // Remove the artificial limitation on quality for mobile devices
                    // Users should be able to select any quality they want
                    // We'll only apply optimizations if there are actual playback issues
                    /*
                    // Set lower quality by default on mobile to reduce resource usage
                    if (string.IsNullOrEmpty(_currentQuality))
                    {
                        _currentQuality = "360"; // Default to 360p on mobile
                        System.Diagnostics.Debug.WriteLine("Set default mobile quality to 360p");
                    }
                    */
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in OptimizeForMobile: {ex.Message}");
            }
        }

        // Style the slider thumb to be red (using default styling)
        private void StyleSliderThumb()
        {
            // Use default slider styling - no custom styling that might break functionality
            // Show controls initially but don't start auto-hide timer until video is loaded
            FadeInControls();
        }

        private void AnimateOpacity(UIElement element, double from, double to, double durationSeconds)
        {
            var animation = new DoubleAnimation();
            animation.From = from;
            animation.To = to;
            animation.Duration = TimeSpan.FromSeconds(durationSeconds);
            animation.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }; // Smooth easing
            
            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            Storyboard.SetTarget(animation, element);
            Storyboard.SetTargetProperty(animation, "Opacity");
            
            storyboard.Begin();
        }

        private string _videoTitle = string.Empty;
        private string _videoAuthor = string.Empty;

        // Title/author shown top-left over the player in fullscreen (mirrors the official app).
        public void SetVideoInfo(string title, string author)
        {
            _videoTitle = title ?? string.Empty;
            _videoAuthor = author ?? string.Empty;

            if (FullscreenTitleText != null) FullscreenTitleText.Text = _videoTitle;
            if (FullscreenAuthorText != null) FullscreenAuthorText.Text = _videoAuthor;

            UpdateFullscreenTitleVisibility();
        }

        // The overlay lives only in fullscreen; its opacity then tracks the controls' fade so it
        // is seen only while the controls are up.
        private void UpdateFullscreenTitleVisibility()
        {
            if (FullscreenTitlePanel == null)
            {
                return;
            }

            var inFullscreen = _isFullscreen && !string.IsNullOrWhiteSpace(_videoTitle);
            FullscreenTitlePanel.Visibility = inFullscreen ? Visibility.Visible : Visibility.Collapsed;
            if (inFullscreen)
            {
                FullscreenTitlePanel.Opacity = _controlsVisible ? 1.0 : 0.0;
            }

            UpdateMinimizeButtonVisibility();
            UpdateSubtitleOverlayMetrics();
        }

        // --- Minimize to the mini-player ------------------------------------------------------

        // Raised by the top-left button and by a downward swipe starting at the player's top edge.
        public event EventHandler MinimizeRequested;

        private const double MinimizeSwipeZoneHeight = 48;  // how close to the top edge it must start
        private const double MinimizeSwipeDistance = 60;    // how far down it must travel
        private bool _minimizeSwipeTracking;
        private Point _minimizeSwipeStart;

        // In fullscreen the top-left belongs to the title overlay, and in mini mode there is
        // nothing to minimize into.
        private void UpdateMinimizeButtonVisibility()
        {
            if (MinimizeButton == null)
            {
                return;
            }

            var visible = !_isFullscreen && !_isMiniMode;
            MinimizeButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            // It fades with the rest of the controls, so when it (re)appears it must match their
            // current state instead of snapping to full opacity while they are hidden.
            if (visible)
            {
                MinimizeButton.Opacity = _controlsVisible ? 0.8 : 0.0;
                MinimizeButton.IsHitTestVisible = _controlsVisible;
            }
        }

        private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        {
            RaiseMinimizeRequested();
        }

        private void RaiseMinimizeRequested()
        {
            var handler = MinimizeRequested;
            if (handler == null)
            {
                return;
            }

            try
            {
                handler(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Player] Minimize subscriber threw - " + ex.Message);
            }
        }

        private void PlayerGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _minimizeSwipeTracking = false;

            if (_isMiniMode || _isFullscreen || PlayerGrid == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(PlayerGrid).Position;

            // Only a gesture that begins at the very top edge counts, so ordinary drags over the
            // video (and the seek bar at the bottom) are unaffected.
            if (point.Y <= MinimizeSwipeZoneHeight)
            {
                _minimizeSwipeTracking = true;
                _minimizeSwipeStart = point;
            }
        }

        private void PlayerGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_minimizeSwipeTracking || PlayerGrid == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(PlayerGrid).Position;
            var dy = point.Y - _minimizeSwipeStart.Y;
            var dx = Math.Abs(point.X - _minimizeSwipeStart.X);

            // Downward, and more vertical than horizontal — otherwise it is a sideways drag.
            if (dy >= MinimizeSwipeDistance && dy > dx)
            {
                _minimizeSwipeTracking = false;
                System.Diagnostics.Debug.WriteLine("[Player] Minimize swipe from top edge");
                RaiseMinimizeRequested();
            }
        }

        private void PlayerGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _minimizeSwipeTracking = false;
        }

        private void FadeInControls()
        {
            // Always fade in controls when requested
            _fadeCounter++;
            System.Diagnostics.Debug.WriteLine($"[Fade Counter: {_fadeCounter}] Fading IN controls");
            // Make sure controls overlay and individual controls are hit-testable before fading in
            ControlsOverlay.IsHitTestVisible = true;
            BottomControlsPanel.IsHitTestVisible = true;
            PlayPauseButton.IsHitTestVisible = true;
            SettingsButton.IsHitTestVisible = true;
            ReplayButton.IsHitTestVisible = true;
            
            // Fade in all controls with smooth animation
            AnimateOpacity(BottomControlsPanel, BottomControlsPanel.Opacity, 1.0, 0.5); // Slower fade in
            // Only fade in play/pause or replay button, not both
            if (PlayPauseButton.Visibility == Visibility.Visible)
            {
                AnimateOpacity(PlayPauseButton, PlayPauseButton.Opacity, 0.8, 0.5);
            }
            if (ReplayButton.Visibility == Visibility.Visible)
            {
                AnimateOpacity(ReplayButton, ReplayButton.Opacity, 0.8, 0.5);
            }
            AnimateOpacity(SettingsButton, SettingsButton.Opacity, 0.8, 0.5);
            if (MinimizeButton != null && MinimizeButton.Visibility == Visibility.Visible)
            {
                MinimizeButton.IsHitTestVisible = true;
                AnimateOpacity(MinimizeButton, MinimizeButton.Opacity, 0.8, 0.5);
            }
            _controlsVisible = true;
            UpdateFullscreenTitleVisibility();
            if (_isFullscreen && FullscreenTitlePanel != null && FullscreenTitlePanel.Visibility == Visibility.Visible)
            {
                AnimateOpacity(FullscreenTitlePanel, FullscreenTitlePanel.Opacity, 1.0, 0.5);
            }

            // Restart the auto-hide timer when showing controls (unless video ended)
            if (_videoLoaded && ReplayButton.Visibility != Visibility.Visible)
            {
                StartAutoHideTimer();
            }
        }

        private void FadeOutControls()
        {
            // Always fade out controls when requested
            _fadeCounter++;
            System.Diagnostics.Debug.WriteLine($"[Fade Counter: {_fadeCounter}] Fading OUT controls");
            // Fade out all controls with smooth animation
            AnimateOpacity(BottomControlsPanel, BottomControlsPanel.Opacity, 0.0, 0.5); // Slower fade out
            AnimateOpacity(PlayPauseButton, PlayPauseButton.Opacity, 0.0, 0.5);
            AnimateOpacity(SettingsButton, SettingsButton.Opacity, 0.0, 0.5);
            if (MinimizeButton != null && MinimizeButton.Visibility == Visibility.Visible)
            {
                AnimateOpacity(MinimizeButton, MinimizeButton.Opacity, 0.0, 0.5);
            }
            if (FullscreenTitlePanel != null && FullscreenTitlePanel.Visibility == Visibility.Visible)
            {
                AnimateOpacity(FullscreenTitlePanel, FullscreenTitlePanel.Opacity, 0.0, 0.5);
            }
            // Only fade out replay button if it's visible
            if (ReplayButton.Visibility == Visibility.Visible)
            {
                AnimateOpacity(ReplayButton, ReplayButton.Opacity, 0.0, 0.5);
            }
            
            // Make controls non-hit-testable after fade out animation completes
            DispatcherTimer hideTimer = new DispatcherTimer();
            hideTimer.Interval = TimeSpan.FromMilliseconds(500); // Match animation duration
            hideTimer.Tick += (s, e) => 
            {
                hideTimer.Stop();
                if (!_controlsVisible) // Only disable hit testing if controls are still meant to be hidden
                {
                    ControlsOverlay.IsHitTestVisible = false;
                    BottomControlsPanel.IsHitTestVisible = false;
                    PlayPauseButton.IsHitTestVisible = false;
                    SettingsButton.IsHitTestVisible = false;
                    ReplayButton.IsHitTestVisible = false;
                    if (MinimizeButton != null) MinimizeButton.IsHitTestVisible = false;
                }
            };
            hideTimer.Start();

            _controlsVisible = false;
            UpdateFullscreenTitleVisibility();
        }

        // Helper method to find visual child elements
        private T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child != null && child is T)
                    return (T)child;

                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        private void StartAutoHideTimer()
        {
            if (_autoHideTimer != null)
            {
                _autoHideTimer.Stop();
                _autoHideTimer.Start();
            }
        }

        private void ResetAutoHideTimer()
        {
            if (_autoHideTimer != null)
            {
                _autoHideTimer.Stop();
                _autoHideTimer.Start();
            }
        }

        private void ConfigureMediaPlayerForBackground(Windows.Media.Playback.MediaPlayer player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                // Required for UWP background-capable audio playback when the app has
                // the Background Media Playback capability enabled in the manifest.
                player.AudioCategory = MediaPlayerAudioCategory.Media;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Background audio category failed - " + ex.Message);
            }

            DisableAutoTransportControls(player);
        }

        // A MediaPlayer auto-integrates with the System Media Transport Controls through its
        // CommandManager. We already drive SMTC manually (InitializeSystemMediaControls), and the
        // two channels talking to the same cross-process SMTC is what produced the constant
        // "RtlNtStatusToDosError(0x80010012)" RPC chatter during demuxed playback. Turning the
        // automatic one off removes the duplicate channel — playback and our own SMTC buttons are
        // unaffected.
        private static void DisableAutoTransportControls(Windows.Media.Playback.MediaPlayer player)
        {
            if (player == null)
            {
                return;
            }

            try
            {
                if (player.CommandManager != null)
                {
                    player.CommandManager.IsEnabled = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Disabling auto SMTC failed - " + ex.Message);
            }
        }

        private void InitializeSystemMediaControls()
        {
            try
            {
                _systemMediaControls = SystemMediaTransportControls.GetForCurrentView();
                if (_systemMediaControls == null)
                {
                    return;
                }

                _systemMediaControls.ButtonPressed -= SystemMediaControls_ButtonPressed;
                _systemMediaControls.ButtonPressed += SystemMediaControls_ButtonPressed;
                _systemMediaControls.IsEnabled = true;
                _systemMediaControls.IsPlayEnabled = true;
                _systemMediaControls.IsPauseEnabled = true;
                _systemMediaControls.IsStopEnabled = true;
                _systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Closed;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: System media controls init failed - " + ex.Message);
            }
        }

        private async void SystemMediaControls_ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    switch (args.Button)
                    {
                        case SystemMediaTransportControlsButton.Play:
                            Play();
                            break;
                        case SystemMediaTransportControlsButton.Pause:
                            Pause();
                            break;
                        case SystemMediaTransportControlsButton.Stop:
                            Stop();
                            break;
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SMTC button error - " + ex.Message);
            }
        }

        public void SetSystemMediaMetadata(string videoId, string title, string author)
        {
            try
            {
                var fixedTitle = NormalizeSystemMediaText(title);
                var fixedAuthor = NormalizeSystemMediaText(author);
                SplitCombinedMetadataIfNeeded(ref fixedTitle, ref fixedAuthor);

                _systemMediaVideoId = string.IsNullOrWhiteSpace(videoId) ? string.Empty : videoId.Trim();
                _systemMediaTitle = string.IsNullOrWhiteSpace(fixedTitle) ? "YouTube video" : fixedTitle;
                _systemMediaAuthor = string.IsNullOrWhiteSpace(fixedAuthor) ? "YouTube" : fixedAuthor;
                _systemMediaThumbnailUrl = string.IsNullOrWhiteSpace(_systemMediaVideoId)
                    ? string.Empty
                    : "https://i.ytimg.com/vi/" + Uri.EscapeDataString(_systemMediaVideoId) + "/hqdefault.jpg";

                _systemMediaMetadataDirty = true;
                UpdateSystemMediaDisplay();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SetSystemMediaMetadata failed - " + ex.Message);
            }
        }

        private static string NormalizeSystemMediaText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var text = value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (text.IndexOf("  ", StringComparison.Ordinal) >= 0)
            {
                text = text.Replace("  ", " ");
            }

            return text;
        }

        private static void SplitCombinedMetadataIfNeeded(ref string title, ref string author)
        {
            title = NormalizeSystemMediaText(title);
            author = NormalizeSystemMediaText(author);

            // Some YouTube notification/feed strings arrive as one combined value.
            // Windows 10 Mobile then receives an empty Title/Artist pair and shows a blank
            // volume flyout. Split the common formats before updating SMTC.
            if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(author))
            {
                var combined = author;
                var markers = new[] { " uploaded: ", " uploaded ", " опубликовал: ", " опубликовала: ", " - " };
                for (int i = 0; i < markers.Length; i++)
                {
                    var marker = markers[i];
                    var index = combined.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index > 0 && index + marker.Length < combined.Length)
                    {
                        author = combined.Substring(0, index).Trim();
                        title = combined.Substring(index + marker.Length).Trim();
                        return;
                    }
                }

                title = combined;
                author = "YouTube";
            }
            else if (!string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(author))
            {
                var combined = title;
                var markers = new[] { " uploaded: ", " uploaded ", " опубликовал: ", " опубликовала: " };
                for (int i = 0; i < markers.Length; i++)
                {
                    var marker = markers[i];
                    var index = combined.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (index > 0 && index + marker.Length < combined.Length)
                    {
                        author = combined.Substring(0, index).Trim();
                        title = combined.Substring(index + marker.Length).Trim();
                        return;
                    }
                }

                author = "YouTube";
            }
        }

        private void UpdateSystemMediaDisplay()
        {
            try
            {
                if (_systemMediaControls == null)
                {
                    InitializeSystemMediaControls();
                }

                if (_systemMediaControls == null)
                {
                    return;
                }

                var title = string.IsNullOrWhiteSpace(_systemMediaTitle) ? "YouTube video" : NormalizeSystemMediaText(_systemMediaTitle);
                var author = string.IsNullOrWhiteSpace(_systemMediaAuthor) ? "YouTube" : NormalizeSystemMediaText(_systemMediaAuthor);
                SplitCombinedMetadataIfNeeded(ref title, ref author);

                var updater = _systemMediaControls.DisplayUpdater;

                // Windows 10 Mobile is picky here: when Thumbnail is set before text, a
                // failed remote image can make the whole volume overlay blank. First commit
                // text-only metadata, then try to add the thumbnail as a second update.
                try
                {
                    updater.ClearAll();
                }
                catch { }

                updater.Type = MediaPlaybackType.Music;
                updater.MusicProperties.Title = string.IsNullOrWhiteSpace(title) ? "YouTube video" : title;
                updater.MusicProperties.Artist = string.IsNullOrWhiteSpace(author) ? "YouTube" : author;
                updater.MusicProperties.AlbumArtist = string.IsNullOrWhiteSpace(author) ? "YouTube" : author;
                updater.MusicProperties.AlbumTitle = "YouTube";
                updater.Update();

                if (!string.IsNullOrWhiteSpace(_systemMediaThumbnailUrl))
                {
                    try
                    {
                        updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(_systemMediaThumbnailUrl));
                        updater.Update();
                    }
                    catch (Exception imageEx)
                    {
                        System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SMTC thumbnail skipped - " + imageEx.Message);
                    }
                }

                _systemMediaMetadataDirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: UpdateSystemMediaDisplay failed - " + ex.Message);
            }
        }

        private void SetPlayPauseIcon(bool isPlaying)
        {
            try
            {
                if (_playPauseIcon == null)
                {
                    return;
                }

                _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(
                    new Uri(isPlaying ? "ms-appx:///Assets/player/pause.png" : "ms-appx:///Assets/player/play.png"));
            }
            catch { }
        }

        private void SetPlaybackUiPlaying()
        {
            _isPlaying = true;
            SetPlayPauseIcon(true);
            UpdateSystemMediaDisplay();
            UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Playing);
            if (_updateTimer != null)
            {
                _updateTimer.Start();
            }
        }

        private void SetPlaybackUiPaused()
        {
            _isPlaying = false;
            SetPlayPauseIcon(false);
            UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Paused);
        }

        private void UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus status)
        {
            try
            {
                if (_systemMediaControls == null)
                {
                    InitializeSystemMediaControls();
                }

                if (_systemMediaControls != null)
                {
                    _systemMediaControls.PlaybackStatus = status;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: UpdateSystemMediaPlaybackStatus failed - " + ex.Message);
            }
        }

        public MediaPlayerElement GetMediaPlayer()
        {
            return MediaPlayer;
        }

        public void SetWindowsMobileAudioMode(bool enabled)
        {
            _isWindowsMobileAudioMode = enabled;
            if (_audioSyncTimer != null)
            {
                _audioSyncTimer.Interval = _isWindowsMobileAudioMode ? SeparateAudioMobileSyncInterval : SeparateAudioSyncInterval;
            }
        }

        private static bool DetectWindowsMobileDevice()
        {
            try
            {
                return string.Equals(
                    Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamily,
                    "Windows.Mobile",
                    StringComparison.OrdinalIgnoreCase
                );
            }
            catch
            {
                return false;
            }
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            // The new-quality source is still being prepared — ignore play requests until ready.
            if (_sourceLoading)
            {
                return;
            }

            // Reset the auto-hide timer
            ResetAutoHideTimer();
            
            // Ensure controls are visible when user interacts
            if (!_controlsVisible)
            {
                FadeInControls();
            }
            
            if (_isPlaying)
            {
                Pause();
                System.Diagnostics.Debug.WriteLine($"Setting play icon, _playPauseIcon is null: {_playPauseIcon == null}");
                
                // Stop auto-hide timer when paused and keep controls visible
                if (_autoHideTimer != null)
                {
                    _autoHideTimer.Stop();
                }
                // Keep controls visible when paused
                if (!_controlsVisible)
                {
                    FadeInControls();
                }
            }
            else
            {
                Play();
                System.Diagnostics.Debug.WriteLine($"Setting pause icon, _playPauseIcon is null: {_playPauseIcon == null}");
                
                // Start the auto-hide timer when playing to fade out controls
                if (_videoLoaded)
                {
                    StartAutoHideTimer();
                }
            }
        }

        private void FullscreenButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset the auto-hide timer
            ResetAutoHideTimer();
            
            // Ensure controls are visible when user interacts
            FadeInControls();
            
            ToggleFullscreen();
        }

        private void ReplayButton_Click(object sender, RoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[ReplayButton] Click event fired");
            
            // Reset the auto-hide timer
            ResetAutoHideTimer();
            
            // Ensure controls are visible when user interacts
            FadeInControls();
            
            // Replay the video from the beginning
            if (MediaPlayer.MediaPlayer != null)
            {
                System.Diagnostics.Debug.WriteLine("[ReplayButton] Resetting video position to 0");
                MediaPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                _isPlaying = true;
                if (_usingSeparateAudio)
                {
                    StartSynchronizedSeparatePlayback(TimeSpan.Zero);
                }
                else
                {
                    MediaPlayer.MediaPlayer.Play();
                }
                if (_playPauseIcon != null)
                {
                    _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/pause.png"));
                }
                _updateTimer.Start();
                
                // Hide replay button and show play/pause button
                System.Diagnostics.Debug.WriteLine("[ReplayButton] Hiding replay button, showing play/pause button");
                ReplayButton.Visibility = Visibility.Collapsed;
                ReplayButton.Opacity = 0;
                ReplayButton.IsHitTestVisible = false;
                PlayPauseButton.Visibility = Visibility.Visible;
                PlayPauseButton.Opacity = 0.8;
                PlayPauseButton.IsHitTestVisible = true;
                
                // Start auto-hide timer
                StartAutoHideTimer();
                
                System.Diagnostics.Debug.WriteLine("[ReplayButton] Replay action completed");
            }
        }

        public void ToggleFullscreen()
        {
            if (_isFullscreen)
            {
                // If settings panel is open in fullscreen mode, close it first
                if (_isSettingsPanelOpen && _fullscreenSettingsPanel != null)
                {
                    _isSettingsPanelOpen = false;
                    AnimateFullscreenSettingsPanel(false);
                }
                ExitFullscreen();
            }
            else
            {
                // Before entering fullscreen, ensure the MediaPlayer is properly configured
                if (MediaPlayer != null)
                {
                    // Store original stretch property
                    _originalMediaPlayerStretch = MediaPlayer.Stretch;
                }
                EnterFullscreen();
            }
        }

        private void EnterFullscreen()
        {
            try
            {
                // Store the current play state before entering fullscreen
                bool wasPlaying = _isPlaying;
                
                // Try to enter fullscreen mode for the entire window
                if (_applicationView != null && !_applicationView.IsFullScreenMode)
                {
                    _isWindowFullscreen = _applicationView.TryEnterFullScreenMode();
                }

                // Create a popup for true fullscreen
                _fullscreenPopup = new Popup();
                
                // Create fullscreen container
                _fullscreenGrid = new Grid();
                _fullscreenGrid.Background = new SolidColorBrush(Windows.UI.Colors.Black);
                
                // Store references to parent containers
                _originalParent = this.Parent as Panel;
                _parentScrollViewer = FindParent<ScrollViewer>(this);
                _parentStackPanel = FindParent<StackPanel>(this);
                _parentGrid = FindParent<Grid>(this);
                
                // Get grid position information if parent is a Grid
                if (_parentGrid != null)
                {
                    _originalGridRow = Grid.GetRow(this);
                    _originalGridColumn = Grid.GetColumn(this);
                    _originalGridRowSpan = Grid.GetRowSpan(this);
                    _originalGridColumnSpan = Grid.GetColumnSpan(this);
                }
                
                // Hide the normal mode skip overlay BEFORE removing from parent
                Grid normalSkipOverlay = FindVisualChild<Grid>(this.Parent as DependencyObject, "SkipOverlay");
                if (normalSkipOverlay != null)
                {
                    normalSkipOverlay.Visibility = Visibility.Collapsed;
                }

                // Remove from original parent
                if (_originalParent != null)
                {
                    _originalParent.Children.Remove(this);
                }

                // Store original properties
                _originalHorizontalAlignment = this.HorizontalAlignment;
                _originalVerticalAlignment = this.VerticalAlignment;
                _originalHeight = this.Height;
                _originalMargin = this.Margin;
                
                // Store original controls visibility
                _originalControlsVisibility = ControlsOverlay.Visibility;

                // Hide parent containers
                if (_parentScrollViewer != null)
                {
                    _parentScrollViewer.Visibility = Visibility.Collapsed;
                }

                // Set the player to fill the entire container
                this.HorizontalAlignment = HorizontalAlignment.Stretch;
                this.VerticalAlignment = VerticalAlignment.Stretch;
                this.Height = double.NaN; // Changed from fixed height to auto
                this.Margin = new Thickness(0);

                // Configure MediaPlayer for fullscreen - Use Uniform to eliminate gaps (consistent with Player.xaml)
                MediaPlayer.Stretch = Stretch.Uniform; // This will maintain aspect ratio with black bars
                MediaPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                MediaPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                
                // Make sure the MediaPlayer element fills the entire space
                MediaPlayer.Margin = new Thickness(0);
                
                // Show controls overlay in fullscreen mode
                ControlsOverlay.Visibility = Visibility.Visible;
                _controlsVisible = true;

                // Add the player to the fullscreen grid
                _fullscreenGrid.Children.Add(this);
                
                // Get the actual window bounds
                var bounds = Window.Current.Bounds;
                
                // Set popup properties to cover entire window
                _fullscreenPopup.Child = _fullscreenGrid;
                _fullscreenPopup.HorizontalOffset = 0;
                _fullscreenPopup.VerticalOffset = 0;
                _fullscreenPopup.Width = bounds.Width;
                _fullscreenPopup.Height = bounds.Height;
                
                // Make sure the grid covers the entire popup
                _fullscreenGrid.Width = bounds.Width;
                _fullscreenGrid.Height = bounds.Height;
                _fullscreenGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
                _fullscreenGrid.VerticalAlignment = VerticalAlignment.Stretch;
                
                // Make sure the popup is on top of everything
                _fullscreenPopup.IsHitTestVisible = true;
                _fullscreenPopup.IsOpen = true;

                _isFullscreen = true;
                UpdateFullscreenTitleVisibility();

                // Update fullscreen icon
                System.Diagnostics.Debug.WriteLine($"Setting exit fullscreen icon, _fullscreenIcon is null: {_fullscreenIcon == null}");
                if (_fullscreenIcon != null)
                {
                    _fullscreenIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/exit_fullscreen.png"));
                }
                
                // Restore the play/pause icon state after entering fullscreen
                if (_playPauseIcon != null)
                {
                    // Determine the correct icon based on current play state
                    string iconPath = wasPlaying ? "ms-appx:///Assets/player/pause.png" : "ms-appx:///Assets/player/play.png";
                    _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                }
                
                // Add handlers to show/hide controls in fullscreen
                _fullscreenGrid.PointerMoved += FullscreenGrid_PointerMoved;
                _fullscreenGrid.Tapped += FullscreenGrid_Tapped;
                
                // If settings panel was open, close it when entering fullscreen
                if (_isSettingsPanelOpen)
                {
                    _isSettingsPanelOpen = false;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error entering fullscreen: " + ex.Message);
                // Clean up any partially created objects to prevent memory leaks
                if (_fullscreenPopup != null)
                {
                    _fullscreenPopup.IsOpen = false;
                    _fullscreenPopup = null;
                }
                _fullscreenGrid = null;
                
                // Fallback to event-based fullscreen request
                if (FullscreenRequested != null)
                {
                    FullscreenRequested(this, null);
                }
            }
        }

        private void FullscreenGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // Show controls when mouse moves over player in fullscreen
            if (_isFullscreen && !_controlsVisible)
            {
                FadeInControls();
            }
            else if (_isFullscreen)
            {
                // Reset the auto-hide timer even if controls are visible
                ResetAutoHideTimer();
            }
        }

        private void FullscreenGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Show controls when tapping anywhere on the player in fullscreen
            if (_isFullscreen && !_controlsVisible)
            {
                FadeInControls();
            }
            else if (_isFullscreen)
            {
                // Reset the auto-hide timer
                ResetAutoHideTimer();
            }
            
            // Prevent tap from propagating to parent elements
            e.Handled = true;
        }

        private void ControlsTimer_Tick(object sender, object e)
        {
            BottomControlsPanel.Visibility = Visibility.Collapsed;
            if (_controlsTimer != null)
            {
                _controlsTimer.Stop();
            }
        }

        private void AutoHideTimer_Tick(object sender, object e)
        {
            // Only fade out controls if video is playing
            if (_isPlaying)
            {
                // Hide controls with fade animation
                FadeOutControls();
            }
            
            // Stop the timer
            if (_autoHideTimer != null)
            {
                _autoHideTimer.Stop();
            }
        }

        private void SkipOverlayTimer_Tick(object sender, object e)
        {
            // Hide the appropriate skip overlay based on fullscreen state
            if (_isFullscreen && SkipOverlayFullscreen != null)
            {
                // Hide fullscreen overlay
                SkipOverlayFullscreen.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Try to find and hide the skip overlay in the parent Video page
                Grid skipOverlay = FindVisualChild<Grid>(this.Parent as DependencyObject, "SkipOverlay");
                if (skipOverlay != null)
                {
                    skipOverlay.Visibility = Visibility.Collapsed;
                }
            }
            
            // Stop the timer
            _skipOverlayTimer.Stop();
        }

        private void ExitFullscreen()
        {
            try
            {
                // Store the current play state before exiting fullscreen
                bool wasPlaying = _isPlaying;
                
                // Exit window fullscreen mode if we entered it
                if (_applicationView != null && _isWindowFullscreen && _applicationView.IsFullScreenMode)
                {
                    _applicationView.ExitFullScreenMode();
                    _isWindowFullscreen = false;
                }

                // Remove handlers
                if (_fullscreenGrid != null)
                {
                    _fullscreenGrid.PointerMoved -= FullscreenGrid_PointerMoved;
                    _fullscreenGrid.Tapped -= FullscreenGrid_Tapped;
                }

                // Show parent containers again
                if (_parentScrollViewer != null)
                {
                    _parentScrollViewer.Visibility = Visibility.Visible;
                }

                if (_fullscreenPopup != null)
                {
                    // Remove from fullscreen popup
                    Grid parentGrid = this.Parent as Grid;
                    if (parentGrid != null)
                    {
                        parentGrid.Children.Clear();
                    }

                    // Restore original properties
                    this.HorizontalAlignment = _originalHorizontalAlignment;
                    this.VerticalAlignment = _originalVerticalAlignment;
                    this.Height = _originalHeight;
                    this.Margin = _originalMargin;

                    // Reset MediaPlayer properties to original values
                    MediaPlayer.Stretch = _originalMediaPlayerStretch;
                    MediaPlayer.HorizontalAlignment = _originalMediaPlayerHorizontalAlignment;
                    MediaPlayer.VerticalAlignment = _originalMediaPlayerVerticalAlignment;
                    MediaPlayer.Margin = new Thickness(0); // Ensure no margin
                    
                    // Restore controls overlay visibility
                    ControlsOverlay.Visibility = _originalControlsVisibility;
                    // Ensure all controls are visible
                    BottomControlsPanel.Opacity = 1;
                    PlayPauseButton.Opacity = 0.8;
                    SettingsButton.Opacity = 0.8;
                    _controlsVisible = true;

                    // Add back to original parent ONLY if it still exists
                    if (_originalParent != null && IsParentStillValid(_originalParent))
                    {
                        // Clear the parent first to ensure clean re-addition
                        Grid gridParent = _originalParent as Grid;
                        if (gridParent != null)
                        {
                            // Remove any existing instances of this control
                            gridParent.Children.Remove(this);
                        }
                        
                        _originalParent.Children.Add(this);
                        
                        // Restore grid positioning if applicable
                        if (_parentGrid != null && _originalGridRow >= 0)
                        {
                            Grid.SetRow(this, _originalGridRow);
                            Grid.SetColumn(this, _originalGridColumn);
                            Grid.SetRowSpan(this, _originalGridRowSpan);
                            Grid.SetColumnSpan(this, _originalGridColumnSpan);
                        }
                    }

                    // Close and dispose of popup
                    _fullscreenPopup.IsOpen = false;
                    _fullscreenPopup = null;
                    _fullscreenGrid = null;

                    _isFullscreen = false;
                    UpdateFullscreenTitleVisibility();

                    // Hide the fullscreen skip overlay when exiting fullscreen
                    if (SkipOverlayFullscreen != null)
                    {
                        SkipOverlayFullscreen.Visibility = Visibility.Collapsed;
                    }
                    
                    // Also ensure the normal mode skip overlay is hidden initially
                    Grid normalSkipOverlay = FindVisualChild<Grid>(this.Parent as DependencyObject, "SkipOverlay");
                    if (normalSkipOverlay != null)
                    {
                        normalSkipOverlay.Visibility = Visibility.Collapsed;
                    }
                    
                    // Update fullscreen icon
                    System.Diagnostics.Debug.WriteLine($"Setting fullscreen icon, _fullscreenIcon is null: {_fullscreenIcon == null}");
                    if (_fullscreenIcon != null)
                    {
                        _fullscreenIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/fullscreen.png"));
                    }
                    
                    // Restore the play/pause icon state after exiting fullscreen
                    if (_playPauseIcon != null)
                    {
                        // Determine the correct icon based on current play state
                        string iconPath = wasPlaying ? "ms-appx:///Assets/player/pause.png" : "ms-appx:///Assets/player/play.png";
                        _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath));
                    }
                    
                    // Reset settings panel state
                    _isSettingsPanelOpen = false;
                    _fullscreenSettingsPanel = null;
                }
                else
                {
                    // Fallback to event-based fullscreen request
                    if (FullscreenRequested != null)
                    {
                        FullscreenRequested(this, null);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error exiting fullscreen: " + ex.Message);
                // Clean up any remaining objects to prevent memory leaks
                if (_fullscreenPopup != null)
                {
                    _fullscreenPopup.IsOpen = false;
                    _fullscreenPopup = null;
                }
                _fullscreenGrid = null;
                
                // Hide the fullscreen skip overlay
                if (SkipOverlayFullscreen != null)
                {
                    SkipOverlayFullscreen.Visibility = Visibility.Collapsed;
                }
                
                // Fallback to event-based fullscreen request
                if (FullscreenRequested != null)
                {
                    FullscreenRequested(this, null);
                }
            }
        }

        // Helper method to check if parent is still valid
        private bool IsParentStillValid(Panel parent)
        {
            try
            {
                // Simple check to see if parent still exists in visual tree
                return parent != null && Window.Current.Content != null;
            }
            catch
            {
                return false;
            }
        }

        // Helper method to find parent of specific type
        private T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            try
            {
                DependencyObject parentObject = VisualTreeHelper.GetParent(child);

                if (parentObject == null) return null;

                T parent = parentObject as T;
                if (parent != null)
                    return parent;
                else
                    return FindParent<T>(parentObject);
            }
            catch
            {
                return null;
            }
        }

        // Keep track of whether settings panel is open
        private bool _isSettingsPanelOpen = false;

        // Method to reset settings button appearance
        public void ResetSettingsButtonAppearance()
        {
            if (SettingsButton != null)
            {
                _isSettingsPanelOpen = false;
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            // Reset the auto-hide timer
            ResetAutoHideTimer();
            
            // Ensure controls are visible when user interacts
            FadeInControls();
            
            // Always (re)open settings
            _isSettingsPanelOpen = true;
            
            // Check if we're in fullscreen mode
            if (_isFullscreen)
            {
                // In fullscreen mode, show settings within the fullscreen popup
                ShowFullscreenSettings();
            }
            else
            {
                // In normal mode, use the event to show settings in the parent page
                SettingsRequested?.Invoke(this, null);
            }
        }

        // Method to show settings panel within the fullscreen popup
        private void ShowFullscreenSettings()
        {
            if (_isSettingsPanelOpen && _fullscreenGrid != null)
            {
                // If settings panel already exists, just show it
                if (_fullscreenSettingsPanel != null)
                {
                    // Show overlay (it's the second to last child, just before settings panel)
                    if (_fullscreenGrid.Children.Count >= 2)
                    {
                        var overlay = _fullscreenGrid.Children[_fullscreenGrid.Children.Count - 2];
                        if (overlay is Grid)
                        {
                            overlay.Visibility = Visibility.Visible;
                        }
                    }
                    
                    // Show settings panel
                    _fullscreenSettingsPanel.Visibility = Visibility.Visible;
                    
                    // ALWAYS reset to main panel when showing settings
                    // Find the child panels in the settings panel
                    if (_fullscreenSettingsPanel.Children.Count > 0)
                    {
                        var contentGrid = _fullscreenSettingsPanel.Children[0] as Grid;
                        if (contentGrid != null && contentGrid.Children.Count > 0)
                        {
                            var settingsContentGrid = contentGrid.Children[0] as Grid;
                            if (settingsContentGrid != null && settingsContentGrid.Children.Count >= 3)
                            {
                                // Show the first panel (main settings) and hide the others
                                settingsContentGrid.Children[0].Visibility = Visibility.Visible; // Main panel
                                settingsContentGrid.Children[1].Visibility = Visibility.Collapsed; // Quality panel
                                settingsContentGrid.Children[2].Visibility = Visibility.Collapsed; // Speed panel
                            }
                        }
                    }
                    
                    // Animate the settings panel sliding up
                    AnimateFullscreenSettingsPanel(true);
                }
                else
                {
                    // Create a settings panel for fullscreen mode
                    CreateFullscreenSettingsPanel();
                }
            }
            else if (!_isSettingsPanelOpen && _fullscreenSettingsPanel != null && _fullscreenGrid != null)
            {
                // Hide the settings panel
                AnimateFullscreenSettingsPanel(false);
            }
        }

        // Reference to the fullscreen settings panel
        private Grid _fullscreenSettingsPanel;

        // Method to create and show settings panel within the fullscreen popup
        private void CreateFullscreenSettingsPanel()
        {
            if (_fullscreenGrid == null) return;

            // Remove existing settings panel if any
            if (_fullscreenSettingsPanel != null)
            {
                // Remove overlay (it's the first child before the settings panel)
                if (_fullscreenGrid.Children.Count >= 2)
                {
                    _fullscreenGrid.Children.RemoveAt(0);
                }
                // Remove settings panel
                _fullscreenGrid.Children.Remove(_fullscreenSettingsPanel);
                _fullscreenSettingsPanel = null;
            }

            // Create overlay background that closes settings when tapped
            var overlay = new Grid();
            overlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0)); // Semi-transparent black
            overlay.Tapped += (s, e) => {
                // Close settings when tapping the overlay
                _isSettingsPanelOpen = false;
                AnimateFullscreenSettingsPanel(false);
                e.Handled = true;
            };

            // Create a new settings panel for fullscreen mode with margins and rounded corners
            _fullscreenSettingsPanel = new Grid();
            _fullscreenSettingsPanel.VerticalAlignment = VerticalAlignment.Bottom;
            _fullscreenSettingsPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            _fullscreenSettingsPanel.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 26, 26, 26)); // Darker background #1A1A1A
            _fullscreenSettingsPanel.CornerRadius = new CornerRadius(15); // Rounded corners on all sides
            _fullscreenSettingsPanel.Height = 300;
            _fullscreenSettingsPanel.Margin = new Thickness(10, 0, 10, 10); // Smaller equal margins on all sides

            // Variables for drag functionality
            double initialY = 0;
            double initialTranslateY = 0;
            bool isDragging = false;

            // Create the content for the settings panel
            var contentGrid = new Grid();
            contentGrid.RowDefinitions.Add(new RowDefinition() { Height = GridLength.Auto }); // Drag area
            contentGrid.RowDefinitions.Add(new RowDefinition() { Height = new GridLength(1, GridUnitType.Star) }); // Content

            // Create drag area with pointer event handlers for swipe-to-close
            var dragArea = new Grid();
            dragArea.Background = new SolidColorBrush(Colors.Transparent);
            dragArea.Height = 40;
            
            // Pointer pressed event
            dragArea.PointerPressed += (s, e) => {
                var pointer = e.Pointer;
                if (dragArea.CapturePointer(pointer))
                {
                    var point = e.GetCurrentPoint(dragArea);
                    initialY = point.Position.Y;
                    var dragTransform = _fullscreenSettingsPanel.RenderTransform as TranslateTransform;
                    initialTranslateY = dragTransform?.Y ?? 0;
                    isDragging = true;
                    e.Handled = true;
                }
            };
            
            // Pointer moved event
            dragArea.PointerMoved += (s, e) => {
                if (isDragging)
                {
                    var point = e.GetCurrentPoint(dragArea);
                    double deltaY = point.Position.Y - initialY;
                    
                    // Only move the panel down when dragging down
                    if (deltaY > 0)
                    {
                        var dragTransform = _fullscreenSettingsPanel.RenderTransform as TranslateTransform;
                        if (dragTransform != null)
                        {
                            dragTransform.Y = initialTranslateY + deltaY;
                        }
                    }
                    e.Handled = true;
                }
            };
            
            // Pointer released event
            dragArea.PointerReleased += (s, e) => {
                if (isDragging)
                {
                    isDragging = false;
                    dragArea.ReleasePointerCapture(e.Pointer);
                    
                    var dragTransform = _fullscreenSettingsPanel.RenderTransform as TranslateTransform;
                    if (dragTransform != null && dragTransform.Y > 150) // If dragged down more than half
                    {
                        // Check if we're in a sub panel and navigate back to main instead of closing
                        if (_fullscreenSettingsPanel != null)
                        {
                            // Find the panels by name
                            var mainPanel = FindVisualChild<StackPanel>(_fullscreenSettingsPanel, "MainSettingsPanel");
                            var qualityPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "QualitySettingsPanel");
                            var speedPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "SpeedSettingsPanel");
                            
                            // Check if we're in a sub panel
                            if (qualityPanel != null && qualityPanel.Visibility == Visibility.Visible ||
                                speedPanel != null && speedPanel.Visibility == Visibility.Visible)
                            {
                                // Hide sub panels and show main panel
                                if (qualityPanel != null) qualityPanel.Visibility = Visibility.Collapsed;
                                if (speedPanel != null) speedPanel.Visibility = Visibility.Collapsed;
                                if (mainPanel != null) mainPanel.Visibility = Visibility.Visible;
                                
                                // Reset the position to fully up since we're not closing
                                var resetAnimation = new DoubleAnimation();
                                resetAnimation.To = 0;
                                resetAnimation.Duration = TimeSpan.FromMilliseconds(300);
                                resetAnimation.EasingFunction = new CircleEase();
                                Storyboard.SetTarget(resetAnimation, dragTransform);
                                Storyboard.SetTargetProperty(resetAnimation, "Y");
                                var resetStoryboard = new Storyboard();
                                resetStoryboard.Children.Add(resetAnimation);
                                resetStoryboard.Begin();
                            }
                            else
                            {
                                // Close the settings panel
                                _isSettingsPanelOpen = false;
                                AnimateFullscreenSettingsPanel(false);
                            }
                        }
                        else
                        {
                            // Close the settings panel
                            _isSettingsPanelOpen = false;
                            AnimateFullscreenSettingsPanel(false);
                        }
                    }
                    else
                    {
                        // Snap back to original position
                        AnimateFullscreenSettingsPanel(true);
                    }
                    e.Handled = true;
                }
            };
            
            // Pointer capture lost event
            dragArea.PointerCaptureLost += (s, e) => {
                if (isDragging)
                {
                    isDragging = false;
                    // Snap back to original position
                    AnimateFullscreenSettingsPanel(true);
                }
            };

            // Tap event for quick close
            dragArea.Tapped += (s, e) => {
                // Check if we're in a sub panel and navigate back to main instead of closing
                if (_fullscreenSettingsPanel != null)
                {
                    // Find the panels by name
                    var mainPanel = FindVisualChild<StackPanel>(_fullscreenSettingsPanel, "MainSettingsPanel");
                    var qualityPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "QualitySettingsPanel");
                    var speedPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "SpeedSettingsPanel");
                    
                    // Check if we're in a sub panel
                    if (qualityPanel != null && qualityPanel.Visibility == Visibility.Visible ||
                        speedPanel != null && speedPanel.Visibility == Visibility.Visible)
                    {
                        // Hide sub panels and show main panel
                        if (qualityPanel != null) qualityPanel.Visibility = Visibility.Collapsed;
                        if (speedPanel != null) speedPanel.Visibility = Visibility.Collapsed;
                        if (mainPanel != null) mainPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // Close settings when tapping the drag area
                        _isSettingsPanelOpen = false;
                        AnimateFullscreenSettingsPanel(false);
                    }
                }
                else
                {
                    // Close settings when tapping the drag area
                    _isSettingsPanelOpen = false;
                    AnimateFullscreenSettingsPanel(false);
                }
                e.Handled = true;
            };

            // Create overlay background that closes settings when tapped
            var settingsOverlay = new Grid();
            settingsOverlay.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0)); // Semi-transparent black
            settingsOverlay.Tapped += (s, e) => {
                // Check if we're in a sub panel and navigate back to main instead of closing
                if (_fullscreenSettingsPanel != null)
                {
                    // Find the panels by name
                    var mainPanel = FindVisualChild<StackPanel>(_fullscreenSettingsPanel, "MainSettingsPanel");
                    var qualityPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "QualitySettingsPanel");
                    var speedPanel = FindVisualChild<ScrollViewer>(_fullscreenSettingsPanel, "SpeedSettingsPanel");
                    
                    // Check if we're in a sub panel
                    if (qualityPanel != null && qualityPanel.Visibility == Visibility.Visible ||
                        speedPanel != null && speedPanel.Visibility == Visibility.Visible)
                    {
                        // Hide sub panels and show main panel
                        if (qualityPanel != null) qualityPanel.Visibility = Visibility.Collapsed;
                        if (speedPanel != null) speedPanel.Visibility = Visibility.Collapsed;
                        if (mainPanel != null) mainPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // Close settings when tapping the overlay
                        _isSettingsPanelOpen = false;
                        AnimateFullscreenSettingsPanel(false);
                    }
                }
                else
                {
                    // Close settings when tapping the overlay
                    _isSettingsPanelOpen = false;
                    AnimateFullscreenSettingsPanel(false);
                }
                e.Handled = true;
            };

            // Create visible drag handle
            var dragHandle = new Rectangle();
            dragHandle.Width = 40;
            dragHandle.Height = 4;
            dragHandle.Fill = new SolidColorBrush(Colors.Gray);
            dragHandle.RadiusX = 2;
            dragHandle.RadiusY = 2;
            dragHandle.HorizontalAlignment = HorizontalAlignment.Center;
            dragHandle.VerticalAlignment = VerticalAlignment.Center;

            dragArea.Children.Add(dragHandle);
            Grid.SetRow(dragArea, 0);
            contentGrid.Children.Add(dragArea);

            // Create settings content grid (matching normal settings panel)
            var settingsContentGrid = new Grid();
            settingsContentGrid.Margin = new Thickness(20, 0, 20, 20);
            Grid.SetRow(settingsContentGrid, 1);

            // Create main settings panel with two full-width buttons
            var mainSettingsPanel = new StackPanel();
            mainSettingsPanel.Name = "MainSettingsPanel";

            // Create quality settings panel with ScrollViewer (matching normal settings panel)
            var qualitySettingsPanel = new ScrollViewer();
            qualitySettingsPanel.Name = "QualitySettingsPanel";
            qualitySettingsPanel.Visibility = Visibility.Collapsed;
            qualitySettingsPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            
            var qualityContentStackPanel = new StackPanel();
           
            var qualityHeader = new TextBlock();
            qualityHeader.Text = "Quality";
            qualityHeader.Foreground = new SolidColorBrush(Colors.White);
            qualityHeader.FontWeight = Windows.UI.Text.FontWeights.SemiBold;
            qualityHeader.Margin = new Thickness(0, 0, 0, 16);
            qualityHeader.FontSize = 16;
            qualityContentStackPanel.Children.Add(qualityHeader);
            
            var qualityOptionsPanel = new StackPanel();
            qualityOptionsPanel.Name = "QualityOptionsPanel";
            qualityContentStackPanel.Children.Add(qualityOptionsPanel);
            qualitySettingsPanel.Content = qualityContentStackPanel;

            // Create speed settings panel with ScrollViewer (matching normal settings panel)
            var speedSettingsPanel = new ScrollViewer();
            speedSettingsPanel.Name = "SpeedSettingsPanel";
            speedSettingsPanel.Visibility = Visibility.Collapsed;
            speedSettingsPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            
            var speedContentStackPanel = new StackPanel();
           
            
            var speedHeader = new TextBlock();
            speedHeader.Text = "Playback speed";
            speedHeader.Foreground = new SolidColorBrush(Colors.White);
            speedHeader.FontWeight = Windows.UI.Text.FontWeights.SemiBold;
            speedHeader.Margin = new Thickness(0, 0, 0, 16);
            speedHeader.FontSize = 16;
            speedContentStackPanel.Children.Add(speedHeader);
            
            var speedOptionsPanel = new StackPanel();
            speedOptionsPanel.Name = "SpeedOptionsPanel";
            speedContentStackPanel.Children.Add(speedOptionsPanel);
            speedSettingsPanel.Content = speedContentStackPanel;

            // Subtitles panel, same shape as the quality and speed ones.
            var subtitlesSettingsPanel = new ScrollViewer();
            subtitlesSettingsPanel.Name = "SubtitlesSettingsPanel";
            subtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            subtitlesSettingsPanel.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            var subtitlesContentStackPanel = new StackPanel();

            var subtitlesHeader = new TextBlock();
            subtitlesHeader.Text = "Subtitles";
            subtitlesHeader.Foreground = new SolidColorBrush(Colors.White);
            subtitlesHeader.FontWeight = Windows.UI.Text.FontWeights.SemiBold;
            subtitlesHeader.Margin = new Thickness(0, 0, 0, 16);
            subtitlesHeader.FontSize = 16;
            subtitlesContentStackPanel.Children.Add(subtitlesHeader);

            var subtitlesOptionsPanel = new StackPanel();
            subtitlesOptionsPanel.Name = "SubtitlesOptionsPanel";
            subtitlesContentStackPanel.Children.Add(subtitlesOptionsPanel);
            subtitlesSettingsPanel.Content = subtitlesContentStackPanel;

            // Create quality option buttons (rebuilt whenever the panel is opened so the
            // highlight follows the current selection).
            PopulateFullscreenQualityOptions(qualityOptionsPanel);

            // Create speed option buttons. Rates above 1x are hidden while a demuxed source is
            // playing: measured on the device, the pipeline accepts the rate for a
            // MediaStreamSource but the media clock keeps running at exactly 1.00x, so those
            // options would simply do nothing. Slower-than-normal rates are unaffected.
            var speedOptions = GetAvailableSpeedOptions();
            for (int i = 0; i < speedOptions.Count; i++)
            {
                var speed = speedOptions[i];
                var speedOptionButton = new Button();
                speedOptionButton.Background = new SolidColorBrush(Colors.Transparent);
                speedOptionButton.HorizontalAlignment = HorizontalAlignment.Stretch;
                speedOptionButton.HorizontalContentAlignment = HorizontalAlignment.Left;
                speedOptionButton.Padding = new Thickness(16, 8, 16, 8);
                speedOptionButton.Height = 48;
                speedOptionButton.Margin = new Thickness(0, 0, 0, 4);
                speedOptionButton.Content = speed;
                speedOptionButton.Foreground = new SolidColorBrush(Colors.White);
                speedOptionButton.Tag = speed;
                speedOptionButton.Click += (s, e) => {
                    var button = s as Button;
                    var speedValue = button?.Tag as string;
                    if (speedValue != null)
                    {
                        // Apply the speed change
                        try
                        {
                            if (MediaPlayer?.MediaPlayer?.PlaybackSession == null) return;
                            var rateString = speedValue.Replace("x", "");
                            double rate;
                            if (double.TryParse(rateString, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out rate))
                            {
                                SetPlaybackRate(rate);
                            }
                        }
                        catch { }
                        
                        // Close settings panel
                        _isSettingsPanelOpen = false;
                        AnimateFullscreenSettingsPanel(false);
                    }
                };
                speedOptionsPanel.Children.Add(speedOptionButton);
            }

            // Quality Button
            var qualityButton = new Button();
            qualityButton.Background = new SolidColorBrush(Colors.Transparent);
            qualityButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            qualityButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            qualityButton.Padding = new Thickness(0);
            qualityButton.Height = 60;
            qualityButton.Margin = new Thickness(0, 0, 0, 8);
            var qualityButtonGrid = new Grid();
            qualityButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            qualityButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            qualityButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            var qualityIcon = new Image();
            qualityIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/quality.png"));
            qualityIcon.Width = 24;
            qualityIcon.Height = 24;
            qualityIcon.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(qualityIcon, 0);
            qualityButtonGrid.Children.Add(qualityIcon);
            var qualityText = new TextBlock();
            qualityText.Text = "Quality";
            qualityText.Foreground = new SolidColorBrush(Colors.White);
            qualityText.VerticalAlignment = VerticalAlignment.Center;
            qualityText.FontSize = 16;
            Grid.SetColumn(qualityText, 1);
            qualityButtonGrid.Children.Add(qualityText);
            var qualitySkipIcon = new Image();
            qualitySkipIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/skip.png"));
            qualitySkipIcon.Width = 24;
            qualitySkipIcon.Height = 24;
            qualitySkipIcon.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(qualitySkipIcon, 2);
            qualityButtonGrid.Children.Add(qualitySkipIcon);
            qualityButton.Content = qualityButtonGrid;
            qualityButton.Click += (s, e) => {
                // Rebuild so the available list and the current-quality highlight are fresh.
                PopulateFullscreenQualityOptions(qualityOptionsPanel);

                // Hide main panel and show quality settings panel
                mainSettingsPanel.Visibility = Visibility.Collapsed;
                qualitySettingsPanel.Visibility = Visibility.Visible;
            };
            mainSettingsPanel.Children.Add(qualityButton);

            // Speed Button
            var speedButton = new Button();
            speedButton.Background = new SolidColorBrush(Colors.Transparent);
            speedButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            speedButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            speedButton.Padding = new Thickness(0);
            speedButton.Height = 60;
            speedButton.Margin = new Thickness(0, 0, 0, 8);
            var speedButtonGrid = new Grid();
            speedButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            speedButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            speedButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            var speedIcon = new Image();
            speedIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/speed.png"));
            speedIcon.Width = 24;
            speedIcon.Height = 24;
            speedIcon.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(speedIcon, 0);
            speedButtonGrid.Children.Add(speedIcon);
            var speedText = new TextBlock();
            speedText.Text = "Playback speed";
            speedText.Foreground = new SolidColorBrush(Colors.White);
            speedText.VerticalAlignment = VerticalAlignment.Center;
            speedText.FontSize = 16;
            Grid.SetColumn(speedText, 1);
            speedButtonGrid.Children.Add(speedText);
            var speedSkipIcon = new Image();
            speedSkipIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/skip.png"));
            speedSkipIcon.Width = 24;
            speedSkipIcon.Height = 24;
            speedSkipIcon.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(speedSkipIcon, 2);
            speedButtonGrid.Children.Add(speedSkipIcon);
            speedButton.Content = speedButtonGrid;
            speedButton.Click += (s, e) => {
                // Hide main panel and show speed settings panel
                mainSettingsPanel.Visibility = Visibility.Collapsed;
                speedSettingsPanel.Visibility = Visibility.Visible;
            };
            mainSettingsPanel.Children.Add(speedButton);

            // Subtitles Button — only offered when the video actually ships captions.
            var subtitlesButton = new Button();
            subtitlesButton.Background = new SolidColorBrush(Colors.Transparent);
            subtitlesButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            subtitlesButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            subtitlesButton.Padding = new Thickness(0);
            subtitlesButton.Height = 60;
            subtitlesButton.Margin = new Thickness(0, 0, 0, 8);
            subtitlesButton.Visibility = _subtitleTracks.HasAny ? Visibility.Visible : Visibility.Collapsed;
            var subtitlesButtonGrid = new Grid();
            subtitlesButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            subtitlesButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            subtitlesButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            var subtitlesIcon = new Image();
            subtitlesIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/comments.png"));
            subtitlesIcon.Width = 24;
            subtitlesIcon.Height = 24;
            subtitlesIcon.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(subtitlesIcon, 0);
            subtitlesButtonGrid.Children.Add(subtitlesIcon);
            var subtitlesText = new TextBlock();
            subtitlesText.Text = "Subtitles";
            subtitlesText.Foreground = new SolidColorBrush(Colors.White);
            subtitlesText.VerticalAlignment = VerticalAlignment.Center;
            subtitlesText.FontSize = 16;
            Grid.SetColumn(subtitlesText, 1);
            subtitlesButtonGrid.Children.Add(subtitlesText);
            var subtitlesSkipIcon = new Image();
            subtitlesSkipIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/skip.png"));
            subtitlesSkipIcon.Width = 24;
            subtitlesSkipIcon.Height = 24;
            subtitlesSkipIcon.Margin = new Thickness(16, 0, 0, 0);
            Grid.SetColumn(subtitlesSkipIcon, 2);
            subtitlesButtonGrid.Children.Add(subtitlesSkipIcon);
            subtitlesButton.Content = subtitlesButtonGrid;
            subtitlesButton.Click += (s, e) => {
                _fullscreenShowingTranslations = false;
                PopulateFullscreenSubtitleOptions(subtitlesOptionsPanel);
                mainSettingsPanel.Visibility = Visibility.Collapsed;
                subtitlesSettingsPanel.Visibility = Visibility.Visible;
            };
            mainSettingsPanel.Children.Add(subtitlesButton);

            // Reload Button
            var reloadButton = new Button();
            reloadButton.Background = new SolidColorBrush(Colors.Transparent);
            reloadButton.HorizontalAlignment = HorizontalAlignment.Stretch;
            reloadButton.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            reloadButton.Padding = new Thickness(0);
            reloadButton.Height = 60;
            reloadButton.Margin = new Thickness(0, 0, 0, 8);
            var reloadButtonGrid = new Grid();
            reloadButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            reloadButtonGrid.ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star) });
            var reloadIcon = new Image();
            reloadIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/reload.png"));
            reloadIcon.Width = 24;
            reloadIcon.Height = 24;
            reloadIcon.Margin = new Thickness(0, 0, 16, 0);
            Grid.SetColumn(reloadIcon, 0);
            reloadButtonGrid.Children.Add(reloadIcon);
            var reloadText = new TextBlock();
            reloadText.Text = "Reload video";
            reloadText.Foreground = new SolidColorBrush(Colors.White);
            reloadText.VerticalAlignment = VerticalAlignment.Center;
            reloadText.FontSize = 16;
            Grid.SetColumn(reloadText, 1);
            reloadButtonGrid.Children.Add(reloadText);
            reloadButton.Content = reloadButtonGrid;
            reloadButton.Click += (s, e) => {
                // Close settings panel
                _isSettingsPanelOpen = false;
                AnimateFullscreenSettingsPanel(false);
                
                // Reload the video
                try
                {
                    // Notify the parent page to reload the video
                    RefreshRequested?.Invoke(this, null);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("Error in reload button: " + ex.Message);
                }
            };
            mainSettingsPanel.Children.Add(reloadButton);

            // Add panels to settings content grid
            settingsContentGrid.Children.Add(mainSettingsPanel);
            settingsContentGrid.Children.Add(qualitySettingsPanel);
            settingsContentGrid.Children.Add(speedSettingsPanel);
            settingsContentGrid.Children.Add(subtitlesSettingsPanel);

            // Add settings content grid to content grid
            contentGrid.Children.Add(settingsContentGrid);

            // Add content grid to settings panel
            _fullscreenSettingsPanel.Children.Add(contentGrid);

            // Create a translate transform for animation
            var settingsTransform = new TranslateTransform();
            settingsTransform.Y = 310; // Start hidden (below screen)
            _fullscreenSettingsPanel.RenderTransform = settingsTransform;

            // Add overlay to fullscreen grid first (so it's behind the settings panel)
            _fullscreenGrid.Children.Add(settingsOverlay);
            
            // Add settings panel to fullscreen grid
            _fullscreenGrid.Children.Add(_fullscreenSettingsPanel);

            // Show main panel and hide others when creating
            mainSettingsPanel.Visibility = Visibility.Visible;
            qualitySettingsPanel.Visibility = Visibility.Collapsed;
            speedSettingsPanel.Visibility = Visibility.Collapsed;
            subtitlesSettingsPanel.Visibility = Visibility.Collapsed;

            // Animate the settings panel sliding up
            AnimateFullscreenSettingsPanel(true);
        }

        // Method to animate the fullscreen settings panel
        private void AnimateFullscreenSettingsPanel(bool show)
        {
            if (_fullscreenSettingsPanel == null) return;

            var animateTransform = _fullscreenSettingsPanel.RenderTransform as TranslateTransform;
            if (animateTransform == null) return;

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0; // Move to visible position
            }
            else
            {
                animation.To = 310; // Move down below screen (height + margin)
            }

            Storyboard.SetTarget(animation, animateTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                // Hide the settings panel and overlay after animation completes
                storyboard.Completed += (s, e) => {
                    if (_fullscreenGrid != null && _fullscreenSettingsPanel != null)
                    {
                        // Instead of removing children, just hide them
                        // Find and hide the overlay (it's the second to last child, just before settings panel)
                        if (_fullscreenGrid.Children.Count >= 2)
                        {
                            var overlay = _fullscreenGrid.Children[_fullscreenGrid.Children.Count - 2];
                            if (overlay is Grid)
                            {
                                overlay.Visibility = Visibility.Collapsed;
                            }
                        }
                        
                        // Hide settings panel
                        _fullscreenSettingsPanel.Visibility = Visibility.Collapsed;
                        
                        // Reset panel states to ensure main panel is shown next time
                        if (_fullscreenSettingsPanel.Children.Count > 0)
                        {
                            var contentGrid = _fullscreenSettingsPanel.Children[0] as Grid;
                            if (contentGrid != null && contentGrid.Children.Count > 0)
                            {
                                var settingsContentGrid = contentGrid.Children[0] as Grid;
                                if (settingsContentGrid != null && settingsContentGrid.Children.Count >= 3)
                                {
                                    // Show the first panel (main settings) and hide the others
                                    settingsContentGrid.Children[0].Visibility = Visibility.Visible; // Main panel
                                    for (int i = 1; i < settingsContentGrid.Children.Count; i++)
                                    {
                                        settingsContentGrid.Children[i].Visibility = Visibility.Collapsed;
                                    }
                                }
                            }
                        }
                        
                        // Reset the flag
                        _isSettingsPanelOpen = false;
                    }
                };
            }

            storyboard.Begin();
        }

        private void MediaPlayer_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            // Get the position of the double tap relative to PlayerGrid for consistency
            var position = e.GetPosition(PlayerGrid);
            var playerWidth = PlayerGrid.ActualWidth;
            
            System.Diagnostics.Debug.WriteLine($"DoubleTapped at X={position.X}, Width={playerWidth}");
            
            // If tap is on the left third, seek backward 10 seconds
            if (position.X < playerWidth / 3)
            {
                System.Diagnostics.Debug.WriteLine("Seeking backward 10 seconds");
                SeekVideo(-10); // Seek backward 10 seconds
                ShowSkipOverlay(false); // Show backward skip overlay
            }
            // If tap is on the right third, seek forward 10 seconds
            else if (position.X > (playerWidth * 2) / 3)
            {
                System.Diagnostics.Debug.WriteLine("Seeking forward 10 seconds");
                SeekVideo(10); // Seek forward 10 seconds
                ShowSkipOverlay(true); // Show forward skip overlay
            }
            // If tap is in the middle third, toggle fullscreen
            else
            {
                System.Diagnostics.Debug.WriteLine("Toggling fullscreen");
                ToggleFullscreen();
            }
            
            // Mark event as handled to prevent propagation
            e.Handled = true;
        }
        
        // Method to show skip overlay with appropriate icon and text
        private void ShowSkipOverlay(bool isForward)
        {
            SkipOverlayRequested?.Invoke(this, isForward);
            Grid skipOverlay = null;
            Border skipOverlayBorder = null;
            StackPanel skipOverlayPanel = null;
            Windows.UI.Xaml.Controls.Image skipIcon = null;
            TextBlock skipText = null;
            
            // Check if we're in fullscreen mode
            if (_isFullscreen)
            {
                // Use the overlay elements from CustomVideoPlayer.xaml
                skipOverlay = SkipOverlayFullscreen;
                skipOverlayBorder = SkipOverlayBorderFullscreen;
                skipOverlayPanel = SkipOverlayPanelFullscreen;
                skipIcon = SkipIconFullscreen;
                skipText = SkipTextFullscreen;
            }
            else
            {
                // Try to find the SkipOverlay in the parent Video page
                skipOverlay = FindVisualChild<Grid>(this.Parent as DependencyObject, "SkipOverlay");
                if (skipOverlay != null)
                {
                    skipOverlayBorder = FindVisualChild<Border>(skipOverlay, "SkipOverlayBorder");
                    skipOverlayPanel = FindVisualChild<StackPanel>(skipOverlay, "SkipOverlayPanel");
                    skipIcon = FindVisualChild<Windows.UI.Xaml.Controls.Image>(skipOverlay, "SkipIcon");
                    skipText = FindVisualChild<TextBlock>(skipOverlay, "SkipText");
                }
            }
            
            if (skipOverlay != null && skipOverlayBorder != null && skipOverlayPanel != null && skipIcon != null && skipText != null)
            {
                // Update the skip overlay UI based on direction
                if (isForward)
                {
                    // Forward skip: text "+10" on left, icon skip.png on right, panel on right side
                    skipText.Text = "+10";
                    skipIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/skip.png"));
                    skipIcon.Margin = new Thickness(10, 0, 0, 0);
                    
                    // Position border on the right side
                    skipOverlayBorder.HorizontalAlignment = HorizontalAlignment.Right;
                    skipOverlayBorder.Margin = new Thickness(0, 0, 20, 0);
                    
                    // Order: text first, icon second (text left, icon right)
                    if (skipOverlayPanel.Children.Count >= 2)
                    {
                        if (skipOverlayPanel.Children[0] != skipText)
                        {
                            skipOverlayPanel.Children.Clear();
                            skipOverlayPanel.Children.Add(skipText);
                            skipOverlayPanel.Children.Add(skipIcon);
                        }
                    }
                }
                else
                {
                    // Backward skip: icon back.png on left, text "-10" on right, panel on left side
                    skipText.Text = "-10";
                    skipIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/back.png"));
                    skipIcon.Margin = new Thickness(0, 0, 10, 0);
                    
                    // Position border on the left side
                    skipOverlayBorder.HorizontalAlignment = HorizontalAlignment.Left;
                    skipOverlayBorder.Margin = new Thickness(20, 0, 0, 0);
                    
                    // Order: icon first, text second (icon left, text right)
                    if (skipOverlayPanel.Children.Count >= 2)
                    {
                        if (skipOverlayPanel.Children[0] != skipIcon)
                        {
                            skipOverlayPanel.Children.Clear();
                            skipOverlayPanel.Children.Add(skipIcon);
                            skipOverlayPanel.Children.Add(skipText);
                        }
                    }
                }
                
                // Show the overlay
                skipOverlay.Visibility = Visibility.Visible;
                
                // Start or reset the timer to hide the overlay
                if (_skipOverlayTimer != null)
                {
                    _skipOverlayTimer.Stop();
                    _skipOverlayTimer.Start();
                }
            }
        }
        
        // Helper method to find visual child elements by name
        private T FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            if (parent == null) return null;
            
            int childrenCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childrenCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var frameworkElement = child as FrameworkElement;
                
                if (frameworkElement != null && frameworkElement.Name == name && child is T)
                {
                    return (T)child;
                }
                
                var result = FindVisualChild<T>(child, name);
                if (result != null)
                {
                    return result;
                }
            }
            
            return null;
        }
        
        // Method to seek video by specified seconds
        public void SeekVideo(double seconds)
        {
            if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            // Chain onto the pending target rather than the live position, so three quick taps
            // move 30 seconds and not 10 — the player has not moved yet at that point.
            var basePosition = _pendingSeekTarget ?? MediaPlayer.MediaPlayer.PlaybackSession.Position;
            RequestSeek(basePosition.Add(TimeSpan.FromSeconds(seconds)));
        }

        // Seeking is deferred until the user stops. With the demuxed source every seek makes the
        // player drop its buffer and refetch fragments, so applying one per tap meant it kept
        // trying to start playing at positions the user was already skipping past.
        private void RequestSeek(TimeSpan targetPosition)
        {
            var clamped = ClampPositionToDuration(targetPosition);
            _pendingSeekTarget = clamped;

            // The UI follows the finger immediately even though the pipeline has not moved yet.
            if (ProgressSlider != null)
            {
                ProgressSlider.Value = clamped.TotalSeconds;
                UpdateSegmentedProgressValue();
            }
            UpdateTimeDisplayForPosition(clamped);

            if (!_controlsVisible)
            {
                FadeInControls();
            }
            ResetAutoHideTimer();

            if (_seekDebounceTimer == null)
            {
                _seekDebounceTimer = new DispatcherTimer();
                _seekDebounceTimer.Interval = TimeSpan.FromMilliseconds(500);
                _seekDebounceTimer.Tick += SeekDebounceTimer_Tick;
            }

            _seekDebounceTimer.Stop();
            _seekDebounceTimer.Start();
        }

        private void SeekDebounceTimer_Tick(object sender, object e)
        {
            _seekDebounceTimer.Stop();

            var target = _pendingSeekTarget;
            _pendingSeekTarget = null;

            if (target == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[Player] Applying settled seek to " + target.Value);
            ApplySeekToPosition(target.Value);
        }

        // Applies a pending seek right away — used when playback must not continue from the old
        // spot, e.g. when the page is leaving or the source is being swapped.
        private void FlushPendingSeek()
        {
            if (_seekDebounceTimer != null)
            {
                _seekDebounceTimer.Stop();
            }

            var target = _pendingSeekTarget;
            _pendingSeekTarget = null;

            if (target != null)
            {
                ApplySeekToPosition(target.Value);
            }
        }

        private void ApplySeekToPosition(TimeSpan newPosition)
        {
            if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            // A single tap fires BOTH the pointer path (press+release) AND the Tapped handler, so
            // ApplySeekToPosition was being called twice a few milliseconds apart. Two seeks that
            // close together on the demuxed MediaStreamSource crash the native pipeline. Coalesce
            // them: a near-identical seek right after another is dropped.
            var nowTicks = DateTime.UtcNow.Ticks;
            if (Math.Abs((newPosition - _lastAppliedSeekPosition).TotalSeconds) < 1.0
                && (nowTicks - _lastAppliedSeekTicks) < TimeSpan.FromMilliseconds(700).Ticks)
            {
                System.Diagnostics.Debug.WriteLine("[Seek] Dropping duplicate seek to " + newPosition);
                return;
            }
            _lastAppliedSeekPosition = newPosition;
            _lastAppliedSeekTicks = nowTicks;

            var wasPlaying = _isPlaying;
            System.Diagnostics.Debug.WriteLine("[Seek] Applying to " + newPosition
                + ", separateAudio=" + _usingSeparateAudio + ", wasPlaying=" + wasPlaying);

            if (_usingSeparateAudio)
            {
                SetSeparateAudioPosition(newPosition, wasPlaying);
            }
            else
            {
                MediaPlayer.MediaPlayer.PlaybackSession.Position = newPosition;
                if (wasPlaying)
                {
                    MediaPlayer.MediaPlayer.Play();
                }
            }

            UpdateTimeDisplay();
        }

        // Method used by description timestamps and chapter links.
        public void SeekTo(TimeSpan targetPosition)
        {
            SeekToPosition(targetPosition, true);
        }

        private void SeekToPosition(TimeSpan targetPosition, bool showControls)
        {
            if (MediaPlayer?.MediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            var newPosition = ClampPositionToDuration(targetPosition);

            if (_usingSeparateAudio)
            {
                SetSeparateAudioPosition(newPosition, _isPlaying);
            }
            else
            {
                MediaPlayer.MediaPlayer.PlaybackSession.Position = newPosition;
            }

            ProgressSlider.Value = newPosition.TotalSeconds;
            UpdateTimeDisplay();

            if (showControls)
            {
                if (!_controlsVisible)
                {
                    FadeInControls();
                }
                ResetAutoHideTimer();
            }
        }

        private TimeSpan ClampPositionToDuration(TimeSpan position)
        {
            if (position < TimeSpan.Zero)
            {
                return TimeSpan.Zero;
            }

            var duration = GetEffectiveDuration();
            if (duration > TimeSpan.Zero && position > duration)
            {
                return duration;
            }

            return position;
        }

        private TimeSpan GetEffectiveDuration()
        {
            if (_parsedDuration > TimeSpan.Zero)
            {
                return _parsedDuration;
            }

            try
            {
                if (MediaPlayer?.MediaPlayer?.PlaybackSession != null)
                {
                    return MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: failed to read duration - " + ex.Message);
            }

            return TimeSpan.Zero;
        }

        private void MediaPlayer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Show controls when tapping anywhere on the player
            if (!_controlsVisible)
            {
                FadeInControls();
            }
            else
            {
                // Reset the auto-hide timer
                ResetAutoHideTimer();
            }
            
            // Prevent tap from propagating to parent elements
            e.Handled = true;
        }

        private void MediaPlayer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            // Show controls when mouse moves over player
            if (!_controlsVisible)
            {
                FadeInControls();
            }
            else
            {
                // Reset the auto-hide timer even if controls are visible
                ResetAutoHideTimer();
            }
        }

        private void MediaPlayer_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // Only start the auto-hide timer if we're not in fullscreen mode
            // In fullscreen mode, we want to be more aggressive about hiding controls
            if (!_isFullscreen)
            {
                // Start the auto-hide timer when mouse leaves the player in normal mode
                StartAutoHideTimer();
            }
            else
            {
                // In fullscreen mode, we still want to hide controls after inactivity
                StartAutoHideTimer();
            }
        }

        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine($"ProgressSlider_ValueChanged: OldValue={e.OldValue}, NewValue={e.NewValue}, IsUserDragging={_isUserDragging}");
            
            // Always update time display and segmented progress visuals.
            UpdateTimeDisplay();
            if (!_isRebuildingTimelineVisual)
            {
                UpdateSegmentedProgressValue();
            }
            
            // Only update slider visual state during dragging - don't seek video
            // Actual seeking happens in ManipulationCompleted or when clicking on track
            // This prevents stuttering during drag operations
        }

        private void ProgressSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            // No seeking while the new-quality source is still loading.
            if (_sourceLoading)
            {
                e.Handled = true;
                return;
            }

            System.Diagnostics.Debug.WriteLine("ProgressSlider_ManipulationStarted: Disabling sync timer.");
            ResetAutoHideTimer();
            if (!_controlsVisible)
            {
                FadeInControls();
            }
            _isUserDragging = true;
            _audioSyncTimer?.Stop(); // Completely disable the timer while seeking
        }

        private void ProgressSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("ProgressSlider_ManipulationCompleted");
            ResetAutoHideTimer();
            if (!_controlsVisible)
            {
                FadeInControls();
            }

            HideScrubPreview();
            SeekToSliderValue();
            _isUserDragging = false;
        }

        private void ProgressSliderTrack_Tapped(object sender, TappedRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("ProgressSliderTrack_Tapped: Handling segmented click-to-seek");

            if (ProgressSlider == null || ProgressSliderHitArea == null || ProgressSliderHitArea.ActualWidth <= 0)
            {
                return;
            }

            var point = e.GetPosition(ProgressSliderHitArea);
            double newValue = GetTimelineValueFromX(point.X);
            ProgressSlider.Value = Math.Max(ProgressSlider.Minimum, Math.Min(newValue, ProgressSlider.Maximum));
            UpdateSegmentedProgressValue();
            SeekToSliderValue();
            e.Handled = true;
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[Seek] PointerPressed on bar; sourceLoading=" + _sourceLoading
                + ", storyboard=" + (_storyboard != null && _storyboard.HasFrames));

            if (ProgressSlider == null || ProgressSliderHitArea == null)
            {
                return;
            }

            // No tap-to-seek while the new-quality source is still loading.
            if (_sourceLoading)
            {
                System.Diagnostics.Debug.WriteLine("[Seek] Blocked: source still loading");
                e.Handled = true;
                return;
            }

            ResetAutoHideTimer();
            if (!_controlsVisible)
            {
                FadeInControls();
            }

            _isUserDragging = true;
            _isProgressPointerCaptured = true;
            _audioSyncTimer?.Stop();

            var element = sender as UIElement;
            if (element != null)
            {
                element.CapturePointer(e.Pointer);
            }

            UpdateProgressSliderValueFromPointer(e);
            e.Handled = true;
        }

        private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isProgressPointerCaptured)
            {
                return;
            }

            UpdateProgressSliderValueFromPointer(e);
            e.Handled = true;
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isProgressPointerCaptured)
            {
                return;
            }

            UpdateProgressSliderValueFromPointer(e);
            CompleteProgressPointerSeek(sender as UIElement, e.Pointer);
            e.Handled = true;
        }

        private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            CompleteProgressPointerSeek(sender as UIElement, e.Pointer);
            e.Handled = true;
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            // A touch drag frequently loses pointer capture WITHOUT a clean PointerReleased — that
            // is why the seek never applied: this used to just drop the gesture. Commit it here
            // instead, exactly as a release would. If a real release already ran, the capture flag
            // is already false and CompleteProgressPointerSeek is a no-op.
            if (_isProgressPointerCaptured)
            {
                CompleteProgressPointerSeek(sender as UIElement, e != null ? e.Pointer : null);
            }
            else
            {
                _isUserDragging = false;
                HideScrubPreview();
            }
        }

        private void UpdateProgressSliderValueFromPointer(PointerRoutedEventArgs e)
        {
            if (ProgressSlider == null || ProgressSliderHitArea == null)
            {
                return;
            }

            var point = e.GetCurrentPoint(ProgressSliderHitArea).Position;
            double newValue = GetTimelineValueFromX(point.X);
            ProgressSlider.Value = Math.Max(ProgressSlider.Minimum, Math.Min(newValue, ProgressSlider.Maximum));
            UpdateTimeDisplay();
            UpdateSegmentedProgressValue();
            ShowScrubPreview(ProgressSlider.Value, point.X);
        }

        // Storyboard scrub preview -------------------------------------------------------------

        private StoryboardSpec _storyboard;
        private readonly Dictionary<string, BitmapImage> _scrubSheetCache = new Dictionary<string, BitmapImage>();
        private string _scrubSheetUrl = string.Empty;
        private bool _scrubPreviewLogged;
        // Guards against the tap double-seek (pointer path + Tapped both fire for one tap).
        private TimeSpan _lastAppliedSeekPosition = TimeSpan.FromSeconds(-100);
        private long _lastAppliedSeekTicks;

        public void SetStoryboardSpec(string spec)
        {
            _storyboard = StoryboardSpec.Parse(spec);
            _scrubSheetUrl = string.Empty;
            _scrubSheetCache.Clear();
            _scrubPreviewLogged = false;
            HideScrubPreview();
            System.Diagnostics.Debug.WriteLine("[Player] Storyboard "
                + (_storyboard != null && _storyboard.HasFrames ? "ready" : "unavailable")
                + " (spec length " + (spec == null ? 0 : spec.Length) + ")");

            // Pre-warm the sprite sheets so the first scrubs are instant. Done as BitmapImages
            // (the platform's own image loader) rather than a parallel HttpClient download — the
            // latter, running alongside the 1080p60 demux, crashed the native pipeline. Capped and
            // small (a handful of ~45 KB sheets), so the decoded memory is negligible.
            if (_storyboard != null && _storyboard.HasFrames)
            {
                try
                {
                    var urls = _storyboard.AllSheetUrls(12);
                    foreach (var url in urls)
                    {
                        if (!_scrubSheetCache.ContainsKey(url))
                        {
                            _scrubSheetCache[url] = new BitmapImage(new Uri(url));
                        }
                    }
                    System.Diagnostics.Debug.WriteLine("[Player] Pre-warmed " + urls.Count + " storyboard sheet(s)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Player] Storyboard pre-warm failed: " + ex.Message);
                }
            }
        }

        private void ShowScrubPreview(double seconds, double pointerX)
        {
            if (ScrubPreviewPanel == null || _storyboard == null || !_storyboard.HasFrames)
            {
                return;
            }

            try
            {
                var frame = _storyboard.FrameAt(seconds);
                if (frame == null)
                {
                    HideScrubPreview();
                    return;
                }

                if (!_scrubPreviewLogged)
                {
                    _scrubPreviewLogged = true;
                    System.Diagnostics.Debug.WriteLine("[Player] Scrub preview showing, first sheet: " + frame.SheetUrl);
                }

                // The chosen level may use small tiles (an 80x45 level is preferred for fast
                // loading). Scale it up to a consistent on-screen size so the preview looks the
                // same regardless of which level a video provides.
                const double DisplayWidth = 128.0;
                var scale = frame.ThumbWidth > 0 ? DisplayWidth / frame.ThumbWidth : 1.0;

                ScrubPreviewClip.Width = frame.ThumbWidth * scale;
                ScrubPreviewClip.Height = frame.ThumbHeight * scale;
                ScrubPreviewScale.ScaleX = scale;
                ScrubPreviewScale.ScaleY = scale;

                if (!string.Equals(_scrubSheetUrl, frame.SheetUrl, StringComparison.Ordinal))
                {
                    _scrubSheetUrl = frame.SheetUrl;
                    BitmapImage sheet;
                    if (!_scrubSheetCache.TryGetValue(frame.SheetUrl, out sheet))
                    {
                        // Decode at native size. The ImageBrush uses Stretch="None" and offsets by
                        // whole tiles, so the decoded pixels MUST match the sheet's real size
                        // (cols*tile x rows*tile) — forcing a decode width broke that mapping and
                        // left the preview blank. The sheet is only ~800x450, so this is cheap, and
                        // only sheets actually viewed are created here.
                        sheet = new BitmapImage(new Uri(frame.SheetUrl));
                        _scrubSheetCache[frame.SheetUrl] = sheet;
                    }

                    ScrubPreviewBrush.ImageSource = sheet;
                }

                // Offset (in scaled space) so the target tile lands at the top-left of the clip.
                ScrubPreviewImageTransform.X = -frame.Column * frame.ThumbWidth * scale;
                ScrubPreviewImageTransform.Y = -frame.Row * frame.ThumbHeight * scale;

                if (ScrubPreviewTime != null)
                {
                    ScrubPreviewTime.Text = FormatTimeSpan(TimeSpan.FromSeconds(seconds));
                }

                // Centre the panel over the finger, clamped to the player width. The seek bar sits
                // 16px in from the player edge, so the pointer's absolute X is that plus pointerX.
                var panelWidth = frame.ThumbWidth + 6;
                var absoluteX = 16 + pointerX;
                var playerWidth = PlayerGrid != null ? PlayerGrid.ActualWidth : ActualWidth;
                var left = absoluteX - panelWidth / 2.0;
                if (left < 4) left = 4;
                if (playerWidth > 0 && left > playerWidth - panelWidth - 4) left = playerWidth - panelWidth - 4;
                ScrubPreviewTransform.X = left;

                ScrubPreviewPanel.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Player] Scrub preview failed: " + ex.Message);
                HideScrubPreview();
            }
        }

        private void HideScrubPreview()
        {
            if (ScrubPreviewPanel != null)
            {
                ScrubPreviewPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void CompleteProgressPointerSeek(UIElement element, Windows.UI.Xaml.Input.Pointer pointer)
        {
            if (!_isProgressPointerCaptured)
            {
                return;
            }

            _isProgressPointerCaptured = false;
            HideScrubPreview();
            SeekToSliderValue();
            _isUserDragging = false;

            if (element != null && pointer != null)
            {
                element.ReleasePointerCapture(pointer);
            }
        }

        private void SeekToSliderValue()
        {
            if (MediaPlayer.MediaPlayer == null || ProgressSlider == null)
            {
                return;
            }

            // Applied immediately: this is the RELEASE of a slider drag / a tap, so the target is
            // already final. The "wait until the user stops scrubbing" the deferral was meant for
            // is achieved by only seeking on release (we never seek per drag-delta). Deferring
            // here as well left the bar sitting at the target for 500ms and then, on a demuxed
            // source, snapping back — which read as the slider jumping to its old position.
            var newPosition = TimeSpan.FromSeconds(ProgressSlider.Value);
            System.Diagnostics.Debug.WriteLine("[Player] Slider seek applied to " + newPosition);

            if (_seekDebounceTimer != null)
            {
                _seekDebounceTimer.Stop();
            }
            _pendingSeekTarget = null;

            ApplySeekToPosition(ClampPositionToDuration(newPosition));
        }

        private void UpdateTimer_Tick(object sender, object e)
        {
            System.Diagnostics.Debug.WriteLine("[UpdateTimer] Tick called");

            // Measures the speed actually achieved: how much media time passes per second of real
            // time. "session reports 2" only says the property was accepted — this says whether
            // the clock really runs at 2x, which is what separates a rendering problem from the
            // pipeline never applying the rate at all.
            try
            {
                var probeSession = MediaPlayer != null && MediaPlayer.MediaPlayer != null
                    ? MediaPlayer.MediaPlayer.PlaybackSession : null;
                if (probeSession != null && Math.Abs(probeSession.PlaybackRate - 1.0) > 0.01 && _isPlaying)
                {
                    var mediaNow = probeSession.Position;
                    var wallNow = DateTime.UtcNow;

                    if (_rateProbeWall != DateTime.MinValue)
                    {
                        var mediaAdvanced = (mediaNow - _rateProbeMedia).TotalSeconds;
                        var wallElapsed = (wallNow - _rateProbeWall).TotalSeconds;
                        if (wallElapsed > 0.05)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[Player][Rate] requested=" + probeSession.PlaybackRate
                                + " media advanced " + mediaAdvanced.ToString("F2")
                                + "s in " + wallElapsed.ToString("F2")
                                + "s => effective " + (mediaAdvanced / wallElapsed).ToString("F2") + "x");
                        }
                    }

                    _rateProbeMedia = mediaNow;
                    _rateProbeWall = wallNow;
                }
                else
                {
                    _rateProbeWall = DateTime.MinValue;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Player][Rate] probe failed: " + ex.Message);
            }
            
            // A pending seek owns the slider until it is applied; letting the timer write the
            // still-old playback position back would make the bar jump backwards under the finger.
            if (MediaPlayer.MediaPlayer != null && !_isUserDragging && _pendingSeekTarget == null)
            {
                System.Diagnostics.Debug.WriteLine("[UpdateTimer] MediaPlayer and not dragging");
                
                // Use parsed duration from API if available, otherwise use MediaPlayer's NaturalDuration
                TimeSpan duration;
                if (_parsedDuration > TimeSpan.Zero)
                {
                    duration = _parsedDuration;
                    System.Diagnostics.Debug.WriteLine($"[UpdateTimer] Using parsed duration: {duration.TotalSeconds:F2}s");
                }
                else
                {
                    duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                    System.Diagnostics.Debug.WriteLine($"[UpdateTimer] Using MediaPlayer duration: {duration.TotalSeconds:F2}s");
                }
                
                var position = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                // Separate audio sync is handled by _audioSyncTimer. Calling it here too
                // causes extra seeks on Windows 10 Mobile and can make the audio stutter.
                System.Diagnostics.Debug.WriteLine($"[UpdateTimer] Position: {position.TotalSeconds:F2}s");
                
                // Always update debug output
                System.Diagnostics.Debug.WriteLine($"[UpdateTimer] Position: {position.TotalSeconds:F2}s, Duration: {duration.TotalSeconds:F2}s");
                
                if (duration.TotalSeconds > 0)
                {
                    System.Diagnostics.Debug.WriteLine("[UpdateTimer] Duration > 0");
                    
                    // Update slider maximum to match video duration in seconds
                    ProgressSlider.Maximum = duration.TotalSeconds;
                    EnsureSegmentedBarMatchesDuration();
                    
                    // Update slider value to current position
                    ProgressSlider.Value = position.TotalSeconds;
                    
                    // Check if video has ended using multiple methods
                    bool isNearEnd = position >= duration - TimeSpan.FromMilliseconds(100);

                    // A buffering stream sits at the same position while _isPlaying stays true,
                    // which looks identical to a wedged pipeline. Counting it as "stuck" made a
                    // long rebuffer end the video outright: replay button, and a playlist moved
                    // to the next item. Only a stall while the session claims to be PLAYING can
                    // be the wedge this safety net is for.
                    var playbackState = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;
                    bool isBuffering = playbackState == Windows.Media.Playback.MediaPlaybackState.Buffering
                        || playbackState == Windows.Media.Playback.MediaPlaybackState.Opening;
                    bool isPositionStuck = (position == _lastPosition) && _isPlaying && !isBuffering;

                    // Playback running far slower than real time (the timer ticks once a second,
                    // so a healthy stream advances ~1s per tick). A few tenths per tick means the
                    // decoder or the network cannot sustain this format — on Windows 10 Mobile
                    // that state degrades into a few frames per second and then kills the app,
                    // so ask the page to step the quality down before that happens.
                    if (_videoLoaded && _isPlaying && !isNearEnd && position.TotalSeconds > 2.0)
                    {
                        var advanced = (position - _lastPosition).TotalSeconds;
                        if (advanced > 0 && advanced < 0.45)
                        {
                            _slowPlaybackCounter++;
                            System.Diagnostics.Debug.WriteLine("[UpdateTimer] Slow playback counter: " + _slowPlaybackCounter + " (advanced " + advanced.ToString("F2") + "s)");
                        }
                        else if (advanced >= 0.45)
                        {
                            _slowPlaybackCounter = 0;
                        }

                        if (_slowPlaybackCounter >= 6)
                        {
                            _slowPlaybackCounter = 0;
                            System.Diagnostics.Debug.WriteLine("[UpdateTimer] Playback cannot keep up; requesting a lower quality");
                            try
                            {
                                var stallHandler = PlaybackStalling;
                                if (stallHandler != null)
                                {
                                    stallHandler(this, null);
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Debug.WriteLine("[UpdateTimer] PlaybackStalling subscriber threw - " + ex.Message);
                            }
                        }
                    }
                    
                    System.Diagnostics.Debug.WriteLine($"[UpdateTimer] isNearEnd: {isNearEnd}, isPositionStuck: {isPositionStuck}, Position: {position.TotalSeconds:F2}s, Duration: {duration.TotalSeconds:F2}s");
                    
                    // Only check for video end if:
                    // 1. Video is actually loaded (_videoLoaded is true)
                    // 2. We've played some portion of the video (position > 1 second)
                    // 3. We're near the end OR position is stuck for 3 consecutive checks
                    if (_videoLoaded && position.TotalSeconds > 1.0)
                    {
                        // If position is stuck, increment counter
                        if (isPositionStuck)
                        {
                            _positionStuckCounter++;
                            System.Diagnostics.Debug.WriteLine($"[UpdateTimer] Position stuck counter: {_positionStuckCounter}");
                        }
                        else
                        {
                            _positionStuckCounter = 0;
                        }
                        
                        // Near the real end, or wedged (never while buffering — see above).
                        if (isNearEnd || (!isBuffering && _positionStuckCounter >= 10 && position.TotalSeconds > 5))
                        {
                            System.Diagnostics.Debug.WriteLine("[UpdateTimer] Video end detected, calling HandleVideoEnd()");
                            // Reset counter
                            _positionStuckCounter = 0;
                            // Video has ended, show replay button
                            HandleVideoEnd();
                        }
                    }
                    
                    // Store current position for next check
                    _lastPosition = position;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[UpdateTimer] Duration is zero or negative");
                }
                
                // Update time display
                UpdateTimeDisplay();
            }
            else
            {
                if (MediaPlayer.MediaPlayer == null)
                {
                    System.Diagnostics.Debug.WriteLine("[UpdateTimer] MediaPlayer is null");
                }
                if (_isUserDragging)
                {
                    System.Diagnostics.Debug.WriteLine("[UpdateTimer] User is dragging");
                }
            }
        }

        private void HandleVideoEnd()
        {
            System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] Method called");
            
            // Pause the video
            if (MediaPlayer.MediaPlayer != null)
            {
                System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] Pausing MediaPlayer");
                _isPlaying = false;
                UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Stopped);
                MediaPlayer.MediaPlayer.Pause();
                PauseSeparateAudio();
                if (_playPauseIcon != null)
                {
                    _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
                }
                _updateTimer.Stop();
                
                // Show replay button instead of play/pause button
                System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] Showing replay button");
                PlayPauseButton.Visibility = Visibility.Collapsed;
                PlayPauseButton.Opacity = 0;
                PlayPauseButton.IsHitTestVisible = false;
                ReplayButton.Visibility = Visibility.Visible;
                ReplayButton.IsHitTestVisible = true;
                AnimateOpacity(ReplayButton, 0, 0.8, 0.5);
                
                // Stop auto-hide timer since video ended
                if (_autoHideTimer != null)
                {
                    _autoHideTimer.Stop();
                }
                
                // Ensure controls are visible
                if (!_controlsVisible)
                {
                    System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] Fading in controls");
                    FadeInControls();
                }
                
                // Make sure controls stay visible when video ends
                _controlsVisible = true;
                
                System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] Method completed");

                // Let the page auto-advance to the next item of a playlist / mix, if any.
                try
                {
                    var endedHandler = VideoEnded;
                    if (endedHandler != null)
                    {
                        endedHandler(this, null);
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] VideoEnded subscriber threw - " + ex.Message);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[HandleVideoEnd] MediaPlayer is null");
            }
        }

        // Update the time display text
        // While a seek is pending the pipeline is still at the old position, so the label has to
        // be told which position to show instead of reading it back.
        private void UpdateTimeDisplayForPosition(TimeSpan position)
        {
            try
            {
                if (TimeDisplayText == null)
                {
                    return;
                }

                var duration = _parsedDuration > TimeSpan.Zero
                    ? _parsedDuration
                    : (MediaPlayer != null && MediaPlayer.MediaPlayer != null
                        ? MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration
                        : TimeSpan.Zero);

                TimeDisplayText.Text = BuildTimeDisplayText(position, duration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Player] Time display update failed: " + ex.Message);
            }
        }

        private void UpdateTimeDisplay()
        {
            try
            {
                if (MediaPlayer == null || MediaPlayer.MediaPlayer == null || TimeDisplayText == null)
                {
                    return;
                }

                var position = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                TimeSpan duration;
                
                // Use parsed duration from API if available, otherwise use MediaPlayer's NaturalDuration
                if (_parsedDuration > TimeSpan.Zero)
                {
                    duration = _parsedDuration;
                    System.Diagnostics.Debug.WriteLine($"Using parsed duration: {duration.TotalSeconds} seconds");
                }
                else
                {
                    duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                    System.Diagnostics.Debug.WriteLine($"Using MediaPlayer duration: {duration.TotalSeconds} seconds");
                }
                
                TimeDisplayText.Text = BuildTimeDisplayText(position, duration);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("UpdateTimeDisplay skipped during player state change: " + ex.Message);
            }
        }

        // Format TimeSpan to mm:ss or hh:mm:ss
        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}:{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
            else
            {
                return $"{timeSpan.Minutes:D2}:{timeSpan.Seconds:D2}";
            }
        }

        // Title of the segment the given position falls into, or null when the video has no
        // chapters. The markers are kept in the order they were parsed, so the match is the last
        // one that starts at or before the position.
        private string GetChapterTitleAt(TimeSpan position)
        {
            if (_chapterMarkers.Count == 0)
            {
                return null;
            }

            string title = null;
            var best = TimeSpan.MinValue;

            foreach (var marker in _chapterMarkers)
            {
                if (marker == null || string.IsNullOrWhiteSpace(marker.Title))
                {
                    continue;
                }

                if (marker.Position <= position && marker.Position >= best)
                {
                    best = marker.Position;
                    title = marker.Title;
                }
            }

            return title;
        }

        // "12:34 / 45:67 · Chapter title" — the segment name is appended only when there is one.
        private string BuildTimeDisplayText(TimeSpan position, TimeSpan duration)
        {
            var text = FormatTimeSpan(position) + " / " + FormatTimeSpan(duration);
            var chapter = GetChapterTitleAt(position);

            if (!string.IsNullOrWhiteSpace(chapter))
            {
                text += " · " + chapter.Trim();
            }

            return text;
        }

        // --- Subtitles ------------------------------------------------------------------------

        private readonly List<Subtitles.SubtitleCue> _subtitleCues = new List<Subtitles.SubtitleCue>();
        private DispatcherTimer _subtitleTimer;
        private string _shownCueText;

        // How far ahead of the reported position cues are looked up.
        //
        // Polling accounts for at most ~100ms of this. The rest compensates for the position the
        // pipeline reports running behind the picture on demuxed sources — measured on the device
        // at roughly a second, and it varies by video, which is why this is adjustable and
        // remembered rather than a fixed constant.
        private const string SubtitleOffsetSettingKey = "SubtitleOffsetMs";
        private const double DefaultSubtitleOffsetMs = 1000;
        private const double SubtitleOffsetStepMs = 250;
        private static double? _subtitleOffsetMs;

        public static TimeSpan SubtitleLeadOffset
        {
            get
            {
                if (_subtitleOffsetMs == null)
                {
                    _subtitleOffsetMs = DefaultSubtitleOffsetMs;
                    try
                    {
                        var stored = ApplicationData.Current.LocalSettings.Values[SubtitleOffsetSettingKey];
                        if (stored is double)
                        {
                            _subtitleOffsetMs = (double)stored;
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Subtitles] Offset load failed: " + ex.Message);
                    }
                }

                return TimeSpan.FromMilliseconds(_subtitleOffsetMs.Value);
            }
        }

        // Positive delta makes subtitles appear earlier.
        public void AdjustSubtitleOffset(double deltaMs)
        {
            var value = SubtitleLeadOffset.TotalMilliseconds + deltaMs;

            // Beyond a few seconds either way the setting is no longer correcting anything.
            value = Math.Max(-5000, Math.Min(5000, value));
            _subtitleOffsetMs = value;

            try
            {
                ApplicationData.Current.LocalSettings.Values[SubtitleOffsetSettingKey] = value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subtitles] Offset save failed: " + ex.Message);
            }

            // Re-evaluate on the next tick instead of waiting for the cue to change.
            _shownCueText = null;
            System.Diagnostics.Debug.WriteLine("[Subtitles] Offset is now " + value + "ms");
        }

        public static string SubtitleOffsetDisplayText
        {
            get
            {
                var seconds = SubtitleLeadOffset.TotalSeconds;
                return (seconds >= 0 ? "+" : "") + seconds.ToString("0.00") + "s";
            }
        }

        // The player owns the track list and the current selection, so the page's bottom sheet and
        // the fullscreen settings panel drive the same state instead of each keeping their own.
        private Subtitles.TrackList _subtitleTracks = new Subtitles.TrackList();
        private Subtitles.SubtitleTrack _activeSubtitleTrack;

        public Subtitles.TrackList SubtitleTracks { get { return _subtitleTracks; } }

        public Subtitles.SubtitleTrack ActiveSubtitleTrack { get { return _activeSubtitleTrack; } }

        public void SetSubtitleTracks(Subtitles.TrackList tracks)
        {
            _subtitleTracks = tracks ?? new Subtitles.TrackList();
            _activeSubtitleTrack = null;
            SetSubtitleCues(null);
        }

        // Downloads the chosen track and shows it. Passing null turns subtitles off.
        public async void SelectSubtitleTrack(Subtitles.SubtitleTrack track)
        {
            _activeSubtitleTrack = track;

            if (track == null)
            {
                SetSubtitleCues(null);
                return;
            }

            try
            {
                var cues = await Subtitles.GetCuesAsync(track);

                // The selection may have changed again while this was downloading.
                if (!ReferenceEquals(_activeSubtitleTrack, track))
                {
                    return;
                }

                SetSubtitleCues(cues);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subtitles] Selection failed: " + ex.Message);
            }
        }

        // Builds the "translate track X into Y" entry the menus offer.
        public static Subtitles.SubtitleTrack MakeTranslatedTrack(
            Subtitles.SubtitleTrack source, Subtitles.TranslationLanguage target)
        {
            if (source == null || target == null)
            {
                return null;
            }

            return new Subtitles.SubtitleTrack
            {
                BaseUrl = source.BaseUrl,
                LanguageCode = source.LanguageCode,
                Name = source.Name,
                IsAutoGenerated = source.IsAutoGenerated,
                IsTranslatable = source.IsTranslatable,
                TranslationLanguageCode = target.LanguageCode,
                TranslationLanguageName = target.Name
            };
        }

        private bool _fullscreenShowingTranslations;

        // Fullscreen twin of the page's subtitle list: the author's tracks first, with the long
        // machine-translation language list behind its own entry.
        private void PopulateFullscreenSubtitleOptions(StackPanel panel)
        {
            if (panel == null)
            {
                return;
            }

            panel.Children.Clear();

            if (_fullscreenShowingTranslations)
            {
                var source = _subtitleTracks.TranslationSource;

                AddFullscreenSubtitleOption(panel, "< Back", false, () =>
                {
                    _fullscreenShowingTranslations = false;
                    PopulateFullscreenSubtitleOptions(panel);
                });

                foreach (var language in _subtitleTracks.TranslationLanguages)
                {
                    var target = language;
                    var isActive = _activeSubtitleTrack != null
                        && string.Equals(_activeSubtitleTrack.TranslationLanguageCode, target.LanguageCode, StringComparison.Ordinal);

                    AddFullscreenSubtitleOption(panel, target.Name, isActive, () =>
                    {
                        SelectSubtitleTrack(MakeTranslatedTrack(source, target));
                        _isSettingsPanelOpen = false;
                        AnimateFullscreenSettingsPanel(false);
                    });
                }

                return;
            }

            // Sync adjustment, offered only while a track is on — with subtitles off it means
            // nothing.
            if (_activeSubtitleTrack != null)
            {
                AddFullscreenSubtitleOption(panel, "Sync: " + SubtitleOffsetDisplayText, false, null);
                AddFullscreenSubtitleOption(panel, "   Earlier (+0.25s)", false, () =>
                {
                    AdjustSubtitleOffset(SubtitleOffsetStepMs);
                    PopulateFullscreenSubtitleOptions(panel);
                });
                AddFullscreenSubtitleOption(panel, "   Later (-0.25s)", false, () =>
                {
                    AdjustSubtitleOffset(-SubtitleOffsetStepMs);
                    PopulateFullscreenSubtitleOptions(panel);
                });
            }

            AddFullscreenSubtitleOption(panel, "Off", _activeSubtitleTrack == null, () =>
            {
                SelectSubtitleTrack(null);
                _isSettingsPanelOpen = false;
                AnimateFullscreenSettingsPanel(false);
            });

            foreach (var track in _subtitleTracks.Tracks)
            {
                var selected = track;
                var isActive = _activeSubtitleTrack != null
                    && string.IsNullOrEmpty(_activeSubtitleTrack.TranslationLanguageCode)
                    && string.Equals(_activeSubtitleTrack.BaseUrl, selected.BaseUrl, StringComparison.Ordinal);

                AddFullscreenSubtitleOption(panel, selected.DisplayName, isActive, () =>
                {
                    SelectSubtitleTrack(selected);
                    _isSettingsPanelOpen = false;
                    AnimateFullscreenSettingsPanel(false);
                });
            }

            if (_subtitleTracks.CanTranslate)
            {
                AddFullscreenSubtitleOption(panel, "Auto-translate >", false, () =>
                {
                    _fullscreenShowingTranslations = true;
                    PopulateFullscreenSubtitleOptions(panel);
                });
            }
        }

        private void AddFullscreenSubtitleOption(StackPanel panel, string label, bool isActive, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                Background = new SolidColorBrush(Colors.Transparent),
                Foreground = new SolidColorBrush(isActive
                    ? Color.FromArgb(255, 255, 0, 51)
                    : Colors.White),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 8, 16, 8),
                Height = 48,
                Margin = new Thickness(0, 0, 0, 4)
            };

            if (onClick == null)
            {
                // A plain caption row, not a choice.
                button.IsHitTestVisible = false;
                button.Opacity = 0.7;
            }
            else
            {
                button.Click += (s, e) => onClick();
            }

            panel.Children.Add(button);
        }

        // Replaces the displayed track. Pass null or an empty list to turn subtitles off.
        public void SetSubtitleCues(IEnumerable<Subtitles.SubtitleCue> cues)
        {
            _subtitleCues.Clear();
            _shownCueText = null;

            if (cues != null)
            {
                foreach (var cue in cues)
                {
                    if (cue != null && !string.IsNullOrEmpty(cue.Text))
                    {
                        _subtitleCues.Add(cue);
                    }
                }
            }

            if (SubtitleText != null)
            {
                SubtitleText.Text = string.Empty;
            }
            if (SubtitleOverlay != null)
            {
                SubtitleOverlay.Visibility = Visibility.Collapsed;
            }

            if (_subtitleCues.Count == 0)
            {
                if (_subtitleTimer != null)
                {
                    _subtitleTimer.Stop();
                }
                return;
            }

            if (_subtitleTimer == null)
            {
                _subtitleTimer = new DispatcherTimer();
                _subtitleTimer.Interval = TimeSpan.FromMilliseconds(100);
                _subtitleTimer.Tick += SubtitleTimer_Tick;
            }

            _subtitleTimer.Start();
        }

        private void SubtitleTimer_Tick(object sender, object e)
        {
            try
            {
                if (SubtitleOverlay == null || SubtitleText == null)
                {
                    return;
                }

                var session = MediaPlayer != null && MediaPlayer.MediaPlayer != null
                    ? MediaPlayer.MediaPlayer.PlaybackSession : null;
                if (session == null)
                {
                    return;
                }

                // While a seek is pending the pipeline still reports the old position, so follow
                // the target instead — the caption then matches what the user is scrubbing to.
                var position = _pendingSeekTarget ?? session.Position;

                // A cue can only ever be found at or after its own timestamp, and the poll adds up
                // to another interval on top, so without compensation subtitles always run late.
                // Looking slightly ahead puts them a touch early instead, which is how captions
                // are meant to read.
                position += SubtitleLeadOffset;

                string text = null;
                foreach (var cue in _subtitleCues)
                {
                    if (cue.Start > position)
                    {
                        break; // sorted by start: nothing later can match
                    }

                    if (position < cue.End)
                    {
                        text = cue.Text;
                        break;
                    }
                }

                if (text == _shownCueText)
                {
                    return;
                }

                _shownCueText = text;
                SubtitleText.Text = text ?? string.Empty;

                // No room for captions in the mini-player.
                SubtitleOverlay.Visibility = (string.IsNullOrEmpty(text) || _isMiniMode)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subtitles] Tick failed: " + ex.Message);
            }
        }

        // The caption sits above the seek bar in the normal player; in fullscreen there is more
        // room, and in the mini-player there is none at all.
        private void UpdateSubtitleOverlayMetrics()
        {
            if (SubtitleOverlay == null)
            {
                return;
            }

            if (_isMiniMode)
            {
                SubtitleOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            SubtitleOverlay.Margin = _isFullscreen
                ? new Thickness(32, 0, 32, 80)
                : new Thickness(16, 0, 16, 56);
            SubtitleText.FontSize = _isFullscreen ? 20 : 16;
        }

        // --- SponsorBlock ---------------------------------------------------------------------

        private readonly List<SponsorBlock.Segment> _skipSegments = new List<SponsorBlock.Segment>();
        private DispatcherTimer _skipCheckTimer;
        private TimeSpan _lastSkippedTo = TimeSpan.MinValue;

        public void SetSkipSegments(IEnumerable<SponsorBlock.Segment> segments)
        {
            _skipSegments.Clear();
            _lastSkippedTo = TimeSpan.MinValue;

            if (segments != null)
            {
                foreach (var segment in segments)
                {
                    if (segment != null && segment.End > segment.Start)
                    {
                        _skipSegments.Add(segment);
                    }
                }
            }

            RebuildSponsorOverlay();

            if (_skipSegments.Count == 0)
            {
                if (_skipCheckTimer != null)
                {
                    _skipCheckTimer.Stop();
                }
                return;
            }

            // Checked more often than the 1s UI tick so a skip never lets a full second of the
            // sponsor read through.
            if (_skipCheckTimer == null)
            {
                _skipCheckTimer = new DispatcherTimer();
                _skipCheckTimer.Interval = TimeSpan.FromMilliseconds(250);
                _skipCheckTimer.Tick += SkipCheckTimer_Tick;
            }

            _skipCheckTimer.Start();
        }

        // The colours SponsorBlock itself uses, so the bar reads the same as in other clients.
        private static Color GetSponsorCategoryColor(string category)
        {
            switch (category)
            {
                case "selfpromo": return Color.FromArgb(255, 255, 255, 0);   // yellow
                case "interaction": return Color.FromArgb(255, 204, 0, 255); // magenta
                case "intro": return Color.FromArgb(255, 0, 255, 255);
                case "outro": return Color.FromArgb(255, 2, 2, 237);
                case "preview": return Color.FromArgb(255, 0, 143, 214);
                case "music_offtopic": return Color.FromArgb(255, 255, 153, 0);
                default: return Color.FromArgb(255, 0, 212, 0);              // sponsor: green
            }
        }

        // Paints each skippable range onto the overlay canvas. A range is clipped against every
        // chapter block it touches rather than mapped straight onto the bar's width, because the
        // timeline is broken up by chapter gaps that carry no time.
        private void RebuildSponsorOverlay()
        {
            try
            {
                if (SponsorOverlay == null)
                {
                    return;
                }

                SponsorOverlay.Children.Clear();

                if (_skipSegments.Count == 0 || _timelineSegmentVisuals.Count == 0)
                {
                    return;
                }

                foreach (var segment in _skipSegments)
                {
                    var brush = new SolidColorBrush(GetSponsorCategoryColor(segment.Category));

                    foreach (var visual in _timelineSegmentVisuals)
                    {
                        var span = visual.EndSeconds - visual.StartSeconds;
                        if (span <= 0 || visual.Container == null || visual.Container.ActualWidth <= 0)
                        {
                            continue;
                        }

                        var from = Math.Max(segment.Start.TotalSeconds, visual.StartSeconds);
                        var to = Math.Min(segment.End.TotalSeconds, visual.EndSeconds);
                        if (to <= from)
                        {
                            continue;
                        }

                        var blockLeft = visual.Container
                            .TransformToVisual(SponsorOverlay)
                            .TransformPoint(new Point(0, 0)).X;

                        var x = blockLeft + (from - visual.StartSeconds) / span * visual.Container.ActualWidth;
                        var width = (to - from) / span * visual.Container.ActualWidth;

                        // A very short sponsor read would otherwise be invisible.
                        if (width < 2)
                        {
                            width = 2;
                        }

                        var bar = new Rectangle
                        {
                            Height = TimelineBarHeight,
                            Width = width,
                            Fill = brush,
                            RadiusX = 2,
                            RadiusY = 2
                        };

                        Canvas.SetLeft(bar, x);
                        Canvas.SetTop(bar, 0);
                        SponsorOverlay.Children.Add(bar);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SponsorBlock] Overlay rebuild failed: " + ex.Message);
            }
        }

        private void SkipCheckTimer_Tick(object sender, object e)
        {
            try
            {
                // A drag or a deferred seek owns the position; jumping out from under it would
                // fight the user.
                if (_isUserDragging || _pendingSeekTarget != null || !_isPlaying)
                {
                    return;
                }

                var session = MediaPlayer != null && MediaPlayer.MediaPlayer != null
                    ? MediaPlayer.MediaPlayer.PlaybackSession : null;
                if (session == null)
                {
                    return;
                }

                var position = session.Position;

                foreach (var segment in _skipSegments)
                {
                    if (position < segment.Start || position >= segment.End)
                    {
                        continue;
                    }

                    // Landing inside the same segment again right after skipping it means the seek
                    // has not taken effect yet — don't issue a second one.
                    if (_lastSkippedTo == segment.End)
                    {
                        return;
                    }

                    _lastSkippedTo = segment.End;
                    System.Diagnostics.Debug.WriteLine(
                        "[SponsorBlock] Skipping " + segment.Category + " "
                        + segment.Start.TotalSeconds.ToString("F1") + "s-"
                        + segment.End.TotalSeconds.ToString("F1") + "s");

                    // Applied straight to the pipeline rather than through RequestSeek: an
                    // automatic skip should not pop the controls open or wait out the 500ms
                    // gesture debounce. The duplicate-seek guard inside also keeps a skip from
                    // being issued twice for the same segment.
                    ApplySeekToPosition(segment.End);
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SponsorBlock] Skip check failed: " + ex.Message);
            }
        }

        public void SetChapters(IEnumerable<VideoChapterMarker> chapters)
        {
            _chapterMarkers.Clear();

            if (chapters != null)
            {
                foreach (var chapter in chapters)
                {
                    if (chapter == null || chapter.Position < TimeSpan.Zero)
                    {
                        continue;
                    }

                    _chapterMarkers.Add(new VideoChapterMarker
                    {
                        Position = chapter.Position,
                        Title = chapter.Title ?? string.Empty
                    });
                }
            }

            RebuildSegmentedProgressBar();

            // The label may already be showing a position that now belongs to a named segment.
            UpdateTimeDisplay();
        }

        private void SegmentedProgressBar_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSegmentMetrics();
            UpdateSegmentedProgressValue();
            RebuildSponsorOverlay();
        }

        private List<TimelineSegmentRange> BuildTimelineSegmentRanges()
        {
            var ranges = new List<TimelineSegmentRange>();
            double minimum = ProgressSlider != null ? ProgressSlider.Minimum : 0;
            double maximum = 0;

            var duration = GetEffectiveDuration();
            if (duration > TimeSpan.Zero)
            {
                maximum = duration.TotalSeconds;
            }
            else if (ProgressSlider != null)
            {
                maximum = ProgressSlider.Maximum;
            }

            if (maximum <= minimum)
            {
                maximum = minimum + 100;
            }

            var breaks = new List<double>();
            foreach (var chapter in _chapterMarkers)
            {
                if (chapter == null)
                {
                    continue;
                }

                double seconds = chapter.Position.TotalSeconds;
                if (double.IsNaN(seconds) || double.IsInfinity(seconds))
                {
                    continue;
                }

                if (seconds > minimum + TimelineMinSegmentSeconds && seconds < maximum - TimelineMinSegmentSeconds)
                {
                    breaks.Add(seconds);
                }
            }

            breaks.Sort();

            double start = minimum;
            double previousBreak = double.NaN;
            foreach (var rawBreak in breaks)
            {
                if (!double.IsNaN(previousBreak) && Math.Abs(rawBreak - previousBreak) < TimelineMinSegmentSeconds)
                {
                    continue;
                }

                if (rawBreak - start > TimelineMinSegmentSeconds)
                {
                    ranges.Add(new TimelineSegmentRange
                    {
                        StartSeconds = start,
                        EndSeconds = rawBreak
                    });
                }

                start = rawBreak;
                previousBreak = rawBreak;
            }

            if (maximum - start > TimelineMinSegmentSeconds)
            {
                ranges.Add(new TimelineSegmentRange
                {
                    StartSeconds = start,
                    EndSeconds = maximum
                });
            }

            if (ranges.Count == 0)
            {
                ranges.Add(new TimelineSegmentRange
                {
                    StartSeconds = minimum,
                    EndSeconds = maximum
                });
            }

            return ranges;
        }

        private void RebuildSegmentedProgressBar()
        {
            try
            {
                if (SegmentedProgressBar == null || ProgressSlider == null)
                {
                    return;
                }

                _isRebuildingTimelineVisual = true;
                SegmentedProgressBar.Children.Clear();
                SegmentedProgressBar.ColumnDefinitions.Clear();
                _timelineSegmentVisuals.Clear();

                var ranges = BuildTimelineSegmentRanges();
                int columnIndex = 0;

                for (int i = 0; i < ranges.Count; i++)
                {
                    if (i > 0)
                    {
                        // This column is a real empty space in the timeline.
                        // Nothing is drawn here, so the slider is genuinely broken into chapters.
                        SegmentedProgressBar.ColumnDefinitions.Add(new ColumnDefinition
                        {
                            Width = new GridLength(TimelineGapWidth, GridUnitType.Pixel)
                        });
                        columnIndex++;
                    }

                    var range = ranges[i];
                    double segmentWeight = Math.Max(TimelineMinSegmentSeconds, range.EndSeconds - range.StartSeconds);
                    SegmentedProgressBar.ColumnDefinitions.Add(new ColumnDefinition
                    {
                        Width = new GridLength(segmentWeight, GridUnitType.Star)
                    });

                    var segmentGrid = new Grid
                    {
                        Height = TimelineBarHeight,
                        VerticalAlignment = VerticalAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Background = new SolidColorBrush(Colors.Transparent)
                    };

                    var background = new Rectangle
                    {
                        Height = TimelineBarHeight,
                        Fill = _timelineBackgroundBrush,
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        VerticalAlignment = VerticalAlignment.Center,
                        RadiusX = 2,
                        RadiusY = 2
                    };

                    var fill = new Rectangle
                    {
                        Height = TimelineBarHeight,
                        Fill = _timelineForegroundBrush,
                        HorizontalAlignment = HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        RadiusX = 2,
                        RadiusY = 2,
                        Width = 0
                    };

                    segmentGrid.Children.Add(background);
                    segmentGrid.Children.Add(fill);
                    segmentGrid.SizeChanged += TimelineSegment_SizeChanged;
                    Grid.SetColumn(segmentGrid, columnIndex);
                    SegmentedProgressBar.Children.Add(segmentGrid);

                    _timelineSegmentVisuals.Add(new TimelineSegmentVisual
                    {
                        StartSeconds = range.StartSeconds,
                        EndSeconds = range.EndSeconds,
                        Container = segmentGrid,
                        Fill = fill
                    });

                    columnIndex++;
                }

                _lastRenderedTimelineMaximum = ProgressSlider.Maximum;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: failed to rebuild segmented timeline - " + ex.Message);
            }
            finally
            {
                _isRebuildingTimelineVisual = false;
                UpdateSegmentMetrics();
                UpdateSegmentedProgressValue();
                RebuildSponsorOverlay();
            }
        }

        private void TimelineSegment_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateSegmentMetrics();
            UpdateSegmentedProgressValue();

            // The chapter blocks have just been given their real widths — the overlay positions
            // are derived from those, so they have to be recomputed here.
            RebuildSponsorOverlay();
        }

        private void UpdateSegmentMetrics()
        {
            if (ProgressSliderHitArea == null)
            {
                return;
            }

            foreach (var segment in _timelineSegmentVisuals)
            {
                if (segment == null || segment.Container == null)
                {
                    continue;
                }

                try
                {
                    var transform = segment.Container.TransformToVisual(ProgressSliderHitArea);
                    var point = transform.TransformPoint(new Point(0, 0));
                    segment.Left = point.X;
                    segment.Width = segment.Container.ActualWidth;
                }
                catch
                {
                    segment.Left = 0;
                    segment.Width = segment.Container.ActualWidth;
                }
            }
        }

        private void UpdateSegmentedProgressValue()
        {
            if (ProgressSlider == null || _timelineSegmentVisuals.Count == 0)
            {
                return;
            }

            double value = Math.Max(ProgressSlider.Minimum, Math.Min(ProgressSlider.Value, ProgressSlider.Maximum));

            foreach (var segment in _timelineSegmentVisuals)
            {
                if (segment == null || segment.Fill == null || segment.Container == null)
                {
                    continue;
                }

                double segmentDuration = segment.EndSeconds - segment.StartSeconds;
                if (segmentDuration <= 0 || segment.Container.ActualWidth <= 0)
                {
                    segment.Fill.Width = 0;
                    continue;
                }

                double fillRatio;
                if (value <= segment.StartSeconds)
                {
                    fillRatio = 0;
                }
                else if (value >= segment.EndSeconds)
                {
                    fillRatio = 1;
                }
                else
                {
                    fillRatio = (value - segment.StartSeconds) / segmentDuration;
                }

                if (fillRatio < 0)
                {
                    fillRatio = 0;
                }
                else if (fillRatio > 1)
                {
                    fillRatio = 1;
                }

                segment.Fill.Width = segment.Container.ActualWidth * fillRatio;
            }
        }

        private double GetTimelineValueFromX(double x)
        {
            if (ProgressSlider == null)
            {
                return 0;
            }

            UpdateSegmentMetrics();

            if (_timelineSegmentVisuals.Count == 0)
            {
                double width = ProgressSliderHitArea != null ? ProgressSliderHitArea.ActualWidth : ProgressSlider.ActualWidth;
                if (width <= 0)
                {
                    return ProgressSlider.Value;
                }

                double fallbackRatio = x / width;
                if (fallbackRatio < 0)
                {
                    fallbackRatio = 0;
                }
                else if (fallbackRatio > 1)
                {
                    fallbackRatio = 1;
                }

                return ProgressSlider.Minimum + ((ProgressSlider.Maximum - ProgressSlider.Minimum) * fallbackRatio);
            }

            for (int i = 0; i < _timelineSegmentVisuals.Count; i++)
            {
                var segment = _timelineSegmentVisuals[i];
                if (segment == null || segment.Width <= 0)
                {
                    continue;
                }

                double left = segment.Left;
                double right = segment.Left + segment.Width;

                if (x < left)
                {
                    if (i == 0)
                    {
                        return segment.StartSeconds;
                    }

                    var previous = _timelineSegmentVisuals[i - 1];
                    return previous != null ? previous.EndSeconds : segment.StartSeconds;
                }

                if (x <= right)
                {
                    double ratio = (x - left) / segment.Width;
                    if (ratio < 0)
                    {
                        ratio = 0;
                    }
                    else if (ratio > 1)
                    {
                        ratio = 1;
                    }

                    return segment.StartSeconds + ((segment.EndSeconds - segment.StartSeconds) * ratio);
                }
            }

            return _timelineSegmentVisuals[_timelineSegmentVisuals.Count - 1].EndSeconds;
        }

        private void EnsureSegmentedBarMatchesDuration()
        {
            if (ProgressSlider == null)
            {
                return;
            }

            if (Math.Abs(_lastRenderedTimelineMaximum - ProgressSlider.Maximum) > 0.25)
            {
                RebuildSegmentedProgressBar();
            }
        }

        public async Task SetSourceFromUriAsync(Uri uri)
        {
            await SetSourceFromUriInternalAsync(uri, null, true, null, true);
        }

        public async Task SetSourceFromUriAsync(Uri uri, bool autoPlay)
        {
            await SetSourceFromUriInternalAsync(uri, null, true, null, autoPlay);
        }

        // Try to play an adaptive manifest (YouTube's native DASH/HLS, or an app-generated
        // combined video+audio DASH manifest) as a single self-muxed source. Handles both
        // remote URLs and local (ms-appdata) manifests. Returns false — without touching the
        // current source — when the platform's AdaptiveMediaSource rejects the manifest, so
        // the caller can fall back.
        public async Task<bool> TrySetAdaptiveSourceFromUriAsync(Uri uri, bool autoPlay)
        {
            if (uri == null)
            {
                return false;
            }

            if (_resumePlaybackAfterQualityChange || _isPlaying || IsMediaPlaybackActuallyPlaying())
            {
                autoPlay = true;
            }

            try
            {
                AdaptiveMediaSourceCreationResult result;

                var absolute = uri.AbsoluteUri ?? string.Empty;
                var isLocal = uri.IsFile
                    || string.Equals(uri.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase);

                if (isLocal)
                {
                    // AdaptiveMediaSource.CreateFromUriAsync cannot fetch a local manifest;
                    // open the file and feed the bytes (absolute <BaseURL>s drive the remote
                    // range requests).
                    var manifestFile = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(uri);
                    var manifestStream = await manifestFile.OpenReadAsync();
                    var contentType = absolute.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0
                        ? "application/vnd.apple.mpegurl"
                        : "application/dash+xml";
                    result = await AdaptiveMediaSource.CreateFromStreamAsync(manifestStream, uri, contentType);
                }
                else
                {
                    result = await AdaptiveMediaSource.CreateFromUriAsync(uri);
                }

                if (result.Status == AdaptiveMediaSourceCreationStatus.Success && result.MediaSource != null)
                {
                    // Single self-muxed source: make sure any prior separate-audio state is gone.
                    ResetSeparateAudio();
                    _usingSeparateAudio = false;
                    _suppressAutoPlayUntilSeparateAudioReady = false;
                    _pendingAutoPlayAfterSeparateAudioReady = false;
                    _playImmediatelyAfterSeparateAudioOpened = false;
                    SetSource(MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource), autoPlay);
                    return true;
                }

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: adaptive source failed: " + result.Status);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: adaptive source exception: " + ex.Message);
            }

            return false;
        }

        // Play a MediaStreamSource produced by the on-the-fly DASH demuxer (self-muxed
        // video+audio). Single source — no separate audio player.
        public bool SetDemuxedSource(Windows.Media.Core.MediaStreamSource mediaStreamSource, bool autoPlay)
        {
            if (mediaStreamSource == null || _playbackReleased)
            {
                return false;
            }

            if (_resumePlaybackAfterQualityChange || _isPlaying || IsMediaPlaybackActuallyPlaying())
            {
                autoPlay = true;
            }

            try
            {
                ResetSeparateAudio();
                _usingSeparateAudio = false;
                _suppressAutoPlayUntilSeparateAudioReady = false;
                _pendingAutoPlayAfterSeparateAudioReady = false;
                _playImmediatelyAfterSeparateAudioOpened = false;

                // Assigned after SetSource, which clears the flag at entry for the muxed path.
                SetSource(MediaSource.CreateFromMediaStreamSource(mediaStreamSource), autoPlay);
                _demuxedSource = mediaStreamSource;

                // Faster-than-normal playback does not work on a demuxed source, so drop back to
                // 1x rather than leaving the player in a state the menu no longer offers.
                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null
                    && MediaPlayer.MediaPlayer.PlaybackSession.PlaybackRate > 1.0)
                {
                    System.Diagnostics.Debug.WriteLine("[Player] Demuxed source cannot play above 1x; resetting speed");
                    SetPlaybackRate(1.0);
                }

                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SetDemuxedSource failed - " + ex.Message);
                return false;
            }
        }

        public async Task SetSourceWithSeparateAudioVideoAsync(Uri videoUri, Uri audioVideoUri)
        {
            // Use the passed Android progressive video as audio when available.
            // If it fails upstream, keep the old HLS manifest audio fallback.
            await SetSourceFromUriInternalAsync(videoUri, audioVideoUri, true, _currentQuality, true);
        }

        public async Task SetSourceWithSeparateAudioVideoAsync(Uri videoUri, Uri audioVideoUri, string qualityTag)
        {
            // For manually selected HLS quality, pick the matching master-playlist
            // variant instead of always selecting the best stream.
            await SetSourceFromUriInternalAsync(videoUri, audioVideoUri, true, qualityTag, true);
        }

        public async Task SetSourceWithSeparateAudioVideoAsync(Uri videoUri, Uri audioVideoUri, string qualityTag, bool autoPlay)
        {
            await SetSourceFromUriInternalAsync(videoUri, audioVideoUri, true, qualityTag, autoPlay);
        }

        private async Task SetSourceFromUriInternalAsync(
            Uri videoUri,
            Uri separateAudioVideoUri,
            bool allowHlsManifestAudio,
            string requestedQualityTag,
            bool autoPlay
        )
        {
            if (videoUri == null)
            {
                return;
            }

            // Preserve playback intent before ResetSeparateAudio()/SetSource() clears _isPlaying.
            // This fixes quality switching when the caller passes autoPlay=false while the
            // previous stream is still actually playing/opening/buffering.
            if (_resumePlaybackAfterQualityChange || _isPlaying || IsMediaPlaybackActuallyPlaying())
            {
                autoPlay = true;
            }

            ResetSeparateAudio();
            _suppressAutoPlayUntilSeparateAudioReady = false;
            _pendingAutoPlayAfterSeparateAudioReady = false;
            _playImmediatelyAfterSeparateAudioOpened = false;

            var absolute = videoUri.AbsoluteUri ?? string.Empty;
            var isM3u8 = absolute.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isM3u8)
            {
                try
                {
                    var selection = await TryPickHlsVideoAndAudioAsync(videoUri, requestedQualityTag);
                    if (selection != null && selection.VideoPlaylist != null)
                    {
                        var audioUri = separateAudioVideoUri;
                        if (audioUri == null && allowHlsManifestAudio)
                        {
                            audioUri = selection.AudioPlaylist;
                        }

                        // SetSource() has autoplay logic. Suppress it until the separate
                        // audio source is also assigned and opened, otherwise video may start
                        // alone and the external audio joins late.
                        _suppressAutoPlayUntilSeparateAudioReady = audioUri != null;
                        if (audioUri != null)
                        {
                            // SetSource() is async void and can reach its Pause() branch before
                            // this method continues. Block that internal pause from the start of
                            // separate-audio loading, then force-play when the audio MediaOpened fires.
                            ArmSeparateAudioAutoplayPauseBlock();
                            _playImmediatelyAfterSeparateAudioOpened = true;
                            _pendingAutoPlayAfterSeparateAudioReady = true;
                        }
                        await SetVisibleSourceFromUriAsync(selection.VideoPlaylist, autoPlay);

                        if (audioUri != null)
                        {
                            // SetSource() calls ResetSeparateAudio() internally, so re-arm the gate here.
                            // Otherwise the visible-video continuation can fall into Pause() before audio opens.
                            _suppressAutoPlayUntilSeparateAudioReady = true;
                            // Compute this BEFORE the audio source is assigned, because on Windows 10 Mobile
                            // MediaOpened can fire while SetSeparateAudioSourceFromUriAsync() is still running.
                            // If we wait until after the await, SetSource() can fall back to Pause().
                            var shouldStartAfterAudio = autoPlay || _resumePlaybackAfterQualityChange || _pendingAutoPlayAfterSeparateAudioReady;
                            _playImmediatelyAfterSeparateAudioOpened = shouldStartAfterAudio;

                            await SetSeparateAudioSourceFromUriAsync(audioUri);
                            _suppressAutoPlayUntilSeparateAudioReady = false;

                            // If the visible player already wanted to autoplay, do it now
                            // through the A/V barrier. If this was a quality switch, resume from
                            // the saved timestamp and keep the UI as Pause after playback starts.
                            shouldStartAfterAudio = shouldStartAfterAudio || _pendingAutoPlayAfterSeparateAudioReady;
                            _pendingAutoPlayAfterSeparateAudioReady = false;

                            if (shouldStartAfterAudio)
                            {
                                var startPosition = _resumePlaybackAfterQualityChange
                                    ? _resumePositionAfterQualityChange
                                    : GetCurrentPlaybackPositionSafe();
                                // Keep _playImmediatelyAfterSeparateAudioOpened=true until
                                // AudioPlayer_MediaOpened fires. SetSeparateAudioSourceFromUriAsync()
                                // can return before that event on Windows 10 Mobile. Clearing it here
                                // makes the MediaOpened event do nothing and playback never starts.
                                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() requested after separate audio source assignment.");
                                StartSynchronizedSeparatePlayback(startPosition);
                            }
                            else
                            {
                                _playImmediatelyAfterSeparateAudioOpened = false;
                                ClearPendingQualityResume();
                                Pause();
                            }
                        }
                        else
                        {
                            _suppressAutoPlayUntilSeparateAudioReady = false;
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: AdaptiveMediaSource exception: " + ex.Message);
                }
            }

            _suppressAutoPlayUntilSeparateAudioReady = separateAudioVideoUri != null;
            if (separateAudioVideoUri != null)
            {
                ArmSeparateAudioAutoplayPauseBlock();
                _playImmediatelyAfterSeparateAudioOpened = true;
                _pendingAutoPlayAfterSeparateAudioReady = true;
            }
            await SetVisibleSourceFromUriAsync(videoUri, autoPlay);

            if (separateAudioVideoUri != null)
            {
                // SetSource() calls ResetSeparateAudio() internally, so re-arm the gate here too.
                _suppressAutoPlayUntilSeparateAudioReady = true;
                // Same rule as the HLS path: mark autoplay intent BEFORE the audio source is set.
                var shouldStartAfterAudio = autoPlay || _resumePlaybackAfterQualityChange || _pendingAutoPlayAfterSeparateAudioReady;
                _playImmediatelyAfterSeparateAudioOpened = shouldStartAfterAudio;

                await SetSeparateAudioSourceFromUriAsync(separateAudioVideoUri);
                _suppressAutoPlayUntilSeparateAudioReady = false;

                shouldStartAfterAudio = shouldStartAfterAudio || _pendingAutoPlayAfterSeparateAudioReady;
                _pendingAutoPlayAfterSeparateAudioReady = false;

                if (shouldStartAfterAudio)
                {
                    var startPosition = _resumePlaybackAfterQualityChange
                        ? _resumePositionAfterQualityChange
                        : GetCurrentPlaybackPositionSafe();
                    // Keep _playImmediatelyAfterSeparateAudioOpened=true until
                    // AudioPlayer_MediaOpened fires. SetSeparateAudioSourceFromUriAsync()
                    // can return before that event on Windows 10 Mobile.
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() requested after separate audio source assignment.");
                    StartSynchronizedSeparatePlayback(startPosition);
                }
                else
                {
                    _playImmediatelyAfterSeparateAudioOpened = false;
                    ClearPendingQualityResume();
                    Pause();
                }
            }
            else
            {
                _suppressAutoPlayUntilSeparateAudioReady = false;
            }
        }

        private async Task SetVisibleSourceFromUriAsync(Uri uri, bool autoPlay)
        {
            if (uri == null)
            {
                return;
            }

            var absolute = uri.AbsoluteUri ?? string.Empty;
            var isM3u8 = absolute.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;
            var isMpd = absolute.IndexOf(".mpd", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isM3u8 || isMpd)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Creating AdaptiveMediaSource for VIDEO: " + uri.AbsoluteUri);

                    AdaptiveMediaSourceCreationResult videoResult;
                    var isLocalManifest = uri.IsFile
                        || string.Equals(uri.Scheme, "ms-appdata", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(uri.Scheme, "ms-appx", StringComparison.OrdinalIgnoreCase);

                    if (isLocalManifest)
                    {
                        // AdaptiveMediaSource.CreateFromUriAsync cannot fetch a local
                        // (ms-appdata) manifest — it returns UnsupportedManifestProfile. For an
                        // app-generated DASH/HLS manifest we must open the file and feed it as a
                        // stream; the absolute <BaseURL> inside then drives the remote segment/
                        // range requests. (Fixes 1080p video-only DASH playback on Win10 Mobile.)
                        var manifestFile = await Windows.Storage.StorageFile.GetFileFromApplicationUriAsync(uri);
                        var manifestStream = await manifestFile.OpenReadAsync();
                        var contentType = isMpd ? "application/dash+xml" : "application/vnd.apple.mpegurl";
                        videoResult = await AdaptiveMediaSource.CreateFromStreamAsync(manifestStream, uri, contentType);
                    }
                    else
                    {
                        videoResult = await AdaptiveMediaSource.CreateFromUriAsync(uri);
                    }

                    if (videoResult.Status == AdaptiveMediaSourceCreationStatus.Success && videoResult.MediaSource != null)
                    {
                        SetSource(MediaSource.CreateFromAdaptiveMediaSource(videoResult.MediaSource), autoPlay);
                        return;
                    }

                    System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer: Video AdaptiveMediaSource failed: {videoResult.Status}");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Video AdaptiveMediaSource exception: " + ex.Message);
                }

                // A manifest that AdaptiveMediaSource rejected must NOT be handed to
                // MediaSource.CreateFromUri — playing the .mpd/.m3u8 file itself always yields
                // SourceNotSupported. Bail so the caller can fall through to its next source.
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Adaptive manifest unusable; not attempting to play it as a plain source");
                return;
            }

            SetSource(MediaSource.CreateFromUri(uri), autoPlay);
        }

        private Windows.Media.Playback.MediaPlayer GetOrCreateSeparateAudioPlayer()
        {
            // The one that actually bit: this is reached from async continuations, and creating a
            // background-enabled player after teardown left an audio-only ghost of the previous
            // video playing over the new one.
            if (_playbackReleased)
            {
                return null;
            }

            if (_separateAudioPlayer == null)
            {
                _separateAudioPlayer = new Windows.Media.Playback.MediaPlayer();
                ConfigureMediaPlayerForBackground(_separateAudioPlayer);
                _separateAudioPlayer.AutoPlay = false;
                _separateAudioPlayer.IsMuted = false;
                _separateAudioPlayer.Volume = 1.0;
                _separateAudioPlayer.IsLoopingEnabled = false;
                _separateAudioPlayer.MediaOpened -= AudioPlayer_MediaOpened;
                _separateAudioPlayer.MediaOpened += AudioPlayer_MediaOpened;
                _separateAudioPlayer.MediaEnded -= AudioPlayer_MediaEnded;
                _separateAudioPlayer.MediaEnded += AudioPlayer_MediaEnded;
                AttachSeparateAudioPlaybackStateHandler(_separateAudioPlayer);
            }

            return _separateAudioPlayer;
        }

        private Windows.Media.Playback.MediaPlayer GetSeparateAudioPlayer()
        {
            return _separateAudioPlayer;
        }

        private async Task SetSeparateAudioSourceFromUriAsync(Uri audioUri)
        {
            if (audioUri == null)
            {
                return;
            }

            try
            {
                var absolute = audioUri.AbsoluteUri ?? string.Empty;
                var isM3u8 = absolute.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase) >= 0;

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Setting separate AUDIO source: " + audioUri.AbsoluteUri);
                _separateAudioMediaOpened = false;

                var separatePlayer = GetOrCreateSeparateAudioPlayer();
                try
                {
                    separatePlayer.AutoPlay = false;
                    separatePlayer.Pause();
                    separatePlayer.IsMuted = false;
                    separatePlayer.Volume = 1.0;
                    separatePlayer.IsLoopingEnabled = false;
                    separatePlayer.MediaOpened -= AudioPlayer_MediaOpened;
                    separatePlayer.MediaOpened += AudioPlayer_MediaOpened;
                    separatePlayer.MediaEnded -= AudioPlayer_MediaEnded;
                    separatePlayer.MediaEnded += AudioPlayer_MediaEnded;
                    AttachSeparateAudioPlaybackStateHandler(separatePlayer);
                }
                catch { }

                // Do not render the audio carrier through the hidden MediaPlayerElement on
                // Windows 10 Mobile. A zero-sized/transparent MediaPlayerElement can still
                // push video frames through the UI/video pipeline, which is what makes sound
                // stutter on old phones. A standalone MediaPlayer plays the audio without
                // attaching a visual surface, so the high-res visible video remains untouched.
                try
                {
                    if (AudioPlayer != null)
                    {
                        AudioPlayer.AutoPlay = false;
                        AudioPlayer.Source = null;
                    }
                }
                catch { }

                // IMPORTANT: mark separate-audio mode BEFORE assigning Source.
                // On Windows 10 Mobile MediaOpened can fire immediately from Source assignment;
                // if _usingSeparateAudio is still false, the autoplay handler returns and Play() never happens.
                _usingSeparateAudio = true;
                // From this point a separate audio MediaOpened must start playback.
                // Do not rely on flags that SetSource()/ResetSeparateAudio may clear while
                // the two MediaPlayer instances open asynchronously on Windows 10 Mobile.
                ArmSeparateAudioAutoplayPauseBlock();
                _playImmediatelyAfterSeparateAudioOpened = true;
                _pendingAutoPlayAfterSeparateAudioReady = true;
                StartSeparateAudioSyncTimer();

                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null)
                {
                    try
                    {
                        _pendingAudioPosition = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                        MediaPlayer.MediaPlayer.IsMuted = true;
                    }
                    catch { }
                }

                if (isM3u8)
                {
                    try
                    {
                        var audioResult = await AdaptiveMediaSource.CreateFromUriAsync(audioUri);
                        if (audioResult.Status == AdaptiveMediaSourceCreationStatus.Success && audioResult.MediaSource != null)
                        {
                            separatePlayer.Source = MediaSource.CreateFromAdaptiveMediaSource(audioResult.MediaSource);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Audio AdaptiveMediaSource failed: " + audioResult.Status);
                            separatePlayer.Source = MediaSource.CreateFromUri(audioUri);
                        }
                    }
                    catch
                    {
                        separatePlayer.Source = MediaSource.CreateFromUri(audioUri);
                    }
                }
                else
                {
                    separatePlayer.Source = MediaSource.CreateFromUri(audioUri);
                }

                // _usingSeparateAudio and pending position are set before Source assignment above
                // because MediaOpened may fire synchronously/very early on Windows 10 Mobile.
                await EnsureAudioPlayerReadyAndSyncAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Separate audio setup failed: " + ex.Message);
                ResetSeparateAudio();
            }
        }

        private void ResetSeparateAudio()
        {
            try
            {
                if (_audioInitCts != null)
                {
                    _audioInitCts.Cancel();
                    _audioInitCts = null;
                }
                CancelPendingAudioResume();
                CancelSeparateAudioBufferRecovery();
                CancelSeparateAudioPostStartWatchdog();
                StopSeparateAudioSyncTimer();
                _usingSeparateAudio = false;
                _pendingAudioPosition = TimeSpan.Zero;
                _separateAudioMediaOpened = false;
                _playImmediatelyAfterSeparateAudioOpened = false;
                _separateAudioStartupGuardUntilUtc = DateTime.MinValue;

                var separatePlayer = GetSeparateAudioPlayer();
                if (separatePlayer != null)
                {
                    try
                    {
                        separatePlayer.MediaEnded -= AudioPlayer_MediaEnded;
                        DetachSeparateAudioPlaybackStateHandler(separatePlayer);
                        separatePlayer.Pause();
                        separatePlayer.Source = null; // Detach the source
                    }
                    catch { }
                }
                if (MediaPlayer?.MediaPlayer != null)
                {
                    try { MediaPlayer.MediaPlayer.IsMuted = false; } catch { }
                }
            }
            catch { }
        }

        private void AttachVisibleVideoPlaybackStateHandler()
        {
            try
            {
                var player = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                if (player != null && player.PlaybackSession != null)
                {
                    player.PlaybackSession.PlaybackStateChanged -= VisibleVideoPlaybackStateChanged;
                    player.PlaybackSession.PlaybackStateChanged += VisibleVideoPlaybackStateChanged;
                }
            }
            catch { }
        }

        private void DetachVisibleVideoPlaybackStateHandler()
        {
            try
            {
                var player = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                if (player != null && player.PlaybackSession != null)
                {
                    player.PlaybackSession.PlaybackStateChanged -= VisibleVideoPlaybackStateChanged;
                }
            }
            catch { }
        }

        private void AttachSeparateAudioPlaybackStateHandler(Windows.Media.Playback.MediaPlayer player)
        {
            try
            {
                if (player != null && player.PlaybackSession != null)
                {
                    player.PlaybackSession.PlaybackStateChanged -= SeparateAudioPlaybackStateChanged;
                    player.PlaybackSession.PlaybackStateChanged += SeparateAudioPlaybackStateChanged;
                }
            }
            catch { }
        }

        private void DetachSeparateAudioPlaybackStateHandler(Windows.Media.Playback.MediaPlayer player)
        {
            try
            {
                if (player != null && player.PlaybackSession != null)
                {
                    player.PlaybackSession.PlaybackStateChanged -= SeparateAudioPlaybackStateChanged;
                }
            }
            catch { }
        }

        private async void VisibleVideoPlaybackStateChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            TrackBufferingForQualityStepDown(sender);
            await DispatchSeparateAudioStateChangeAsync("video");
        }

        // --- Rebuffering detector -------------------------------------------------------------
        // A stream that keeps re-buffering is a bitrate problem the slow-playback heuristic misses
        // (that one only fires when the clock advances but too slowly). Here we count how often the
        // session drops back into Buffering after it has started; several times inside a short
        // window means the current quality is too heavy for the connection, so we ask the page to
        // step it down one rung — the same response as a stall.

        private readonly List<DateTime> _bufferingEvents = new List<DateTime>();
        private bool _wasBuffering;
        private static readonly TimeSpan BufferingWindow = TimeSpan.FromSeconds(25);
        private const int BufferingCountForStepDown = 3;

        // Called by the page after a quality change so a fresh stream starts from a clean slate.
        public void ResetBufferingDetector()
        {
            _bufferingEvents.Clear();
            _wasBuffering = false;
        }

        private void TrackBufferingForQualityStepDown(Windows.Media.Playback.MediaPlaybackSession session)
        {
            try
            {
                if (session == null)
                {
                    return;
                }

                var isBuffering = session.PlaybackState
                    == Windows.Media.Playback.MediaPlaybackState.Buffering;

                // Only the transition into Buffering counts as one event.
                if (!isBuffering)
                {
                    _wasBuffering = false;
                    return;
                }
                if (_wasBuffering)
                {
                    return;
                }
                _wasBuffering = true;

                // Buffering before the first frame is just startup, not a bitrate problem.
                if (!_videoLoaded)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                _bufferingEvents.Add(now);
                _bufferingEvents.RemoveAll(t => (now - t) > BufferingWindow);

                System.Diagnostics.Debug.WriteLine(
                    "[Buffering] Event " + _bufferingEvents.Count + " within " + BufferingWindow.TotalSeconds + "s");

                if (_bufferingEvents.Count < BufferingCountForStepDown)
                {
                    return;
                }

                // A step-down replaces the stream; clear so the new one is judged fresh.
                _bufferingEvents.Clear();
                _wasBuffering = false;

                // Demuxed >1x cannot be the cause here, and stepping down is exactly the stall
                // response, so reuse that event.
                System.Diagnostics.Debug.WriteLine("[Buffering] Frequent rebuffering; requesting lower quality");
                var handler = PlaybackStalling;
                if (handler != null)
                {
                    handler(this, null);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Buffering] Detector failed: " + ex.Message);
            }
        }

        private async void SeparateAudioPlaybackStateChanged(Windows.Media.Playback.MediaPlaybackSession sender, object args)
        {
            await DispatchSeparateAudioStateChangeAsync("audio");
        }

        private async Task DispatchSeparateAudioStateChangeAsync(string source)
        {
            try
            {
                var dispatcher = this.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => HandleSeparateAudioPlaybackStateChanged(source));
                    return;
                }

                HandleSeparateAudioPlaybackStateChanged(source);
            }
            catch { }
        }

        private void HandleSeparateAudioPlaybackStateChanged(string source)
        {
            try
            {
                if (!_usingSeparateAudio || !_isPlaying || _isSeparateAudioSeekPending || _isSyncingSeparateAudio)
                {
                    return;
                }

                var videoPlayer = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                var audioPlayer = GetSeparateAudioPlayer();
                if (videoPlayer == null || audioPlayer == null || videoPlayer.PlaybackSession == null || audioPlayer.PlaybackSession == null)
                {
                    return;
                }

                var videoState = videoPlayer.PlaybackSession.PlaybackState;
                var audioState = audioPlayer.PlaybackSession.PlaybackState;

                if (IsBlockingPlaybackState(videoState) || IsBlockingPlaybackState(audioState))
                {
                    TriggerSeparateAudioBufferRecovery("state-" + source, videoPlayer, audioPlayer);
                    return;
                }

                // If one side silently stopped after a seek/buffer while the other one is still running,
                // do not let them drift. Re-enter the same recovery barrier.
                if (videoState != MediaPlaybackState.Playing || audioState != MediaPlaybackState.Playing)
                {
                    TriggerSeparateAudioBufferRecovery("not-playing-" + source, videoPlayer, audioPlayer);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[A/V state] Error: " + ex.Message);
            }
        }

        private TimeSpan GetSafeSeparateAudioMasterPosition(
            Windows.Media.Playback.MediaPlayer videoPlayer,
            Windows.Media.Playback.MediaPlayer audioPlayer)
        {
            try
            {
                if (videoPlayer != null && videoPlayer.PlaybackSession != null)
                {
                    var pos = videoPlayer.PlaybackSession.Position;
                    if (pos < TimeSpan.Zero)
                    {
                        return TimeSpan.Zero;
                    }

                    return pos;
                }
            }
            catch { }

            try
            {
                if (audioPlayer != null && audioPlayer.PlaybackSession != null)
                {
                    var pos = audioPlayer.PlaybackSession.Position;
                    if (pos < TimeSpan.Zero)
                    {
                        return TimeSpan.Zero;
                    }

                    return pos;
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        private bool TrySoftCorrectSeparateAudioDrift(
            string reason,
            Windows.Media.Playback.MediaPlayer videoPlayer,
            Windows.Media.Playback.MediaPlayer audioPlayer)
        {
            try
            {
                if (!_usingSeparateAudio || !_isPlaying || videoPlayer == null || audioPlayer == null ||
                    videoPlayer.PlaybackSession == null || audioPlayer.PlaybackSession == null)
                {
                    return false;
                }

                var videoState = videoPlayer.PlaybackSession.PlaybackState;
                var audioState = audioPlayer.PlaybackSession.PlaybackState;
                if (IsBlockingPlaybackState(videoState) || IsBlockingPlaybackState(audioState))
                {
                    return false;
                }

                var now = DateTime.UtcNow;
                var cooldown = _isWindowsMobileAudioMode ? SeparateAudioMobileHardSyncCooldown : SeparateAudioDefaultHardSyncCooldown;
                if (now - _lastSeparateAudioHardSyncUtc < cooldown)
                {
                    return true;
                }

                var targetPosition = videoPlayer.PlaybackSession.Position;
                if (targetPosition < TimeSpan.Zero)
                {
                    targetPosition = TimeSpan.Zero;
                }

                _lastSeparateAudioHardSyncUtc = now;
                System.Diagnostics.Debug.WriteLine("[A/V sync] Soft audio correction after " + reason + " at " + targetPosition);

                audioPlayer.PlaybackSession.PlaybackRate = videoPlayer.PlaybackSession.PlaybackRate;
                audioPlayer.PlaybackSession.Position = targetPosition;
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[A/V sync] Soft correction error: " + ex.Message);
                return false;
            }
        }

        private void TriggerSeparateAudioBufferRecovery(
            string reason,
            Windows.Media.Playback.MediaPlayer videoPlayer,
            Windows.Media.Playback.MediaPlayer audioPlayer)
        {
            try
            {
                if (!_usingSeparateAudio || !_isPlaying || videoPlayer == null || audioPlayer == null)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                var cooldown = _isWindowsMobileAudioMode ? SeparateAudioMobileBufferRecoveryCooldown : SeparateAudioDefaultBufferRecoveryCooldown;
                if (_isRecoveringSeparateAudioBuffering && now - _lastSeparateAudioBufferRecoveryUtc < cooldown)
                {
                    return;
                }

                _lastSeparateAudioBufferRecoveryUtc = now;
                _isRecoveringSeparateAudioBuffering = true;
                StopSeparateAudioSyncTimer();

                var restartPosition = GetSafeSeparateAudioMasterPosition(videoPlayer, audioPlayer);

                try { videoPlayer.Pause(); } catch { }
                try { audioPlayer.Pause(); } catch { }

                CancelSeparateAudioBufferRecovery();
                _isRecoveringSeparateAudioBuffering = true;
                var cts = new CancellationTokenSource();
                _separateAudioBufferRecoveryCts = cts;
                var ignored = RecoverSeparateAudioAfterBufferingAsync(restartPosition, reason, cts);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[A/V recovery] Trigger error: " + ex.Message);
            }
        }

        private async Task RecoverSeparateAudioAfterBufferingAsync(TimeSpan restartPosition, string reason, CancellationTokenSource cts)
        {
            var token = cts.Token;
            try
            {
                var cooldown = _isWindowsMobileAudioMode ? SeparateAudioMobileBufferRecoveryCooldown : SeparateAudioDefaultBufferRecoveryCooldown;
                await Task.Delay(cooldown, token);
                if (token.IsCancellationRequested || !_isPlaying || !_usingSeparateAudio)
                {
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[A/V recovery] Restarting after " + reason + " at " + restartPosition);
                await SetSeparateAudioPositionAsync(restartPosition, true);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[A/V recovery] Error: " + ex.Message);
            }
            finally
            {
                if (_separateAudioBufferRecoveryCts == cts)
                {
                    _separateAudioBufferRecoveryCts = null;
                }

                _isRecoveringSeparateAudioBuffering = false;
            }
        }

        private void CancelSeparateAudioBufferRecovery()
        {
            try
            {
                if (_separateAudioBufferRecoveryCts != null)
                {
                    _separateAudioBufferRecoveryCts.Cancel();
                    _separateAudioBufferRecoveryCts = null;
                }

                _isRecoveringSeparateAudioBuffering = false;
            }
            catch { }
        }

        private void StartSeparateAudioPostStartWatchdog(TimeSpan expectedStartPosition)
        {
            try
            {
                CancelSeparateAudioPostStartWatchdog();

                if (!_usingSeparateAudio || !_isPlaying)
                {
                    return;
                }

                var cts = new CancellationTokenSource();
                _separateAudioPostStartWatchCts = cts;
                var ignored = WatchSeparateAudioAfterStartAsync(expectedStartPosition, cts);
            }
            catch { }
        }

        private async Task WatchSeparateAudioAfterStartAsync(TimeSpan expectedStartPosition, CancellationTokenSource cts)
        {
            var token = cts.Token;
            var duration = _isWindowsMobileAudioMode ? SeparateAudioMobilePostStartWatchDuration : SeparateAudioDefaultPostStartWatchDuration;
            var poll = _isWindowsMobileAudioMode ? SeparateAudioMobilePostStartWatchPoll : SeparateAudioDefaultPostStartWatchPoll;
            var maxDrift = _isWindowsMobileAudioMode ? SeparateAudioMobileStartDrift : SeparateAudioDefaultStartDrift;
            var untilUtc = DateTime.UtcNow + duration;

            try
            {
                while (!token.IsCancellationRequested && DateTime.UtcNow < untilUtc)
                {
                    await Task.Delay(poll, token);
                    if (token.IsCancellationRequested || !_isPlaying || !_usingSeparateAudio || _isSeparateAudioSeekPending || _isSyncingSeparateAudio)
                    {
                        continue;
                    }

                    var videoPlayer = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                    var audioPlayer = GetSeparateAudioPlayer();
                    if (videoPlayer == null || audioPlayer == null || videoPlayer.PlaybackSession == null || audioPlayer.PlaybackSession == null)
                    {
                        continue;
                    }

                    var videoState = videoPlayer.PlaybackSession.PlaybackState;
                    var audioState = audioPlayer.PlaybackSession.PlaybackState;

                    if (IsBlockingPlaybackState(videoState) || IsBlockingPlaybackState(audioState) ||
                        videoState != MediaPlaybackState.Playing || audioState != MediaPlaybackState.Playing)
                    {
                        TriggerSeparateAudioBufferRecovery("post-start-watchdog", videoPlayer, audioPlayer);
                        return;
                    }

                    var drift = videoPlayer.PlaybackSession.Position - audioPlayer.PlaybackSession.Position;
                    if (drift.Duration() > maxDrift)
                    {
                        if (!TrySoftCorrectSeparateAudioDrift("post-start-drift", videoPlayer, audioPlayer))
                        {
                            TriggerSeparateAudioBufferRecovery("post-start-drift", videoPlayer, audioPlayer);
                        }
                        return;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[A/V post-start watchdog] Error: " + ex.Message);
            }
            finally
            {
                if (_separateAudioPostStartWatchCts == cts)
                {
                    _separateAudioPostStartWatchCts = null;
                }
            }
        }

        private void CancelSeparateAudioPostStartWatchdog()
        {
            try
            {
                if (_separateAudioPostStartWatchCts != null)
                {
                    _separateAudioPostStartWatchCts.Cancel();
                    _separateAudioPostStartWatchCts = null;
                }
            }
            catch { }
        }

        private void AudioSyncTimer_Tick(object sender, object e)
        {
            try
            {
                if (_audioSyncTimer != null)
                {
                    _audioSyncTimer.Interval = GetSeparateAudioSyncTimerInterval();
                }

                if (!_usingSeparateAudio || !_isPlaying || _isSeparateAudioSeekPending) return;

                var videoPlayer = MediaPlayer?.MediaPlayer;
                var audioPlayer = GetSeparateAudioPlayer();
                if (videoPlayer == null || audioPlayer == null) return;

                var videoSession = videoPlayer.PlaybackSession;
                var audioSession = audioPlayer.PlaybackSession;
                if (videoSession == null || audioSession == null) return;

                var audioState = audioSession.PlaybackState;
                var videoState = videoSession.PlaybackState;

                // If either side starts buffering, immediately stop the side that is still
                // playing and re-enter the same start barrier used by Play()/seek.
                // This prevents the audible audio from running ahead of a stuck video frame.
                if (IsBlockingPlaybackState(videoState) || IsBlockingPlaybackState(audioState))
                {
                    TriggerSeparateAudioBufferRecovery("timer-buffering", videoPlayer, audioPlayer);
                    return;
                }

                // Rare guard only: if one side stopped after a buffer event, start through
                // the barrier, not by blindly calling Play() on just one player.
                if (videoState != MediaPlaybackState.Playing || audioState != MediaPlaybackState.Playing)
                {
                    TriggerSeparateAudioBufferRecovery("timer-not-playing", videoPlayer, audioPlayer);
                    return;
                }

                var drift = videoSession.Position - audioSession.Position;
                var maxDrift = _isWindowsMobileAudioMode ? SeparateAudioMobileMaxDrift : SeparateAudioMaxDrift;
                if (drift.Duration() <= maxDrift)
                {
                    return;
                }

                var now = DateTime.UtcNow;
                if (_isWindowsMobileAudioMode &&
                    now - _lastSeparateAudioHardSyncUtc < SeparateAudioMobileHardSyncCooldown)
                {
                    return;
                }

                // Video is the master clock. Correct only the hidden audio carrier.
                // Pausing and restarting both players for ordinary drift causes visible
                // stutter after Pause/Play on fixed-quality HLS streams.
                if (!TrySoftCorrectSeparateAudioDrift("timer-drift", videoPlayer, audioPlayer))
                {
                    TriggerSeparateAudioBufferRecovery("timer-drift", videoPlayer, audioPlayer);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioSyncTimer] Error: {ex.Message}");
            }
        }

        private void ArmSeparateAudioStartupGuard()
        {
            _separateAudioStartupGuardUntilUtc = DateTime.UtcNow +
                (_isWindowsMobileAudioMode ? SeparateAudioMobileStartupGuardDuration : SeparateAudioDefaultStartupGuardDuration);
        }

        private TimeSpan GetSeparateAudioSyncTimerInterval()
        {
            if (DateTime.UtcNow < _separateAudioStartupGuardUntilUtc)
            {
                return _isWindowsMobileAudioMode ? SeparateAudioMobileStartupGuardPoll : SeparateAudioDefaultStartupGuardPoll;
            }

            return _isWindowsMobileAudioMode ? SeparateAudioMobileSyncInterval : SeparateAudioSyncInterval;
        }

        private void StartSeparateAudioSyncTimer()
        {
            try
            {
                if (_audioSyncTimer != null && _usingSeparateAudio && _isPlaying)
                {
                    _audioSyncTimer.Interval = GetSeparateAudioSyncTimerInterval();
                    _audioSyncTimer.Start();
                }
            }
            catch { }
        }


        private void StopSeparateAudioSyncTimer()
        {
            try
            {
                if (_audioSyncTimer != null)
                {
                    _audioSyncTimer.Stop();
                }
            }
            catch { }
        }

        private void CancelPendingAudioResume()
        {
            try
            {
                if (_audioResumeCts != null)
                {
                    _audioResumeCts.Cancel();
                    _audioResumeCts = null;
                }
                _isSeparateAudioSeekPending = false;
                _isSyncingSeparateAudio = false;
            }
            catch { }
        }

        private void PauseSeparateAudio()
        {
            try
            {
                CancelPendingAudioResume();
                var separatePlayer = GetSeparateAudioPlayer();
                if (_usingSeparateAudio && separatePlayer != null)
                {
                    separatePlayer.IsLoopingEnabled = false;
                    separatePlayer.Pause();
                }
            }
            catch { }
        }

        private bool IsBlockingPlaybackState(MediaPlaybackState state)
        {
            return state == MediaPlaybackState.None ||
                   state == MediaPlaybackState.Opening ||
                   state == MediaPlaybackState.Buffering;
        }

        private bool IsReadyForBarrierStart(Windows.Media.Playback.MediaPlayer player, bool mediaOpened)
        {
            try
            {
                if (player == null || player.PlaybackSession == null)
                {
                    return false;
                }

                var state = player.PlaybackSession.PlaybackState;
                if (state == MediaPlaybackState.Opening || state == MediaPlaybackState.Buffering)
                {
                    return false;
                }

                // After MediaOpened, PlaybackState can still be None on old Windows 10 builds
                // while the player is paused at a valid frame. Treat opened+not-buffering as ready.
                if (mediaOpened)
                {
                    return true;
                }

                return state == MediaPlaybackState.Paused || state == MediaPlaybackState.Playing;
            }
            catch
            {
                return false;
            }
        }

        private async Task<bool> WaitForSeparateAvReadyForStartAsync(
            Windows.Media.Playback.MediaPlayer videoPlayer,
            Windows.Media.Playback.MediaPlayer audioPlayer,
            CancellationToken token)
        {
            var timeout = _isWindowsMobileAudioMode ? SeparateAudioMobileReadyTimeout : SeparateAudioDefaultReadyTimeout;
            var pollDelay = _isWindowsMobileAudioMode ? SeparateAudioMobileReadyPoll : SeparateAudioDefaultReadyPoll;
            var stableFor = _isWindowsMobileAudioMode ? SeparateAudioMobileReadyStable : SeparateAudioDefaultReadyStable;

            // Resume from Pause is different from initial load: both sources are already opened,
            // but UWP HLS can keep reporting Buffering until Play() is called. Do a short
            // readiness check, then allow the paired Play() calls and let the watchdog recover.
            var resumeFromOpenedSources = _visibleVideoMediaOpened &&
                (!_usingSeparateAudio || audioPlayer == null || _separateAudioMediaOpened);
            if (resumeFromOpenedSources)
            {
                timeout = _isWindowsMobileAudioMode ? TimeSpan.FromMilliseconds(1800) : TimeSpan.FromMilliseconds(900);
                stableFor = _isWindowsMobileAudioMode ? TimeSpan.FromMilliseconds(120) : TimeSpan.FromMilliseconds(80);
            }

            var startedUtc = DateTime.UtcNow;
            DateTime? bothReadySinceUtc = null;

            while (!token.IsCancellationRequested)
            {
                var videoReady = IsReadyForBarrierStart(videoPlayer, _visibleVideoMediaOpened);
                var audioReady = !_usingSeparateAudio || audioPlayer == null || IsReadyForBarrierStart(audioPlayer, _separateAudioMediaOpened);

                if (videoReady && audioReady)
                {
                    if (!bothReadySinceUtc.HasValue)
                    {
                        bothReadySinceUtc = DateTime.UtcNow;
                    }

                    if (DateTime.UtcNow - bothReadySinceUtc.Value >= stableFor)
                    {
                        return true;
                    }
                }
                else
                {
                    bothReadySinceUtc = null;
                }

                if (DateTime.UtcNow - startedUtc >= timeout)
                {
                    var videoOpenedEnough = _visibleVideoMediaOpened;
                    var audioOpenedEnough = !_usingSeparateAudio || audioPlayer == null || _separateAudioMediaOpened;

                    System.Diagnostics.Debug.WriteLine(
                        "[A/V barrier] Timeout. videoReady=" + videoReady +
                        ", audioReady=" + audioReady +
                        ", videoOpened=" + videoOpenedEnough +
                        ", audioOpened=" + audioOpenedEnough
                    );

                    // UWP HLS can stay in Buffering while paused and leave that state only
                    // after Play() is called again. If both sources are already opened, let
                    // the adjacent Play() calls proceed and let the post-start watchdog
                    // recover if one side still fails to run.
                    return videoOpenedEnough && audioOpenedEnough;
                }

                await Task.Delay(pollDelay, token);
            }

            return false;
        }

        private async Task<bool> WaitForVisibleVideoOpenedOrUsableAsync(CancellationToken token)
        {
            var startedUtc = DateTime.UtcNow;
            var timeout = _isWindowsMobileAudioMode ? SeparateAudioMobileReadyTimeout : SeparateAudioDefaultReadyTimeout;
            var pollDelay = _isWindowsMobileAudioMode ? SeparateAudioMobileReadyPoll : SeparateAudioDefaultReadyPoll;

            while (!token.IsCancellationRequested)
            {
                var videoPlayer = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                if (IsReadyForBarrierStart(videoPlayer, _visibleVideoMediaOpened))
                {
                    return true;
                }

                if (DateTime.UtcNow - startedUtc >= timeout)
                {
                    return false;
                }

                await Task.Delay(pollDelay, token);
            }

            return false;
        }

        private void ArmSeparateAudioAutoplayPauseBlock()
        {
            _separateAudioAutoplayPauseBlockUntilUtc = DateTime.UtcNow.AddSeconds(20);
        }

        private void ArmShortSeparateAudioAutoplayPauseBlock()
        {
            _separateAudioAutoplayPauseBlockUntilUtc = DateTime.UtcNow.AddMilliseconds(1800);
        }

        private bool IsSeparateAudioAutoplayPauseBlocked()
        {
            return DateTime.UtcNow <= _separateAudioAutoplayPauseBlockUntilUtc;
        }

        private void SetSeparateAudioPosition(TimeSpan position, bool resumeIfPlaying)
        {
            var ignored = SetSeparateAudioPositionAsync(position, resumeIfPlaying);
        }

        private async Task SetSeparateAudioPositionAsync(TimeSpan position, bool resumeIfPlaying)
        {
            CancellationTokenSource cts = null;
            try
            {
                var videoPlayer = MediaPlayer?.MediaPlayer;
                var audioPlayer = GetSeparateAudioPlayer();
                if (videoPlayer == null) return;

                CancelPendingAudioResume();
                CancelSeparateAudioPostStartWatchdog();
                cts = new CancellationTokenSource();
                _audioResumeCts = cts;
                var token = cts.Token;

                StopSeparateAudioSyncTimer();
                _isSeparateAudioSeekPending = true;
                _isSyncingSeparateAudio = true;

                // Hard rule: before every start/resume/seek both players are paused first.
                // No audio is allowed to continue while the visible video is opening/buffering.
                try { videoPlayer.Pause(); } catch { }
                if (_usingSeparateAudio && audioPlayer != null)
                {
                    try
                    {
                        audioPlayer.IsLoopingEnabled = false;
                        audioPlayer.Pause();
                        audioPlayer.PlaybackSession.PlaybackRate = videoPlayer.PlaybackSession.PlaybackRate;
                    }
                    catch { }
                }

                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                // One explicit alignment point: load/play, seek, pause/resume. No tight loop.
                videoPlayer.PlaybackSession.Position = position;
                if (_usingSeparateAudio && audioPlayer != null)
                {
                    audioPlayer.PlaybackSession.Position = position;
                }

                if (!resumeIfPlaying)
                {
                    _isPlaying = false;
                    return;
                }

                _isPlaying = true;
                SetPlayPauseIcon(true);
                UpdateSystemMediaDisplay();
                UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Playing);

                // Barrier: wait until BOTH players have opened and are not buffering/opening.
                // This is the important part: neither video nor separate audio may start alone.
                var ready = await WaitForSeparateAvReadyForStartAsync(videoPlayer, audioPlayer, token);
                if (token.IsCancellationRequested || !_isPlaying) return;

                if (!ready)
                {
                    System.Diagnostics.Debug.WriteLine("[SetSeparateAudioPosition] A/V barrier did not become ready; keeping both paused.");
                    _isPlaying = false;
                    SetPlayPauseIcon(false);
                    UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Paused);
                    return;
                }

                // Re-align immediately before the adjacent Play() calls because the players may
                // have updated their internal position while opening/buffering.
                videoPlayer.PlaybackSession.Position = position;
                if (_usingSeparateAudio && audioPlayer != null)
                {
                    audioPlayer.PlaybackSession.Position = position;
                }

                // Visible video first, audible audio immediately after. Because both passed the
                // barrier, this avoids the old case where audio started while video was loading.
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() called after A/V barrier.");
                videoPlayer.Play();
                if (_usingSeparateAudio && audioPlayer != null)
                {
                    audioPlayer.Play();
                }
                SetPlaybackUiPlaying();

                ArmSeparateAudioStartupGuard();
                StartSeparateAudioPostStartWatchdog(position);
                StartSeparateAudioSyncTimer();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SetSeparateAudioPosition] Error: {ex.Message}");
            }
            finally
            {
                if (_audioResumeCts == cts)
                {
                    _audioResumeCts = null;
                    _isSeparateAudioSeekPending = false;
                    _isSyncingSeparateAudio = false;
                }
            }
        }

        private void StartSynchronizedSeparatePlayback(TimeSpan targetPosition)
        {
            SetSeparateAudioPosition(targetPosition, true);
        }



        private void SyncSeparateAudioWithVideo(bool resumeIfPlaying)
        {
            try
            {
                if (!_usingSeparateAudio || MediaPlayer == null || MediaPlayer.MediaPlayer == null || MediaPlayer.MediaPlayer.PlaybackSession == null)
                {
                    return;
                }

                // Event-only sync. This method is called only from explicit state changes:
                // seek, pause, play/resume and speed change. The timer is not started.
                SetSeparateAudioPosition(MediaPlayer.MediaPlayer.PlaybackSession.Position, resumeIfPlaying && _isPlaying);
            }
            catch { }
        }

        private async void AudioPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            _separateAudioMediaOpened = true;
            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Separate audio opened successfully");

            // This event means the separate audio carrier is ready. For quality switching
            // we must start immediately here; waiting for old autoplay flags was unreliable
            // on Windows 10 Mobile because SetSource()/ResetSeparateAudio can clear them.
            try
            {
                var dispatcher = this.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StartPlaybackAfterSeparateAudioOpened());
                }
                else
                {
                    StartPlaybackAfterSeparateAudioOpened();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: failed to autoplay after separate audio opened - " + ex.Message);
            }
        }

        private void StartPlaybackAfterSeparateAudioOpened()
        {
            try
            {
                // MediaOpened can arrive before SetSeparateAudioSourceFromUriAsync() finishes its setup.
                // Force the mode on here instead of returning silently.
                _usingSeparateAudio = true;

                var startPosition = _resumePlaybackAfterQualityChange
                    ? _resumePositionAfterQualityChange
                    : GetCurrentPlaybackPositionSafe();

                _suppressAutoPlayUntilSeparateAudioReady = false;

                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null)
                {
                    try { MediaPlayer.MediaPlayer.IsMuted = true; } catch { }
                    try { MediaPlayer.MediaPlayer.PlaybackSession.Position = startPosition; } catch { }
                }

                var audioPlayer = GetSeparateAudioPlayer();
                if (audioPlayer != null)
                {
                    try { audioPlayer.PlaybackSession.Position = startPosition; } catch { }
                }

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() called immediately after separate audio opened.");
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() called.");

                // Start both players directly. Calling Play() here used to route through
                // SetSeparateAudioPositionAsync(), which pauses both streams first and can
                // fail the barrier on Windows 10 Mobile. At MediaOpened time both sources
                // are already opened enough to start.
                _isPlaying = true;
                SetPlaybackUiPlaying();
                try { if (MediaPlayer != null && MediaPlayer.MediaPlayer != null) MediaPlayer.MediaPlayer.Play(); } catch { }
                try { if (audioPlayer != null) audioPlayer.Play(); } catch { }

                ArmShortSeparateAudioAutoplayPauseBlock();
                ArmSeparateAudioStartupGuard();
                StartSeparateAudioPostStartWatchdog(startPosition);
                StartSeparateAudioSyncTimer();

                _playImmediatelyAfterSeparateAudioOpened = false;
                _pendingAutoPlayAfterSeparateAudioReady = false;
                ClearPendingQualityResume();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: StartPlaybackAfterSeparateAudioOpened failed - " + ex.Message);

                // Last-resort: do not let UI/position code prevent actual playback.
                try { if (MediaPlayer != null && MediaPlayer.MediaPlayer != null) MediaPlayer.MediaPlayer.Play(); } catch { }
                try { var p = GetSeparateAudioPlayer(); if (p != null) p.Play(); } catch { }
            }
        }

        private void AudioPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            try
            {
                if (!_usingSeparateAudio || MediaPlayer == null || MediaPlayer.MediaPlayer == null)
                {
                    return;
                }

                var videoSession = MediaPlayer.MediaPlayer.PlaybackSession;
                if (videoSession == null)
                {
                    return;
                }

                var duration = _parsedDuration > TimeSpan.Zero
                    ? _parsedDuration
                    : videoSession.NaturalDuration;

                // Autosync test mode: do not restart/reseek the separate audio when it ends.
                // This avoids accidental looping or rebuffer spikes on Windows 10 Mobile.
                PauseSeparateAudio();
            }
            catch { }
        }

        // Speeds offered to the user. A demuxed (MediaStreamSource) source cannot actually play
        // faster than 1x on this platform, so those entries are left out rather than shown as
        // dead options — the menu should not promise something the pipeline ignores.
        public List<string> GetAvailableSpeedOptions()
        {
            var all = new List<string> { "0.25x", "0.5x", "0.75x", "1.0x", "1.25x", "1.5x", "1.75x", "2.0x" };

            if (_demuxedSource == null)
            {
                return all;
            }

            return new List<string> { "0.25x", "0.5x", "0.75x", "1.0x" };
        }

        // Whether the visible video is currently the on-the-fly demuxer (muxer) source.
        public bool IsUsingDemuxer { get { return _demuxedSource != null; } }

        // Remembered so a source swap (e.g. a quality change) can restore the rate on media open.
        private double _desiredPlaybackRate = 1.0;

        public void SetPlaybackRate(double rate)
        {
            _desiredPlaybackRate = rate;
            try
            {
                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null)
                {
                    var session = MediaPlayer.MediaPlayer.PlaybackSession;
                    session.PlaybackRate = rate;

                    // Read back: with a demuxed (MediaStreamSource) source the pipeline can simply
                    // refuse a rate and silently stay at 1.0. The old empty catch here hid both
                    // that and any exception, which is why the speed menu looked dead.
                    System.Diagnostics.Debug.WriteLine(
                        "[Player] Playback rate requested " + rate
                        + ", session reports " + session.PlaybackRate);

                    // The pipeline accepts any rate, but above 1x it then needs samples that much
                    // faster. The demuxer has to widen its read-ahead or playback stays pinned at
                    // roughly real time no matter what the session reports.
                    DashDemuxer.ApplyPlaybackRate(_demuxedSource, rate);
                }

                var separatePlayer = GetSeparateAudioPlayer();
                if (_usingSeparateAudio && separatePlayer != null)
                {
                    separatePlayer.PlaybackSession.PlaybackRate = rate;

                    // Speed change is one of the allowed sync events. We align once here
                    // and do not keep correcting in the background.
                    SyncSeparateAudioWithVideo(_isPlaying);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Player] Setting playback rate failed: " + ex.Message);
            }
        }

        private async Task EnsureAudioPlayerReadyAndSyncAsync()
        {
            try
            {
                if (!_usingSeparateAudio) return;

                var separatePlayer = GetOrCreateSeparateAudioPlayer();

                try
                {
                    separatePlayer.AutoPlay = false;
                    separatePlayer.IsMuted = false;
                    separatePlayer.Volume = 1.0;
                    separatePlayer.IsLoopingEnabled = false;
                    separatePlayer.MediaOpened -= AudioPlayer_MediaOpened;
                    separatePlayer.MediaOpened += AudioPlayer_MediaOpened;
                    separatePlayer.MediaEnded -= AudioPlayer_MediaEnded;
                    separatePlayer.MediaEnded += AudioPlayer_MediaEnded;
                    AttachSeparateAudioPlaybackStateHandler(separatePlayer);

                    if (MediaPlayer?.MediaPlayer != null)
                    {
                        separatePlayer.PlaybackSession.PlaybackRate = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackRate;
                    }
                }
                catch { }

                if (_isPlaying)
                {
                    StartSynchronizedSeparatePlayback(_pendingAudioPosition);
                }
                else
                {
                    separatePlayer.Pause();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: EnsureAudioPlayerReady failed: " + ex.Message);
            }
        }

        private sealed class HlsSelection
        {
            public Uri VideoPlaylist { get; set; }
            public Uri AudioPlaylist { get; set; }
            public int SelectedQuality { get; set; }
            public string SelectedCodecs { get; set; }
        }

        private sealed class HlsVariant
        {
            public string PlaylistUrl { get; set; }
            public string AudioGroup { get; set; }
            public int Quality { get; set; }
            public long Bandwidth { get; set; }
            public string Codecs { get; set; }
            public int CodecRank { get; set; }
            public int Width { get; set; }
            public int Height { get; set; }
            public string Resolution { get; set; }
            public string VideoRange { get; set; }
        }

        private static async Task<HlsSelection> TryPickHlsVideoAndAudioAsync(Uri masterUri, string requestedQualityTag)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Get, masterUri);
                req.Headers.TryAddWithoutValidation("User-Agent", YouTubeIosUserAgent);
                req.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");
                req.Headers.TryAddWithoutValidation("Referer", YouTubeReferer);

                var resp = await _hlsHttpClient.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    return null;
                }

                var text = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(text) || text.IndexOf("#EXTM3U", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    return null;
                }

                var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

                // Pick default audio for group later.
                // Map: groupId -> defaultUri (prefer DEFAULT=YES).
                var audioDefaultByGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var audioFirstByGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var audioUrisByGroup = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < lines.Length; i++)
                {
                    var l = (lines[i] ?? string.Empty).Trim();
                    if (!l.StartsWith("#EXT-X-MEDIA", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var type = ExtractAttributeValue(l, "TYPE");
                    if (!string.Equals(type, "AUDIO", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var groupId = ExtractAttributeValue(l, "GROUP-ID");
                    var uriStr = ExtractAttributeValue(l, "URI");
                    if (string.IsNullOrWhiteSpace(groupId) || string.IsNullOrWhiteSpace(uriStr))
                    {
                        continue;
                    }

                    var isDefault = l.IndexOf("DEFAULT=YES", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isDefault && !audioDefaultByGroup.ContainsKey(groupId))
                    {
                        audioDefaultByGroup[groupId] = uriStr;
                    }
                    if (!audioFirstByGroup.ContainsKey(groupId))
                    {
                        audioFirstByGroup[groupId] = uriStr;
                    }

                    List<string> groupUris;
                    if (!audioUrisByGroup.TryGetValue(groupId, out groupUris))
                    {
                        groupUris = new List<string>();
                        audioUrisByGroup[groupId] = groupUris;
                    }
                    groupUris.Add(uriStr);
                }

                int requestedQuality = ParseQualityTag(requestedQualityTag);
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Requested HLS quality: " + (requestedQuality > 0 ? requestedQuality + "p" : "Auto"));

                var variants = new List<HlsVariant>();

                for (int i = 0; i < lines.Length - 1; i++)
                {
                    var line = (lines[i] ?? string.Empty).Trim();
                    if (!line.StartsWith("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var attrs = line;
                    var next = (lines[i + 1] ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(next) || next.StartsWith("#"))
                    {
                        continue;
                    }

                    var codecs = ExtractAttributeValue(attrs, "CODECS");

                    // UWP/VS2015 target: keep H.264 variants only. VP9 and HLG variants from
                    // YouTube master playlists are intentionally ignored for this player path.
                    if (!IsH264HlsCodec(codecs))
                    {
                        continue;
                    }

                    long bw = ParseHlsBandwidth(attrs);
                    int width;
                    int height;
                    if (!TryParseHlsResolution(attrs, out width, out height))
                    {
                        continue;
                    }

                    int quality = Math.Min(width, height);
                    string resolution = width + "x" + height;
                    string videoRange = ExtractAttributeValue(attrs, "VIDEO-RANGE");
                    string audioGroup = ExtractAttributeValue(attrs, "AUDIO");

                    if (quality <= 0)
                    {
                        continue;
                    }

                    variants.Add(new HlsVariant
                    {
                        PlaylistUrl = next,
                        AudioGroup = audioGroup,
                        Quality = quality,
                        Bandwidth = bw,
                        Codecs = codecs,
                        CodecRank = GetHlsCodecRank(codecs),
                        Width = width,
                        Height = height,
                        Resolution = resolution,
                        VideoRange = videoRange
                    });

                    System.Diagnostics.Debug.WriteLine(
                        "CustomVideoPlayer: HLS candidate: "
                        + quality + "p, resolution=" + resolution
                        + ", bandwidth=" + bw
                        + ", codecs=" + codecs
                        + ", videoRange=" + (string.IsNullOrWhiteSpace(videoRange) ? "" : videoRange)
                    );
                }

                HlsVariant selected = PickHlsVariant(variants, requestedQuality);

                if (selected != null && !string.IsNullOrWhiteSpace(selected.PlaylistUrl))
                {
                    Uri videoUri;
                    if (!Uri.TryCreate(selected.PlaylistUrl, UriKind.Absolute, out videoUri))
                    {
                        videoUri = new Uri(masterUri, selected.PlaylistUrl);
                    }

                    Uri audioUri = null;
                    if (!string.IsNullOrWhiteSpace(selected.AudioGroup))
                    {
                        string defaultAudioStr;
                        audioDefaultByGroup.TryGetValue(selected.AudioGroup, out defaultAudioStr);

                        string firstAudioStr;
                        audioFirstByGroup.TryGetValue(selected.AudioGroup, out firstAudioStr);

                        List<string> groupAudioUris;
                        audioUrisByGroup.TryGetValue(selected.AudioGroup, out groupAudioUris);

                        var audioStr = PickPreferredAudioVariant(groupAudioUris, defaultAudioStr, firstAudioStr);
                        if (string.IsNullOrWhiteSpace(audioStr))
                        {
                            audioStr = defaultAudioStr ?? firstAudioStr;
                        }

                        if (!string.IsNullOrWhiteSpace(audioStr))
                        {
                            if (!Uri.TryCreate(audioStr, UriKind.Absolute, out audioUri))
                            {
                                audioUri = new Uri(masterUri, audioStr);
                            }
                        }
                    }

                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Picked HLS video: " + videoUri.AbsoluteUri);
                    System.Diagnostics.Debug.WriteLine(
                        "CustomVideoPlayer: Picked HLS quality: "
                        + selected.Quality + "p"
                        + ", resolution=" + selected.Resolution
                        + ", codecs=" + selected.Codecs
                    );
                    if (audioUri != null)
                    {
                        System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Picked HLS audio fallback: " + audioUri.AbsoluteUri);
                    }

                    return new HlsSelection
                    {
                        VideoPlaylist = videoUri,
                        AudioPlaylist = audioUri,
                        SelectedQuality = selected.Quality,
                        SelectedCodecs = selected.Codecs
                    };
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Variant resolve failed: " + ex.Message);
                return null;
            }
        }

        private static HlsVariant PickHlsVariant(List<HlsVariant> variants, int requestedQuality)
        {
            if (variants == null || variants.Count == 0)
            {
                return null;
            }

            // Manual quality selection must be driven by RESOLUTION, not by codec rank.
            // Example portrait mapping: 1080x1920 => 1080p, 720x1280 => 720p,
            // 480x854 => 480p, 360x640 => 360p, 144x256 => 144p.
            if (requestedQuality > 0)
            {
                HlsVariant bestExact = null;
                for (int i = 0; i < variants.Count; i++)
                {
                    var candidate = variants[i];
                    if (candidate == null || candidate.Quality != requestedQuality)
                    {
                        continue;
                    }

                    if (IsBetterSameQualityHlsVariant(candidate, bestExact))
                    {
                        bestExact = candidate;
                    }
                }

                if (bestExact != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "CustomVideoPlayer: Exact HLS quality match by RESOLUTION: "
                        + bestExact.Resolution
                        + " => " + bestExact.Quality + "p"
                    );
                    return bestExact;
                }

                HlsVariant bestLower = null;
                for (int i = 0; i < variants.Count; i++)
                {
                    var candidate = variants[i];
                    if (candidate == null || candidate.Quality > requestedQuality)
                    {
                        continue;
                    }

                    if (bestLower == null
                        || candidate.Quality > bestLower.Quality
                        || (candidate.Quality == bestLower.Quality && IsBetterSameQualityHlsVariant(candidate, bestLower)))
                    {
                        bestLower = candidate;
                    }
                }

                if (bestLower != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "CustomVideoPlayer: Exact requested HLS quality was not found; using nearest lower RESOLUTION "
                        + bestLower.Resolution
                        + " => " + bestLower.Quality + "p"
                    );
                    return bestLower;
                }

                HlsVariant nearestHigher = null;
                for (int i = 0; i < variants.Count; i++)
                {
                    var candidate = variants[i];
                    if (candidate == null)
                    {
                        continue;
                    }

                    if (nearestHigher == null
                        || candidate.Quality < nearestHigher.Quality
                        || (candidate.Quality == nearestHigher.Quality && IsBetterSameQualityHlsVariant(candidate, nearestHigher)))
                    {
                        nearestHigher = candidate;
                    }
                }

                if (nearestHigher != null)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "CustomVideoPlayer: Exact requested HLS quality was not found; using nearest available RESOLUTION "
                        + nearestHigher.Resolution
                        + " => " + nearestHigher.Quality + "p"
                    );
                }

                return nearestHigher;
            }

            HlsVariant bestAuto = null;
            for (int i = 0; i < variants.Count; i++)
            {
                var candidate = variants[i];
                if (candidate == null)
                {
                    continue;
                }

                if (bestAuto == null
                    || candidate.Quality > bestAuto.Quality
                    || (candidate.Quality == bestAuto.Quality && IsBetterSameQualityHlsVariant(candidate, bestAuto)))
                {
                    bestAuto = candidate;
                }
            }

            return bestAuto;
        }

        private static bool IsBetterSameQualityHlsVariant(HlsVariant candidate, HlsVariant current)
        {
            if (candidate == null)
            {
                return false;
            }

            if (current == null)
            {
                return true;
            }

            // Prefer SDR if both variants have the same RESOLUTION. UWP/VS2015 targets are
            // safer with SDR H.264 than HDR/HLG variants.
            var candidateIsSdr = string.IsNullOrWhiteSpace(candidate.VideoRange)
                || string.Equals(candidate.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase);
            var currentIsSdr = string.IsNullOrWhiteSpace(current.VideoRange)
                || string.Equals(current.VideoRange, "SDR", StringComparison.OrdinalIgnoreCase);
            if (candidateIsSdr != currentIsSdr)
            {
                return candidateIsSdr;
            }

            // Then prefer the safest H.264/AAC codec at the same resolution.
            if (candidate.CodecRank != current.CodecRank)
            {
                return candidate.CodecRank < current.CodecRank;
            }

            return candidate.Bandwidth > current.Bandwidth;
        }

        private static bool IsH264HlsCodec(string codecs)
        {
            return !string.IsNullOrWhiteSpace(codecs)
                && codecs.IndexOf("avc1.", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int GetHlsCodecRank(string codecs)
        {
            if (string.Equals(codecs, RequiredHlsAvcCodec, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            if (IsH264HlsCodec(codecs)
                && codecs.IndexOf("mp4a.40.2", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 1;
            }

            return 2;
        }

        private static long ParseHlsBandwidth(string attrs)
        {
            if (string.IsNullOrWhiteSpace(attrs))
            {
                return -1;
            }

            var bwIdx = attrs.IndexOf("BANDWIDTH=", StringComparison.OrdinalIgnoreCase);
            if (bwIdx < 0)
            {
                return -1;
            }

            bwIdx += "BANDWIDTH=".Length;
            int end = bwIdx;
            while (end < attrs.Length && char.IsDigit(attrs[end]))
            {
                end++;
            }

            long parsed;
            return long.TryParse(attrs.Substring(bwIdx, end - bwIdx), out parsed) ? parsed : -1;
        }

        private static int ParseHlsQuality(string attrs)
        {
            int width;
            int height;
            if (!TryParseHlsResolution(attrs, out width, out height))
            {
                return -1;
            }

            return Math.Min(width, height);
        }

        private static bool TryParseHlsResolution(string attrs, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (string.IsNullOrWhiteSpace(attrs))
            {
                return false;
            }

            var resIdx = attrs.IndexOf("RESOLUTION=", StringComparison.OrdinalIgnoreCase);
            if (resIdx < 0)
            {
                return false;
            }

            resIdx += "RESOLUTION=".Length;
            int end = resIdx;
            while (end < attrs.Length && attrs[end] != ',' && !char.IsWhiteSpace(attrs[end]))
            {
                end++;
            }

            var res = attrs.Substring(resIdx, end - resIdx).Trim().Trim('"');
            var x = res.IndexOf('x');
            if (x <= 0 || x >= res.Length - 1)
            {
                return false;
            }

            if (!int.TryParse(res.Substring(0, x), out width) || !int.TryParse(res.Substring(x + 1), out height))
            {
                width = 0;
                height = 0;
                return false;
            }

            return width > 0 && height > 0;
        }

        private static int ParseQualityTag(string qualityTag)
        {
            if (string.IsNullOrWhiteSpace(qualityTag))
            {
                return 0;
            }

            var digits = new System.Text.StringBuilder();
            for (int i = 0; i < qualityTag.Length; i++)
            {
                if (char.IsDigit(qualityTag[i]))
                {
                    digits.Append(qualityTag[i]);
                }
            }

            int result;
            return int.TryParse(digits.ToString(), out result) ? result : 0;
        }

        private static string ExtractQuotedAttribute(string line, string key)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            // Looks for KEY="value" (case-insensitive)
            var pattern = key + "=\"";
            var idx = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            idx += pattern.Length;
            var end = line.IndexOf('"', idx);
            if (end < 0 || end <= idx)
            {
                return null;
            }

            return line.Substring(idx, end - idx);
        }

        private static string PickPreferredAudioVariant(List<string> audioUris, string defaultAudioUri, string firstAudioUri)
        {
            // YouTube HLS audio variants may expose both itag 233 and 234.
            // Prefer 233 explicitly, then fall back to DEFAULT or first discovered.
            if (audioUris != null)
            {
                for (int i = 0; i < audioUris.Count; i++)
                {
                    if (GetItagFromUrl(audioUris[i]) == 233)
                    {
                        return audioUris[i];
                    }
                }
            }

            if (GetItagFromUrl(defaultAudioUri) == 233)
            {
                return defaultAudioUri;
            }

            if (GetItagFromUrl(firstAudioUri) == 233)
            {
                return firstAudioUri;
            }

            return defaultAudioUri ?? firstAudioUri;
        }

        private static int GetItagFromUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return -1;
            }

            const string pathNeedle = "/itag/";
            var pathIdx = url.IndexOf(pathNeedle, StringComparison.OrdinalIgnoreCase);
            if (pathIdx >= 0)
            {
                var start = pathIdx + pathNeedle.Length;
                int end = start;
                while (end < url.Length && char.IsDigit(url[end]))
                {
                    end++;
                }

                if (end > start)
                {
                    int itagFromPath;
                    if (int.TryParse(url.Substring(start, end - start), out itagFromPath))
                    {
                        return itagFromPath;
                    }
                }
            }

            const string queryNeedle = "itag=";
            var queryIdx = url.IndexOf(queryNeedle, StringComparison.OrdinalIgnoreCase);
            if (queryIdx >= 0)
            {
                var start = queryIdx + queryNeedle.Length;
                int end = start;
                while (end < url.Length && char.IsDigit(url[end]))
                {
                    end++;
                }

                if (end > start)
                {
                    int itagFromQuery;
                    if (int.TryParse(url.Substring(start, end - start), out itagFromQuery))
                    {
                        return itagFromQuery;
                    }
                }
            }

            return -1;
        }

        private static string ExtractAttributeValue(string line, string key)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(key))
            {
                return null;
            }

            // Supports KEY="value" and KEY=value (until comma)
            var quoted = ExtractQuotedAttribute(line, key);
            if (!string.IsNullOrWhiteSpace(quoted))
            {
                return quoted;
            }

            var pattern = key + "=";
            var idx = line.IndexOf(pattern, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return null;
            }

            idx += pattern.Length;
            if (idx >= line.Length)
            {
                return null;
            }

            int end = idx;
            while (end < line.Length && line[end] != ',' && !char.IsWhiteSpace(line[end]))
            {
                end++;
            }

            var value = line.Substring(idx, end - idx).Trim().Trim('"');
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        private TimeSpan GetCurrentPlaybackPositionSafe()
        {
            try
            {
                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null && MediaPlayer.MediaPlayer.PlaybackSession != null)
                {
                    var position = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                    if (position < TimeSpan.Zero)
                    {
                        return TimeSpan.Zero;
                    }

                    return position;
                }
            }
            catch { }

            return TimeSpan.Zero;
        }

        private bool IsMediaPlaybackActuallyPlaying()
        {
            try
            {
                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null && MediaPlayer.MediaPlayer.PlaybackSession != null)
                {
                    var state = MediaPlayer.MediaPlayer.PlaybackSession.PlaybackState;
                    return state == MediaPlaybackState.Playing || state == MediaPlaybackState.Buffering;
                }
            }
            catch { }

            return false;
        }

        private void ApplyPendingQualityResumePositionToVisiblePlayer()
        {
            try
            {
                if (!_resumePlaybackAfterQualityChange)
                {
                    return;
                }

                if (MediaPlayer != null && MediaPlayer.MediaPlayer != null && MediaPlayer.MediaPlayer.PlaybackSession != null)
                {
                    var position = _resumePositionAfterQualityChange;
                    if (position < TimeSpan.Zero)
                    {
                        position = TimeSpan.Zero;
                    }

                    MediaPlayer.MediaPlayer.PlaybackSession.Position = position;
                }
            }
            catch { }
        }

        private void ClearPendingQualityResume()
        {
            _resumePlaybackAfterQualityChange = false;
            _resumePositionAfterQualityChange = TimeSpan.Zero;
        }

        public void SetSource(MediaSource source)
        {
            SetSource(source, true);
        }

        public async void SetSource(MediaSource source, bool autoPlay)
        {
            // Refuse to resurrect playback after the page has been torn down. Note it recreates
            // the MediaPlayer below when one is missing, so without this check a late-arriving
            // quality switch silently brings the old video back to life.
            if (_playbackReleased)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SetSource ignored - playback already released");
                try { if (source != null) source.Dispose(); } catch { }
                return;
            }

            // Cleared here, at the synchronous start, so the muxed path always drops the flag;
            // SetDemuxedSource re-assigns it right after calling this.
            _demuxedSource = null;

            try
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Setting source");
                ResetSeparateAudio();

                if (MediaPlayer.MediaPlayer == null)
                {
                    MediaPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
                }
                ConfigureMediaPlayerForBackground(MediaPlayer.MediaPlayer);
                UpdateSystemMediaDisplay();

                if (ErrorMessageText != null)
                {
                    ErrorMessageText.Visibility = Visibility.Collapsed;
                }

                _videoLoaded = false;
                ProgressSlider.Value = 0;
                ProgressSlider.Maximum = 100;

                if (ReplayButton != null)
                {
                    ReplayButton.Visibility = Visibility.Collapsed;
                    ReplayButton.Opacity = 0;
                    ReplayButton.IsHitTestVisible = false;
                }

                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Visibility = Visibility.Visible;
                    if (_playPauseIcon != null)
                    {
                        _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
                    }
                    PlayPauseButton.Opacity = 0.8;
                    PlayPauseButton.IsHitTestVisible = true;
                }

                // Ensure the subscription is correct
                MediaPlayer.MediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                MediaPlayer.MediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
                MediaPlayer.MediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
                MediaPlayer.MediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
                AttachVisibleVideoPlaybackStateHandler();
                ResetBufferingDetector();
                _visibleVideoMediaOpened = false;

                // IMPORTANT: If SetMediaPlayer() was used, assign the source directly to the internal player.
                // Keep the outgoing source so it can be disposed below — replacing Source does not
                // release it, and a leaked MediaStreamSource keeps its demuxer prefetching.
                var previousSource = MediaPlayer.MediaPlayer.Source as IDisposable;
                MediaPlayer.MediaPlayer.Source = source;
                if (previousSource != null && !ReferenceEquals(previousSource, source))
                {
                    try { previousSource.Dispose(); } catch { }
                }
                UpdateSystemMediaDisplay();

                _isPlaying = false;
                SetPlayPauseIcon(false);
                ProgressSlider.Value = 0;

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Source set, waiting until media is opened before play");

                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await WaitForVisibleVideoOpenedOrUsableAsync(cts.Token);

                    if (_suppressAutoPlayUntilSeparateAudioReady || _playImmediatelyAfterSeparateAudioOpened)
                    {
                        _pendingAutoPlayAfterSeparateAudioReady = autoPlay || _resumePlaybackAfterQualityChange || _playImmediatelyAfterSeparateAudioOpened;
                        System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Visible video ready; waiting for separate audio before autoplay=" + _pendingAutoPlayAfterSeparateAudioReady);
                        return;
                    }

                    var shouldAutoPlayNow = autoPlay || _resumePlaybackAfterQualityChange || _playImmediatelyAfterSeparateAudioOpened;

                    if (_resumePlaybackAfterQualityChange)
                    {
                        ApplyPendingQualityResumePositionToVisiblePlayer();
                    }

                    if (_isPlaying && !shouldAutoPlayNow)
                    {
                        ClearPendingQualityResume();
                        return;
                    }

                    if (shouldAutoPlayNow)
                    {
                        Play();
                    }
                    else if (_suppressAutoPlayUntilSeparateAudioReady || _playImmediatelyAfterSeparateAudioOpened || _pendingAutoPlayAfterSeparateAudioReady)
                    {
                        System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Pause() skipped because separate audio autoplay is pending.");
                        return;
                    }
                    else
                    {
                        Pause();
                    }

                    ClearPendingQualityResume();
                }
                catch (OperationCanceledException)
                {
                    EndSourceLoading();
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Timeout waiting for media to load");
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Video loading timed out. Check your internet connection.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                }
                finally
                {
                    try { cts.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error in SetSource: " + ex.Message);
                if (ErrorMessageText != null)
                {
                    ErrorMessageText.Text = "Could not load the video. Please try again.";
                    ErrorMessageText.Visibility = Visibility.Visible;
                }
                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Visibility = Visibility.Visible;
                    if (_playPauseIcon != null)
                    {
                        _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
                    }
                    PlayPauseButton.Opacity = 0.8;
                    PlayPauseButton.IsHitTestVisible = true;
                }
                if (ReplayButton != null)
                {
                    ReplayButton.Visibility = Visibility.Collapsed;
                    ReplayButton.Opacity = 0;
                    ReplayButton.IsHitTestVisible = false;
                }
            }
        }

        // --- Mini-player support -----------------------------------------------------------

        public bool IsPlaying { get { return _isPlaying; } }

        // Clears the "released" latch so a player whose media was released (mini-player closed,
        // page left) can bind a new source again. The video page is cached and reused, so without
        // this the reused page would silently refuse to play anything.
        public void ResetForReuse()
        {
            _playbackReleased = false;
        }

        // Eligible to hand off to the floating mini-player: a video is up, we are not already
        // fullscreen, and there is no separate HLS audio stream to keep in sync (the demuxed path,
        // which is what 1080p uses, is a single player and moves cleanly).
        public bool CanMinimizeToMiniPlayer
        {
            get { return _visibleVideoMediaOpened && !_isFullscreen && !_usingSeparateAudio && !_playbackReleased; }
        }

        // In mini mode the control shows only the video; its own controls and gestures are off, so
        // the mini-player's own pause/close buttons are the only interactive elements.
        public void SetMiniMode(bool on)
        {
            try
            {
                _isMiniMode = on;

                if (ControlsOverlay != null)
                {
                    ControlsOverlay.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
                    ControlsOverlay.IsHitTestVisible = !on;
                }
                if (on && FullscreenTitlePanel != null)
                {
                    FullscreenTitlePanel.Visibility = Visibility.Collapsed;
                }

                UpdateMinimizeButtonVisibility();
                UpdateSubtitleOverlayMetrics();

                // Stop the auto-hide timer poking the (hidden) controls while minimized.
                if (on)
                {
                    _autoHideTimer?.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: SetMiniMode failed - " + ex.Message);
            }
        }

        public void Play()
        {
            try
            {
                if (MediaPlayer?.MediaPlayer == null)
                {
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Could not play the video. The player is not initialized.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                    return;
                }

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Play() called.");
                SetPlaybackUiPlaying();

                if (_usingSeparateAudio)
                {
                    var targetPosition = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                    SetSeparateAudioPosition(targetPosition, true);
                }
                else
                {
                    MediaPlayer.MediaPlayer.Play();
                }

                if (!_videoLoaded) _videoLoaded = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer: Error in Play() - {ex.Message}");
            }
        }

        private void AlignSeparateAudioToVideoPositionWhilePaused(TimeSpan position)
        {
            try
            {
                if (!_usingSeparateAudio)
                {
                    return;
                }

                var videoPlayer = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                var audioPlayer = GetSeparateAudioPlayer();
                if (audioPlayer == null || audioPlayer.PlaybackSession == null)
                {
                    return;
                }

                if (position < TimeSpan.Zero)
                {
                    position = TimeSpan.Zero;
                }

                _isSeparateAudioSeekPending = true;
                try
                {
                    if (videoPlayer != null && videoPlayer.PlaybackSession != null)
                    {
                        audioPlayer.PlaybackSession.PlaybackRate = videoPlayer.PlaybackSession.PlaybackRate;
                    }

                    audioPlayer.PlaybackSession.Position = position;
                }
                catch { }
                finally
                {
                    _isSeparateAudioSeekPending = false;
                }
            }
            catch { }
        }

        public void Pause()
        {
            try
            {
                if (IsSeparateAudioAutoplayPauseBlocked())
                {
                    System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Pause() skipped because separate audio autoplay is pending.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Pause() called.");
                SetPlaybackUiPaused();
                _isSyncingSeparateAudio = false;
                StopSeparateAudioSyncTimer();
                CancelPendingAudioResume();
                CancelSeparateAudioBufferRecovery();
                CancelSeparateAudioPostStartWatchdog();

                TimeSpan pausePosition = TimeSpan.Zero;
                if (MediaPlayer?.MediaPlayer != null)
                {
                    try { pausePosition = MediaPlayer.MediaPlayer.PlaybackSession.Position; } catch { }
                    try { MediaPlayer.MediaPlayer.Pause(); } catch { }
                }

                if (_usingSeparateAudio)
                {
                    var audioPlayer = GetSeparateAudioPlayer();
                    if (audioPlayer != null)
                    {
                        try { audioPlayer.Pause(); } catch { }
                    }

                    // Do not re-seek the visible HLS video on pause. Some UWP HLS builds
                    // rebuffer after that and need several Play/Pause clicks to recover.
                    // Keep video as the master and align only the separate audio carrier.
                    AlignSeparateAudioToVideoPositionWhilePaused(pausePosition);
                }

                SetPlayPauseIcon(false);

                if (_updateTimer != null)
                {
                    _updateTimer.Stop();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Error in Pause - " + ex.Message);
            }
        }

        public void Stop()
        {
            _isPlaying = false;
            SetPlayPauseIcon(false);
            UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Stopped);
            CancelPendingAudioResume();
            CancelSeparateAudioBufferRecovery();
            CancelSeparateAudioPostStartWatchdog();
            if (MediaPlayer?.MediaPlayer != null)
            {
                try { MediaPlayer.MediaPlayer.Pause(); MediaPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero; } catch { }
            }

            var audioPlayer = GetSeparateAudioPlayer();
            if (audioPlayer != null)
            {
                try { audioPlayer.Pause(); audioPlayer.PlaybackSession.Position = TimeSpan.Zero; } catch { }
            }

            CancelPendingAudioResume();
            StopSeparateAudioSyncTimer();
            _pendingAutoPlayAfterSeparateAudioReady = false;
            _playImmediatelyAfterSeparateAudioOpened = false;
            _separateAudioAutoplayPauseBlockUntilUtc = DateTime.MinValue;

            if (_playPauseIcon != null)
            {
                _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
            }
            if (ProgressSlider != null) ProgressSlider.Value = 0;
            _updateTimer?.Stop();
        }

        private async void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            _visibleVideoMediaOpened = true;
            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Media opened successfully");

            // MediaPlayer.MediaOpened fires on a background thread. The body below touches
            // XAML UI (ProgressSlider, ReplayButton, PlayPauseButton), which throws a
            // wrong-thread exception and crashes the process if run off the UI thread.
            // Marshal to the dispatcher, mirroring AudioPlayer_MediaOpened.
            try
            {
                var dispatcher = this.Dispatcher;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => ApplyVisibleMediaOpened());
                }
                else
                {
                    ApplyVisibleMediaOpened();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: MediaOpened handling failed - " + ex.Message);
            }
        }

        private void ApplyVisibleMediaOpened()
        {
            // The new source is ready — unlock play/seek and hide the spinner.
            EndSourceLoading();

            // A fresh source resets the rate to 1x. Re-apply the user's chosen speed, but only on
            // a non-demuxed source — the demuxer can't exceed 1x, and SetDemuxedSource already
            // forces it back to 1x.
            if (_desiredPlaybackRate > 0 && Math.Abs(_desiredPlaybackRate - 1.0) > 0.001
                && _demuxedSource == null)
            {
                try
                {
                    MediaPlayer.MediaPlayer.PlaybackSession.PlaybackRate = _desiredPlaybackRate;
                    System.Diagnostics.Debug.WriteLine("[Player] Re-applied rate " + _desiredPlaybackRate + " on media open");
                }
                catch { }
            }

            UpdateSystemMediaDisplay();
            if (_isPlaying)
            {
                SetPlayPauseIcon(true);
                UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Playing);
            }
            // Update time display when media is opened
            UpdateTimeDisplay();
            
            // Set slider maximum to video duration in seconds
            if (MediaPlayer.MediaPlayer != null)
            {
                // Use parsed duration from API if available, otherwise use MediaPlayer's NaturalDuration
                TimeSpan duration;
                if (_parsedDuration > TimeSpan.Zero)
                {
                    duration = _parsedDuration;
                }
                else
                {
                    duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                }
                
                if (duration.TotalSeconds > 0)
                {
                    ProgressSlider.Maximum = duration.TotalSeconds;
                    System.Diagnostics.Debug.WriteLine($"CustomVideoPlayer: Set slider maximum to {duration.TotalSeconds} seconds");
                }
            }
            
            // Mark video as loaded and start auto-hide behavior
            _videoLoaded = true;
            
            // Ensure replay button is hidden when media opens
            if (ReplayButton != null)
            {
                ReplayButton.Visibility = Visibility.Collapsed;
                ReplayButton.Opacity = 0;
                ReplayButton.IsHitTestVisible = false;
            }
            
            // Ensure play button is visible when media opens
            if (PlayPauseButton != null)
            {
                PlayPauseButton.Visibility = Visibility.Visible;
                PlayPauseButton.Opacity = 0.8;
                PlayPauseButton.IsHitTestVisible = true;
            }
            
            // Start the auto-hide timer now that video is loaded
            StartAutoHideTimer();
        }

        private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            var forwardedArgs = new CustomVideoPlayerMediaFailedEventArgs(args);
            try
            {
                var handler = MediaFailed;
                if (handler != null)
                {
                    handler(this, forwardedArgs);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: MediaFailed subscriber threw - " + ex.Message);
            }

            if (forwardedArgs.Handled)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Media failure handled by parent.");
                // The parent takes over (e.g. decoder fallback) — release the loading lock so the
                // controls do not stay stuck behind the spinner.
                var unlock = Dispatcher.RunAsync(
                    Windows.UI.Core.CoreDispatcherPriority.Normal,
                    () => EndSourceLoading()
                );
                return;
            }

            var error = args.Error;
            var errorMessage = args.ErrorMessage;
            var _ = Dispatcher.RunAsync(
                Windows.UI.Core.CoreDispatcherPriority.Normal,
                () => HandleUnhandledMediaFailure(error, errorMessage)
            );
        }

        private void HandleUnhandledMediaFailure(MediaPlayerError error, string errorMessage)
        {
            // Source failed — do not leave the controls locked behind the spinner.
            EndSourceLoading();

            _visibleVideoMediaOpened = false;
            _isPlaying = false;
            UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Stopped);
            PauseSeparateAudio();
            System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Media failed - " + errorMessage);
            System.Diagnostics.Debug.WriteLine("Error: " + error);
            
            // Handle specific error types
            switch (error)
            {
                case Windows.Media.Playback.MediaPlayerError.SourceNotSupported:
                    System.Diagnostics.Debug.WriteLine("Source not supported - possibly expired cookies or unsupported format");
                    // Display user-friendly error message
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Could not load the video. The link may have expired or the format is not supported.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                    break;
                case Windows.Media.Playback.MediaPlayerError.NetworkError:
                    System.Diagnostics.Debug.WriteLine("Network error occurred");
                    // Display user-friendly error message
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Network error. Check your internet connection.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                    break;
                case Windows.Media.Playback.MediaPlayerError.DecodingError:
                    System.Diagnostics.Debug.WriteLine("Decode error occurred - possible video rendering issue on mobile");
                    // This is likely the issue on mobile devices where video isn't visible but audio works
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Video playback error. Try selecting a lower quality.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                    break;
                default:
                    System.Diagnostics.Debug.WriteLine("Unknown error occurred");
                    // Display generic error message
                    if (ErrorMessageText != null)
                    {
                        ErrorMessageText.Text = "Could not play the video.";
                        ErrorMessageText.Visibility = Visibility.Visible;
                    }
                    break;
            }
            
            // Mark video as loaded even on failure to allow normal UI behavior
            _videoLoaded = true;
            
            // Hide loading indicators and show play button if any
            if (PlayPauseButton != null)
            {
                PlayPauseButton.Visibility = Visibility.Visible;
                if (_playPauseIcon != null)
                {
                    _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
                }
                PlayPauseButton.Opacity = 0.8;
                PlayPauseButton.IsHitTestVisible = true;
            }
            
            // Ensure replay button is hidden on failure
            if (ReplayButton != null)
            {
                ReplayButton.Visibility = Visibility.Collapsed;
                ReplayButton.Opacity = 0;
                ReplayButton.IsHitTestVisible = false;
            }
            
            // Stop update timer
            if (_updateTimer != null)
            {
                _updateTimer.Stop();
            }
        }

        // Method to refresh/reload the current video
        public void RefreshVideo()
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Refreshing video");
                
                // Hide any previous error messages
                if (ErrorMessageText != null)
                {
                    ErrorMessageText.Visibility = Visibility.Collapsed;
                }
                
                // Reset video loaded state
                _videoLoaded = false;
                
                // Reset slider
                ProgressSlider.Value = 0;
                ProgressSlider.Maximum = 100;
                
                // Hide replay button and show play button during refresh
                if (ReplayButton != null)
                {
                    ReplayButton.Visibility = Visibility.Collapsed;
                    ReplayButton.Opacity = 0;
                    ReplayButton.IsHitTestVisible = false;
                }
                if (PlayPauseButton != null)
                {
                    PlayPauseButton.Visibility = Visibility.Visible;
                    if (_playPauseIcon != null)
                    {
                        _playPauseIcon.Source = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/player/play.png"));
                    }
                    PlayPauseButton.Opacity = 0.8;
                    PlayPauseButton.IsHitTestVisible = true;
                }
                
                // Stop any current playback
                _isPlaying = false;
                UpdateSystemMediaPlaybackStatus(MediaPlaybackStatus.Stopped);
                if (MediaPlayer.MediaPlayer != null)
                {
                    MediaPlayer.MediaPlayer.Pause();
                    MediaPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                }
                if (_usingSeparateAudio)
                {
                    SetSeparateAudioPosition(TimeSpan.Zero, false);
                }
                
                _updateTimer.Stop();
                
                // Reset position tracking
                _lastPosition = TimeSpan.Zero;
                _positionStuckCounter = 0;
                
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: Video refresh completed, UI reset");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Error in RefreshVideo: " + ex.Message);
            }
        }



        // IDisposable implementation
        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            // Unsubscribe from window size changes
            Window.Current.SizeChanged -= Current_SizeChanged;
            
            // Dispose of timers
            if (_updateTimer != null)
            {
                _updateTimer.Tick -= UpdateTimer_Tick;
                _updateTimer.Stop();
                _updateTimer = null;
            }

            if (_audioSyncTimer != null)
            {
                _audioSyncTimer.Tick -= AudioSyncTimer_Tick;
                _audioSyncTimer.Stop();
                _audioSyncTimer = null;
            }
            
            // Dispose of controls timer
            if (_controlsTimer != null)
            {
                _controlsTimer.Tick -= ControlsTimer_Tick;
                _controlsTimer.Stop();
                _controlsTimer = null;
            }
            
            // Dispose of auto-hide timer
            if (_autoHideTimer != null)
            {
                _autoHideTimer.Tick -= AutoHideTimer_Tick;
                _autoHideTimer.Stop();
                _autoHideTimer = null;
            }
            
            // Clean up popup if it exists
            if (_fullscreenPopup != null)
            {
                // Remove event handlers first
                if (_fullscreenGrid != null)
                {
                    _fullscreenGrid.Tapped -= FullscreenGrid_Tapped;
                }
                _fullscreenPopup.IsOpen = false;
                _fullscreenPopup = null;
                _fullscreenGrid = null;
            }
            
            // Clean up fullscreen settings panel
            _fullscreenSettingsPanel = null;
            
            // Unsubscribe from media events
            if (MediaPlayer?.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                MediaPlayer.MediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
                DetachVisibleVideoPlaybackStateHandler();
            }

            if (_systemMediaControls != null)
            {
                _systemMediaControls.ButtonPressed -= SystemMediaControls_ButtonPressed;
                _systemMediaControls.PlaybackStatus = MediaPlaybackStatus.Closed;
                _systemMediaControls.IsEnabled = false;
            }

            ResetSeparateAudio();
            ReleaseMediaPlayers();

            _isDisposed = true;
        }

        // Stop playback and free the media players + their sources. Call this when leaving the
        // page. Safe to call more than once.
        public void StopAndReleasePlayback()
        {
            // Latched before the teardown: a quality switch started on this page may still be
            // fetching, and when it finishes it would happily build a fresh source — and a fresh
            // separate audio player — on a control the user has already navigated away from.
            // That is the old video whose audio starts playing underneath the new one.
            _playbackReleased = true;

            try { Stop(); } catch { }
            try { ResetSeparateAudio(); } catch { }
            ReleaseMediaPlayers();
        }

        // Fully tear down both players. Without this the background-enabled MediaPlayer keeps
        // decoding after the page is gone: old videos kept playing over new ones and survived
        // closing the app, and the leaked players (each still pulling DASH fragments) piled up
        // until the process died with no managed exception.
        private void ReleaseMediaPlayers()
        {
            try
            {
                var player = MediaPlayer == null ? null : MediaPlayer.MediaPlayer;
                if (player != null)
                {
                    try { player.Pause(); } catch { }

                    // Disposing the source closes our MediaStreamSource, which stops the
                    // demuxer's background prefetching (see DashMediaSource.OnClosed).
                    var source = player.Source as IDisposable;
                    try { player.Source = null; } catch { }
                    if (source != null) { try { source.Dispose(); } catch { } }

                    try { MediaPlayer.SetMediaPlayer(null); } catch { }
                    try { player.Dispose(); } catch { }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: ReleaseMediaPlayers (video) failed - " + ex.Message);
            }

            try
            {
                var audioPlayer = _separateAudioPlayer;
                if (audioPlayer != null)
                {
                    try { audioPlayer.Pause(); } catch { }
                    var audioSource = audioPlayer.Source as IDisposable;
                    try { audioPlayer.Source = null; } catch { }
                    if (audioSource != null) { try { audioSource.Dispose(); } catch { } }
                    try { audioPlayer.Dispose(); } catch { }
                    _separateAudioPlayer = null;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomVideoPlayer: ReleaseMediaPlayers (audio) failed - " + ex.Message);
            }
        }
    }
}
