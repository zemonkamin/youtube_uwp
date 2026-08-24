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

using Windows.UI.Xaml.Shapes;

namespace YouTube
{
    public sealed partial class Video : Page
    {
        private enum UserVideoRating
        {
            None,
            Like,
            Dislike
        }

        private enum ChannelSubscriptionState
        {
            Unknown,
            NotSubscribed,
            Subscribed
        }

        private enum ChannelNotificationState
        {
            Unknown,
            Default,
            All,
            None
        }

        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string PreferredVideoQualitySettingKey = "PreferredVideoQuality";
        private readonly HttpClient httpClient = new HttpClient();
        private string currentVideoId;
        private string currentChannelId = string.Empty;
        private string currentChannelName = string.Empty;
        private string currentQualityTag = string.Empty;
        private double currentSpeed = 1.0;
        private readonly List<PlayerFormatModel> availableFormats = new List<PlayerFormatModel>();
        private readonly object _androidPlayerRequestGate = new object();
        private Task<string> _androidPlayerRequestTask;
        private string _androidPlayerRequestVideoId = string.Empty;
        private string _lastAndroidPlayerJson = string.Empty;
        private string _lastAndroidPlayerVideoId = string.Empty;
        private readonly object _playerScriptGate = new object();
        private Task<string> _playerScriptRequestTask;
        private string _playerScriptRequestUrl = string.Empty;
        private string _lastPlayerScript = string.Empty;
        private string _lastPlayerScriptUrl = string.Empty;
        private string _lastPlayerJson = string.Empty;
        private readonly HashSet<int> _excludedH264VideoOnlyItags = new HashSet<int>();
        private int _currentH264VideoOnlyItag = -1;
        // Actual short-side tier selected for playback. youtube-ios keeps this separately from
        // the user's pick so Auto can be displayed as e.g. "Auto · 1080p".
        private int _readyHeight;
        private bool _qualityChangeInProgress;
        private string _historyReportedVideoId;
        // One Auto-mode retry at max quality per video, so a dead video cannot loop.
        private bool _autoQualityFallbackAttempted;
        // Set when this page has handed its player to the floating mini-player, so teardown leaves it alone.
        private bool _minimizedToMiniPlayer;
        // Set just before GoForward so OnNavigatedTo reattaches the player instead of reloading.
        private bool _restoringFromMiniPlayer;
        // True once the user picks a quality in this video's menu (including "Auto"); until then
        // the effective quality comes from the Settings default.
        private bool _qualityExplicitlyChosen;
        // Playlist / mix ("jam") watch context.
        private string currentPlaylistId = string.Empty;
        private string _playlistQueueTitle = string.Empty;
        private int _playlistCurrentIndex = -1;
        private readonly List<RelatedVideoCardItem> _playlistQueue = new List<RelatedVideoCardItem>();
        // Serializes playlist next/previous/auto-advance requests. This matters in
        // background/mini-player mode where MediaEnded and SMTC can arrive almost together.
        private bool _playlistSwitchInProgress;
        // Accumulated mix ("jam") history. Static on purpose: every video opens a brand-new
        // Video page, so an instance field would lose the list on each navigation.
        private static string _jamQueuePlaylistId;
        private static readonly List<RelatedVideoCardItem> _jamQueueItems = new List<RelatedVideoCardItem>();
        private bool _decoderFallbackInProgress;
        private int _decoderFallbackAttemptsForCurrentQuality;
        private const int MaxVideoOnlyDecodeFallbackAttempts = 5;
        private bool _manualDecodeFailureStopScheduled;
        private bool _playbackErrorDialogOpen;
        private bool _wasPortrait = true;
        // Windows Phone raises several SizeChanged events while rotating. Reparenting the player
        // into its fullscreen Popup on the first intermediate size is unreliable, so only the
        // latest rotation event is allowed to change fullscreen state after a short settle delay.
        private const string AutoFullscreenLandscapeSettingKey = "AutoFullscreenLandscape";
        private int _orientationFullscreenGeneration;
        private bool _windowSizeChangedSubscribed;
        private bool _updatingLandscapeActionLabels;
        private double _actionsWidthAllLabels = double.NaN;
        private double _actionsWidthWithoutDownloadLabel = double.NaN;
        private double _actionsWidthWithoutSaveLabel = double.NaN;
        private double _actionsWidthIconsOnly = double.NaN;
        private string _currentVideoDescription = string.Empty;
        private string _currentVideoThumbnailUrl = string.Empty;
        private string _currentChannelThumbnailUrl = string.Empty;
        private bool _offlineMode;
        private DownloadedVideoItem _offlineItem;
        private const double RelatedThumbnailAspectRatio = 16.0 / 9.0;
        private const double DefaultVideoPlayerAspectRatio = 16.0 / 9.0;
        private double _currentVideoPlayerAspectRatio = DefaultVideoPlayerAspectRatio;
        private const double MinVideoPlayerHeight = 211.0;
        // youtube-ios uses deviceMaxHeight as the cap for Auto. Its normal-device cap is
        // 1080p; unsupported H.264 itags are excluded and retried at the next lower tier.
        private const int AutomaticVideoQualityCap = 1080;
        // youtube-ios lets SABR stay below the device tier when the connection cannot sustain
        // it. The UWP demuxer has fixed-format URLs, so Auto measures the selected CDN host
        // before opening the stream and keeps a safety margin for audio and throughput jitter.
        private const int AutoNetworkProbeBytes = 256 * 1024;
        private const double AutoNetworkSafetyFactor = 0.72;
        private const int AutoAudioBandwidthReserve = 192000;
        private double _autoNetworkBitsPerSecond;
        private DateTimeOffset _autoNetworkEstimateTime;
        private const int MaxRelatedVideosToShow = 32;
        private const int MaxRelatedJsonNodesToScan = 60000;
        private const double TitleDescriptionSkeletonDefaultAspectRatio = 1440.0 / 889.0;
        private double _relatedSkeletonCardAspectRatio = 16.0 / 9.0;
        private double _titleDescriptionSkeletonAspectRatio = TitleDescriptionSkeletonDefaultAspectRatio;
        private UserVideoRating _currentUserRating = UserVideoRating.None;
        private bool _ratingRequestInProgress;
        private int _ratingStateGeneration;
        private ChannelSubscriptionState _currentSubscriptionState = ChannelSubscriptionState.Unknown;
        private ChannelNotificationState _currentNotificationState = ChannelNotificationState.Default;
        private bool _subscriptionRequestInProgress;
        private int _subscriptionStateGeneration;
        private string _subscribeParams = string.Empty;
        private string _unsubscribeParams = string.Empty;
        private string _subscribeClickTrackingParams = string.Empty;
        private string _unsubscribeClickTrackingParams = string.Empty;
        private const string DefaultSubscribeParams = "CgIIAxgA";
        private const string DefaultUnsubscribeParams = "EgIIAxgA";
        private const string YouTubeDataApiBaseUrl = "https://www.googleapis.com/youtube/v3/";
        private const string InnertubeTvClientName = "TVHTML5";
        private const string InnertubeTvClientVersion = "7.20260429.11.00";
        private const string InnertubeTvClientHeaderName = "85";
        private const string InnertubeFallbackVisitorData = "CgtjTS00dGRYTXhBOCif8OnOBjIoCgJQTBIiEh4SHAsMDg8QERITFBUWFxgZGhscHR4fICEiIyQlJicgSA%3D%3D";
        private const string InnertubeMwebClientName = "MWEB";
        private const string InnertubeMwebClientVersion = "2.20251222.01.00";
        private const string InnertubeMwebClientHeaderName = "2";
        private const string InnertubeMwebUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
        private const string InnertubeTvUserAgent = "Mozilla/5.0 (Web0S; Linux; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.79 Safari/537.36 YouTube/7.20260429.11.00";

        // ANDROID_VR (Oculus Quest) player client — ported from MeeTube (clientconfig.cpp /
        // contextbuilder.cpp). WITH a fresh server-issued visitorData this client returns
        // ready-to-play progressive AND adaptive URLs: server-signed (sig/lsig applied),
        // no &n=, no &pot=, no client-side signature decipher. WithOUT visitorData YouTube
        // now answers per-video "Sign in to confirm you're not a bot" (LOGIN_REQUIRED).
        private const string InnertubeAndroidVrClientName = "ANDROID_VR";
        private const string InnertubeAndroidVrClientVersion = "1.65.10";
        private const string InnertubeAndroidVrClientHeaderName = "28";
        private const string InnertubeAndroidVrUserAgent = "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; eureka-user Build/SQ3A.220605.009.A1) gzip";

        // Playback client chain. Keep these values and the request order in sync with
        // youtube-ios/src/net/YTApi.m; googlevideo signs the resulting URLs for the client
        // which requested them, so body, X-YouTube-Client-* and User-Agent must agree.
        private const string InnertubeVisionClientName = "VISIONOS";
        private const string InnertubeVisionClientVersion = "1.02";
        private const string InnertubeVisionClientHeaderName = "101";
        private const string InnertubeVisionUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 15_7_3) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.0 Safari/605.1.15";
        private const string InnertubeIosPlayerClientVersion = "20.49.6";
        private const string InnertubeIosPlayerClientHeaderName = "5";
        private const string InnertubeIosPlayerUserAgent = "com.google.ios.youtube/19.16.3 (iPhone16,2; U; CPU iOS 18_0 like Mac OS X)";
        private const string InnertubePlaybackTvClientVersion = "7.20250209.19.00";
        private const string InnertubePlaybackTvLegacyClientVersion = "7.20220918.10.00";
        private const string InnertubePlaybackTvUserAgent = "Mozilla/5.0 (SMART-TV; LINUX; Tizen 5.0) AppleWebKit/537.36 (KHTML, like Gecko) Version/5.0 TV Safari/537.36";

        // Server-issued visitorData for the current session. MeeTube seeds this from the
        // first youtubei response's responseContext and reuses it; ANDROID_VR needs it to
        // clear the 2026-07 anti-bot wall. Captured opportunistically, fetched on demand.
        private string _sessionVisitorData = string.Empty;
        private readonly object _visitorDataGate = new object();
        private Task<string> _visitorDataFetchTask;
        private string _playbackMediaUserAgent = InnertubeAndroidVrUserAgent;
        private string _lastAndroidPlayerUserAgent = InnertubeAndroidVrUserAgent;

        private const string DescriptionUrlPattern = @"(http|https)://[\w\-_]+(\.[\w\-_]+)+([\w\-\.,@?^=%&:/~\+#]*[\w\-\@?^=%&/~\+#])?";
        private const string DescriptionTimecodePattern = @"(?<![\d:])(?:\d{1,2}:)?\d{1,2}:\d{2}(?![\d:])";
        private readonly List<YouTube.CustomVideoPlayer.VideoChapterMarker> _descriptionChapterMarkers =
            new List<YouTube.CustomVideoPlayer.VideoChapterMarker>();

        // Description bottom sheet fields
        private double _descriptionSheetHiddenOffset = 400;
        private double _descriptionInitialY;
        private double _descriptionInitialTransformY;
        private bool _descriptionIsDragging;
        private double _lastKnownPlayerHeight;

        // Comments bottom sheet fields
        private double _commentsInitialY;
        private double _commentsInitialTransformY;
        private bool _commentsIsDragging;
        private List<CommentItem> _currentComments = new List<CommentItem>();
        private bool _landscapeDescriptionExpanded;
        private bool _landscapeDescriptionNeedsToggle;

        // Share bottom sheet fields
        private double _shareInitialY;
        private double _shareInitialTransformY;
        private bool _shareIsDragging;
        private double _sharePopupInitialY;
        private double _sharePopupInitialTransformY;
        private bool _sharePopupIsDragging;
        private bool _shareSheetIsOpen;
        private bool _shareWithTimestamp;
        private Storyboard _shareTimeToggleStoryboard;
        private int _shareTimeToggleAnimationGeneration;

        // Save-to-playlist bottom sheet fields
        private readonly ObservableCollection<SavePlaylistItemViewModel> _savePlaylistItems =
            new ObservableCollection<SavePlaylistItemViewModel>();
        private double _saveInitialY;
        private double _saveInitialTransformY;
        private bool _saveIsDragging;
        private bool _saveSheetIsOpen;
        private int _savePlaylistLoadGeneration;
        private bool _mainSaveStateSupportedByNext;

        // Player settings bottom sheet fields
        private double _settingsInitialY;
        private double _settingsInitialTransformY;
        private bool _settingsIsDragging;
        private Popup _fullscreenSettingsPopup;
        private Grid _fullscreenSettingsPopupRoot;
        private Panel _settingsBottomSheetOriginalParent;
        private int _settingsBottomSheetOriginalIndex = -1;

        // Subscription / notifications bottom sheet fields
        private double _subscriptionMenuInitialY;
        private double _subscriptionMenuInitialTransformY;
        private bool _subscriptionMenuIsDragging;


        // Display request to prevent sleep mode
        private Windows.System.Display.DisplayRequest _displayRequest = null;

        private sealed class RatingLoadResult
        {
            public bool Found { get; set; }
            public UserVideoRating Rating { get; set; }
            public string Source { get; set; }
        }

        private sealed class SubscriptionLoadResult
        {
            public bool Found { get; set; }
            public ChannelSubscriptionState State { get; set; }
            public ChannelNotificationState NotificationState { get; set; }
            public string SubscribeParams { get; set; }
            public string UnsubscribeParams { get; set; }
            public string SubscribeClickTrackingParams { get; set; }
            public string UnsubscribeClickTrackingParams { get; set; }
            public string ChannelId { get; set; }
            public string Source { get; set; }
        }

        private sealed class RatingToggleMatch
        {
            public UserVideoRating Rating { get; set; }
            public bool IsToggled { get; set; }
            public string Path { get; set; }
        }

        public sealed class SavePlaylistItemViewModel : INotifyPropertyChanged
        {
            private bool _isSaved;
            private bool _isBusy;

            public SavePlaylistItemViewModel(PlaylistItem playlist, bool isSaved)
            {
                Playlist = playlist ?? new PlaylistItem();
                _isSaved = isSaved;
            }

            public PlaylistItem Playlist { get; private set; }
            public string PlaylistId { get { return Playlist.PlaylistId ?? string.Empty; } }
            public string Title { get { return Playlist.Title ?? string.Empty; } }
            public string ThumbnailUrl { get { return Playlist.ThumbnailUrl ?? string.Empty; } }
            public string MetadataText { get { return Playlist.MetadataText ?? string.Empty; } }
            public string VideoCountText { get { return Playlist.VideoCountText ?? string.Empty; } }
            public Visibility VideoCountVisibility { get { return Playlist.VideoCountVisibility; } }
            public SolidColorBrush AccentBackColor { get { return Playlist.AccentBackColor; } }
            public SolidColorBrush AccentFrontColor { get { return Playlist.AccentFrontColor; } }
            public bool IsSaved { get { return _isSaved; } }
            public bool IsEnabled { get { return !_isBusy; } }
            public double RowOpacity { get { return _isBusy ? 0.55 : 1.0; } }
            public string IconPath { get { return _isSaved ? "Assets/save_clicked.png" : "Assets/save.png"; } }

            public event PropertyChangedEventHandler PropertyChanged;

            public void SetBusy(bool value)
            {
                if (_isBusy == value)
                {
                    return;
                }
                _isBusy = value;
                RaisePropertyChanged("IsEnabled");
                RaisePropertyChanged("RowOpacity");
            }

            public void SetSaved(bool value)
            {
                if (_isSaved == value)
                {
                    return;
                }
                _isSaved = value;
                RaisePropertyChanged("IsSaved");
                RaisePropertyChanged("IconPath");
            }

            private void RaisePropertyChanged(string propertyName)
            {
                var handler = PropertyChanged;
                if (handler != null)
                {
                    handler(this, new PropertyChangedEventArgs(propertyName));
                }
            }
        }

        public Video()
        {
            this.InitializeComponent();
            SavePlaylistsList.ItemsSource = _savePlaylistItems;
            SaveActionText.Text = Localization.GetString("Save");
            DownloadActionText.Text = Localization.GetString("Download");
            DownloadQualityTitleText.Text = Localization.GetString("DownloadQualityTitle");
            DownloadUnavailableText.Text = Localization.GetString("DownloadUnavailable");
            SaveSheetTitleText.Text = Localization.GetString("SelectPlaylist");
            SavePlaylistsEmptyText.Text = Localization.GetString("NoData");
            UpdateLandscapeDescriptionExpansionState();
            this.Loaded += Video_Loaded;
            this.Unloaded += Video_Unloaded;

            // Cached from the start so that expanding back from the mini-player (GoForward) reuses
            // THIS exact instance with all its state. Setting it late (right before GoBack) does
            // not cache the page — the frame decides caching using the mode set ahead of time.
            // Because the instance is now reused, leaving the page only STOPS playback; it never
            // disposes the control (see the teardown below), or reuse would get a dead player.
            this.NavigationCacheMode = Windows.UI.Xaml.Navigation.NavigationCacheMode.Required;

            // Window events are attached from Loaded and removed from Unloaded. This page is
            // NavigationCacheMode.Required, so the constructor only runs once while the visual tree
            // can be loaded/unloaded many times (especially around the mini-player).
        }

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
            ApplyCurrentVideoTheme();

            // A cached Video page is loaded again after returning from the mini-player, so
            // restore the window handler every time and baseline orientation without causing an
            // artificial fullscreen transition merely because the page became visible.
            SubscribeWindowSizeChanged();
            ResetOrientationTrackingToCurrentWindow();
            UpdateVideoPlayerLayout();

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

        private async void Window_SizeChanged(object sender, Windows.UI.Core.WindowSizeChangedEventArgs e)
        {
            UpdateFullscreenSettingsPopupBounds(e.Size);
            if (SettingsBottomSheetPanel != null
                && SettingsBottomSheetPanel.Visibility == Visibility.Visible)
            {
                UpdateSettingsBottomSheetHeight();
            }

            if (_minimizedToMiniPlayer)
            {
                return;
            }

            var isPortraitAtEvent = IsCurrentViewPortrait();

            // IMPORTANT: decide whether this is a real orientation transition BEFORE
            // UpdateVideoPlayerLayout() updates _wasPortrait. Ordinary SizeChanged events in
            // landscape (fullscreen exit, system bars, popup/layout changes, etc.) must not
            // trigger auto-fullscreen again.
            var orientationChanged = isPortraitAtEvent != _wasPortrait;
            var wasPortraitBeforeChange = _wasPortrait;

            UpdateVideoPlayerLayout();

            if (!orientationChanged)
            {
                return;
            }

            var generation = ++_orientationFullscreenGeneration;

            // WP10M emits a few intermediate sizes while physically rotating. Act only after the
            // final orientation has settled, and only for the transition captured above.
            await Task.Delay(180);

            if (generation != _orientationFullscreenGeneration
                || _minimizedToMiniPlayer
                || CustomVideoPlayer == null)
            {
                return;
            }

            try
            {
                var isPortraitNow = IsCurrentViewPortrait();

                // Ignore a stale transition if the phone rotated back during the delay.
                if (isPortraitNow != isPortraitAtEvent)
                {
                    return;
                }

                if (wasPortraitBeforeChange
                    && !isPortraitNow
                    && IsAutoFullscreenLandscapeEnabled()
                    && !CustomVideoPlayer.IsFullscreen)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Portrait -> landscape: entering fullscreen once");
                    CustomVideoPlayer.ToggleFullscreen();
                }
                else if (!wasPortraitBeforeChange
                    && isPortraitNow
                    && CustomVideoPlayer.IsFullscreen)
                {
                    // Landscape -> portrait closes fullscreen only at the orientation transition.
                    // A manual fullscreen exit while still landscape is left alone.
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Landscape -> portrait: leaving fullscreen once");
                    CustomVideoPlayer.ToggleFullscreen();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Orientation fullscreen transition failed: " + ex.Message);
            }
        }

        private void VideoPlayerContainer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateVideoPlayerHeight(e.NewSize.Width);
        }

        // Decoded at card width with a maxres->API-thumbnail fallback; see ThumbnailImageLoader
        // for why a Source binding was replaced by per-item assignment.
        private void RelatedThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Windows.UI.Xaml.Controls.Image;
            if (image == null)
            {
                return;
            }

            var item = image.DataContext as RelatedVideoCardItem;
            if (item == null || string.IsNullOrWhiteSpace(item.large_thumbnail))
            {
                image.Source = null;
                return;
            }

            VideoThumbnailController.Assign(image, item.video_id, item.thumbnail, 360);
        }

        private void RelatedChannelIcon_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var image = sender as Image;
            var item = args.NewValue as RelatedVideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.channel_thumbnail);
        }

        private void RelatedChannelIcon_Loaded(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            var item = image == null ? null : image.DataContext as RelatedVideoCardItem;
            ChannelIconController.Assign(image, item == null ? string.Empty : item.channel_thumbnail);
        }

        private void CommentAuthorImage_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var ellipse = sender as Ellipse;
            var brush = ellipse == null ? null : ellipse.Fill as ImageBrush;
            var item = args.NewValue as CommentItem;
            ChannelIconController.AssignAlways(brush, item == null ? string.Empty : item.AuthorThumbnail);
        }

        private void CommentAuthorImage_Loaded(object sender, RoutedEventArgs e)
        {
            var ellipse = sender as Ellipse;
            var brush = ellipse == null ? null : ellipse.Fill as ImageBrush;
            var item = ellipse == null ? null : ellipse.DataContext as CommentItem;
            ChannelIconController.AssignAlways(brush, item == null ? string.Empty : item.AuthorThumbnail);
        }

        private void PlaylistQueueThumbnail_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            var rectangle = sender as Rectangle;
            AssignPlaylistQueueThumbnail(rectangle, args.NewValue as RelatedVideoCardItem);
        }

        private void PlaylistQueueThumbnail_Loaded(object sender, RoutedEventArgs e)
        {
            var rectangle = sender as Rectangle;
            AssignPlaylistQueueThumbnail(rectangle, rectangle == null ? null : rectangle.DataContext as RelatedVideoCardItem);
        }

        private static void AssignPlaylistQueueThumbnail(Rectangle rectangle, RelatedVideoCardItem item)
        {
            if (rectangle == null)
            {
                return;
            }

            var brush = rectangle.Fill as ImageBrush;
            if (brush == null)
            {
                brush = new ImageBrush { Stretch = Stretch.UniformToFill };
                rectangle.Fill = brush;
            }

            var token = new object();
            rectangle.Tag = token;
            brush.ImageSource = null;

            if (item == null)
            {
                return;
            }

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(item.large_thumbnail))
            {
                candidates.Add(item.large_thumbnail);
            }
            if (!string.IsNullOrWhiteSpace(item.thumbnail)
                && !candidates.Any(url => string.Equals(url, item.thumbnail, StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(item.thumbnail);
            }

            LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, 0, token);
        }

        private static void LoadPlaylistQueueThumbnailCandidate(
            Rectangle rectangle,
            ImageBrush brush,
            IList<string> candidates,
            int index,
            object token)
        {
            if (rectangle == null || brush == null || !ReferenceEquals(rectangle.Tag, token))
            {
                return;
            }

            if (candidates == null || index >= candidates.Count)
            {
                brush.ImageSource = null;
                return;
            }

            var url = candidates[index];
            if (string.IsNullOrWhiteSpace(url))
            {
                LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
                return;
            }

            try
            {
                var bitmap = new BitmapImage
                {
                    DecodePixelType = DecodePixelType.Logical,
                    DecodePixelWidth = 208
                };
                bitmap.ImageFailed += (bitmapSender, failedArgs) =>
                {
                    if (!ReferenceEquals(rectangle.Tag, token) || !ReferenceEquals(brush.ImageSource, bitmap))
                    {
                        return;
                    }

                    LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
                };
                bitmap.UriSource = new Uri(url, UriKind.Absolute);

                if (ReferenceEquals(rectangle.Tag, token))
                {
                    brush.ImageSource = bitmap;
                }
            }
            catch
            {
                LoadPlaylistQueueThumbnailCandidate(rectangle, brush, candidates, index + 1, token);
            }
        }

        private void RelatedThumbnailHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var thumbnailHost = sender as FrameworkElement;
            if (thumbnailHost == null)
                return;

            var width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var targetHeight = Math.Round(width / RelatedThumbnailAspectRatio);
            if (double.IsNaN(thumbnailHost.Height) || Math.Abs(thumbnailHost.Height - targetHeight) > 0.5)
                thumbnailHost.Height = targetHeight;

            VideoCardController.ApplyResponsiveLayout(
                thumbnailHost,
                IsCurrentViewPortrait());
        }

        private void RelatedSkeletonCardHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var cardHost = sender as FrameworkElement;
            if (cardHost == null)
                return;

            UpdateRelatedSkeletonCardHeight(cardHost, e.NewSize.Width);
        }

        private void TitleDescriptionSkeletonHost_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var host = sender as FrameworkElement;
            if (host == null)
                return;

            UpdateTitleDescriptionSkeletonHeight(host, e.NewSize.Width);
        }

        private void TitleDescriptionSkeletonImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                _titleDescriptionSkeletonAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            }

            var parent = image.Parent as FrameworkElement;
            if (parent != null)
            {
                UpdateTitleDescriptionSkeletonHeight(parent, parent.ActualWidth);
            }
        }

        private void UpdateTitleDescriptionSkeletonHeight(FrameworkElement host, double width)
        {
            if (host == null)
                return;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = host.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var aspectRatio = _titleDescriptionSkeletonAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
                aspectRatio = TitleDescriptionSkeletonDefaultAspectRatio;

            var targetHeight = Math.Round(width / aspectRatio);
            if (double.IsNaN(host.Height) || Math.Abs(host.Height - targetHeight) > 0.5)
                host.Height = targetHeight;
        }

        private void SkeletonCardImage_ImageOpened(object sender, RoutedEventArgs e)
        {
            var image = sender as Image;
            if (image == null)
                return;

            var bitmap = image.Source as BitmapImage;
            if (bitmap != null && bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0)
            {
                _relatedSkeletonCardAspectRatio = (double)bitmap.PixelWidth / bitmap.PixelHeight;
            }

            var parent = image.Parent as FrameworkElement;
            if (parent != null)
            {
                UpdateRelatedSkeletonCardHeight(parent, parent.ActualWidth);
            }
        }

        private void UpdateRelatedSkeletonCardHeight(FrameworkElement cardHost, double width)
        {
            if (cardHost == null)
                return;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                width = cardHost.ActualWidth;

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
                return;

            var aspectRatio = _relatedSkeletonCardAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
                aspectRatio = RelatedThumbnailAspectRatio;

            var targetHeight = Math.Round(width / aspectRatio);
            if (double.IsNaN(cardHost.Height) || Math.Abs(cardHost.Height - targetHeight) > 0.5)
                cardHost.Height = targetHeight;
        }

        private void UpdateTitleDescriptionSkeletonLayout(bool isPortrait)
        {
            if (TitleDescriptionSkeletonImagePart != null)
                TitleDescriptionSkeletonImagePart.Visibility = isPortrait ? Visibility.Visible : Visibility.Collapsed;

            if (TitleDescriptionSkeletonClassicPart != null)
                TitleDescriptionSkeletonClassicPart.Visibility = isPortrait ? Visibility.Collapsed : Visibility.Visible;

            if (isPortrait && TitleDescriptionSkeletonHost != null)
                UpdateTitleDescriptionSkeletonHeight(TitleDescriptionSkeletonHost, TitleDescriptionSkeletonHost.ActualWidth);
        }

        private void ShowRelatedVideos(List<RelatedVideoCardItem> relatedVideos)
        {
            if (RelatedVideosContainer != null)
            {
                RelatedVideosContainer.ItemsSource = relatedVideos;
                RelatedVideosContainer.Visibility = Visibility.Visible;
            }

            if (RelatedVideosContainerVertical != null)
            {
                RelatedVideosContainerVertical.ItemsSource = relatedVideos;
                RelatedVideosContainerVertical.Visibility = Visibility.Visible;
            }

            if (RelatedVideosFallback != null)
                RelatedVideosFallback.Visibility = Visibility.Collapsed;

            if (RelatedVideosFallbackVertical != null)
                RelatedVideosFallbackVertical.Visibility = Visibility.Collapsed;

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void ShowRelatedVideosFallback()
        {
            if (RelatedVideosContainer != null)
            {
                RelatedVideosContainer.ItemsSource = null;
                RelatedVideosContainer.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosContainerVertical != null)
            {
                RelatedVideosContainerVertical.ItemsSource = null;
                RelatedVideosContainerVertical.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosFallback != null)
                RelatedVideosFallback.Visibility = Visibility.Visible;

            if (RelatedVideosFallbackVertical != null)
                RelatedVideosFallbackVertical.Visibility = Visibility.Visible;

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void HideRelatedVideosLoadingPlaceholders()
        {
            if (RelatedVideosContainer != null)
            {
                RelatedVideosContainer.ItemsSource = null;
                RelatedVideosContainer.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosContainerVertical != null)
            {
                RelatedVideosContainerVertical.ItemsSource = null;
                RelatedVideosContainerVertical.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosFallback != null)
                RelatedVideosFallback.Visibility = Visibility.Collapsed;

            if (RelatedVideosFallbackVertical != null)
                RelatedVideosFallbackVertical.Visibility = Visibility.Collapsed;

            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }

            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateVideoPlayerHeight()
        {
            double width = 0;
            if (VideoPlayerContainer != null)
            {
                width = VideoPlayerContainer.ActualWidth;
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                if (PlayerInfoPanel != null)
                {
                    width = PlayerInfoPanel.ActualWidth;
                }
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                width = Window.Current.Bounds.Width;
                if (RelatedColumn != null && RelatedColumn.Width.Value > 0 && Window.Current.Bounds.Width > RelatedColumn.Width.Value)
                {
                    width = Window.Current.Bounds.Width - RelatedColumn.Width.Value;
                }
            }

            UpdateVideoPlayerHeight(width);
        }

        private void UpdateVideoPlayerHeight(double width)
        {
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                return;
            }

            var aspectRatio = _currentVideoPlayerAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            var targetHeight = Math.Round(width / aspectRatio);
            if (targetHeight < MinVideoPlayerHeight)
            {
                targetHeight = MinVideoPlayerHeight;
            }

            if (VideoPlayerContainer != null &&
                (double.IsNaN(VideoPlayerContainer.Height) || Math.Abs(VideoPlayerContainer.Height - targetHeight) > 0.5))
            {
                VideoPlayerContainer.Height = targetHeight;
            }

            if (CustomVideoPlayer != null &&
                (double.IsNaN(CustomVideoPlayer.Height) || Math.Abs(CustomVideoPlayer.Height - targetHeight) > 0.5))
            {
                CustomVideoPlayer.Height = targetHeight;
                CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        private void SetVideoPlayerAspectRatioFromFormat(PlayerFormatModel format)
        {
            if (format == null || format.Width <= 0 || format.Height <= 0)
            {
                return;
            }

            SetVideoPlayerAspectRatio((double)format.Width / format.Height);
        }

        private void SetVideoPlayerAspectRatio(double aspectRatio)
        {
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            // Guard against broken metadata. YouTube mobile/Continuum can report unusual sizes,
            // but real video aspect ratios still stay in a sane range.
            if (aspectRatio < 0.45 || aspectRatio > 3.5)
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            if (Math.Abs(_currentVideoPlayerAspectRatio - aspectRatio) > 0.001)
            {
                _currentVideoPlayerAspectRatio = aspectRatio;
                System.Diagnostics.Debug.WriteLine("[Video] Player aspect ratio set to " + aspectRatio.ToString("0.###"));
            }

            if (_minimizedToMiniPlayer
                && ReferenceEquals(MiniPlayer.ActivePlayer, CustomVideoPlayer))
            {
                MiniPlayer.UpdateAspectRatio(aspectRatio);
            }

            UpdateVideoPlayerHeight();
        }

        private void UpdateVideoPlayerAspectRatioFromFormats(string requestedQualityTag)
        {
            var format = SelectBestVideoFormatForAspectRatio(availableFormats, requestedQualityTag);
            if (format != null)
            {
                SetVideoPlayerAspectRatioFromFormat(format);
            }
            else
            {
                SetVideoPlayerAspectRatio(DefaultVideoPlayerAspectRatio);
            }
        }

        private static PlayerFormatModel SelectBestVideoFormatForAspectRatio(IList<PlayerFormatModel> formats, string requestedQualityTag)
        {
            if (formats == null)
            {
                return null;
            }

            int requestedHeight = ParseInt(requestedQualityTag);
            PlayerFormatModel best = null;

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || !format.HasVideo || format.Width <= 0 || format.Height <= 0)
                {
                    continue;
                }

                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                if (best == null || format.QualityTier > best.QualityTier)
                {
                    best = format;
                }
            }

            if (best != null)
            {
                return best;
            }

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || !format.HasVideo || format.Width <= 0 || format.Height <= 0)
                {
                    continue;
                }

                if (best == null || format.QualityTier > best.QualityTier)
                {
                    best = format;
                }
            }

            return best;
        }

        private void UpdateVideoPlayerLayout(bool updateOrientationState = true)
        {
            // While handing the player to the mini-player the window shrinks to the small
            // always-on-top size, which is technically landscape. Reacting to that would drag the
            // page into fullscreen on its way out.
            if (_minimizedToMiniPlayer)
            {
                return;
            }

            bool isPortrait = IsCurrentViewPortrait();

            UpdateNavigationChrome(isPortrait);

            UpdateTitleDescriptionSkeletonLayout(isPortrait);
            MovePlaylistQueueForLayout(isPortrait);
            MoveVideoActionsForLayout(isPortrait);
            UpdateLandscapeDetailsVisibility(isPortrait);

            if (isPortrait)
            {
                // Portrait mode - player takes full width, related videos shown below
                if (PlayerColumn != null)
                    PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null)
                    RelatedColumn.Width = new GridLength(0);

                if (RelatedPanel != null)
                    RelatedPanel.Visibility = Visibility.Collapsed;
                if (RelatedPanelVertical != null)
                    RelatedPanelVertical.Visibility = Visibility.Visible;

                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                    CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                if (PlayerInfoPanel != null)
                    PlayerInfoPanel.Margin = new Thickness(0);

                UpdateVideoPlayerHeight();
            }
            else
            {
                // Landscape keeps the video/details column on the left and the playlist plus
                // recommendations rail on the right.
                var windowWidth = Window.Current.Bounds.Width;
                double relatedWidth;
                if (windowWidth <= 700)
                    relatedWidth = Math.Max(220, Math.Min(280, windowWidth * 0.30));
                else if (windowWidth <= 1000)
                    relatedWidth = Math.Max(260, Math.Min(340, windowWidth * 0.32));
                else
                    relatedWidth = Math.Max(320, Math.Min(400, windowWidth * 0.33));

                var maximumRelatedWidth = Math.Max(200, windowWidth * 0.45);
                if (relatedWidth > maximumRelatedWidth)
                    relatedWidth = maximumRelatedWidth;

                if (PlayerColumn != null)
                    PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null)
                    RelatedColumn.Width = new GridLength(relatedWidth);

                if (RelatedPanel != null)
                    RelatedPanel.Visibility = Visibility.Visible;
                if (RelatedPanelVertical != null)
                    RelatedPanelVertical.Visibility = Visibility.Collapsed;

                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                    CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                if (PlayerInfoPanel != null)
                    PlayerInfoPanel.Margin = new Thickness(0);

                UpdateVideoPlayerHeight();
            }

            UpdateVideoPlayerHeight();
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => UpdateVideoPlayerHeight());

            if (_offlineMode)
            {
                if (PlayerColumn != null) PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null) RelatedColumn.Width = new GridLength(0);
                if (RelatedPanel != null) RelatedPanel.Visibility = Visibility.Collapsed;
                if (RelatedPanelVertical != null) RelatedPanelVertical.Visibility = Visibility.Collapsed;
                if (RelatedVideosFallback != null) RelatedVideosFallback.Visibility = Visibility.Collapsed;
                if (RelatedVideosFallbackVertical != null) RelatedVideosFallbackVertical.Visibility = Visibility.Collapsed;
            }

            // Only the normal layout path advances the orientation baseline. Mini-player restore
            // resyncs may run while the phone is physically rotating, so those must never swallow
            // a real portrait/landscape transition.
            if (updateOrientationState)
            {
                _wasPortrait = isPortrait;
            }
        }

        private void UpdateNavigationChrome(bool isPortrait)
        {
            var show = !isPortrait;
            if (VideoNavbarRow != null)
                VideoNavbarRow.Height = new GridLength(show ? 56 : 0);
            if (VideoTabbarRow != null)
                VideoTabbarRow.Height = new GridLength(show ? 52 : 0);
            if (navbar != null)
            {
                navbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show)
                    navbar.SetVerticalLayout();
            }
            if (tabbar != null)
                tabbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void App_ThemeChanged(object sender, EventArgs e)
        {
            ApplyCurrentVideoTheme();
        }

        private void ApplyCurrentVideoTheme()
        {
            RequestedTheme = App.GetCurrentElementTheme();

            if (_fullscreenSettingsPopupRoot != null)
                _fullscreenSettingsPopupRoot.RequestedTheme = RequestedTheme;

            // These controls receive concrete brushes because they are built in C# rather than
            // XAML. Refresh them when a cached Video page returns or the system theme changes.
            UpdateSubscriptionVisualState();
            ForceShareTimeToggleVisual(_shareWithTimestamp);
            RefreshRuntimeOptionPanelTheme(QualityOptionsPanel);
            RefreshRuntimeOptionPanelTheme(SpeedOptionsPanel);
            RefreshRuntimeOptionPanelTheme(AudioTrackOptionsPanel);
            RefreshRuntimeOptionPanelTheme(SubtitlesOptionsPanel);
        }

        private static void RefreshRuntimeOptionPanelTheme(DependencyObject root)
        {
            if (root == null)
                return;

            var primary = App.GetThemeBrush("AppPrimaryTextBrush");
            var control = root as Control;
            if (control != null && primary != null && !IsVideoAccentBrush(control.Foreground))
                control.Foreground = primary;

            var text = root as TextBlock;
            if (text != null && primary != null && !IsVideoAccentBrush(text.Foreground))
                text.Foreground = primary;

            var icon = root as FontIcon;
            if (icon != null && primary != null && !IsVideoAccentBrush(icon.Foreground))
                icon.Foreground = primary;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                RefreshRuntimeOptionPanelTheme(VisualTreeHelper.GetChild(root, i));
        }

        private static bool IsVideoAccentBrush(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid != null
                && solid.Color.R == 255
                && solid.Color.G == 0
                && solid.Color.B == 51;
        }

        // The queue is one live control so its expansion state, ItemsSource and current marker are
        // preserved. Portrait keeps the original placement in the main content; landscape moves
        // that same Border above the related-video rail. This only uses Panel reparenting/Grid.Row,
        // which works on the VS2015 / Windows 10 Mobile UWP target.
        private void MovePlaylistQueueForLayout(bool isPortrait)
        {
            if (PlaylistQueuePanel == null || PlayerInfoPanel == null || LandscapePlaylistHost == null)
            {
                return;
            }

            try
            {
                if (isPortrait)
                {
                    if (!PlayerInfoPanel.Children.Contains(PlaylistQueuePanel))
                    {
                        var oldParent = PlaylistQueuePanel.Parent as Panel;
                        if (oldParent != null)
                            oldParent.Children.Remove(PlaylistQueuePanel);
                        PlayerInfoPanel.Children.Add(PlaylistQueuePanel);
                    }

                    Grid.SetRow(PlaylistQueuePanel, 4);
                    PlaylistQueuePanel.Margin = new Thickness(16, 0, 16, 16);
                }
                else
                {
                    if (!LandscapePlaylistHost.Children.Contains(PlaylistQueuePanel))
                    {
                        var oldParent = PlaylistQueuePanel.Parent as Panel;
                        if (oldParent != null)
                            oldParent.Children.Remove(PlaylistQueuePanel);
                        LandscapePlaylistHost.Children.Add(PlaylistQueuePanel);
                    }

                    Grid.SetRow(PlaylistQueuePanel, 0);
                    PlaylistQueuePanel.Margin = new Thickness(8, 0, 8, 12);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Layout reparent failed: " + ex.Message);
            }
        }

        private void MoveVideoActionsForLayout(bool isPortrait)
        {
            if (VideoActionsScrollViewer == null || PlayerInfoPanel == null || LandscapeActionsHost == null)
                return;

            try
            {
                var target = isPortrait ? (Panel)PlayerInfoPanel : LandscapeActionsHost;
                if (!target.Children.Contains(VideoActionsScrollViewer))
                {
                    var oldParent = VideoActionsScrollViewer.Parent as Panel;
                    if (oldParent != null)
                        oldParent.Children.Remove(VideoActionsScrollViewer);
                    target.Children.Add(VideoActionsScrollViewer);
                }

                if (isPortrait)
                {
                    Grid.SetRow(VideoActionsScrollViewer, 3);
                    VideoActionsScrollViewer.Margin = new Thickness(16, 2, 16, 16);
                    LandscapeActionsHost.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Grid.SetRow(VideoActionsScrollViewer, 0);
                    VideoActionsScrollViewer.Margin = new Thickness(0);
                    LandscapeActionsHost.Visibility = Visibility.Visible;
                }

                ScheduleLandscapeActionLabelUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Action-row reparent failed: " + ex.Message);
            }
        }

        private void ChannelAndActionsRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleLandscapeActionLabelUpdate();
        }

        private void ScheduleLandscapeActionLabelUpdate()
        {
            if (Dispatcher == null) return;
            var ignored = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                UpdateLandscapeActionLabelVisibility);
        }

        private void UpdateLandscapeActionLabelVisibility()
        {
            if (_updatingLandscapeActionLabels
                || ShareActionText == null
                || SaveActionText == null
                || DownloadActionText == null
                || ShareButton == null
                || SaveButton == null
                || DownloadButton == null)
            {
                return;
            }

            _updatingLandscapeActionLabels = true;
            try
            {
                if (IsCurrentViewPortrait())
                {
                    SetActionLabelVisible(ShareActionText, ShareButton, true);
                    SetActionLabelVisible(SaveActionText, SaveButton, true);
                    SetActionLabelVisible(DownloadActionText, DownloadButton, true);
                    return;
                }

                EnsureLandscapeActionWidthMeasurements();

                // Keep enough room for the avatar plus an ellipsized channel name. The
                // Subscribe button remains untouched; only Share/Save/Download lose labels.
                var subscribeWidth = SubscribeButtonContainer != null
                    && SubscribeButtonContainer.Visibility == Visibility.Visible
                    ? Math.Max(SubscribeButtonContainer.ActualWidth,
                        SubscribeButtonContainer.DesiredSize.Width)
                    : 0.0;
                var available = Math.Max(0.0,
                    (ChannelAndActionsRow == null ? 0.0 : ChannelAndActionsRow.ActualWidth)
                    - 124.0 - subscribeWidth - 12.0);

                var showDownload = _actionsWidthAllLabels <= available;
                var showSave = showDownload
                    || _actionsWidthWithoutDownloadLabel <= available;
                var showShare = showSave
                    || _actionsWidthWithoutSaveLabel <= available;

                if (_actionsWidthIconsOnly > available)
                {
                    showDownload = false;
                    showSave = false;
                    showShare = false;
                }

                SetActionLabelVisible(DownloadActionText, DownloadButton, showDownload);
                SetActionLabelVisible(SaveActionText, SaveButton, showSave);
                SetActionLabelVisible(ShareActionText, ShareButton, showShare);
            }
            finally
            {
                _updatingLandscapeActionLabels = false;
            }
        }

        private void EnsureLandscapeActionWidthMeasurements()
        {
            if (!double.IsNaN(_actionsWidthAllLabels) || VideoActionsPanel == null)
                return;

            SetActionLabelVisible(ShareActionText, ShareButton, true);
            SetActionLabelVisible(SaveActionText, SaveButton, true);
            SetActionLabelVisible(DownloadActionText, DownloadButton, true);
            _actionsWidthAllLabels = MeasureVideoActionsWidth();

            SetActionLabelVisible(DownloadActionText, DownloadButton, false);
            _actionsWidthWithoutDownloadLabel = MeasureVideoActionsWidth();

            SetActionLabelVisible(SaveActionText, SaveButton, false);
            _actionsWidthWithoutSaveLabel = MeasureVideoActionsWidth();

            SetActionLabelVisible(ShareActionText, ShareButton, false);
            _actionsWidthIconsOnly = MeasureVideoActionsWidth();
        }

        private double MeasureVideoActionsWidth()
        {
            VideoActionsPanel.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            return VideoActionsPanel.DesiredSize.Width;
        }

        private static void SetActionLabelVisible(
            TextBlock label,
            Button button,
            bool visible)
        {
            if (label != null)
                label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (button != null)
                button.Padding = visible ? new Thickness(16, 8, 16, 8) : new Thickness(8);
        }

        private void UpdateLandscapeDetailsVisibility(bool isPortrait)
        {
            var hasComments = _currentComments != null && _currentComments.Count > 0;
            if (LandscapeDescriptionContainer != null)
                LandscapeDescriptionContainer.Visibility = isPortrait ? Visibility.Collapsed : Visibility.Visible;
            if (LandscapeCommentsPanel != null)
                LandscapeCommentsPanel.Visibility = !isPortrait && hasComments
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (CommentsContainerButton != null)
                CommentsContainerButton.Visibility = isPortrait && hasComments
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

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
            CustomVideoPlayer.MinimizeRequested += CustomVideoPlayer_MinimizeRequested;
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
            CustomVideoPlayer.VideoEnded += CustomVideoPlayer_VideoEnded;
            CustomVideoPlayer.NextRequested += CustomVideoPlayer_NextRequested;
            CustomVideoPlayer.PreviousRequested += CustomVideoPlayer_PreviousRequested;
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
                CustomVideoPlayer.MinimizeRequested -= CustomVideoPlayer_MinimizeRequested;
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
                SubscribeButtonContainer.Visibility = offline ? Visibility.Collapsed : Visibility.Visible;
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

        private async Task LoadVideoDetailsAsync(string videoId)
        {
            Task<string> playerTask = null;
            Task<string> nextTask = null;
            Task<List<CommentItem>> commentsTask = null;
            Task warmupAndroidPlayerTask = null;
            Task sideDataTask = null;

            try
            {
                System.Diagnostics.Debug.WriteLine("[Video] Fast loading video details for: " + videoId);

                SetSkeletonVisibility(true);
                ResetSecondaryContentForFastLoad();

                // Start all independent network requests immediately. The old flow waited for
                // player -> next -> rating/subscription -> comments -> related -> playback.
                // This keeps the player path short and lets the rest fill in progressively.
                // Ordered to keep the critical path — and only it — running first: the player
                // response, then the stream URLs. Firing next/comments in parallel here added two
                // more simultaneous requests to the very moment YouTube is deciding whether this
                // looks like a bot, which made the CAPTCHA wall show up more often. Those
                // secondary requests now start AFTER playback is bound.
                playerTask = PostInnertubeAsync("player", BuildPlayerPayload(videoId));

                var playerJson = await playerTask;
                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                _lastPlayerJson = playerJson;
                var playerRoot = await ParseJsonObjectAsync(playerJson);
                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                // Seed the session visitorData from the primary player response BEFORE the
                // ANDROID_VR stream calls fire, so they clear the anti-bot wall on the first try
                // (see GetSessionVisitorDataAsync).
                CaptureVisitorData(playerRoot);
                ApplyPlayerMetadata(playerRoot);

                // The stream URLs are fetched inside BindPlayerSourceAsync (ANDROID_VR). This is
                // the request that matters most, so nothing else competes with it yet.
                await BindPlayerSourceAsync(playerRoot, true);

                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                // Playback is ready; do not keep the full-page skeleton until comments/related finish.
                SetSkeletonVisibility(false);

                // Only now start the secondary requests, so the earlier burst is smaller.
                nextTask = PostInnertubeAsync("next", BuildNextPayload(videoId, currentPlaylistId));
                commentsTask = LoadCommentsSafeAsync(videoId);
                warmupAndroidPlayerTask = WarmUpAndroidPlayerAsync(videoId);
                sideDataTask = LoadSideDataAsync(videoId, nextTask, commentsTask);

                // Record the view in the account's watch history (fire-and-forget).
                var historyTask = ReportWatchHistoryAsync(videoId);

                await ObserveBackgroundTaskAsync(sideDataTask, "side video data");
                await ObserveBackgroundTaskAsync(warmupAndroidPlayerTask, "Android player warmup");

                System.Diagnostics.Debug.WriteLine("[Video] Fast load completed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Error loading video: " + ex.Message);
                System.Diagnostics.Debug.WriteLine("[Video] Stack: " + ex.StackTrace);

                SetSkeletonVisibility(false);
                await ObserveBackgroundTaskAsync(sideDataTask, "side video data after main load failure");
                await ObserveBackgroundTaskAsync(nextTask, "next request after main load failure");
                await ObserveBackgroundTaskAsync(commentsTask, "comments request after main load failure");
                await ObserveBackgroundTaskAsync(warmupAndroidPlayerTask, "Android player warmup after main load failure");
            }
        }

        private async Task LoadSideDataAsync(string videoId, Task<string> nextTask, Task<List<CommentItem>> commentsTask)
        {
            Task<List<RelatedVideoCardItem>> relatedVideosTask = null;
            Task ratingTask = null;
            Task subscriptionTask = null;

            try
            {
                var nextJson = await nextTask;
                var nextRoot = await ParseJsonObjectAsync(nextJson);
                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                ApplyNextMetadata(nextRoot);

                // Watch queue for the current playlist / mix (jam), if we were opened with one.
                // Mixes are personalized, so the queue is refetched signed in; without that the
                // anonymous /next above answers with a generic mix built from the seed video only.
                JsonObject playlistNextRoot = null;
                if (!string.IsNullOrWhiteSpace(currentPlaylistId))
                {
                    playlistNextRoot = await TryLoadAuthenticatedPlaylistNextAsync(videoId, currentPlaylistId);
                    if (!IsStillCurrentVideo(videoId))
                    {
                        return;
                    }
                }

                ExtractPlaylistQueueFromNext(nextRoot, playlistNextRoot);
                ApplyPlaylistQueueUi();

                // These may do authenticated calls. Start both immediately; Config.RefreshAccessTokenAsync
                // already serializes token refreshes so duplicate OAuth calls are avoided.
                ratingTask = LoadUserVideoRatingAsync(videoId, nextRoot);
                subscriptionTask = LoadChannelSubscriptionStateAsync(videoId, nextRoot);

                // Resume overlays are personalized and are absent from anonymous WEB /next.
                // Load recommendations through the signed-in TV client; the helper falls back
                // to the already-compatible anonymous path when the account has no token.
                relatedVideosTask = LoadRelatedVideosSafeAsync(videoId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] /next side data failed: " + ex.Message);
                relatedVideosTask = LoadRelatedVideosSafeAsync(videoId);
            }

            // Bind comments and related independently so a slow comments request cannot hold back related videos.
            var commentsApplyTask = ApplyCommentsWhenReadyAsync(videoId, commentsTask);
            var relatedApplyTask = ApplyRelatedVideosWhenReadyAsync(videoId, relatedVideosTask);
            await ObserveBackgroundTaskAsync(Task.WhenAll(commentsApplyTask, relatedApplyTask), "comments/related bind");

            var stateTasks = new List<Task>();
            if (ratingTask != null)
            {
                stateTasks.Add(ratingTask);
            }
            if (subscriptionTask != null)
            {
                stateTasks.Add(subscriptionTask);
            }
            if (stateTasks.Count > 0)
            {
                await ObserveBackgroundTaskAsync(Task.WhenAll(stateTasks), "rating/subscription state");
            }
        }

        private async Task ApplyCommentsWhenReadyAsync(string videoId, Task<List<CommentItem>> commentsTask)
        {
            var comments = await commentsTask;
            if (IsStillCurrentVideo(videoId))
            {
                ApplyComments(comments);
            }
        }

        private async Task ApplyRelatedVideosWhenReadyAsync(string videoId, Task<List<RelatedVideoCardItem>> relatedVideosTask)
        {
            List<RelatedVideoCardItem> relatedVideos = null;

            try
            {
                relatedVideos = relatedVideosTask != null ? await relatedVideosTask : new List<RelatedVideoCardItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[RelatedVideos] Fast related parse failed: " + ex.Message);
            }

            // If the shared /next response did not contain parseable related cards, do one
            // fallback /next request before giving up. This keeps the fast path fast, but
            // prevents the placeholder cards from hanging forever when YouTube changes a renderer.
            if (IsStillCurrentVideo(videoId) && (relatedVideos == null || relatedVideos.Count == 0))
            {
                relatedVideos = await LoadRelatedVideosSafeAsync(videoId);
            }

            if (IsStillCurrentVideo(videoId))
            {
                ApplyRelatedVideos(relatedVideos);
            }
        }

        private static Task<JsonObject> ParseJsonObjectAsync(string json)
        {
            return Task.Run<JsonObject>(delegate
            {
                return Windows.Data.Json.JsonValue.Parse(json).GetObject();
            });
        }

        private async Task<List<CommentItem>> LoadCommentsSafeAsync(string videoId)
        {
            try
            {
                var comments = await Config.GetCommentsAsync(videoId).ConfigureAwait(false);
                return comments ?? new List<CommentItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Comments loading failed: " + ex.Message);
                return new List<CommentItem>();
            }
        }

        private async Task<List<RelatedVideoCardItem>> LoadRelatedVideosSafeAsync(string videoId)
        {
            try
            {
                var relatedVideos = await LoadRelatedVideosAsync(videoId).ConfigureAwait(false);
                return relatedVideos ?? new List<RelatedVideoCardItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Related videos fallback failed: " + ex.Message);
                return new List<RelatedVideoCardItem>();
            }
        }

        private Task<List<RelatedVideoCardItem>> ExtractRelatedVideosFromNextRootAsync(JsonObject nextRoot)
        {
            return Task.Run<List<RelatedVideoCardItem>>(delegate
            {
                var relatedVideos = new List<RelatedVideoCardItem>();
                ExtractRelatedVideosFromJson(nextRoot, relatedVideos);
                return relatedVideos;
            });
        }

        private async Task WarmUpAndroidPlayerAsync(string videoId)
        {
            try
            {
                await PostAndroidPlayerAsync(videoId).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Android player warmup failed: " + ex.Message);
            }
        }

        private async Task ObserveBackgroundTaskAsync(Task task, string name)
        {
            if (task == null)
            {
                return;
            }

            try
            {
                await task;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Background task failed (" + name + "): " + ex.Message);
            }
        }

        private bool IsStillCurrentVideo(string videoId)
        {
            return string.Equals(videoId, currentVideoId, StringComparison.Ordinal);
        }

        private void SetSkeletonVisibility(bool visible)
        {
            if (SkeletonLoader != null)
            {
                SkeletonLoader.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void ResetSecondaryContentForFastLoad()
        {
            if (CommentsContainerButton != null)
            {
                CommentsContainerButton.Visibility = Visibility.Collapsed;
            }
            if (CommentsList != null)
            {
                CommentsList.ItemsSource = null;
            }
            _currentComments = new List<CommentItem>();
            if (LandscapeCommentsList != null)
                LandscapeCommentsList.ItemsSource = null;
            if (LandscapeCommentsPanel != null)
                LandscapeCommentsPanel.Visibility = Visibility.Collapsed;

            // During fast loading, keep the related-video area visually filled with
            // placeholder cards. The real related cards replace these in ShowRelatedVideos().
            if (RelatedVideosContainer != null)
            {
                RelatedVideosContainer.ItemsSource = null;
                RelatedVideosContainer.Visibility = Visibility.Collapsed;
            }
            if (RelatedVideosContainerVertical != null)
            {
                RelatedVideosContainerVertical.ItemsSource = null;
                RelatedVideosContainerVertical.Visibility = Visibility.Collapsed;
            }
            if (RelatedVideosFallback != null)
            {
                RelatedVideosFallback.Visibility = Visibility.Visible;
            }
            if (RelatedVideosFallbackVertical != null)
            {
                RelatedVideosFallbackVertical.Visibility = Visibility.Visible;
            }
            if (RelatedVideosLoadingRing != null)
            {
                RelatedVideosLoadingRing.IsActive = false;
                RelatedVideosLoadingRing.Visibility = Visibility.Collapsed;
            }
            if (RelatedVideosLoadingRingVertical != null)
            {
                RelatedVideosLoadingRingVertical.IsActive = false;
                RelatedVideosLoadingRingVertical.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateSystemMediaMetadataForCurrentVideo()
        {
            try
            {
                if (CustomVideoPlayer == null)
                {
                    return;
                }

                var title = VideoTitleText != null ? VideoTitleText.Text : string.Empty;
                var author = !string.IsNullOrWhiteSpace(currentChannelName)
                    ? currentChannelName
                    : (VideoAuthorText != null ? VideoAuthorText.Text : string.Empty);

                CustomVideoPlayer.SetSystemMediaMetadata(currentVideoId, title, author);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to update system media metadata: " + ex.Message);
            }
        }

        private void ApplyPlayerMetadata(JsonObject playerRoot)
        {
            // Extract video details from player response
            if (playerRoot.ContainsKey("videoDetails"))
            {
                var videoDetails = playerRoot.GetNamedObject("videoDetails");

                // Title
                if (VideoTitleText != null && videoDetails.ContainsKey("title"))
                {
                    VideoTitleText.Text = videoDetails.GetNamedString("title");
                    System.Diagnostics.Debug.WriteLine("[Video] Title: " + VideoTitleText.Text);
                }

                // Author/Channel
                if (videoDetails.ContainsKey("author"))
                {
                    currentChannelName = videoDetails.GetNamedString("author");
                    if (VideoAuthorText != null)
                    {
                        VideoAuthorText.Text = currentChannelName;
                    }
                    System.Diagnostics.Debug.WriteLine("[Video] Author: " + currentChannelName);
                }

                if (videoDetails.ContainsKey("thumbnail"))
                {
                    _currentVideoThumbnailUrl = ExtractLastThumbnailUrl(
                        videoDetails.GetNamedObject("thumbnail", null));
                }
                if (string.IsNullOrWhiteSpace(_currentVideoThumbnailUrl))
                    _currentVideoThumbnailUrl = "https://i.ytimg.com/vi/" + currentVideoId + "/hqdefault.jpg";

                // View count
                if (videoDetails.ContainsKey("viewCount"))
                {
                    var viewCount = videoDetails.GetNamedString("viewCount");
                    if (VideoViewsText != null)
                    {
                        VideoViewsText.Text = FormatCount(viewCount);
                    }
                    System.Diagnostics.Debug.WriteLine("[Video] Views: " + FormatCount(viewCount));
                }

                // Hand the title/author to the player for its fullscreen top-left overlay.
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SetVideoInfo(
                        VideoTitleText != null ? VideoTitleText.Text : string.Empty,
                        currentChannelName);
                }

                // Duration
                if (CustomVideoPlayer != null)
                {
                    if (videoDetails.ContainsKey("lengthSeconds"))
                    {
                        long lengthSeconds;
                        if (long.TryParse(videoDetails.GetNamedString("lengthSeconds"), out lengthSeconds) && lengthSeconds > 0)
                        {
                            CustomVideoPlayer.ParsedDuration = TimeSpan.FromSeconds(lengthSeconds);
                            System.Diagnostics.Debug.WriteLine("[Video] Duration seconds: " + lengthSeconds);
                        }
                        else
                        {
                            CustomVideoPlayer.ParsedDuration = TimeSpan.Zero;
                        }
                    }
                    else
                    {
                        CustomVideoPlayer.ParsedDuration = TimeSpan.Zero;
                    }
                }

                // Description
                if (videoDetails.ContainsKey("shortDescription"))
                {
                    _currentVideoDescription = videoDetails.GetNamedString("shortDescription");
                    System.Diagnostics.Debug.WriteLine("[Video] Description length: " + _currentVideoDescription.Length);
                }
                else
                {
                    _currentVideoDescription = string.Empty;
                    System.Diagnostics.Debug.WriteLine("[Video] No description in player response");
                }
                UpdateDescriptionChaptersFromDescription();
                UpdateLandscapeDescriptionText();
                var ignoredDownloadState = UpdateDownloadButtonAsync();
                LoadSponsorBlockSegments(currentVideoId);
                LoadSubtitleTracks(playerRoot);

                // Audio-track choice does not carry across videos.
                _selectedAudioTrackId = null;
                _availableAudioTracks = new List<Config.AudioTrackInfo>();

                // Get publish date from microformat if available
                if (playerRoot.ContainsKey("microformat"))
                {
                    var microformat = playerRoot.GetNamedObject("microformat");
                    if (microformat.ContainsKey("playerMicroformatRenderer"))
                    {
                        var renderer = microformat.GetNamedObject("playerMicroformatRenderer");
                        if (renderer.ContainsKey("publishDate"))
                        {
                            var publishDate = renderer.GetNamedString("publishDate");
                            if (VideoUploadDateText != null)
                            {
                                VideoUploadDateText.Text = FormatPublishDate(publishDate);
                            }
                            System.Diagnostics.Debug.WriteLine("[Video] PublishDate: " + publishDate);
                        }
                    }
                }

                // Channel thumbnail from player response (fallback)
                if (videoDetails.ContainsKey("channelId"))
                {
                    currentChannelId = videoDetails.GetNamedString("channelId");
                    System.Diagnostics.Debug.WriteLine("[Video] Channel ID: " + currentChannelId);
                }

                UpdateSystemMediaMetadataForCurrentVideo();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Video] WARNING: No videoDetails in player response");
            }
        }

        private void ApplyNextMetadata(JsonObject nextRoot)
        {
            // Extract publish date from /next response (preferred) - now shown in description bottom sheet
            string publishDateFromNext = ExtractPublishDateFromNext(nextRoot);
            if (!string.IsNullOrEmpty(publishDateFromNext))
            {
                if (VideoUploadDateText != null)
                {
                    VideoUploadDateText.Text = publishDateFromNext;
                }
                System.Diagnostics.Debug.WriteLine("[Video] PublishDate (from /next): " + publishDateFromNext);
            }

            // Extract channel avatar from /next response
            string channelAvatarUrl = ExtractChannelAvatarFromNext(nextRoot);
            _currentChannelThumbnailUrl = channelAvatarUrl ?? string.Empty;
            if (!string.IsNullOrEmpty(channelAvatarUrl) && ChannelImage != null)
            {
                try
                {
                    var bitmap = new Windows.UI.Xaml.Media.Imaging.BitmapImage(new Uri(channelAvatarUrl));
                    ChannelImage.ImageSource = bitmap;
                    System.Diagnostics.Debug.WriteLine("[Video] Channel avatar loaded");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Error loading channel avatar: " + ex.Message);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Video] No channel avatar URL found");
            }

            // Extract like count from /next response
            if (LikeCountText != null)
            {
                string likeCount = ExtractLikeCountFromNext(nextRoot);
                if (!string.IsNullOrEmpty(likeCount))
                {
                    LikeCountText.Text = likeCount;
                    System.Diagnostics.Debug.WriteLine("[Video] Likes: " + LikeCountText.Text);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Could not extract like count");
                }
            }

            // Extract subscriber count from /next response
            if (SubscriberCountText != null)
            {
                string subscriberCount = ExtractSubscriberCountFromNext(nextRoot);
                string cleanedCount = Regex
                    .Replace(subscriberCount, @"subscribers?", "", RegexOptions.IgnoreCase)
                    .Trim();
                if (!string.IsNullOrEmpty(cleanedCount))
                {
                    SubscriberCountText.Text = cleanedCount;
                    System.Diagnostics.Debug.WriteLine("[Video] Subscribers: " + SubscriberCountText.Text);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Could not extract subscriber count");
                }
            }
        }

        private void ApplyRelatedVideos(List<RelatedVideoCardItem> relatedVideos)
        {
            if (relatedVideos != null && relatedVideos.Count > 0)
            {
                ShowRelatedVideos(relatedVideos);
                System.Diagnostics.Debug.WriteLine("[Video] Related videos bound to related video containers: " + relatedVideos.Count);
            }
            else
            {
                HideRelatedVideosLoadingPlaceholders();
                System.Diagnostics.Debug.WriteLine("[Video] Related videos unavailable after fallback; hiding loading placeholders");
            }
        }

        private void ApplyComments(List<CommentItem> comments)
        {
            comments = comments ?? new List<CommentItem>();
            _currentComments = comments;
            System.Diagnostics.Debug.WriteLine("[Video] Loaded " + comments.Count + " comments");

            // Show last comment preview if comments exist
            if (comments.Count > 0)
            {
                var lastComment = comments[0];

                if (CommentsContainerButton != null)
                {
                    CommentsContainerButton.Visibility = Visibility.Visible;
                }

                if (LastCommentAuthor != null)
                {
                    LastCommentAuthor.Text = lastComment.Author;
                }

                if (LastCommentTime != null)
                {
                    LastCommentTime.Text = lastComment.PublishedAt;
                }

                if (LastCommentText != null)
                {
                    LastCommentText.Text =
                        lastComment.Text.Length > 100
                            ? lastComment.Text.Substring(0, 100) + "..."
                            : lastComment.Text;
                }

                // Load through the explicit 64px avatar loader. Direct string -> ImageBrush
                // conversion is unreliable on older Windows 10 Mobile builds.
                if (LastCommentAuthorImage != null)
                {
                    ChannelIconController.AssignAlways(
                        LastCommentAuthorImage == null ? null : LastCommentAuthorImage.Fill as ImageBrush,
                        lastComment.AuthorThumbnail);
                }

                System.Diagnostics.Debug.WriteLine("[Video] Last comment preview shown: " + lastComment.Author);
            }
            else
            {
                if (CommentsContainerButton != null)
                {
                    CommentsContainerButton.Visibility = Visibility.Collapsed;
                }
                System.Diagnostics.Debug.WriteLine("[Video] No comments to display");
            }

            if (CommentsList != null)
            {
                CommentsList.ItemsSource = comments;
                System.Diagnostics.Debug.WriteLine("[Video] Comments bound to CommentsList");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Video] ERROR: CommentsList is null!");
            }

            if (LandscapeCommentsList != null)
                LandscapeCommentsList.ItemsSource = comments;
            UpdateLandscapeDetailsVisibility(IsCurrentViewPortrait());
        }

        private async Task<string> PostInnertubeAsync(string endpoint, string payload)
        {
            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                if (endpoint == "player")
                {
                    // Exact IOS primary /player request from youtube-ios.
                    request.Headers.TryAddWithoutValidation("User-Agent", InnertubeIosPlayerUserAgent);
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", InnertubeIosPlayerClientHeaderName);
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", InnertubeIosPlayerClientVersion);
                }
                else
                {
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"
                    );
                    request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                }

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private static string BuildPlayerPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            // Match get_url.py exactly: IOS client → streamingData.hlsManifestUrl
            client["clientName"] = JsonValue.CreateStringValue("IOS");
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeIosPlayerClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("iPhone16,2");
            client["osName"] = JsonValue.CreateStringValue("iOS");
            client["osVersion"] = JsonValue.CreateStringValue("18.0");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private async Task<string> PostAndroidPlayerAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            if (string.Equals(_lastAndroidPlayerVideoId, videoId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_lastAndroidPlayerJson))
            {
                _playbackMediaUserAgent = _lastAndroidPlayerUserAgent;
                return _lastAndroidPlayerJson;
            }

            Task<string> requestTask;
            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_lastAndroidPlayerVideoId, videoId, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(_lastAndroidPlayerJson))
                {
                    _playbackMediaUserAgent = _lastAndroidPlayerUserAgent;
                    return _lastAndroidPlayerJson;
                }

                if (_androidPlayerRequestTask == null || !string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                {
                    _androidPlayerRequestVideoId = videoId;
                    _androidPlayerRequestTask = PostAndroidPlayerCoreAsync(videoId);
                }

                requestTask = _androidPlayerRequestTask;
            }

            string json;
            try
            {
                json = await requestTask.ConfigureAwait(false);
            }
            catch
            {
                lock (_androidPlayerRequestGate)
                {
                    if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                    {
                        _androidPlayerRequestTask = null;
                    }
                }
                throw;
            }

            lock (_androidPlayerRequestGate)
            {
                if (string.Equals(_androidPlayerRequestVideoId, videoId, StringComparison.Ordinal))
                {
                    // Only cache a response that actually carries streams. A stream-less
                    // (bot-gated / throttled) response must NOT stick, or every later itag18 /
                    // muxed lookup for this video reuses the broken player response.
                    if (PlayerJsonHasStreams(json))
                    {
                        _lastAndroidPlayerVideoId = videoId;
                        _lastAndroidPlayerJson = json;
                        _lastAndroidPlayerUserAgent = _playbackMediaUserAgent;
                    }
                    else
                    {
                        _lastAndroidPlayerJson = string.Empty;
                    }

                    _androidPlayerRequestTask = null;
                }
            }

            return json;
        }

        private async Task<string> PostAndroidPlayerCoreAsync(string videoId)
        {
            // Same chain as YTApi.androidVrPlayerResponse:primary:. The IOS response has
            // already been requested by LoadVideoDetailsFastAsync and is deliberately held
            // for the last fallback while its responseContext seeds visitorData.
            var primary = _lastPlayerJson;
            if (string.IsNullOrWhiteSpace(primary))
            {
                primary = await FetchIosPlayerAsync(videoId, null).ConfigureAwait(false);
            }
            CaptureVisitorDataFromString(primary);

            // VISIONOS is first: unlike the other anonymous app clients its media session is
            // not cut off around the first minute.
            var vision = await FetchVisionPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(vision))
            {
                _playbackMediaUserAgent = InnertubeVisionUserAgent;
                System.Diagnostics.Debug.WriteLine("[Video] Playback URLs from VISIONOS");
                return vision;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] VISIONOS returned no ready URLs (" + GetPlayabilityReason(vision)
                + "); trying ANDROID_VR"
            );

            var json = await FetchAndroidVrPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(json))
            {
                _playbackMediaUserAgent = InnertubeAndroidVrUserAgent;
                return json;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] ANDROID_VR returned no ready URLs (" + GetPlayabilityReason(json)
                + "); refreshing visitorData and retrying"
            );
            InvalidateSessionVisitorData();

            var retryJson = await FetchAndroidVrPlayerAsync(videoId).ConfigureAwait(false);
            if (PlayerJsonHasStreams(retryJson))
            {
                _playbackMediaUserAgent = InnertubeAndroidVrUserAgent;
                System.Diagnostics.Debug.WriteLine("[Video] ANDROID_VR retry with fresh visitorData succeeded");
                return retryJson;
            }

            string progressiveFallback = string.Empty;
            string progressiveFallbackAgent = string.Empty;
            var accessToken = await GetTvAccessTokenAsync(false).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var tv = await FetchTvPlaybackPlayerAsync(
                    videoId,
                    InnertubePlaybackTvClientVersion,
                    accessToken
                ).ConfigureAwait(false);
                CaptureVisitorDataFromString(tv);
                if (PlayerJsonHasAdaptiveStreams(tv))
                {
                    _playbackMediaUserAgent = InnertubePlaybackTvUserAgent;
                    return tv;
                }
                if (PlayerJsonHasStreams(tv))
                {
                    progressiveFallback = tv;
                    progressiveFallbackAgent = InnertubePlaybackTvUserAgent;
                }

                var legacy = await FetchTvPlaybackPlayerAsync(
                    videoId,
                    InnertubePlaybackTvLegacyClientVersion,
                    accessToken
                ).ConfigureAwait(false);
                if (PlayerJsonHasAdaptiveStreams(legacy))
                {
                    _playbackMediaUserAgent = InnertubePlaybackTvUserAgent;
                    return legacy;
                }
                if (string.IsNullOrWhiteSpace(progressiveFallback) && PlayerJsonHasStreams(legacy))
                {
                    progressiveFallback = legacy;
                    progressiveFallbackAgent = InnertubePlaybackTvUserAgent;
                }
            }

            if (PlayerJsonHasStreams(primary))
            {
                _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                return primary;
            }

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                var signedInIos = await FetchIosPlayerAsync(videoId, accessToken).ConfigureAwait(false);
                if (PlayerJsonHasStreams(signedInIos))
                {
                    _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                    return signedInIos;
                }
            }

            if (!string.IsNullOrWhiteSpace(progressiveFallback))
            {
                _playbackMediaUserAgent = progressiveFallbackAgent;
                return progressiveFallback;
            }

            // youtube-ios keeps the primary IOS response when it at least contains ready HLS.
            if (PlayerJsonHasHlsManifest(primary))
            {
                _playbackMediaUserAgent = InnertubeIosPlayerUserAgent;
                return primary;
            }

            return string.IsNullOrWhiteSpace(retryJson) ? json : retryJson;
        }

        private async Task<string> FetchAndroidVrPlayerAsync(string videoId)
        {
            // Ported from MeeTube (core::fetchPlayer / ContextBuilder): the ANDROID_VR client
            // WITH the session visitorData is the anonymous default that returns direct,
            // ready-to-fetch progressive + adaptive URLs (no signature cipher, no &n=, no Po
            // token). No InnerTube API key is sent — the X-YouTube-Client-* headers identify
            // the client, exactly as MeeTube's transport does.
            var visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
            return await PostDirectPlayerPayloadAsync(
                BuildAndroidVrPlayerPayload(videoId, visitorData),
                InnertubeAndroidVrUserAgent,
                InnertubeAndroidVrClientHeaderName,
                InnertubeAndroidVrClientVersion,
                visitorData,
                null,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchVisionPlayerAsync(string videoId)
        {
            var visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeVisionClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeVisionClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("RealityDevice14,1");
            client["osName"] = JsonValue.CreateStringValue("visionOS");
            client["osVersion"] = JsonValue.CreateStringValue("1.0.2.21O209");
            client["userAgent"] = JsonValue.CreateStringValue(InnertubeVisionUserAgent);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                client["visitorData"] = JsonValue.CreateStringValue(visitorData);
            }
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubeVisionUserAgent,
                InnertubeVisionClientHeaderName,
                InnertubeVisionClientVersion,
                visitorData,
                null,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchIosPlayerAsync(string videoId, string accessToken)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("IOS");
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeIosPlayerClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Apple");
            client["deviceModel"] = JsonValue.CreateStringValue("iPhone16,2");
            client["osName"] = JsonValue.CreateStringValue("iOS");
            client["osVersion"] = JsonValue.CreateStringValue("18.0");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);

            string visitorData = null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                visitorData = await GetSessionVisitorDataAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(visitorData))
                {
                    client["visitorData"] = JsonValue.CreateStringValue(visitorData);
                }
            }
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubeIosPlayerUserAgent,
                InnertubeIosPlayerClientHeaderName,
                InnertubeIosPlayerClientVersion,
                visitorData,
                accessToken,
                false
            ).ConfigureAwait(false);
        }

        private async Task<string> FetchTvPlaybackPlayerAsync(
            string videoId,
            string clientVersion,
            string accessToken
        )
        {
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(clientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["deviceMake"] = JsonValue.CreateStringValue("Samsung");
            client["deviceModel"] = JsonValue.CreateStringValue("SmartTV");
            client["osName"] = JsonValue.CreateStringValue("Tizen");
            client["osVersion"] = JsonValue.CreateStringValue("5.0");

            var context = new JsonObject();
            context["client"] = client;

            var contentPlaybackContext = new JsonObject();
            contentPlaybackContext["html5Preference"] = JsonValue.CreateStringValue("HTML5_PREF_WANTS");
            var sts = await global::Config.GetSignatureTimestampAsync(false).ConfigureAwait(false);
            if (sts > 0)
            {
                contentPlaybackContext["signatureTimestamp"] = JsonValue.CreateNumberValue(sts);
            }

            var playbackContext = new JsonObject();
            playbackContext["contentPlaybackContext"] = contentPlaybackContext;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["playbackContext"] = playbackContext;

            return await PostDirectPlayerPayloadAsync(
                payload.Stringify(),
                InnertubePlaybackTvUserAgent,
                InnertubeTvClientHeaderName,
                clientVersion,
                null,
                accessToken,
                true
            ).ConfigureAwait(false);
        }

        private async Task<string> PostDirectPlayerPayloadAsync(
            string payload,
            string userAgent,
            string clientName,
            string clientVersion,
            string visitorData,
            string accessToken,
            bool includeInnertubeKey
        )
        {
            try
            {
                var url = "https://www.youtube.com/youtubei/v1/player?"
                    + (includeInnertubeKey ? "key=" + InnertubeApiKey + "&" : string.Empty)
                    + "prettyPrint=false";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                    if (!includeInnertubeKey)
                    {
                        request.Headers.TryAddWithoutValidation("Accept", "application/json");
                        request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(
                            "Accept-Language",
                            Config.Hl + "," + Config.Hl + ";q=0.9"
                        );
                    }
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientName);
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
                    if (!includeInnertubeKey)
                    {
                        request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    }
                    if (!string.IsNullOrWhiteSpace(visitorData))
                    {
                        request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorData);
                    }
                    if (!string.IsNullOrWhiteSpace(accessToken))
                    {
                        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                    }

                    var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] " + clientName + " /player failed: "
                            + (int)response.StatusCode + " " + response.ReasonPhrase
                        );
                        return string.Empty;
                    }

                    return json;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] " + clientName + " /player error: " + ex.Message
                );
                return string.Empty;
            }
        }

        private void CaptureVisitorDataFromString(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            try
            {
                CaptureVisitorData(JsonValue.Parse(json).GetObject());
            }
            catch
            {
            }
        }

        private static bool PlayerJsonHasStreams(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                if (!root.ContainsKey("streamingData"))
                {
                    return false;
                }

                var streamingData = root.GetNamedObject("streamingData");
                return JsonFormatArrayHasReadyUrl(streamingData, "adaptiveFormats")
                    || JsonFormatArrayHasReadyUrl(streamingData, "formats");
            }
            catch
            {
            }

            return false;
        }

        private static bool PlayerJsonHasAdaptiveStreams(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                if (!root.ContainsKey("streamingData"))
                {
                    return false;
                }

                return JsonFormatArrayHasReadyUrl(
                    root.GetNamedObject("streamingData"),
                    "adaptiveFormats"
                );
            }
            catch
            {
                return false;
            }
        }

        private static bool PlayerJsonHasHlsManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                return root.ContainsKey("streamingData")
                    && !string.IsNullOrWhiteSpace(
                        GetJsonString(root.GetNamedObject("streamingData"), "hlsManifestUrl")
                    );
            }
            catch
            {
                return false;
            }
        }

        private static bool JsonFormatArrayHasReadyUrl(JsonObject streamingData, string name)
        {
            if (streamingData == null || !streamingData.ContainsKey(name))
            {
                return false;
            }

            var formats = streamingData.GetNamedArray(name);
            for (int i = 0; i < formats.Count; i++)
            {
                if (formats[i].ValueType == JsonValueType.Object
                    && !string.IsNullOrWhiteSpace(GetJsonString(formats[i].GetObject(), "url")))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetPlayabilityReason(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return "empty response";
            }

            try
            {
                var root = JsonValue.Parse(json).GetObject();
                if (root.ContainsKey("playabilityStatus"))
                {
                    var status = root.GetNamedObject("playabilityStatus");
                    var s = GetJsonString(status, "status");
                    var reason = GetJsonString(status, "reason");
                    return (string.IsNullOrWhiteSpace(s) ? "?" : s) + (string.IsNullOrWhiteSpace(reason) ? string.Empty : ": " + reason);
                }
            }
            catch
            {
            }

            return "no streamingData";
        }

        private void InvalidateSessionVisitorData()
        {
            lock (_visitorDataGate)
            {
                _sessionVisitorData = string.Empty;
                _visitorDataFetchTask = null;
            }
        }

        private static string BuildAndroidVrPlayerPayload(string videoId, string visitorData)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeAndroidVrClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeAndroidVrClientVersion);
            client["deviceMake"] = JsonValue.CreateStringValue("Oculus");
            client["deviceModel"] = JsonValue.CreateStringValue("Quest 3");
            client["androidSdkVersion"] = JsonValue.CreateNumberValue(32);
            client["osName"] = JsonValue.CreateStringValue("Android");
            client["osVersion"] = JsonValue.CreateStringValue("12L");
            client["platform"] = JsonValue.CreateStringValue("MOBILE");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            if (!string.IsNullOrWhiteSpace(visitorData))
            {
                client["visitorData"] = JsonValue.CreateStringValue(visitorData);
            }
            context["client"] = client;

            // ANDROID_VR sends no playbackContext (that is the TVHTML5-only "reloaded" fix in
            // MeeTube). contentCheckOk / racyCheckOk keep age/consent-gated videos playable.
            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private static string BuildWebSafariPlayerPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20260114.08.00");
            client["userAgent"] = JsonValue.CreateStringValue(
                "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)"
            );
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["visitorData"] = JsonValue.CreateStringValue(GetFallbackInnertubeVisitorData());
            context["client"] = client;

            var contentPlaybackContext = new JsonObject();
            contentPlaybackContext["html5Preference"] = JsonValue.CreateStringValue("HTML5_PREF_WANTS");
            var playbackContext = new JsonObject();
            playbackContext["contentPlaybackContext"] = contentPlaybackContext;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["playbackContext"] = playbackContext;
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private async Task<string> PostWebSafariPlayerAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            var url = "https://www.youtube.com/youtubei/v1/player?key=" + InnertubeApiKey + "&prettyPrint=false";
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildWebSafariPlayerPayload(videoId),
                    Encoding.UTF8,
                    "application/json"
                );
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.5 Safari/605.1.15,gzip(gfe)"
                );
                request.Headers.TryAddWithoutValidation("Accept", "application/json");
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId));
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20260114.08.00");
                request.Headers.TryAddWithoutValidation("X-Goog-Visitor-Id", GetFallbackInnertubeVisitorData());

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] WEB Safari player failed: "
                        + response.StatusCode
                        + " "
                        + json
                    );
                    return string.Empty;
                }

                return json;
            }
        }

        private static string BuildNextPayload(string videoId)
        {
            return BuildNextPayload(videoId, null);
        }

        private static string BuildNextPayload(string videoId, string playlistId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20250101");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);

            // With a playlistId the /next response also carries the watch queue
            // (playlistPanelRenderer). This is what drives playlists and auto-generated
            // mixes/"jams" (RD...), which exist only in this watch context.
            if (!string.IsNullOrWhiteSpace(playlistId))
            {
                payload["playlistId"] = JsonValue.CreateStringValue(playlistId);
            }

            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private static string BuildAuthenticatedNextPayload(string videoId, bool mobileWebClient)
        {
            var context = new JsonObject();
            var client = new JsonObject();

            if (mobileWebClient)
            {
                client["clientName"] = JsonValue.CreateStringValue(InnertubeMwebClientName);
                client["clientVersion"] = JsonValue.CreateStringValue(InnertubeMwebClientVersion);
                client["hl"] = JsonValue.CreateStringValue(Config.Hl);
                client["gl"] = JsonValue.CreateStringValue(Config.Gl);
                client["osName"] = JsonValue.CreateStringValue("iOS");
                client["osVersion"] = JsonValue.CreateStringValue("18");
                client["platform"] = JsonValue.CreateStringValue("MOBILE");
            }
            else
            {
                client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
                client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
                client["hl"] = JsonValue.CreateStringValue(Config.Hl);
                client["gl"] = JsonValue.CreateStringValue(Config.Gl);
                client["platform"] = JsonValue.CreateStringValue("TV");
                client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            }

            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        // The queue of a mix ("jam") is personalized: signed in, YouTube seeds it from the
        // account's listening history, so the anonymous WEB /next used for the rest of the page
        // returns a completely different — and to the user, wrong — set of videos. The queue is
        // therefore fetched separately with the TV client and the OAuth bearer, which is the only
        // client that bearer is accepted by.
        private static string BuildAuthenticatedPlaylistNextPayload(string videoId, string playlistId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["playlistId"] = JsonValue.CreateStringValue(playlistId);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private async Task<JsonObject> TryLoadAuthenticatedPlaylistNextAsync(string videoId, string playlistId)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(playlistId))
            {
                return null;
            }

            try
            {
                var accessToken = await GetTvAccessTokenAsync(false);
                if (string.IsNullOrWhiteSpace(accessToken))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[PlaylistQueue] Not signed in; falling back to the anonymous queue"
                    );
                    return null;
                }

                var url = BuildInnertubeUrl("next");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedPlaylistNextPayload(videoId, playlistId),
                        Encoding.UTF8,
                        "application/json"
                    );

                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeTvClientHeaderName,
                        InnertubeTvClientVersion,
                        InnertubeTvUserAgent
                    );

                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[PlaylistQueue] Authenticated /next failed: "
                            + (int)response.StatusCode + " " + response.ReasonPhrase
                        );
                        return null;
                    }

                    return JsonValue.Parse(json).GetObject();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Authenticated /next error: " + ex.Message);
                return null;
            }
        }

        private static string BuildRatingPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var target = new JsonObject();
            target["videoId"] = JsonValue.CreateStringValue(videoId);

            var payload = new JsonObject();
            payload["context"] = context;
            payload["target"] = target;
            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildInnertubeUrl(string endpoint)
        {
            if (string.Equals(endpoint, "player", StringComparison.OrdinalIgnoreCase))
            {
                return "https://www.youtube.com/youtubei/v1/player?noauth=1&prettyPrint=false";
            }

            return "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
        }

        private async Task BindPlayerSourceAsync(string playerJson)
        {
            await BindPlayerSourceAsync(playerJson, true);
        }

        private async Task BindPlayerSourceAsync(string playerJson, bool autoPlay)
        {
            var rootValue = JsonValue.Parse(playerJson);
            await BindPlayerSourceAsync(rootValue.GetObject(), autoPlay);
        }

        private static void LogSelectedPlaybackUrls(
            string effectiveQualityTag,
            string requestedCapTag,
            PlayerFormatModel video,
            PlayerFormatModel audio)
        {
            var mode = string.IsNullOrWhiteSpace(effectiveQualityTag)
                ? "Auto"
                : effectiveQualityTag + "p";
            var cap = ParseInt(requestedCapTag);

            if (video != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] VIDEO mode=" + mode
                    + " cap=" + cap + "p"
                    + " actual=" + video.QualityTier + "p"
                    + " dimensions=" + video.Width + "x" + video.Height
                    + " fps=" + video.Fps
                    + " itag=" + video.Itag
                    + " mime=" + video.MimeType
                    + " URL=" + video.Url);
            }

            if (audio != null)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] AUDIO mode=" + mode
                    + " track=" + (string.IsNullOrWhiteSpace(audio.AudioTrackId) ? "default" : audio.AudioTrackId)
                    + " bitrate=" + audio.Bitrate
                    + " itag=" + audio.Itag
                    + " mime=" + audio.MimeType
                    + " URL=" + audio.Url);
            }
        }

        private async Task BindPlayerSourceAsync(JsonObject rootObject, bool autoPlay)
        {
            CaptureVisitorData(rootObject);
            availableFormats.Clear();
            CollectFormatsFromStreamingData(rootObject);
            _currentH264VideoOnlyItag = -1;
            _readyHeight = 0;

            var hlsManifestUrl = ExtractHlsManifestUrl(rootObject);
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Total available formats: {availableFormats.Count}"
            );
            string effectiveQualityTag = GetEffectiveVideoQualityTag();
            bool isAutomaticQuality = string.IsNullOrWhiteSpace(effectiveQualityTag);
            string sourceQualityTag = string.IsNullOrWhiteSpace(effectiveQualityTag)
                ? GetAutomaticScreenQualityCap().ToString(System.Globalization.CultureInfo.InvariantCulture)
                : effectiveQualityTag;
            if (!isAutomaticQuality)
            {
                // A user's explicit tier is authoritative even when it exceeds the physical
                // screen. Screen sizing and the CDN bandwidth probe belong exclusively to Auto.
                System.Diagnostics.Debug.WriteLine(
                    "[Video][Manual] exact requested cap=" + sourceQualityTag
                    + "p; screen/network Auto checks bypassed");
            }
            UpdateVideoPlayerAspectRatioFromFormats(sourceQualityTag);
            var selected = SelectPreferredProgressiveFormat(availableFormats, sourceQualityTag, false);
            bool isWindowsMobile = IsWindowsMobileDevice();

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetWindowsMobileAudioMode(isWindowsMobile);
                CustomVideoPlayer.CurrentQuality = string.IsNullOrWhiteSpace(effectiveQualityTag) ? null : effectiveQualityTag;

                // Scrub-preview frames. Present in the primary (IOS) player response; if that one
                // lacks them, fall back to the ANDROID response the quality list already fetched.
                var storyboardSpec = ExtractStoryboardSpec(rootObject);
                if (string.IsNullOrWhiteSpace(storyboardSpec))
                {
                    try
                    {
                        var androidJson = await PostAndroidPlayerAsync(currentVideoId);
                        if (!string.IsNullOrWhiteSpace(androidJson))
                        {
                            storyboardSpec = ExtractStoryboardSpec(JsonValue.Parse(androidJson).GetObject());
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] Storyboard android fallback failed: " + ex.Message);
                    }
                }

                System.Diagnostics.Debug.WriteLine("[Video] Storyboard spec length: "
                    + (storyboardSpec == null ? 0 : storyboardSpec.Length));
                CustomVideoPlayer.SetStoryboardSpec(storyboardSpec);
            }

            // Auto follows youtube-ios: best H.264 video-only up to deviceMaxHeight plus AAC
            // audio-only. An ordinary 360p choice may use itag 18, but an explicitly selected
            // audio track must stay on the demuxed path so the choice actually takes effect.
            if (!string.IsNullOrWhiteSpace(effectiveQualityTag))
            {
                System.Diagnostics.Debug.WriteLine("[Video] Effective requested quality before source selection: " + effectiveQualityTag + "p");
            }
            if (ShouldUseItag18AsMainVideo(effectiveQualityTag))
            {
                var itag18 = await GetAndroidItag18FormatAsync(currentVideoId);
                if (itag18 != null && !string.IsNullOrWhiteSpace(itag18.Url))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video][PlaybackURL] MUXED mode="
                        + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
                        + " actual=" + itag18.QualityTier + "p"
                        + " dimensions=" + itag18.Width + "x" + itag18.Height
                        + " itag=" + itag18.Itag
                        + " mime=" + itag18.MimeType
                        + " URL=" + itag18.Url
                    );

                    SetVideoPlayerAspectRatioFromFormat(itag18);
                    _readyHeight = itag18.QualityTier;
                    if (CustomVideoPlayer != null)
                    {
                        await CustomVideoPlayer.SetSourceFromUriAsync(new Uri(itag18.Url), autoPlay);
                    }
                    return;
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] WARNING: itag18 was requested but not found; falling back to HLS/progressive selection"
                );
            }

            // Requested quality is above 360p. Windows 10 Mobile's AdaptiveMediaSource cannot
            // render YouTube's on-demand DASH, so we demux the adaptive fMP4 streams ourselves
            // and feed samples to a MediaStreamSource, then fall back to HLS / muxed.

            // 1) On-the-fly DASH demux: H.264 video-only + AAC audio-only fed into a
            //    MediaStreamSource that MediaPlayer decodes and A/V-syncs. This is the real
            //    >360p (up to 1080p) path on Windows 10 Mobile.
            if (!ShouldUseItag18AsMainVideo(effectiveQualityTag) && CustomVideoPlayer != null)
            {
                var demuxVideo = await GetAndroidH264VideoOnlyFormatAsync(
                    currentVideoId,
                    sourceQualityTag,
                    isAutomaticQuality);
                var demuxAudio = await GetAndroidAudioOnlyFormatAsync(currentVideoId, _selectedAudioTrackId);
                if (demuxVideo != null && demuxAudio != null
                    && !string.IsNullOrWhiteSpace(demuxVideo.Url) && !string.IsNullOrWhiteSpace(demuxAudio.Url))
                {
                    LogSelectedPlaybackUrls(effectiveQualityTag, sourceQualityTag, demuxVideo, demuxAudio);
                    try
                    {
                        var mss = await DashDemuxer.CreateAsync(
                            httpClient,
                            demuxVideo,
                            demuxAudio,
                            _playbackMediaUserAgent
                        );
                        if (mss != null)
                        {
                            _currentH264VideoOnlyItag = demuxVideo.Itag;
                            _readyHeight = demuxVideo.QualityTier;
                            SetVideoPlayerAspectRatioFromFormat(demuxVideo);
                            if (CustomVideoPlayer.SetDemuxedSource(mss, autoPlay))
                            {
                                System.Diagnostics.Debug.WriteLine(
                                    "[Video][PlaybackURL] OPENED mode="
                                    + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
                                    + " actual=" + demuxVideo.QualityTier + "p"
                                    + " itag=" + demuxVideo.Itag);
                                return;
                            }
                        }

                        System.Diagnostics.Debug.WriteLine("[Video] DASH demux source unavailable; falling back");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Video] DASH demux failed: " + ex.Message + "; falling back");
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[Video] DASH demux not attempted (missing video-only or audio-only adaptive stream)");
                }
            }

            // 2) HLS manifest (adaptive, already multiplexes audio). Rare in the SABR era
            //    but native on Windows Phone when present.
            if (!string.IsNullOrWhiteSpace(hlsManifestUrl) && CustomVideoPlayer != null)
            {
                _readyHeight = 0;
                System.Diagnostics.Debug.WriteLine(
                    "[Video][PlaybackURL] HLS mode="
                    + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
                    + " URL=" + hlsManifestUrl
                );
                await CustomVideoPlayer.SetSourceFromUriAsync(new Uri(hlsManifestUrl), autoPlay);
                return;
            }

            // 3) Best muxed progressive <= requested (single file, always renders, no
            //    separate audio, no crash). Tops out at whatever muxed YouTube offers
            //    (usually 720p itag22, else 360p itag18) — the reliable fallback.
            var muxed = await GetAndroidBestMuxedFormatAsync(currentVideoId, sourceQualityTag);
            if (muxed == null || string.IsNullOrWhiteSpace(muxed.Url))
            {
                muxed = selected;
            }

            if (muxed != null && !string.IsNullOrWhiteSpace(muxed.Url))
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Video][PlaybackURL] MUXED mode={(string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")} actual={muxed.QualityTier}p dimensions={muxed.Width}x{muxed.Height} itag={muxed.Itag} mime={muxed.MimeType} URL={muxed.Url}"
                );
                SetVideoPlayerAspectRatioFromFormat(muxed);
                _readyHeight = muxed.QualityTier;
                if (CustomVideoPlayer != null)
                {
                    await CustomVideoPlayer.SetSourceFromUriAsync(new Uri(muxed.Url), autoPlay);
                }
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video] No playable source found for requested quality "
                + (string.IsNullOrWhiteSpace(effectiveQualityTag) ? "Auto" : effectiveQualityTag + "p")
            );

            // Nothing was handed to the player, so MediaOpened/MediaFailed will never fire —
            // release the loading lock here.
            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.EndSourceLoading();
            }
        }

        private async Task<string> GetNativeDashManifestUrlAsync(string videoId, JsonObject primaryRoot)
        {
            // YouTube's own DASH manifest (segment-list, self-muxed) if the player response
            // exposes one. Prefer the primary response, then the ANDROID_VR response.
            var url = ExtractDashManifestUrl(primaryRoot);
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (!string.IsNullOrWhiteSpace(androidJson))
                {
                    var androidRoot = JsonValue.Parse(androidJson).GetObject();
                    url = ExtractDashManifestUrl(androidRoot);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetNativeDashManifestUrlAsync failed: " + ex.Message);
            }

            return url ?? string.Empty;
        }

        private static string ExtractDashManifestUrl(JsonObject root)
        {
            if (root == null || !root.ContainsKey("streamingData"))
            {
                return string.Empty;
            }

            var streamingData = root.GetNamedObject("streamingData");
            return GetJsonString(streamingData, "dashManifestUrl");
        }

        private async Task<PlayerFormatModel> GetAndroidBestMuxedFormatAsync(string videoId, string requestedQualityTag)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                return SelectMuxedFormatForRequestedHeight(androidFormats, requestedQualityTag);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetAndroidBestMuxedFormatAsync failed: " + ex.Message);
                return null;
            }
        }

        // The muxed progressive format that best satisfies the requested height: the HIGHEST
        // muxed height <= requested (so "720p" gives itag 22, not itag 18). For Auto/empty it
        // returns the smallest muxed (bandwidth-friendly, always plays). Unlike
        // SelectPreferredProgressiveFormat this does not hard-prefer itag 18.
        private static PlayerFormatModel SelectMuxedFormatForRequestedHeight(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag
        )
        {
            if (formats == null)
            {
                return null;
            }

            int requestedHeight = ParseInt(requestedQualityTag);
            var muxed = new List<PlayerFormatModel>();
            foreach (var f in formats)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Url))
                {
                    continue;
                }

                if (f.IsAdaptive || !f.HasAudio || !f.HasVideo || f.QualityTier <= 0)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                if (mime.IndexOf("video/mp4") < 0)
                {
                    continue;
                }

                muxed.Add(f);
            }

            if (muxed.Count == 0)
            {
                return null;
            }

            PlayerFormatModel smallest = null;
            foreach (var f in muxed)
            {
                if (smallest == null || f.QualityTier < smallest.QualityTier)
                {
                    smallest = f;
                }
            }

            if (requestedHeight <= 0)
            {
                return smallest;
            }

            PlayerFormatModel best = null;
            foreach (var f in muxed)
            {
                if (f.QualityTier <= requestedHeight && (best == null || f.QualityTier > best.QualityTier))
                {
                    best = f;
                }
            }

            return best ?? smallest;
        }

        private async Task ReloadPlayerOnlyAsync(bool stopBeforeLoad)
        {
            // Pin the video this reload belongs to. Fetching a new quality takes seconds, and the
            // user may well open another video meanwhile — without this the finished reload bound
            // the OLD video's source and it started playing under the new one.
            var reloadVideoId = currentVideoId;
            if (string.IsNullOrWhiteSpace(reloadVideoId))
            {
                return;
            }

            try
            {
                if (stopBeforeLoad && CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.Stop();
                }

                var playerJson = _lastPlayerJson;
                if (string.IsNullOrWhiteSpace(playerJson))
                {
                    playerJson = await PostInnertubeAsync("player", BuildPlayerPayload(reloadVideoId));

                    if (!IsStillCurrentVideo(reloadVideoId))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] Quality reload for " + reloadVideoId + " abandoned - another video is open");
                        return;
                    }

                    _lastPlayerJson = playerJson;
                }

                if (!IsStillCurrentVideo(reloadVideoId))
                {
                    return;
                }

                await BindPlayerSourceAsync(playerJson, false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Player-only reload failed: " + ex.Message);
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.EndSourceLoading();
                }
            }
        }

        private async Task<Uri> GetSeparateAudioUriForPlaybackAsync(string videoId, bool isWindowsMobile)
        {
            try
            {
                var selectedFormat = await GetAndroidItag18FormatAsync(videoId);
                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] itag18 separate audio carrier was not found; trying legacy audio carrier fallback"
                    );
                    selectedFormat = await GetAndroidAudioVideoTrackFormatAsync(videoId, true, isWindowsMobile);
                }

                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Android separate audio-video source was not found"
                    );
                    return null;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Android video-as-audio source selected: itag={selectedFormat.Itag}, height={selectedFormat.Height}, mime={selectedFormat.MimeType}"
                );
                return new Uri(selectedFormat.Url);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Failed to get separate Android audio-video source: " + ex.Message
                );
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidItag18FormatAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidPlayerJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                return SelectItag18ProgressiveFormat(androidFormats);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get Android itag18 format: " + ex.Message);
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidH264VideoOnlyFormatAsync(
            string videoId,
            string requestedQualityTag,
            bool useAutomaticNetworkSelection = false)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidPlayerJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);
                if (useAutomaticNetworkSelection)
                {
                    return await SelectAutomaticH264VideoOnlyFormatAsync(
                        androidFormats,
                        requestedQualityTag,
                        _excludedH264VideoOnlyItags);
                }
                return SelectPreferredH264VideoOnlyFormat(androidFormats, requestedQualityTag, _excludedH264VideoOnlyItags);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get Android H.264 video-only format: " + ex.Message);
                return null;
            }
        }

        private async Task<string> GetWebSafariHlsManifestUrlAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            try
            {
                var safariPlayerJson = await PostWebSafariPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(safariPlayerJson))
                {
                    return string.Empty;
                }

                var safariRoot = JsonValue.Parse(safariPlayerJson).GetObject();
                var hlsManifestUrl = ExtractHlsManifestUrl(safariRoot);
                if (string.IsNullOrWhiteSpace(hlsManifestUrl))
                {
                    var safariFormats = new List<PlayerFormatModel>();
                    CollectFormatsFromStreamingData(safariRoot, safariFormats);
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] yt-dlp WEB Safari player returned no HLS manifest; formats="
                        + safariFormats.Count
                    );

                    if (safariRoot.ContainsKey("playabilityStatus"))
                    {
                        var status = safariRoot.GetNamedObject("playabilityStatus");
                        System.Diagnostics.Debug.WriteLine(
                            "[Video] WEB Safari playability: "
                            + GetJsonString(status, "status")
                            + " "
                            + GetJsonString(status, "reason")
                        );
                    }

                    return string.Empty;
                }

                System.Diagnostics.Debug.WriteLine("[Video] yt-dlp WEB Safari HLS manifest found");
                return hlsManifestUrl;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to get WEB Safari HLS manifest: " + ex.Message);
                return string.Empty;
            }
        }

        private static string GetFallbackInnertubeVisitorData()
        {
            try
            {
                return Uri.UnescapeDataString(InnertubeFallbackVisitorData);
            }
            catch
            {
                return InnertubeFallbackVisitorData;
            }
        }

        // Adopt the server-issued visitorData from any youtubei response's responseContext
        // (MeeTube's core::Http visitor sink). Only stores the first non-empty value so the
        // whole session presents one stable returning identity.
        private void CaptureVisitorData(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("responseContext"))
                {
                    return;
                }

                var responseContext = root.GetNamedObject("responseContext");
                var visitorData = GetJsonString(responseContext, "visitorData");
                if (string.IsNullOrWhiteSpace(visitorData))
                {
                    return;
                }

                lock (_visitorDataGate)
                {
                    if (string.IsNullOrWhiteSpace(_sessionVisitorData))
                    {
                        _sessionVisitorData = visitorData;
                        System.Diagnostics.Debug.WriteLine("[Video] Captured session visitorData from responseContext");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] CaptureVisitorData failed: " + ex.Message);
            }
        }

        // The visitorData ANDROID_VR needs. Returns the captured session value, else fetches
        // a fresh one from an anonymous WEB /player (deduped), else the hardcoded fallback.
        private async Task<string> GetSessionVisitorDataAsync()
        {
            lock (_visitorDataGate)
            {
                if (!string.IsNullOrWhiteSpace(_sessionVisitorData))
                {
                    return _sessionVisitorData;
                }
            }

            Task<string> fetchTask;
            lock (_visitorDataGate)
            {
                if (!string.IsNullOrWhiteSpace(_sessionVisitorData))
                {
                    return _sessionVisitorData;
                }

                if (_visitorDataFetchTask == null)
                {
                    _visitorDataFetchTask = FetchFreshVisitorDataAsync();
                }

                fetchTask = _visitorDataFetchTask;
            }

            string fetched = null;
            try
            {
                fetched = await fetchTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] visitorData fetch failed: " + ex.Message);
            }

            if (!string.IsNullOrWhiteSpace(fetched))
            {
                lock (_visitorDataGate)
                {
                    if (string.IsNullOrWhiteSpace(_sessionVisitorData))
                    {
                        _sessionVisitorData = fetched;
                    }

                    return _sessionVisitorData;
                }
            }

            return GetFallbackInnertubeVisitorData();
        }

        // One lightweight anonymous WEB /player call whose only purpose is to read a fresh
        // responseContext.visitorData (mirrors how MeeTube's first youtubei call seeds it).
        private async Task<string> FetchFreshVisitorDataAsync()
        {
            var videoId = currentVideoId;
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            try
            {
                var url = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildWebVisitorSeedPayload(videoId),
                        Encoding.UTF8,
                        "application/json"
                    );
                    request.Headers.TryAddWithoutValidation(
                        "User-Agent",
                        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
                    );
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");
                    request.Headers.TryAddWithoutValidation("Accept-Language", Config.Hl);
                    request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", "1");
                    request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", "2.20260626.01.00");

                    var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                    var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        return string.Empty;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    if (root.ContainsKey("responseContext"))
                    {
                        return GetJsonString(root.GetNamedObject("responseContext"), "visitorData");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] FetchFreshVisitorDataAsync failed: " + ex.Message);
            }

            return string.Empty;
        }

        private static string BuildWebVisitorSeedPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue("WEB");
            client["clientVersion"] = JsonValue.CreateStringValue("2.20260626.01.00");
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private async Task<string> FixNParameterWithPlayerScriptAsync(string videoId, string streamUrl)
        {
            if (string.IsNullOrWhiteSpace(videoId) || string.IsNullOrWhiteSpace(streamUrl))
            {
                return streamUrl;
            }

            var originalN = GetQueryParameterFromUrl(streamUrl, "n");
            if (string.IsNullOrWhiteSpace(originalN))
            {
                System.Diagnostics.Debug.WriteLine("[Video] H.264 video-only URL has no n parameter; using yt-dlp JS-less client URL as-is");
                return streamUrl;
            }

            try
            {
                var playerScriptUrl = await GetPlayerScriptUrlAsync(videoId);
                if (string.IsNullOrWhiteSpace(playerScriptUrl))
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Could not find YouTube player base.js URL");
                    return string.Empty;
                }

                var playerScript = await GetPlayerScriptAsync(playerScriptUrl);
                if (string.IsNullOrWhiteSpace(playerScript))
                {
                    return string.Empty;
                }

                var nFunctionExpression = ExtractNFunctionExpression(playerScript);
                if (string.IsNullOrWhiteSpace(nFunctionExpression))
                {
                    System.Diagnostics.Debug.WriteLine("[Video] Could not extract n function name from player script");
                    return string.Empty;
                }

                var fixedN = await ExecuteNFunctionInWebViewAsync(playerScript, nFunctionExpression, originalN);
                if (string.IsNullOrWhiteSpace(fixedN) || string.Equals(fixedN, originalN, StringComparison.Ordinal))
                {
                    return string.Empty;
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Fixed n parameter using player script: "
                    + originalN
                    + " -> "
                    + fixedN
                );
                return ReplaceQueryParameter(streamUrl, "n", fixedN);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to fix n parameter: " + ex.Message);
                return string.Empty;
            }
        }

        private static string PrepareVideoOnlyUrlForUwpPlayback(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            if (!string.IsNullOrWhiteSpace(GetQueryParameterFromUrl(url, "range")))
            {
                return url;
            }

            System.Diagnostics.Debug.WriteLine("[Video] Added range=0- to H.264 video-only URL for UWP playback");
            return ReplaceQueryParameter(url, "range", "0-");
        }

        private async Task<Uri> CreateVideoOnlyDashManifestUriAsync(
            string videoId,
            PlayerFormatModel format,
            string streamUrl
        )
        {
            if (
                format == null
                || string.IsNullOrWhiteSpace(streamUrl)
                || string.IsNullOrWhiteSpace(format.InitRangeStart)
                || string.IsNullOrWhiteSpace(format.InitRangeEnd)
                || string.IsNullOrWhiteSpace(format.IndexRangeStart)
                || string.IsNullOrWhiteSpace(format.IndexRangeEnd)
            )
            {
                return null;
            }

            try
            {
                var durationSeconds = GetDashDurationSeconds(streamUrl);
                var codecs = ExtractCodecsFromMimeType(format.MimeType);
                var bandwidth = format.AverageBitrate > 0
                    ? format.AverageBitrate
                    : format.Bitrate;
                if (bandwidth <= 0)
                {
                    bandwidth = 500000;
                }

                var frameRate = format.Fps > 0 ? " frameRate=\"" + format.Fps + "\"" : string.Empty;
                var mpd = new StringBuilder();
                mpd.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                mpd.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT" + durationSeconds + "S\" minBufferTime=\"PT1.5S\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\">");
                mpd.AppendLine("  <Period>");
                mpd.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\" startWithSAP=\"1\">");
                mpd.AppendLine("      <Representation id=\"" + format.Itag + "\" codecs=\"" + EscapeXml(codecs) + "\" bandwidth=\"" + bandwidth + "\" width=\"" + format.Width + "\" height=\"" + format.Height + "\"" + frameRate + ">");
                mpd.AppendLine("        <BaseURL>" + EscapeXml(streamUrl) + "</BaseURL>");
                mpd.AppendLine("        <SegmentBase indexRange=\"" + EscapeXml(format.IndexRangeStart) + "-" + EscapeXml(format.IndexRangeEnd) + "\">");
                mpd.AppendLine("          <Initialization range=\"" + EscapeXml(format.InitRangeStart) + "-" + EscapeXml(format.InitRangeEnd) + "\" />");
                mpd.AppendLine("        </SegmentBase>");
                mpd.AppendLine("      </Representation>");
                mpd.AppendLine("    </AdaptationSet>");
                mpd.AppendLine("  </Period>");
                mpd.AppendLine("</MPD>");

                var safeVideoId = Regex.Replace(videoId ?? string.Empty, @"[^A-Za-z0-9_-]+", "_");
                if (string.IsNullOrWhiteSpace(safeVideoId))
                {
                    safeVideoId = "video";
                }

                var fileName = "youtube_videoonly_" + safeVideoId + "_" + format.Itag + ".mpd";
                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.ReplaceExisting
                );
                await FileIO.WriteTextAsync(file, mpd.ToString());

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Created DASH manifest for H.264 video-only playback: "
                    + fileName
                    + ", initRange="
                    + format.InitRangeStart
                    + "-"
                    + format.InitRangeEnd
                    + ", indexRange="
                    + format.IndexRangeStart
                    + "-"
                    + format.IndexRangeEnd
                );
                return new Uri("ms-appdata:///temp/" + fileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to create DASH manifest for video-only source: " + ex.Message);
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidAudioOnlyFormatAsync(string videoId, string preferredTrackId = null)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return null;
                }

                var androidRoot = JsonValue.Parse(androidJson).GetObject();
                var androidFormats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, androidFormats);

                // Remember what languages this video offers so the settings menu can list them.
                _availableAudioTracks = Config.EnumerateAudioTracks(androidFormats);

                return SelectAudioOnlyAacFormat(androidFormats, preferredTrackId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetAndroidAudioOnlyFormatAsync failed: " + ex.Message);
                return null;
            }
        }

        // --- Audio track selection ------------------------------------------------------------

        private List<Config.AudioTrackInfo> _availableAudioTracks = new List<Config.AudioTrackInfo>();
        private string _selectedAudioTrackId;

        // Ensures the track list is populated even when the current playback path never demuxed
        // (e.g. Auto/progressive) — it fetches the ANDROID formats just to enumerate languages.
        private async Task EnsureAudioTracksLoadedAsync()
        {
            if (_availableAudioTracks != null && _availableAudioTracks.Count > 0)
            {
                return;
            }
            try
            {
                var androidJson = await PostAndroidPlayerAsync(currentVideoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return;
                }
                var root = JsonValue.Parse(androidJson).GetObject();
                var formats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(root, formats);
                _availableAudioTracks = Config.EnumerateAudioTracks(formats);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] EnsureAudioTracksLoaded failed: " + ex.Message);
            }
        }

        // AAC (mp4a) audio-only track for the combined DASH manifest. AAC is the WP-friendly
        // codec. On a multi-language video the Windows system language is preferred, then the
        // server default; passing a specific trackId overrides that. Within the chosen
        // track, the highest-bitrate m4a wins.
        private static PlayerFormatModel SelectAudioOnlyAacFormat(
            IList<PlayerFormatModel> formats, string preferredTrackId = null)
        {
            if (formats == null)
            {
                return null;
            }

            // All usable AAC audio-only formats.
            var audio = new List<PlayerFormatModel>();
            foreach (var f in formats)
            {
                if (f == null || string.IsNullOrWhiteSpace(f.Url) || !f.IsAdaptive || !f.HasAudio || f.HasVideo)
                {
                    continue;
                }
                var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                if (mime.IndexOf("audio/mp4") < 0 && mime.IndexOf("mp4a") < 0)
                {
                    continue;
                }
                audio.Add(f);
            }

            if (audio.Count == 0)
            {
                return null;
            }

            // Explicit choice, Windows system language, YouTube's default, then highest bitrate.
            List<PlayerFormatModel> pool = null;
            if (!string.IsNullOrEmpty(preferredTrackId))
            {
                pool = audio.FindAll(f => string.Equals(f.AudioTrackId, preferredTrackId, StringComparison.Ordinal));
            }
            if (pool == null || pool.Count == 0)
            {
                var systemScore = 0;
                foreach (var f in audio)
                {
                    systemScore = Math.Max(systemScore, Config.SystemAudioTrackMatchScore(f.AudioTrackId));
                }
                if (systemScore > 0)
                {
                    pool = audio.FindAll(f => Config.SystemAudioTrackMatchScore(f.AudioTrackId) == systemScore);
                }
                else
                {
                    var defaults = audio.FindAll(f => f.AudioIsDefault);
                    pool = defaults.Count > 0 ? defaults : audio;
                }
            }

            PlayerFormatModel best = null;
            foreach (var f in pool)
            {
                if (best == null || f.Bitrate > best.Bitrate)
                {
                    best = f;
                }
            }

            return best;
        }

        // Exact screen-tier part of youtube-ios YTSabr.tierForScreen:. UIKit passes the
        // physical short edge (points * scale), not the current video rectangle.
        private static int GetAutomaticScreenQualityCap()
        {
            var tiers = new[] { 144, 240, 360, 480, 720, 1080 };
            double scale = 1.0;
            double shortEdge = 0.0;
            try
            {
                scale = Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
                if (scale <= 0.0)
                {
                    scale = 1.0;
                }
                var bounds = Window.Current.Bounds;
                shortEdge = Math.Min(bounds.Width, bounds.Height) * scale;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video][Auto] screen query failed: " + ex.Message);
            }

            var nearest = AutomaticVideoQualityCap;
            if (shortEdge > 0.0)
            {
                nearest = tiers[0];
                var nearestDistance = Math.Abs(shortEdge - nearest);
                for (int i = 1; i < tiers.Length; i++)
                {
                    var distance = Math.Abs(shortEdge - tiers[i]);
                    if (distance < nearestDistance)
                    {
                        nearest = tiers[i];
                        nearestDistance = distance;
                    }
                }
            }

            nearest = Math.Min(nearest, AutomaticVideoQualityCap);
            System.Diagnostics.Debug.WriteLine(
                "[Video][Auto] screen=" + shortEdge.ToString("0", CultureInfo.InvariantCulture)
                + "px scale=" + scale.ToString("0.##", CultureInfo.InvariantCulture)
                + " cap=" + nearest + "p");
            return nearest;
        }

        private async Task<PlayerFormatModel> SelectAutomaticH264VideoOnlyFormatAsync(
            IList<PlayerFormatModel> formats,
            string screenQualityTag,
            ISet<int> excludedItags)
        {
            var screenChoice = SelectPreferredH264VideoOnlyFormat(
                formats,
                screenQualityTag,
                excludedItags);
            if (screenChoice == null)
            {
                return null;
            }

            var now = DateTimeOffset.UtcNow;
            if (_autoNetworkBitsPerSecond <= 0.0
                || (now - _autoNetworkEstimateTime).TotalSeconds > 30.0)
            {
                _autoNetworkBitsPerSecond = await MeasurePlaybackBandwidthAsync(screenChoice.Url);
                _autoNetworkEstimateTime = now;
            }

            if (_autoNetworkBitsPerSecond <= 0.0)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video][Auto] bandwidth unavailable; using screen cap "
                    + screenChoice.QualityTier + "p");
                return screenChoice;
            }

            var usableVideoBitsPerSecond = (_autoNetworkBitsPerSecond * AutoNetworkSafetyFactor)
                - AutoAudioBandwidthReserve;
            var screenCap = ParseInt(screenQualityTag);
            var tiersDescending = new[] { 1080, 720, 480, 360, 240, 144 };
            PlayerFormatModel lowest = null;
            for (int i = 0; i < tiersDescending.Length; i++)
            {
                var tier = tiersDescending[i];
                if (screenCap > 0 && tier > screenCap)
                {
                    continue;
                }

                var candidate = SelectPreferredH264VideoOnlyFormat(
                    formats,
                    tier.ToString(CultureInfo.InvariantCulture),
                    excludedItags);
                if (candidate == null)
                {
                    continue;
                }

                if (lowest == null || candidate.QualityTier < lowest.QualityTier)
                {
                    lowest = candidate;
                }

                var bitrate = candidate.AverageBitrate > 0
                    ? candidate.AverageBitrate
                    : candidate.Bitrate;
                if (bitrate <= 0 || bitrate <= usableVideoBitsPerSecond)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Video][Auto] bandwidth="
                        + (_autoNetworkBitsPerSecond / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture)
                        + "Mbps usableVideo="
                        + (Math.Max(0.0, usableVideoBitsPerSecond) / 1000000.0).ToString("0.00", CultureInfo.InvariantCulture)
                        + "Mbps selected=" + candidate.QualityTier + "p"
                        + " bitrate=" + bitrate);
                    return candidate;
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "[Video][Auto] connection is below the lowest advertised bitrate; selected "
                + (lowest == null ? screenChoice.QualityTier : lowest.QualityTier) + "p");
            return lowest ?? screenChoice;
        }

        private async Task<double> MeasurePlaybackBandwidthAsync(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return 0.0;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new RangeHeaderValue(0, AutoNetworkProbeBytes - 1);
                    if (!string.IsNullOrWhiteSpace(_playbackMediaUserAgent))
                    {
                        request.Headers.TryAddWithoutValidation("User-Agent", _playbackMediaUserAgent);
                    }

                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    using (var response = await httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[Video][Auto] bandwidth probe HTTP " + (int)response.StatusCode);
                            return 0.0;
                        }

                        long received = 0;
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[32768];
                            while (received < AutoNetworkProbeBytes)
                            {
                                var wanted = (int)Math.Min(buffer.Length, AutoNetworkProbeBytes - received);
                                var count = await stream.ReadAsync(buffer, 0, wanted);
                                if (count <= 0)
                                {
                                    break;
                                }
                                received += count;
                            }
                        }
                        timer.Stop();

                        if (received < 32768 || timer.Elapsed.TotalSeconds <= 0.0)
                        {
                            return 0.0;
                        }
                        return received * 8.0 / timer.Elapsed.TotalSeconds;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video][Auto] bandwidth probe failed: " + ex.Message);
                return 0.0;
            }
        }

        // Build a combined video+audio on-demand DASH manifest (two AdaptationSets, each an
        // indexed single-file SegmentBase representation). AdaptiveMediaSource muxes/syncs the
        // two remote streams itself. Requires init/index byte ranges on both formats.
        private async Task<Uri> CreateCombinedDashManifestUriAsync(
            string videoId,
            PlayerFormatModel video,
            PlayerFormatModel audio
        )
        {
            if (
                video == null
                || audio == null
                || string.IsNullOrWhiteSpace(video.Url)
                || string.IsNullOrWhiteSpace(audio.Url)
                || string.IsNullOrWhiteSpace(video.InitRangeStart)
                || string.IsNullOrWhiteSpace(video.InitRangeEnd)
                || string.IsNullOrWhiteSpace(video.IndexRangeStart)
                || string.IsNullOrWhiteSpace(video.IndexRangeEnd)
                || string.IsNullOrWhiteSpace(audio.InitRangeStart)
                || string.IsNullOrWhiteSpace(audio.InitRangeEnd)
                || string.IsNullOrWhiteSpace(audio.IndexRangeStart)
                || string.IsNullOrWhiteSpace(audio.IndexRangeEnd)
            )
            {
                System.Diagnostics.Debug.WriteLine("[Video] Combined DASH manifest skipped: missing byte ranges");
                return null;
            }

            try
            {
                var durationSeconds = GetDashDurationSeconds(video.Url);
                var videoCodecs = ExtractCodecsFromMimeType(video.MimeType);
                var audioCodecs = ExtractCodecsFromMimeType(audio.MimeType);
                var videoBandwidth = video.AverageBitrate > 0
                    ? video.AverageBitrate
                    : (video.Bitrate > 0 ? video.Bitrate : 1500000);
                var audioBandwidth = audio.AverageBitrate > 0
                    ? audio.AverageBitrate
                    : (audio.Bitrate > 0 ? audio.Bitrate : 128000);
                var frameRate = video.Fps > 0 ? " frameRate=\"" + video.Fps + "\"" : string.Empty;

                var mpd = new StringBuilder();
                mpd.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
                mpd.AppendLine("<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT" + durationSeconds + "S\" minBufferTime=\"PT1.5S\" profiles=\"urn:mpeg:dash:profile:isoff-on-demand:2011\">");
                mpd.AppendLine("  <Period>");
                mpd.AppendLine("    <AdaptationSet mimeType=\"video/mp4\" segmentAlignment=\"true\" startWithSAP=\"1\">");
                mpd.AppendLine("      <Representation id=\"" + video.Itag + "\" codecs=\"" + EscapeXml(videoCodecs) + "\" bandwidth=\"" + videoBandwidth + "\" width=\"" + video.Width + "\" height=\"" + video.Height + "\"" + frameRate + ">");
                mpd.AppendLine("        <BaseURL>" + EscapeXml(video.Url) + "</BaseURL>");
                mpd.AppendLine("        <SegmentBase indexRange=\"" + EscapeXml(video.IndexRangeStart) + "-" + EscapeXml(video.IndexRangeEnd) + "\">");
                mpd.AppendLine("          <Initialization range=\"" + EscapeXml(video.InitRangeStart) + "-" + EscapeXml(video.InitRangeEnd) + "\" />");
                mpd.AppendLine("        </SegmentBase>");
                mpd.AppendLine("      </Representation>");
                mpd.AppendLine("    </AdaptationSet>");
                mpd.AppendLine("    <AdaptationSet mimeType=\"audio/mp4\" segmentAlignment=\"true\" startWithSAP=\"1\">");
                mpd.AppendLine("      <Representation id=\"" + audio.Itag + "\" codecs=\"" + EscapeXml(audioCodecs) + "\" bandwidth=\"" + audioBandwidth + "\" audioSamplingRate=\"44100\">");
                mpd.AppendLine("        <AudioChannelConfiguration schemeIdUri=\"urn:mpeg:dash:23003:3:audio_channel_configuration:2011\" value=\"2\" />");
                mpd.AppendLine("        <BaseURL>" + EscapeXml(audio.Url) + "</BaseURL>");
                mpd.AppendLine("        <SegmentBase indexRange=\"" + EscapeXml(audio.IndexRangeStart) + "-" + EscapeXml(audio.IndexRangeEnd) + "\">");
                mpd.AppendLine("          <Initialization range=\"" + EscapeXml(audio.InitRangeStart) + "-" + EscapeXml(audio.InitRangeEnd) + "\" />");
                mpd.AppendLine("        </SegmentBase>");
                mpd.AppendLine("      </Representation>");
                mpd.AppendLine("    </AdaptationSet>");
                mpd.AppendLine("  </Period>");
                mpd.AppendLine("</MPD>");

                var safeVideoId = Regex.Replace(videoId ?? string.Empty, @"[^A-Za-z0-9_-]+", "_");
                if (string.IsNullOrWhiteSpace(safeVideoId))
                {
                    safeVideoId = "video";
                }

                var fileName = "youtube_av_" + safeVideoId + "_" + video.Itag + "_" + audio.Itag + ".mpd";
                var file = await ApplicationData.Current.TemporaryFolder.CreateFileAsync(
                    fileName,
                    CreationCollisionOption.ReplaceExisting
                );
                await FileIO.WriteTextAsync(file, mpd.ToString());

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Created combined A/V DASH manifest: " + fileName
                    + " (video itag=" + video.Itag + ", audio itag=" + audio.Itag + ")"
                );
                return new Uri("ms-appdata:///temp/" + fileName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to create combined DASH manifest: " + ex.Message);
                return null;
            }
        }

        private static string GetDashDurationSeconds(string streamUrl)
        {
            var dur = GetQueryParameterFromUrl(streamUrl, "dur");
            double duration;
            if (
                !string.IsNullOrWhiteSpace(dur)
                && double.TryParse(dur, NumberStyles.Float, CultureInfo.InvariantCulture, out duration)
                && duration > 0
            )
            {
                return duration.ToString("0.###", CultureInfo.InvariantCulture);
            }

            return "0";
        }

        private static string ExtractCodecsFromMimeType(string mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                return "avc1.4d401e";
            }

            var match = Regex.Match(mimeType, "codecs=\"(?<codecs>[^\"]+)\"");
            return match.Success ? match.Groups["codecs"].Value : "avc1.4d401e";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private async Task<string> GetPlayerScriptUrlAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            var watchUrl = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(videoId);
            using (var request = new HttpRequestMessage(HttpMethod.Get, watchUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36"
                );
                request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);

                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                var match = Regex.Match(
                    html,
                    @"(?<path>(?:https?:)?(?:\\?/\\?/www\.youtube\.com)?\\?/s\\?/player\\?/[0-9a-fA-F]+\\?/[^""']+?base\.js)"
                );
                if (!match.Success)
                {
                    match = Regex.Match(
                        html,
                        @"(?<path>/s/player/[0-9a-fA-F]+/[^""'\\]+base\.js)"
                    );
                }

                if (!match.Success)
                {
                    return string.Empty;
                }

                var playerPath = match.Groups["path"].Value.Replace("\\/", "/");
                if (playerPath.StartsWith("//", StringComparison.Ordinal))
                {
                    return "https:" + playerPath;
                }

                if (playerPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || playerPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    return playerPath;
                }

                return "https://www.youtube.com" + playerPath;
            }
        }

        private async Task<string> GetPlayerScriptAsync(string playerScriptUrl)
        {
            if (string.IsNullOrWhiteSpace(playerScriptUrl))
            {
                return string.Empty;
            }

            Task<string> requestTask;
            lock (_playerScriptGate)
            {
                if (string.Equals(_lastPlayerScriptUrl, playerScriptUrl, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(_lastPlayerScript))
                {
                    return _lastPlayerScript;
                }

                if (_playerScriptRequestTask != null
                    && string.Equals(_playerScriptRequestUrl, playerScriptUrl, StringComparison.Ordinal))
                {
                    requestTask = _playerScriptRequestTask;
                }
                else
                {
                    _playerScriptRequestUrl = playerScriptUrl;
                    _playerScriptRequestTask = DownloadPlayerScriptCoreAsync(playerScriptUrl);
                    requestTask = _playerScriptRequestTask;
                }
            }

            string script;
            try
            {
                script = await requestTask;
            }
            finally
            {
                lock (_playerScriptGate)
                {
                    _playerScriptRequestTask = null;
                }
            }

            lock (_playerScriptGate)
            {
                _lastPlayerScriptUrl = playerScriptUrl;
                _lastPlayerScript = script;
            }

            return script;
        }

        private async Task<string> DownloadPlayerScriptCoreAsync(string playerScriptUrl)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, playerScriptUrl))
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0 Safari/537.36"
                );
                var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            }
        }

        private async Task<string> ExecuteNFunctionInWebViewAsync(
            string playerScript,
            string nFunctionExpression,
            string nValue
        )
        {
            var tcs = new TaskCompletionSource<string>();
            var dispatcher = CoreApplication.MainView.CoreWindow.Dispatcher;

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                WebView webView = null;
                try
                {
                    webView = new WebView();
                    var ready = new TaskCompletionSource<bool>();
                    Windows.Foundation.TypedEventHandler<WebView, WebViewNavigationCompletedEventArgs> handler = null;
                    handler = (sender, args) =>
                    {
                        webView.NavigationCompleted -= handler;
                        ready.TrySetResult(true);
                    };
                    webView.NavigationCompleted += handler;
                    webView.NavigateToString("<html><head><meta charset=\"utf-8\" /></head><body></body></html>");
                    await ready.Task;

                    var scriptToEvaluate = BuildPlayerScriptWithNExport(playerScript, nFunctionExpression);
                    await webView.InvokeScriptAsync("eval", new[] { scriptToEvaluate });

                    var callScript = "window.__yt_nsig(" + ToJavaScriptStringLiteral(nValue) + ");";
                    var result = await webView.InvokeScriptAsync("eval", new[] { callScript });
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Video] WebView n function failed: " + ex.Message);
                    tcs.TrySetResult(string.Empty);
                }
            });

            return await tcs.Task;
        }

        private static string BuildPlayerScriptWithNExport(string playerScript, string nFunctionExpression)
        {
            var exportScript =
                ";window.__yt_nsig=function(n){return "
                + nFunctionExpression
                + "(n);};";
            var closingIndex = playerScript.LastIndexOf(";})(_yt_player);", StringComparison.Ordinal);
            if (closingIndex >= 0)
            {
                return playerScript.Insert(closingIndex, exportScript);
            }

            closingIndex = playerScript.LastIndexOf("})(_yt_player);", StringComparison.Ordinal);
            if (closingIndex >= 0)
            {
                return playerScript.Insert(closingIndex, exportScript);
            }

            return playerScript + exportScript;
        }

        private static string ExtractNFunctionExpression(string playerScript)
        {
            if (string.IsNullOrWhiteSpace(playerScript))
            {
                return string.Empty;
            }

            var patterns = new[]
            {
                @"\.get\(""n""\)\)&&\([a-zA-Z_$][\w$]*=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\([a-zA-Z_$][\w$]*\)",
                @"[a-zA-Z_$][\w$]*=String\.fromCharCode\(110\),[a-zA-Z_$][\w$]*=[a-zA-Z_$][\w$]*\.get\([a-zA-Z_$][\w$]*\)\)&&\([a-zA-Z_$][\w$]*=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\([a-zA-Z_$][\w$]*\)",
                @"\b(?<var>[a-zA-Z_$][\w$]*)&&\(\k<var>=(?<name>[a-zA-Z_$][\w$]*)(?:\[(?<idx>\d+)\])?\(\k<var>\)",
                @"(?<name>[a-zA-Z_$][\w$]*)=function\([a-zA-Z_$][\w$]*\)\{(?=[\s\S]{0,1200}\.split\(""""\))(?=[\s\S]{0,2400}\.join\(""""\))"
            };

            for (int i = 0; i < patterns.Length; i++)
            {
                var match = Regex.Match(playerScript, patterns[i]);
                if (!match.Success)
                {
                    continue;
                }

                var name = match.Groups["name"].Value;
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var indexGroup = match.Groups["idx"];
                if (indexGroup != null && indexGroup.Success && !string.IsNullOrWhiteSpace(indexGroup.Value))
                {
                    return name + "[" + indexGroup.Value + "]";
                }

                return name;
            }

            return string.Empty;
        }

        private async Task<PlayerFormatModel> GetAndroidSingleMediaSourceForMobileAsync(string videoId)
        {
            try
            {
                var selectedFormat = await GetAndroidAudioVideoTrackFormatAsync(videoId, false, false);
                if (selectedFormat == null || string.IsNullOrWhiteSpace(selectedFormat.Url))
                {
                    return null;
                }

                return selectedFormat;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Failed to get Android single-source media for mobile: " + ex.Message
                );
                return null;
            }
        }

        private async Task<PlayerFormatModel> GetAndroidAudioVideoTrackFormatAsync(
            string videoId,
            bool preferSmallest,
            bool preferMobileCarrier
        )
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var androidPlayerJson = await PostAndroidPlayerAsync(videoId);
            if (string.IsNullOrWhiteSpace(androidPlayerJson))
            {
                return null;
            }

            var androidRoot = JsonValue.Parse(androidPlayerJson).GetObject();
            var androidFormats = new List<PlayerFormatModel>();
            CollectFormatsFromStreamingData(androidRoot, androidFormats);

            if (preferSmallest)
            {
                if (preferMobileCarrier)
                {
                    var mobileCarrier = SelectPreferredMobileAudioCarrierFormat(androidFormats);
                    if (mobileCarrier != null)
                    {
                        return mobileCarrier;
                    }
                }

                return SelectPreferredAudioVideoTrackFormat(androidFormats);
            }

            // Not used for the main Mobile path anymore. If this fallback is called,
            // still prefer the best progressive MP4 available.
            return SelectPreferredProgressiveFormat(androidFormats, string.Empty, false)
                ?? SelectPreferredAudioVideoTrackFormat(androidFormats);
        }

        private static string ExtractLastThumbnailUrl(JsonObject objectWithThumbs)
        {
            if (objectWithThumbs == null || !objectWithThumbs.ContainsKey("thumbnails"))
            {
                return string.Empty;
            }

            var thumbs = objectWithThumbs.GetNamedArray("thumbnails");
            if (thumbs.Count == 0)
            {
                return string.Empty;
            }

            for (int i = (int)thumbs.Count - 1; i >= 0; i--)
            {
                var thumbObj = thumbs[i].GetObject();
                var url = GetJsonString(thumbObj, "url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }
            }

            return string.Empty;
        }

        private void CollectFormatsFromStreamingData(JsonObject root)
        {
            CollectFormatsFromStreamingData(root, availableFormats);
        }

        private static void CollectFormatsFromStreamingData(
            JsonObject root,
            IList<PlayerFormatModel> targetFormats
        )
        {
            if (
                root == null
                || targetFormats == null
                || !root.ContainsKey("streamingData")
            )
            {
                return;
            }

            var streamingData = root.GetNamedObject("streamingData");
            AddFormatsFromArray(streamingData, "formats", targetFormats, false);
            AddFormatsFromArray(streamingData, "adaptiveFormats", targetFormats, true);
        }

        private static void AddFormatsFromArray(
            JsonObject streamingData,
            string arrayName,
            IList<PlayerFormatModel> targetFormats,
            bool isAdaptive
        )
        {
            if (
                streamingData == null
                || targetFormats == null
                || !streamingData.ContainsKey(arrayName)
            )
            {
                return;
            }

            var formatsArray = streamingData.GetNamedArray(arrayName);
            for (int i = 0; i < formatsArray.Count; i++)
            {
                if (formatsArray[i].ValueType != JsonValueType.Object)
                {
                    continue;
                }

                var formatObj = formatsArray[i].GetObject();
                var streamUrl = GetStreamUrlFromFormat(formatObj);
                if (string.IsNullOrWhiteSpace(streamUrl))
                {
                    continue;
                }

                var mimeType = GetJsonString(formatObj, "mimeType");
                targetFormats.Add(
                    new PlayerFormatModel
                    {
                        Url = streamUrl,
                        Width = ParseIntOrDefault(formatObj, "width"),
                        Height = ParseIntOrDefault(formatObj, "height"),
                        MimeType = mimeType,
                        Itag = ParseIntOrDefault(formatObj, "itag"),
                        Fps = ParseIntOrDefault(formatObj, "fps"),
                        Bitrate = ParseIntOrDefault(formatObj, "bitrate"),
                        AverageBitrate = ParseIntOrDefault(formatObj, "averageBitrate"),
                        InitRangeStart = GetRangeValue(formatObj, "initRange", "start"),
                        InitRangeEnd = GetRangeValue(formatObj, "initRange", "end"),
                        IndexRangeStart = GetRangeValue(formatObj, "indexRange", "start"),
                        IndexRangeEnd = GetRangeValue(formatObj, "indexRange", "end"),
                        HasAudio = FormatHasAudio(formatObj, mimeType),
                        HasVideo = FormatHasVideo(formatObj, mimeType),
                        IsAdaptive = isAdaptive,
                        AudioTrackId = GetAudioTrackField(formatObj, "id"),
                        AudioTrackName = GetAudioTrackField(formatObj, "displayName"),
                        AudioIsDefault = GetAudioTrackIsDefault(formatObj),
                    }
                );
            }
        }

        // The per-format "audioTrack" block appears only on multi-language videos.
        private static string GetAudioTrackField(JsonObject formatObj, string key)
        {
            try
            {
                if (formatObj != null && formatObj.ContainsKey("audioTrack"))
                {
                    var track = formatObj.GetNamedObject("audioTrack");
                    if (track.ContainsKey(key))
                    {
                        return track.GetNamedString(key);
                    }
                }
            }
            catch { }
            return string.Empty;
        }

        private static bool GetAudioTrackIsDefault(JsonObject formatObj)
        {
            try
            {
                if (formatObj != null && formatObj.ContainsKey("audioTrack"))
                {
                    var track = formatObj.GetNamedObject("audioTrack");
                    if (track.ContainsKey("audioIsDefault"))
                    {
                        return track.GetNamedBoolean("audioIsDefault");
                    }
                }
            }
            catch { }
            return false;
        }

        private static string GetStreamUrlFromFormat(JsonObject formatObj)
        {
            if (formatObj == null)
            {
                return string.Empty;
            }

            var url = GetJsonString(formatObj, "url");
            if (!string.IsNullOrWhiteSpace(url))
            {
                return url;
            }

            var signatureCipher = FirstNonEmpty(
                GetJsonString(formatObj, "signatureCipher"),
                GetJsonString(formatObj, "cipher")
            );

            if (string.IsNullOrWhiteSpace(signatureCipher))
            {
                return string.Empty;
            }

            var parsed = ParseQueryString(signatureCipher);
            string cipherUrl;
            if (!parsed.TryGetValue("url", out cipherUrl) || string.IsNullOrWhiteSpace(cipherUrl))
            {
                return string.Empty;
            }

            string signature;
            if (
                parsed.TryGetValue("sig", out signature)
                || parsed.TryGetValue("signature", out signature)
            )
            {
                if (!string.IsNullOrWhiteSpace(signature))
                {
                    return AppendQueryParameter(cipherUrl, "sig", signature);
                }
            }

            string s;
            if (parsed.TryGetValue("s", out s) && !string.IsNullOrWhiteSpace(s))
            {
                return string.Empty;
            }

            return cipherUrl;
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(query))
            {
                return result;
            }

            var parts = query.Split('&');
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var separator = part.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                var key = Uri.UnescapeDataString(part.Substring(0, separator));
                var value = Uri.UnescapeDataString(part.Substring(separator + 1));
                result[key] = value;
            }

            return result;
        }

        private static string AppendQueryParameter(string baseUrl, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                return baseUrl;
            }

            var separator = baseUrl.IndexOf('?') >= 0 ? "&" : "?";
            return baseUrl
                + separator
                + Uri.EscapeDataString(key)
                + "="
                + Uri.EscapeDataString(value);
        }

        private static string GetQueryParameterFromUrl(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var questionIndex = url.IndexOf('?');
            if (questionIndex < 0 || questionIndex >= url.Length - 1)
            {
                return string.Empty;
            }

            var fragmentIndex = url.IndexOf('#', questionIndex + 1);
            var query = fragmentIndex >= 0
                ? url.Substring(questionIndex + 1, fragmentIndex - questionIndex - 1)
                : url.Substring(questionIndex + 1);
            var parsed = ParseQueryString(query);
            string value;
            return parsed.TryGetValue(key, out value) ? value : string.Empty;
        }

        private static string ReplaceQueryParameter(string url, string key, string value)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return url;
            }

            var fragment = string.Empty;
            var fragmentIndex = url.IndexOf('#');
            if (fragmentIndex >= 0)
            {
                fragment = url.Substring(fragmentIndex);
                url = url.Substring(0, fragmentIndex);
            }

            var questionIndex = url.IndexOf('?');
            var path = questionIndex >= 0 ? url.Substring(0, questionIndex) : url;
            var query = questionIndex >= 0 && questionIndex < url.Length - 1
                ? url.Substring(questionIndex + 1)
                : string.Empty;
            var parts = string.IsNullOrWhiteSpace(query)
                ? new string[0]
                : query.Split('&');
            var result = new List<string>();
            var replaced = false;

            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                var separator = part.IndexOf('=');
                var rawKey = separator >= 0 ? part.Substring(0, separator) : part;
                var decodedKey = Uri.UnescapeDataString(rawKey);
                if (string.Equals(decodedKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty));
                    replaced = true;
                }
                else
                {
                    result.Add(part);
                }
            }

            if (!replaced)
            {
                result.Add(Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value ?? string.Empty));
            }

            return path + "?" + string.Join("&", result) + fragment;
        }

        private static string ToJavaScriptStringLiteral(string value)
        {
            if (value == null)
            {
                return "null";
            }

            var builder = new StringBuilder();
            builder.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                switch (ch)
                {
                    case '\\':
                        builder.Append("\\\\");
                        break;
                    case '"':
                        builder.Append("\\\"");
                        break;
                    case '\r':
                        builder.Append("\\r");
                        break;
                    case '\n':
                        builder.Append("\\n");
                        break;
                    case '\t':
                        builder.Append("\\t");
                        break;
                    default:
                        if (ch < 32)
                        {
                            builder.Append("\\u");
                            builder.Append(((int)ch).ToString("x4"));
                        }
                        else
                        {
                            builder.Append(ch);
                        }
                        break;
                }
            }

            builder.Append('"');
            return builder.ToString();
        }

        private static bool IsWindowsMobileDevice()
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

        private static string ExtractHlsManifestUrl(JsonObject root)
        {
            if (root == null || !root.ContainsKey("streamingData"))
            {
                return string.Empty;
            }

            var streamingData = root.GetNamedObject("streamingData");
            return GetJsonString(streamingData, "hlsManifestUrl");
        }

        private PlayerFormatModel SelectPreferredFormat()
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Total available formats: {availableFormats.Count}"
            );
            return SelectPreferredProgressiveFormat(availableFormats, GetEffectiveVideoQualityTag(), false);
        }

        // storyboards.playerStoryboardSpecRenderer.spec — the sprite-sheet scrubbing frames.
        private static string ExtractStoryboardSpec(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("storyboards"))
                {
                    return string.Empty;
                }

                var storyboards = root.GetNamedObject("storyboards");
                if (!storyboards.ContainsKey("playerStoryboardSpecRenderer"))
                {
                    return string.Empty;
                }

                var renderer = storyboards.GetNamedObject("playerStoryboardSpecRenderer");
                return GetJsonString(renderer, "spec");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Storyboard spec extract failed: " + ex.Message);
                return string.Empty;
            }
        }

        private string GetEffectiveVideoQualityTag()
        {
            var explicitQuality = NormalizeQualityTag(currentQualityTag);
            if (!string.IsNullOrWhiteSpace(explicitQuality))
            {
                return explicitQuality;
            }

            // The override is empty. That means one of two things, and they must behave
            // differently: if the user actively chose "Auto" in this session it is a real Auto
            // (device-capped adaptive pick); otherwise the video just opened and should inherit the Settings
            // default. Without this distinction, picking Auto would bounce straight back to the
            // preferred quality and be impossible to select.
            if (_qualityExplicitlyChosen)
            {
                return string.Empty;
            }

            var preferredRaw = GetPreferredVideoQualitySetting();
            if (string.Equals(preferredRaw, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Using preferred video quality from Settings: Auto");
                return string.Empty;
            }

            var preferredQuality = NormalizeQualityTag(preferredRaw);
            if (!string.IsNullOrWhiteSpace(preferredQuality))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Using preferred video quality from Settings: "
                    + preferredQuality + "p");
                return preferredQuality;
            }

            // Invalid/corrupt setting never means "pick maximum"; fall back deterministically.
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    PreferredVideoQualitySettingKey] = "Auto";
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string GetPreferredVideoQualitySetting()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;

                // There is exactly one source of truth for video quality. Older builds looked at
                // several legacy keys; a stale "1080" in one of them could unexpectedly override
                // what Settings actually showed.
                if (!values.ContainsKey(PreferredVideoQualitySettingKey))
                {
                    values[PreferredVideoQualitySettingKey] = "Auto";
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Preferred video quality was unset; initialized to Auto");
                    return "Auto";
                }

                var raw = values[PreferredVideoQualitySettingKey];
                var value = raw != null ? raw.ToString() : string.Empty;

                if (string.IsNullOrWhiteSpace(value))
                {
                    values[PreferredVideoQualitySettingKey] = "Auto";
                    return "Auto";
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Preferred video quality: " + value);
                return value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Preferred quality read failed; using Auto: " + ex.Message);
                return "Auto";
            }
        }

        private static PlayerFormatModel SelectPreferredAudioOnlyTrackFormat(
            IList<PlayerFormatModel> formats
        )
        {
            if (formats == null)
            {
                return null;
            }

            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (!format.HasAudio || format.HasVideo)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();

                // Windows 10 Mobile is much happier with AAC in MP4 than Opus/WebM.
                if (mime.IndexOf("audio/mp4") < 0 && mime.IndexOf("mp4a") < 0)
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = new[] { 140, 139, 141 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            return candidates[0];
        }

        private static PlayerFormatModel SelectPreferredMobileAudioCarrierFormat(
            IList<PlayerFormatModel> formats
        )
        {
            if (formats == null)
            {
                return null;
            }

            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (format.IsAdaptive || !format.HasAudio || !format.HasVideo)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();

                // For Windows 10 Mobile the second stream is only an audio carrier.
                // Prefer tiny 3GP/H.263 carriers first: they are much cheaper than
                // decoding another MP4/H.264 360p stream while the visible video is high-res.
                if (
                    mime.IndexOf("video/3gpp") < 0
                    && mime.IndexOf("video/mp4") < 0
                    && mime.IndexOf("3gp") < 0
                )
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = new[] { 18, 59, 22, 17, 36 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            PlayerFormatModel best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (best == null)
                {
                    best = current;
                    continue;
                }

                var bestHeight = best.QualityTier > 0 ? best.QualityTier : 9999;
                var currentHeight = current.QualityTier > 0 ? current.QualityTier : 9999;
                if (currentHeight < bestHeight)
                {
                    best = current;
                }
            }

            return best;
        }

        private static PlayerFormatModel SelectItag18ProgressiveFormat(IList<PlayerFormatModel> formats)
        {
            if (formats == null)
            {
                return null;
            }

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (format.Itag == 18 && IsProgressiveMp4(format) && format.HasAudio && format.HasVideo)
                {
                    return format;
                }
            }

            return null;
        }

        private static PlayerFormatModel SelectPreferredH264VideoOnlyFormat(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag,
            ISet<int> excludedItags = null
        )
        {
            if (formats == null)
            {
                return null;
            }

            var requestedHeight = ParseInt(requestedQualityTag);
            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (excludedItags != null && excludedItags.Contains(format.Itag))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (!format.IsAdaptive || !format.HasVideo || format.HasAudio)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();
                if (mime.IndexOf("video/mp4") < 0 || mime.IndexOf("avc1") < 0)
                {
                    continue;
                }

                // As in youtube-ios chooseVideo:maxHeight:, a requested height is a ceiling.
                // This also lets Auto retry the next lower tier after a decoder failure.
                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = GetPreferredH264VideoOnlyItags(requestedHeight);
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            PlayerFormatModel best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (!IsUwpPreferredVideoOnlyFormat(current))
                {
                    continue;
                }

                if (best == null || current.QualityTier > best.QualityTier)
                {
                    best = current;
                }
            }

            if (best != null)
            {
                return best;
            }

            best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (best == null || current.QualityTier > best.QualityTier)
                {
                    best = current;
                }
            }

            return best;
        }

        private static bool IsUwpPreferredVideoOnlyFormat(PlayerFormatModel format)
        {
            if (format == null)
            {
                return false;
            }

            // UWP on older/mobile devices is much happier with 30fps H.264 streams.
            return format.Fps <= 0 || format.Fps <= 30;
        }

        private static int[] GetPreferredH264VideoOnlyItags(int requestedHeight)
        {
            if (requestedHeight >= 1080)
            {
                return new[] { 137, 299, 136, 298, 135, 134, 133, 160 };
            }

            if (requestedHeight >= 720)
            {
                return new[] { 136, 298, 135, 134, 133, 160 };
            }

            if (requestedHeight >= 480)
            {
                return new[] { 135, 134, 133, 160 };
            }

            if (requestedHeight >= 360)
            {
                return new[] { 134, 133, 160 };
            }

            if (requestedHeight >= 240)
            {
                return new[] { 133, 160 };
            }

            return new[] { 299, 137, 298, 136, 135, 134, 133, 160 };
        }

        private static PlayerFormatModel SelectPreferredAudioVideoTrackFormat(
            IList<PlayerFormatModel> formats
        )
        {
            // This source is used only as hidden audio. Prefer the lightest stable
            // progressive MP4 video that still carries AAC audio.
            var lowQuality = SelectPreferredProgressiveFormat(formats, "360", true);
            if (lowQuality != null)
            {
                return lowQuality;
            }

            return SelectPreferredProgressiveFormat(formats, string.Empty, true);
        }

        private static PlayerFormatModel SelectPreferredProgressiveFormat(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag,
            bool preferSmallest
        )
        {
            PlayerFormatModel best = null;
            int requestedHeight = ParseInt(requestedQualityTag);
            var progressiveCandidates = new List<PlayerFormatModel>();

            if (formats == null)
            {
                return null;
            }

            foreach (var format in formats)
            {
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (
                    !string.IsNullOrWhiteSpace(format.MimeType)
                    && format.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) < 0
                )
                {
                    continue;
                }

                if (!IsProgressiveMp4(format))
                {
                    continue;
                }

                if (!format.HasAudio || !format.HasVideo)
                {
                    continue;
                }

                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Added progressive candidate: itag={format.Itag}, width={format.Width}, height={format.Height}, mime={format.MimeType}"
                );
                progressiveCandidates.Add(format);
            }

            if (progressiveCandidates.Count == 0)
            {
                return null;
            }

            var preferredItags = preferSmallest
                ? new[] { 18, 59, 22 }
                : new[] { 18, 22, 59 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < progressiveCandidates.Count; i++)
                {
                    if (progressiveCandidates[i].Itag == preferredItags[p])
                    {
                        return progressiveCandidates[i];
                    }
                }
            }

            for (int i = 0; i < progressiveCandidates.Count; i++)
            {
                var format = progressiveCandidates[i];
                if (best == null)
                {
                    best = format;
                    continue;
                }

                var bestHeight = best.QualityTier > 0 ? best.QualityTier : 0;
                var currentHeight = format.QualityTier > 0 ? format.QualityTier : 0;
                if (preferSmallest)
                {
                    if (bestHeight == 0 || (currentHeight > 0 && currentHeight < bestHeight))
                    {
                        best = format;
                    }
                }
                else if (currentHeight > bestHeight)
                {
                    best = format;
                }
            }

            return best;
        }

        private static bool FormatHasAudio(JsonObject formatObj, string mimeType)
        {
            var mime = string.IsNullOrWhiteSpace(mimeType)
                ? string.Empty
                : mimeType.ToLowerInvariant();

            return mime.IndexOf("audio/") >= 0
                || mime.IndexOf("mp4a") >= 0
                || mime.IndexOf("opus") >= 0
                || mime.IndexOf("vorbis") >= 0
                || !string.IsNullOrWhiteSpace(GetJsonString(formatObj, "audioQuality"))
                || !string.IsNullOrWhiteSpace(GetJsonString(formatObj, "audioSampleRate"))
                || (formatObj != null && formatObj.ContainsKey("audioChannels"));
        }

        private static bool FormatHasVideo(JsonObject formatObj, string mimeType)
        {
            var mime = string.IsNullOrWhiteSpace(mimeType)
                ? string.Empty
                : mimeType.ToLowerInvariant();

            return mime.IndexOf("video/") >= 0
                || mime.IndexOf("avc1") >= 0
                || mime.IndexOf("vp9") >= 0
                || mime.IndexOf("av01") >= 0
                || ParseIntOrDefault(formatObj, "height") > 0
                || ParseIntOrDefault(formatObj, "width") > 0;
        }

        private static bool IsProgressiveMp4(PlayerFormatModel format)
        {
            if (format == null || string.IsNullOrWhiteSpace(format.MimeType))
            {
                return false;
            }

            var mime = format.MimeType.ToLowerInvariant();
            if (mime.IndexOf("video/mp4") < 0)
            {
                return false;
            }

            if (format.IsAdaptive)
            {
                return false;
            }

            // Windows 10 Mobile supports H.264 + AAC, but we should be less restrictive
            // Allow any video/mp4 format (most will have compatible codecs)
            var hasAacAudio = mime.IndexOf("mp4a") >= 0 || format.HasAudio;
            var hasH264Video = mime.IndexOf("avc1") >= 0 || format.HasVideo;

            // Log for debugging
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Format check: itag={format.Itag}, mime={format.MimeType}, hasAAC={hasAacAudio}, hasH264={hasH264Video}"
            );

            // Relaxed check: allow video/mp4 with any audio codec
            // Most progressive MP4s from YouTube should work on Windows 10 Mobile
            bool hasAudio = hasAacAudio || mime.IndexOf("audio") >= 0;
            if (!hasAudio && !hasH264Video)
            {
                // Only reject if it clearly has no audio and no video codec info
                return false;
            }

            // Exclude adaptive formats (video-only or audio-only)
            // These itags are known to be adaptive (separate video/audio streams)
            if (
                format.Itag == 299
                || format.Itag == 298
                || format.Itag == 137
                || format.Itag == 136
                || format.Itag == 135
                || format.Itag == 134
                || format.Itag == 133
                || format.Itag == 160
                || format.Itag == 140
                || format.Itag == 141
                || format.Itag == 251
                || format.Itag == 250
                || format.Itag == 249
                || format.Itag == 171
            )
            {
                return false;
            }

            return true;
        }

        private static int ParseIntOrDefault(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return 0;
            }

            var value = obj.GetNamedValue(key);
            if (value == null)
            {
                return 0;
            }

            if (value.ValueType == JsonValueType.Number)
            {
                return (int)value.GetNumber();
            }

            if (value.ValueType == JsonValueType.String)
            {
                return ParseInt(value.GetString());
            }

            return 0;
        }

        private static string GetRangeValue(JsonObject obj, string rangeKey, string valueKey)
        {
            if (
                obj == null
                || string.IsNullOrWhiteSpace(rangeKey)
                || string.IsNullOrWhiteSpace(valueKey)
                || !obj.ContainsKey(rangeKey)
            )
            {
                return string.Empty;
            }

            try
            {
                var range = obj.GetNamedObject(rangeKey);
                return GetJsonString(range, valueKey);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeQualityTag(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
            {
                return string.Empty;
            }

            quality = quality.Trim();
            if (string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quality, "Стандарт", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (quality.EndsWith("p", StringComparison.OrdinalIgnoreCase))
            {
                quality = quality.Substring(0, quality.Length - 1);
            }

            return quality.Trim();
        }

        private bool ShouldUseItag18AsMainVideo(string qualityTag)
        {
            var normalized = NormalizeQualityTag(qualityTag);
            return string.IsNullOrWhiteSpace(_selectedAudioTrackId)
                && string.Equals(normalized, "360", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static string FormatCount(string value)
        {
            long number;
            if (!long.TryParse(value, out number))
            {
                return value;
            }

            if (number >= 1000000000)
            {
                return (number / 1000000000.0).ToString("F1") + "B";
            }

            if (number >= 1000000)
            {
                return (number / 1000000.0).ToString("F1") + "M";
            }

            if (number >= 1000)
            {
                return (number / 1000.0).ToString("F1") + "K";
            }

            return number.ToString();
        }

        private static string FormatPublishDate(string publishDate)
        {
            try
            {
                DateTime date;
                if (DateTime.TryParse(publishDate, out date))
                {
                    var now = DateTime.Now;
                    var diff = now - date;

                    if (diff.TotalDays < 1)
                    {
                        return Localization.GetString("Today");
                    }
                    else if (diff.TotalDays < 7)
                    {
                        return Localization.Format("DaysAgo", (int)diff.TotalDays);
                    }
                    else if (diff.TotalDays < 30)
                    {
                        return Localization.Format("WeeksAgo", (int)(diff.TotalDays / 7));
                    }
                    else if (diff.TotalDays < 365)
                    {
                        return Localization.Format("MonthsAgo", (int)(diff.TotalDays / 30));
                    }
                    else
                    {
                        return Localization.Format("YearsAgo", (int)(diff.TotalDays / 365));
                    }
                }
                return publishDate;
            }
            catch
            {
                return publishDate;
            }
        }

        private static IEnumerable<JsonObject> EnumerateObjects(IJsonValue value)
        {
            var stack = new Stack<IJsonValue>();
            stack.Push(value);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                if (current.ValueType == JsonValueType.Object)
                {
                    var obj = current.GetObject();
                    yield return obj;
                    foreach (var pair in obj)
                    {
                        stack.Push(pair.Value);
                    }
                }
                else if (current.ValueType == JsonValueType.Array)
                {
                    var arr = current.GetArray();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        stack.Push(arr[i]);
                    }
                }
            }
        }

        private static string ExtractTextFromField(
            JsonObject obj,
            string fieldName,
            string fallback
        )
        {
            if (obj == null || !obj.ContainsKey(fieldName))
            {
                return fallback;
            }

            var field = obj.GetNamedValue(fieldName);
            if (field == null || field.ValueType != JsonValueType.Object)
            {
                return fallback;
            }

            var fieldObject = field.GetObject();
            if (fieldObject.ContainsKey("simpleText"))
            {
                return fieldObject.GetNamedString("simpleText", fallback);
            }

            if (fieldObject.ContainsKey("runs"))
            {
                var runs = fieldObject.GetNamedArray("runs");
                var sb = new StringBuilder();
                for (int i = 0; i < runs.Count; i++)
                {
                    var runObj = runs[i].GetObject();
                    var text = runObj.GetNamedString("text", string.Empty);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                    }
                }

                var collected = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(collected) ? fallback : collected;
            }

            return fallback;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }
            return string.Empty;
        }

        private static string ExtractLikeCountFromNext(Windows.Data.Json.JsonObject nextRoot)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "[LikeCount] Starting like count extraction from /next..."
                );

                // Path: contents.twoColumnWatchNextResults.results.results.contents[0].videoPrimaryInfoRenderer.videoActions.menuRenderer.topLevelButtons[0]
                if (nextRoot.ContainsKey("contents"))
                {
                    System.Diagnostics.Debug.WriteLine("[LikeCount] Found contents");
                    var contents = nextRoot.GetNamedObject("contents");
                    if (contents.ContainsKey("twoColumnWatchNextResults"))
                    {
                        var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
                        if (twoColumn.ContainsKey("results"))
                        {
                            var results = twoColumn.GetNamedObject("results");
                            if (results.ContainsKey("results"))
                            {
                                var resultsContent = results.GetNamedObject("results");
                                if (resultsContent.ContainsKey("contents"))
                                {
                                    var contentsArray = resultsContent.GetNamedArray("contents");
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[LikeCount] contents array count: {contentsArray.Count}"
                                    );

                                    if (contentsArray.Count > 0)
                                    {
                                        // Get first item: videoPrimaryInfoRenderer
                                        var firstItem = contentsArray[0];
                                        if (
                                            firstItem.ValueType
                                            == Windows.Data.Json.JsonValueType.Object
                                        )
                                        {
                                            var itemObj = firstItem.GetObject();
                                            if (itemObj.ContainsKey("videoPrimaryInfoRenderer"))
                                            {
                                                System.Diagnostics.Debug.WriteLine(
                                                    "[LikeCount] Found videoPrimaryInfoRenderer at index 0"
                                                );
                                                var primaryInfo = itemObj.GetNamedObject(
                                                    "videoPrimaryInfoRenderer"
                                                );
                                                if (primaryInfo.ContainsKey("videoActions"))
                                                {
                                                    System.Diagnostics.Debug.WriteLine(
                                                        "[LikeCount] Found videoActions"
                                                    );
                                                    var videoActions = primaryInfo.GetNamedObject(
                                                        "videoActions"
                                                    );
                                                    if (videoActions.ContainsKey("menuRenderer"))
                                                    {
                                                        System.Diagnostics.Debug.WriteLine(
                                                            "[LikeCount] Found menuRenderer"
                                                        );
                                                        var menuRenderer =
                                                            videoActions.GetNamedObject(
                                                                "menuRenderer"
                                                            );
                                                        if (
                                                            menuRenderer.ContainsKey(
                                                                "topLevelButtons"
                                                            )
                                                        )
                                                        {
                                                            var buttons =
                                                                menuRenderer.GetNamedArray(
                                                                    "topLevelButtons"
                                                                );
                                                            System.Diagnostics.Debug.WriteLine(
                                                                $"[LikeCount] topLevelButtons count: {buttons.Count}"
                                                            );

                                                            if (buttons.Count > 0)
                                                            {
                                                                var firstButton = buttons[0];
                                                                if (
                                                                    firstButton.ValueType
                                                                    == Windows
                                                                        .Data
                                                                        .Json
                                                                        .JsonValueType
                                                                        .Object
                                                                )
                                                                {
                                                                    var buttonObj =
                                                                        firstButton.GetObject();
                                                                    if (
                                                                        buttonObj.ContainsKey(
                                                                            "segmentedLikeDislikeButtonViewModel"
                                                                        )
                                                                    )
                                                                    {
                                                                        System.Diagnostics.Debug.WriteLine(
                                                                            "[LikeCount] Found segmentedLikeDislikeButtonViewModel"
                                                                        );
                                                                        var likeButton =
                                                                            buttonObj.GetNamedObject(
                                                                                "segmentedLikeDislikeButtonViewModel"
                                                                            );

                                                                        // Navigate through nested likeButtonViewModel
                                                                        if (
                                                                            likeButton.ContainsKey(
                                                                                "likeButtonViewModel"
                                                                            )
                                                                        )
                                                                        {
                                                                            var likeVM1 =
                                                                                likeButton.GetNamedObject(
                                                                                    "likeButtonViewModel"
                                                                                );
                                                                            if (
                                                                                likeVM1.ContainsKey(
                                                                                    "likeButtonViewModel"
                                                                                )
                                                                            )
                                                                            {
                                                                                var likeVM2 =
                                                                                    likeVM1.GetNamedObject(
                                                                                        "likeButtonViewModel"
                                                                                    );
                                                                                if (
                                                                                    likeVM2.ContainsKey(
                                                                                        "toggleButtonViewModel"
                                                                                    )
                                                                                )
                                                                                {
                                                                                    var toggleVM1 =
                                                                                        likeVM2.GetNamedObject(
                                                                                            "toggleButtonViewModel"
                                                                                        );
                                                                                    if (
                                                                                        toggleVM1.ContainsKey(
                                                                                            "toggleButtonViewModel"
                                                                                        )
                                                                                    )
                                                                                    {
                                                                                        var toggleVM2 =
                                                                                            toggleVM1.GetNamedObject(
                                                                                                "toggleButtonViewModel"
                                                                                            );
                                                                                        if (
                                                                                            toggleVM2.ContainsKey(
                                                                                                "toggledButtonViewModel"
                                                                                            )
                                                                                        )
                                                                                        {
                                                                                            var toggledVM =
                                                                                                toggleVM2.GetNamedObject(
                                                                                                    "toggledButtonViewModel"
                                                                                                );
                                                                                            if (
                                                                                                toggledVM.ContainsKey(
                                                                                                    "buttonViewModel"
                                                                                                )
                                                                                            )
                                                                                            {
                                                                                                var btnVM =
                                                                                                    toggledVM.GetNamedObject(
                                                                                                        "buttonViewModel"
                                                                                                    );

                                                                                                // Try "title" field first
                                                                                                if (
                                                                                                    btnVM.ContainsKey(
                                                                                                        "title"
                                                                                                    )
                                                                                                )
                                                                                                {
                                                                                                    var title =
                                                                                                        btnVM.GetNamedString(
                                                                                                            "title"
                                                                                                        );
                                                                                                    System.Diagnostics.Debug.WriteLine(
                                                                                                        $"[LikeCount] title: {title}"
                                                                                                    );
                                                                                                    if (
                                                                                                        !string.IsNullOrEmpty(
                                                                                                            title
                                                                                                        )
                                                                                                        && System.Text.RegularExpressions.Regex.IsMatch(
                                                                                                            title,
                                                                                                            @"\d"
                                                                                                        )
                                                                                                    )
                                                                                                    {
                                                                                                        System.Diagnostics.Debug.WriteLine(
                                                                                                            $"[LikeCount] Found via title: {title}"
                                                                                                        );
                                                                                                        return title;
                                                                                                    }
                                                                                                }

                                                                                                // Try "accessibilityText" field
                                                                                                if (
                                                                                                    btnVM.ContainsKey(
                                                                                                        "accessibilityText"
                                                                                                    )
                                                                                                )
                                                                                                {
                                                                                                    var accessibilityText =
                                                                                                        btnVM.GetNamedString(
                                                                                                            "accessibilityText"
                                                                                                        );
                                                                                                    System.Diagnostics.Debug.WriteLine(
                                                                                                        $"[LikeCount] accessibilityText: {accessibilityText}"
                                                                                                    );

                                                                                                    // Extract number from text like "1,234" or "along with 1,234 other people"
                                                                                                    var match =
                                                                                                        System.Text.RegularExpressions.Regex.Match(
                                                                                                            accessibilityText,
                                                                                                            @"along with ([\d,]+)"
                                                                                                        );
                                                                                                    if (
                                                                                                        match.Success
                                                                                                    )
                                                                                                    {
                                                                                                        var likeCount =
                                                                                                            match
                                                                                                                .Groups[
                                                                                                                    1
                                                                                                                ]
                                                                                                                .Value;
                                                                                                        System.Diagnostics.Debug.WriteLine(
                                                                                                            $"[LikeCount] Found via 'along with': {likeCount}"
                                                                                                        );
                                                                                                        return likeCount;
                                                                                                    }

                                                                                                    // Fallback: extract any number
                                                                                                    match =
                                                                                                        System.Text.RegularExpressions.Regex.Match(
                                                                                                            accessibilityText,
                                                                                                            @"([\d,]+)"
                                                                                                        );
                                                                                                    if (
                                                                                                        match.Success
                                                                                                    )
                                                                                                    {
                                                                                                        var likeCount =
                                                                                                            match
                                                                                                                .Groups[
                                                                                                                    1
                                                                                                                ]
                                                                                                                .Value;
                                                                                                        System.Diagnostics.Debug.WriteLine(
                                                                                                            $"[LikeCount] Found via regex fallback: {likeCount}"
                                                                                                        );
                                                                                                        return likeCount;
                                                                                                    }
                                                                                                }
                                                                                            }
                                                                                        }
                                                                                    }
                                                                                }
                                                                            }
                                                                        }
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[LikeCount] contents not found");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[LikeCount] Error extracting like count: {ex.Message}"
                );
                System.Diagnostics.Debug.WriteLine($"[LikeCount] Stack: {ex.StackTrace}");
            }

            System.Diagnostics.Debug.WriteLine("[LikeCount] Could not extract like count");
            return string.Empty;
        }

        private static string ExtractPublishDateFromNext(Windows.Data.Json.JsonObject nextRoot)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "[PublishDate] Starting publish date extraction..."
                );

                // Old UI path: contents.twoColumnWatchNextResults.results.results.contents[0].videoPrimaryInfoRenderer.dateText
                if (nextRoot.ContainsKey("contents"))
                {
                    var contents = nextRoot.GetNamedObject("contents");
                    if (contents.ContainsKey("twoColumnWatchNextResults"))
                    {
                        var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
                        if (twoColumn.ContainsKey("results"))
                        {
                            var results = twoColumn.GetNamedObject("results");
                            if (results.ContainsKey("results"))
                            {
                                var resultsContent = results.GetNamedObject("results");
                                if (resultsContent.ContainsKey("contents"))
                                {
                                    var contentsArray = resultsContent.GetNamedArray("contents");
                                    if (contentsArray.Count > 0)
                                    {
                                        var firstItem = contentsArray[0];
                                        if (
                                            firstItem.ValueType
                                            == Windows.Data.Json.JsonValueType.Object
                                        )
                                        {
                                            var itemObj = firstItem.GetObject();
                                            if (itemObj.ContainsKey("videoPrimaryInfoRenderer"))
                                            {
                                                System.Diagnostics.Debug.WriteLine(
                                                    "[PublishDate] Found videoPrimaryInfoRenderer at old path"
                                                );

                                                var primaryInfo = itemObj.GetNamedObject(
                                                    "videoPrimaryInfoRenderer"
                                                );
                                                var dateFromPrimaryInfo = ExtractTextFromField(
                                                    primaryInfo,
                                                    "dateText",
                                                    string.Empty
                                                );

                                                if (!string.IsNullOrWhiteSpace(dateFromPrimaryInfo))
                                                {
                                                    System.Diagnostics.Debug.WriteLine(
                                                        $"[PublishDate] Found via old dateText path: {dateFromPrimaryInfo}"
                                                    );
                                                    return dateFromPrimaryInfo;
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // Fallback for newer /next layouts where videoPrimaryInfoRenderer moved deeper.
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    if (obj.ContainsKey("videoPrimaryInfoRenderer"))
                    {
                        var primaryInfo = obj.GetNamedObject("videoPrimaryInfoRenderer");
                        var dateFromMovedPrimaryInfo = ExtractTextFromField(
                            primaryInfo,
                            "dateText",
                            string.Empty
                        );

                        if (!string.IsNullOrWhiteSpace(dateFromMovedPrimaryInfo))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[PublishDate] Found via moved videoPrimaryInfoRenderer: {dateFromMovedPrimaryInfo}"
                            );
                            return dateFromMovedPrimaryInfo;
                        }
                    }
                }

                // Fallback for metadata rows like "Published", "Upload date" / localized rows.
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    if (!obj.ContainsKey("metadataRowRenderer"))
                    {
                        continue;
                    }

                    var row = obj.GetNamedObject("metadataRowRenderer");
                    var title = ExtractTextFromField(row, "title", string.Empty).ToLower();
                    if (
                        !title.Contains("publish")
                        && !title.Contains("upload")
                        && !title.Contains("date")
                        && !title.Contains("опублик")
                        && !title.Contains("загруз")
                        && !title.Contains("дата")
                    )
                    {
                        continue;
                    }

                    if (row.ContainsKey("contents"))
                    {
                        var rowContents = row.GetNamedArray("contents");
                        var builder = new StringBuilder();
                        for (int i = 0; i < rowContents.Count; i++)
                        {
                            if (
                                rowContents[i].ValueType
                                != Windows.Data.Json.JsonValueType.Object
                            )
                            {
                                continue;
                            }

                            var contentObj = rowContents[i].GetObject();
                            var text = FirstNonEmpty(
                                contentObj.GetNamedString("simpleText", string.Empty),
                                ExtractTextFromField(contentObj, "text", string.Empty)
                            );

                            if (string.IsNullOrWhiteSpace(text) && contentObj.ContainsKey("runs"))
                            {
                                var runs = contentObj.GetNamedArray("runs");
                                var runsText = new StringBuilder();
                                for (int r = 0; r < runs.Count; r++)
                                {
                                    if (
                                        runs[r].ValueType
                                        == Windows.Data.Json.JsonValueType.Object
                                    )
                                    {
                                        runsText.Append(
                                            runs[r].GetObject().GetNamedString("text", string.Empty)
                                        );
                                    }
                                }
                                text = runsText.ToString().Trim();
                            }

                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                if (builder.Length > 0)
                                {
                                    builder.Append(" ");
                                }
                                builder.Append(text.Trim());
                            }
                        }

                        var metadataDate = builder.ToString().Trim();
                        if (!string.IsNullOrWhiteSpace(metadataDate))
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[PublishDate] Found via metadata row: {metadataDate}"
                            );
                            return metadataDate;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PublishDate] Error: {ex.Message}");
            }

            return string.Empty;
        }

        private static string ExtractChannelAvatarFromNext(Windows.Data.Json.JsonObject nextRoot)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChannelAvatar] Starting channel avatar extraction..."
                );

                // Path: contents.twoColumnWatchNextResults.results.results.contents[1].videoSecondaryInfoRenderer.owner.videoOwnerRenderer.thumbnail.thumbnails[0].url
                if (nextRoot.ContainsKey("contents"))
                {
                    var contents = nextRoot.GetNamedObject("contents");
                    if (contents.ContainsKey("twoColumnWatchNextResults"))
                    {
                        var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
                        if (twoColumn.ContainsKey("results"))
                        {
                            var results = twoColumn.GetNamedObject("results");
                            if (results.ContainsKey("results"))
                            {
                                var resultsContent = results.GetNamedObject("results");
                                if (resultsContent.ContainsKey("contents"))
                                {
                                    var contentsArray = resultsContent.GetNamedArray("contents");
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[ChannelAvatar] contents array count: {contentsArray.Count}"
                                    );

                                    if (contentsArray.Count > 1)
                                    {
                                        // Get second item: videoSecondaryInfoRenderer
                                        var secondItem = contentsArray[1];
                                        if (
                                            secondItem.ValueType
                                            == Windows.Data.Json.JsonValueType.Object
                                        )
                                        {
                                            var itemObj = secondItem.GetObject();
                                            if (itemObj.ContainsKey("videoSecondaryInfoRenderer"))
                                            {
                                                System.Diagnostics.Debug.WriteLine(
                                                    "[ChannelAvatar] Found videoSecondaryInfoRenderer"
                                                );
                                                var secondaryInfo = itemObj.GetNamedObject(
                                                    "videoSecondaryInfoRenderer"
                                                );
                                                if (secondaryInfo.ContainsKey("owner"))
                                                {
                                                    System.Diagnostics.Debug.WriteLine(
                                                        "[ChannelAvatar] Found owner"
                                                    );
                                                    var owner = secondaryInfo.GetNamedObject(
                                                        "owner"
                                                    );
                                                    if (owner.ContainsKey("videoOwnerRenderer"))
                                                    {
                                                        System.Diagnostics.Debug.WriteLine(
                                                            "[ChannelAvatar] Found videoOwnerRenderer"
                                                        );
                                                        var videoOwner = owner.GetNamedObject(
                                                            "videoOwnerRenderer"
                                                        );
                                                        if (videoOwner.ContainsKey("thumbnail"))
                                                        {
                                                            var thumbnail =
                                                                videoOwner.GetNamedObject(
                                                                    "thumbnail"
                                                                );
                                                            if (thumbnail.ContainsKey("thumbnails"))
                                                            {
                                                                var thumbnails =
                                                                    thumbnail.GetNamedArray(
                                                                        "thumbnails"
                                                                    );
                                                                System.Diagnostics.Debug.WriteLine(
                                                                    $"[ChannelAvatar] thumbnails count: {thumbnails.Count}"
                                                                );

                                                                if (thumbnails.Count > 0)
                                                                {
                                                                    var firstThumb = thumbnails[0]
                                                                        .GetObject();
                                                                    if (
                                                                        firstThumb.ContainsKey(
                                                                            "url"
                                                                        )
                                                                    )
                                                                    {
                                                                        var url =
                                                                            firstThumb.GetNamedString(
                                                                                "url"
                                                                            );
                                                                        System.Diagnostics.Debug.WriteLine(
                                                                            $"[ChannelAvatar] Found URL: {url}"
                                                                        );
                                                                        return url;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            System.Diagnostics.Debug.WriteLine(
                                                                "[ChannelAvatar] thumbnail key not found"
                                                            );
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ChannelAvatar] Error: {ex.Message}");
            }

            System.Diagnostics.Debug.WriteLine("[ChannelAvatar] No channel avatar found");
            return string.Empty;
        }

        private static string ExtractSubscriberCountFromNext(Windows.Data.Json.JsonObject nextRoot)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SubscriberCount] Starting subscriber count extraction..."
                );

                // Path: contents.twoColumnWatchNextResults.results.results.contents[1].videoSecondaryInfoRenderer.owner.videoOwnerRenderer.subscriberCountText
                if (nextRoot.ContainsKey("contents"))
                {
                    System.Diagnostics.Debug.WriteLine("[SubscriberCount] Found contents");
                    var contents = nextRoot.GetNamedObject("contents");
                    if (contents.ContainsKey("twoColumnWatchNextResults"))
                    {
                        var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
                        if (twoColumn.ContainsKey("results"))
                        {
                            var results = twoColumn.GetNamedObject("results");
                            if (results.ContainsKey("results"))
                            {
                                var resultsContent = results.GetNamedObject("results");
                                if (resultsContent.ContainsKey("contents"))
                                {
                                    var contentsArray = resultsContent.GetNamedArray("contents");
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[SubscriberCount] contents array count: {contentsArray.Count}"
                                    );

                                    if (contentsArray.Count > 1)
                                    {
                                        // Get second item: videoSecondaryInfoRenderer
                                        var secondItem = contentsArray[1];
                                        if (
                                            secondItem.ValueType
                                            == Windows.Data.Json.JsonValueType.Object
                                        )
                                        {
                                            var itemObj = secondItem.GetObject();
                                            if (itemObj.ContainsKey("videoSecondaryInfoRenderer"))
                                            {
                                                System.Diagnostics.Debug.WriteLine(
                                                    "[SubscriberCount] Found videoSecondaryInfoRenderer at index 1"
                                                );
                                                var secondaryInfo = itemObj.GetNamedObject(
                                                    "videoSecondaryInfoRenderer"
                                                );
                                                if (secondaryInfo.ContainsKey("owner"))
                                                {
                                                    System.Diagnostics.Debug.WriteLine(
                                                        "[SubscriberCount] Found owner"
                                                    );
                                                    var owner = secondaryInfo.GetNamedObject(
                                                        "owner"
                                                    );
                                                    if (owner.ContainsKey("videoOwnerRenderer"))
                                                    {
                                                        System.Diagnostics.Debug.WriteLine(
                                                            "[SubscriberCount] Found videoOwnerRenderer"
                                                        );
                                                        var videoOwner = owner.GetNamedObject(
                                                            "videoOwnerRenderer"
                                                        );
                                                        if (
                                                            videoOwner.ContainsKey(
                                                                "subscriberCountText"
                                                            )
                                                        )
                                                        {
                                                            System.Diagnostics.Debug.WriteLine(
                                                                "[SubscriberCount] Found subscriberCountText"
                                                            );
                                                            var subText = videoOwner.GetNamedObject(
                                                                "subscriberCountText"
                                                            );

                                                            // Try simpleText first
                                                            if (subText.ContainsKey("simpleText"))
                                                            {
                                                                var simpleText =
                                                                    subText.GetNamedString(
                                                                        "simpleText"
                                                                    );
                                                                System.Diagnostics.Debug.WriteLine(
                                                                    $"[SubscriberCount] Found via simpleText: {simpleText}"
                                                                );
                                                                return simpleText;
                                                            }

                                                            // Try runs
                                                            if (subText.ContainsKey("runs"))
                                                            {
                                                                var runs = subText.GetNamedArray(
                                                                    "runs"
                                                                );
                                                                if (runs.Count > 0)
                                                                {
                                                                    var firstRun = runs[0]
                                                                        .GetObject();
                                                                    if (
                                                                        firstRun.ContainsKey("text")
                                                                    )
                                                                    {
                                                                        var text =
                                                                            firstRun.GetNamedString(
                                                                                "text"
                                                                            );
                                                                        System.Diagnostics.Debug.WriteLine(
                                                                            $"[SubscriberCount] Found via runs: {text}"
                                                                        );
                                                                        return text;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                        else
                                                        {
                                                            System.Diagnostics.Debug.WriteLine(
                                                                "[SubscriberCount] subscriberCountText key not found"
                                                            );
                                                            // Print all keys for debugging
                                                            foreach (var key in videoOwner.Keys)
                                                            {
                                                                System.Diagnostics.Debug.WriteLine(
                                                                    $"[SubscriberCount] videoOwnerRenderer key: {key}"
                                                                );
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                    else
                                    {
                                        System.Diagnostics.Debug.WriteLine(
                                            $"[SubscriberCount] contents array has only {contentsArray.Count} items, need at least 2"
                                        );
                                    }
                                }
                            }
                        }
                    }
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SubscriberCount] contents not found");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[SubscriberCount] Error extracting subscriber count: {ex.Message}"
                );
                System.Diagnostics.Debug.WriteLine($"[SubscriberCount] Stack: {ex.StackTrace}");
            }

            System.Diagnostics.Debug.WriteLine(
                "[SubscriberCount] Could not extract subscriber count"
            );
            return string.Empty;
        }

        private static string ExtractTextFromRunsOrSimpleText(Windows.Data.Json.IJsonValue value)
        {
            try
            {
                if (value.ValueType == Windows.Data.Json.JsonValueType.String)
                {
                    return value.GetString();
                }

                if (value.ValueType == Windows.Data.Json.JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("simpleText"))
                    {
                        return obj.GetNamedString("simpleText");
                    }
                    if (obj.ContainsKey("content"))
                    {
                        var content = obj.GetNamedValue("content");
                        if (content != null && content.ValueType == JsonValueType.String)
                        {
                            return content.GetString();
                        }
                    }
                    if (obj.ContainsKey("runs"))
                    {
                        var runs = obj.GetNamedArray("runs");
                        var sb = new StringBuilder();
                        for (int i = 0; i < runs.Count; i++)
                        {
                            var runObj = runs[i].GetObject();
                            var text = runObj.GetNamedString("text", string.Empty);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                sb.Append(text);
                            }
                        }
                        return sb.ToString();
                    }
                }
            }
            catch { }

            return string.Empty;
        }

        private static string GetJsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            var value = obj.GetNamedValue(key);
            return value != null && value.ValueType == JsonValueType.String
                ? value.GetString()
                : string.Empty;
        }

        private void CustomVideoPlayer_FullscreenRequested(object sender, object e) { }

        private void VideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            // In landscape the complete inline description lives directly below the title.
            // The portrait bottom sheet must never be opened from that layout.
            if (!IsCurrentViewPortrait())
            {
                return;
            }
            ShowDescriptionBottomSheet();
        }

        private void ChannelPicture_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var channelTarget = FirstNonEmpty(currentChannelId, currentChannelName, VideoAuthorText != null ? VideoAuthorText.Text : string.Empty);
            if (string.IsNullOrWhiteSpace(channelTarget))
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[Video] Navigating to channel: " + channelTarget);
            Frame.Navigate(typeof(Channel), channelTarget);
        }

        private async void SubscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                ShowSubscriptionMenuBottomSheet();
                return;
            }

            await SetChannelSubscriptionStateAsync(true);
        }

        private async Task SetChannelSubscriptionStateAsync(bool subscribe)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var channelIdAtClick = currentChannelId;
            var oldState = _currentSubscriptionState;
            var oldNotificationState = _currentNotificationState;
            var newState = subscribe ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
            var newNotificationState = subscribe ? ChannelNotificationState.Default : ChannelNotificationState.Default;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentSubscriptionState = newState;
            _currentNotificationState = newNotificationState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var success = await SetChannelSubscriptionAsync(channelIdAtClick, subscribe, token);
                if (stateGeneration != _subscriptionStateGeneration || !string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    await ShowRatingMessageAsync(
                        Localization.GetString("SubscriptionFailed"),
                        Localization.GetString("SubscriptionRejectedDetailed")
                    );
                    return;
                }

                _currentSubscriptionState = newState;
                _currentNotificationState = newNotificationState;
                UpdateSubscriptionVisualState();
                UpdateSubscriptionMenuVisualState();

                if (!subscribe)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Subscription] Error updating subscription: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("SubscriptionFailed"), ex.Message);
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async Task ToggleChannelSubscriptionAsync()
        {
            await SetChannelSubscriptionStateAsync(_currentSubscriptionState != ChannelSubscriptionState.Subscribed);
        }

        private async Task ChangeChannelSubscriptionAsync(bool subscribe)
        {
            await SetChannelSubscriptionStateAsync(subscribe);
        }


        private async Task<bool> SetChannelSubscriptionAsync(string channelId, bool subscribe, string accessToken)
        {
            var endpoint = subscribe ? "subscription/subscribe" : "subscription/unsubscribe";
            var parameters = subscribe ? _subscribeParams : _unsubscribeParams;
            var clickTrackingParams = subscribe ? _subscribeClickTrackingParams : _unsubscribeClickTrackingParams;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                parameters = subscribe ? DefaultSubscribeParams : DefaultUnsubscribeParams;
            }

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildSubscriptionPayload(channelId, parameters, clickTrackingParams),
                    Encoding.UTF8,
                    "application/json"
                );
                AddYouTubeAuthHeaders(request, accessToken, true);

                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Subscription] Innertube subscription update OK: " + (subscribe ? "subscribe" : "unsubscribe"));
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Subscription] Innertube subscription update failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
                return false;
            }
        }

        private async Task<bool> ModifyChannelNotificationPreferenceAsync(ChannelNotificationState targetState)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var oldState = _currentNotificationState;
            var channelIdAtClick = currentChannelId;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentNotificationState = targetState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var paramsValue = BuildNotificationPreferenceParams(channelIdAtClick, targetState);
                var url = BuildInnertubeUrl("notification/modify_channel_preference");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildNotificationPreferencePayload(paramsValue),
                        Encoding.UTF8,
                        "application/json"
                    );
                    AddYouTubeAuthHeaders(request, token, true);

                    var response = await httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        _currentNotificationState = oldState;
                        UpdateSubscriptionVisualState();
                        UpdateSubscriptionMenuVisualState();
                        System.Diagnostics.Debug.WriteLine(
                            "[Subscription] Notification preference update failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + body
                        );
                        await ShowRatingMessageAsync(Localization.GetString("NotificationsFailed"), Localization.GetString("NotificationPreferenceFailed"));
                        return false;
                    }
                }

                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = targetState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    AnimateSubscriptionMenuBottomSheet(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = oldState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Subscription] Notification preference update error: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("NotificationsFailed"), ex.Message);
                return false;
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private static string BuildNotificationPreferencePayload(string parameters)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var user = new JsonObject();
            user["enableSafetyMode"] = JsonValue.CreateBooleanValue(false);
            context["user"] = user;

            var request = new JsonObject();
            request["internalExperimentFlags"] = new JsonArray();
            request["consistencyTokenJars"] = new JsonArray();
            context["request"] = request;

            var payload = new JsonObject();
            payload["context"] = context;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildNotificationPreferenceParams(string channelId, ChannelNotificationState targetState)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return string.Empty;
            }

            byte stateCode = 1;
            if (targetState == ChannelNotificationState.All)
            {
                stateCode = 2;
            }
            else if (targetState == ChannelNotificationState.None)
            {
                stateCode = 3;
            }

            var channelBytes = Encoding.UTF8.GetBytes(channelId);
            var bytes = new List<byte>();
            bytes.Add(0x0A);
            bytes.Add((byte)channelBytes.Length);
            bytes.AddRange(channelBytes);
            bytes.Add(0x12);
            bytes.Add(0x02);
            bytes.Add(0x08);
            bytes.Add(stateCode);
            bytes.Add(0x18);
            bytes.Add(0x00);
            bytes.Add(0x20);
            bytes.Add(0x04);

            return Uri.EscapeDataString(Convert.ToBase64String(bytes.ToArray()));
        }

        private async Task LoadChannelSubscriptionStateAsync(string videoId, JsonObject alreadyLoadedNextRoot)
        {
            var loadGeneration = _subscriptionStateGeneration;

            if (string.IsNullOrWhiteSpace(videoId))
            {
                if (loadGeneration == _subscriptionStateGeneration)
                {
                    _currentSubscriptionState = ChannelSubscriptionState.Unknown;
                    UpdateSubscriptionVisualState();
                }
                return;
            }

            var preliminaryResult = ExtractSubscriptionStateFromNext(alreadyLoadedNextRoot, "public /next");
            ApplySubscriptionEndpointData(preliminaryResult);
            if (string.IsNullOrWhiteSpace(currentChannelId) && preliminaryResult != null && !string.IsNullOrWhiteSpace(preliminaryResult.ChannelId))
            {
                currentChannelId = preliminaryResult.ChannelId;
            }

            var token = await GetTvAccessTokenAsync(false);
            if (loadGeneration != _subscriptionStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    UpdateSubscriptionVisualState();
                }
                System.Diagnostics.Debug.WriteLine("[Subscription] TV refresh/access token not available; using public /next state");
                return;
            }

            try
            {
                var nextResult = await TryLoadChannelSubscriptionFromAuthenticatedNextAsync(videoId, token);
                if (loadGeneration != _subscriptionStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (nextResult != null && nextResult.Found)
                {
                    ApplyLoadedSubscriptionState(nextResult);
                    System.Diagnostics.Debug.WriteLine("[Subscription] Current subscription state from " + nextResult.Source + ": " + _currentSubscriptionState);
                    return;
                }

                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                    System.Diagnostics.Debug.WriteLine("[Subscription] Current subscription state from public /next fallback: " + _currentSubscriptionState);
                    return;
                }

                _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                UpdateSubscriptionVisualState();
                System.Diagnostics.Debug.WriteLine("[Subscription] Could not determine subscription state; using NotSubscribed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subscription] Error loading subscription state: " + ex.Message);
                if (loadGeneration == _subscriptionStateGeneration && string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    if (preliminaryResult != null && preliminaryResult.Found)
                    {
                        ApplyLoadedSubscriptionState(preliminaryResult);
                    }
                    else
                    {
                        _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                        UpdateSubscriptionVisualState();
                    }
                }
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromAuthenticatedNextAsync(string videoId, string accessToken)
        {
            var tvResult = await TryLoadChannelSubscriptionFromNextClientAsync(
                videoId,
                accessToken,
                false,
                "authenticated /next TVHTML5"
            );

            if (tvResult != null && tvResult.Found)
            {
                return tvResult;
            }

            return await TryLoadChannelSubscriptionFromNextClientAsync(
                videoId,
                accessToken,
                true,
                "authenticated /next MWEB"
            );
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromNextClientAsync(
            string videoId,
            string accessToken,
            bool mobileWebClient,
            string sourceName
        )
        {
            try
            {
                var url = BuildInnertubeUrl("next");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedNextPayload(videoId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json"
                    );

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeMwebClientHeaderName,
                            InnertubeMwebClientVersion,
                            InnertubeMwebUserAgent
                        );
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeTvClientHeaderName,
                            InnertubeTvClientVersion,
                            InnertubeTvUserAgent
                        );
                    }

                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Subscription] "
                            + sourceName
                            + " failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + json
                        );
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    return ExtractSubscriptionStateFromNext(root, sourceName);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subscription] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyLoadedSubscriptionState(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            ApplySubscriptionEndpointData(result);

            if (!string.IsNullOrWhiteSpace(result.ChannelId))
            {
                currentChannelId = result.ChannelId;
            }

            _currentSubscriptionState = result.State;
            if (result.NotificationState != ChannelNotificationState.Unknown)
            {
                _currentNotificationState = result.NotificationState;
            }
            else if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                _currentNotificationState = ChannelNotificationState.Default;
            }
            else
            {
                _currentNotificationState = ChannelNotificationState.Default;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Subscription] Notification state: "
                + _currentNotificationState
                + " from "
                + result.Source
            );

            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();
        }

        private void ApplySubscriptionEndpointData(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeParams))
            {
                _subscribeParams = result.SubscribeParams;
            }

            if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams))
            {
                _unsubscribeParams = result.UnsubscribeParams;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
            {
                _subscribeClickTrackingParams = result.SubscribeClickTrackingParams;
            }

            if (!string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
            {
                _unsubscribeClickTrackingParams = result.UnsubscribeClickTrackingParams;
            }
        }

        private static SubscriptionLoadResult ExtractSubscriptionStateFromNext(JsonObject root, string sourceName)
        {
            var result = new SubscriptionLoadResult
            {
                Found = false,
                State = ChannelSubscriptionState.Unknown,
                NotificationState = ChannelNotificationState.Unknown,
                Source = sourceName
            };

            if (root == null)
            {
                return result;
            }

            bool foundDirect;
            var directState = FindDirectSubscriptionState(root, string.Empty, 0, out foundDirect);
            if (foundDirect)
            {
                result.Found = true;
                result.State = directState;
            }

            CollectSubscriptionEndpointData(root, string.Empty, result, 0);

            if (!result.Found)
            {
                if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams) || HasUnsubscribeEndpoint(root, 0))
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.Subscribed;
                }
                else if (!string.IsNullOrWhiteSpace(result.SubscribeParams) || HasSubscribeEndpoint(root, 0))
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.NotSubscribed;
                }
            }

            bool foundNotification;
            var notificationState = FindCurrentNotificationState(root, out foundNotification);
            if (foundNotification)
            {
                result.NotificationState = notificationState;
            }
            else if (result.State == ChannelSubscriptionState.Subscribed)
            {
                result.NotificationState = ChannelNotificationState.Default;
            }

            return result;
        }

        private static ChannelSubscriptionState FindDirectSubscriptionState(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelSubscriptionState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.Boolean && IsSubscriptionBooleanKey(lowerKey, nextPath))
                        {
                            found = true;
                            return pair.Value.GetBoolean() ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                        }

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsSubscriptionStatusKey(lowerKey, nextPath))
                        {
                            bool parsed;
                            var parsedState = ParseSubscriptionStateText(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return parsedState;
                            }
                        }

                        bool nestedFound;
                        var nested = FindDirectSubscriptionState(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindDirectSubscriptionState(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static bool IsSubscriptionBooleanKey(string lowerKey, string lowerPath)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            var path = lowerPath ?? string.Empty;
            if (!(path.Contains("subscribe") || path.Contains("subscription") || path.Contains("videoowner") || path.Contains("owner")))
            {
                return false;
            }

            return compact == "subscribed"
                || compact == "issubscribed"
                || compact == "subscribedtochannel"
                || compact == "issubscribedtochannel"
                || compact == "channelissubscribed"
                || compact == "iscurrentusersubscribed"
                || compact == "viewersubscribed";
        }

        private static bool IsSubscriptionStatusKey(string lowerKey, string lowerPath)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            var path = lowerPath ?? string.Empty;
            if (!(path.Contains("subscribe") || path.Contains("subscription") || path.Contains("notification")))
            {
                return false;
            }

            return compact == "subscriptionstatus"
                || compact == "subscribestatus"
                || compact == "subscribebuttonstate"
                || compact == "state"
                || compact == "status";
        }

        private static ChannelSubscriptionState ParseSubscriptionStateText(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return ChannelSubscriptionState.Unknown;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized == "SUBSCRIBED" || normalized == "SUBSCRIPTION_STATUS_SUBSCRIBED" || normalized == "CHANNEL_SUBSCRIBED")
            {
                parsed = true;
                return ChannelSubscriptionState.Subscribed;
            }

            if (normalized == "UNSUBSCRIBED" || normalized == "NOT_SUBSCRIBED" || normalized == "SUBSCRIPTION_STATUS_UNSUBSCRIBED" || normalized == "CHANNEL_NOT_SUBSCRIBED")
            {
                parsed = true;
                return ChannelSubscriptionState.NotSubscribed;
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static void CollectSubscriptionEndpointData(
            IJsonValue value,
            string path,
            SubscriptionLoadResult result,
            int depth
        )
        {
            if (value == null || result == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("subscribeEndpoint"))
                    {
                        ReadSubscriptionEndpointData(
                            obj.GetNamedValue("subscribeEndpoint"),
                            obj,
                            true,
                            result
                        );
                    }

                    if (obj.ContainsKey("unsubscribeEndpoint"))
                    {
                        ReadSubscriptionEndpointData(
                            obj.GetNamedValue("unsubscribeEndpoint"),
                            obj,
                            false,
                            result
                        );
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();
                        CollectSubscriptionEndpointData(pair.Value, nextPath, result, depth + 1);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectSubscriptionEndpointData(array[(int)i], path + "[]", result, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static void ReadSubscriptionEndpointData(
            IJsonValue endpointValue,
            JsonObject parentObject,
            bool subscribe,
            SubscriptionLoadResult result
        )
        {
            if (endpointValue == null || endpointValue.ValueType != JsonValueType.Object || result == null)
            {
                return;
            }

            try
            {
                var endpoint = endpointValue.GetObject();
                var parameters = GetJsonStringSafe(endpoint, "params");
                var clickTrackingParams = GetJsonStringSafe(parentObject, "clickTrackingParams");
                if (string.IsNullOrWhiteSpace(clickTrackingParams))
                {
                    clickTrackingParams = GetJsonStringSafe(endpoint, "clickTrackingParams");
                }

                var endpointChannelId = GetFirstChannelIdFromEndpoint(endpoint);
                if (!string.IsNullOrWhiteSpace(endpointChannelId) && string.IsNullOrWhiteSpace(result.ChannelId))
                {
                    result.ChannelId = endpointChannelId;
                }

                if (subscribe)
                {
                    if (!string.IsNullOrWhiteSpace(parameters) && string.IsNullOrWhiteSpace(result.SubscribeParams))
                    {
                        result.SubscribeParams = parameters;
                    }

                    if (!string.IsNullOrWhiteSpace(clickTrackingParams) && string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
                    {
                        result.SubscribeClickTrackingParams = clickTrackingParams;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(parameters) && string.IsNullOrWhiteSpace(result.UnsubscribeParams))
                    {
                        result.UnsubscribeParams = parameters;
                    }

                    if (!string.IsNullOrWhiteSpace(clickTrackingParams) && string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
                    {
                        result.UnsubscribeClickTrackingParams = clickTrackingParams;
                    }
                }
            }
            catch
            {
            }
        }

        private static string GetFirstChannelIdFromEndpoint(JsonObject endpoint)
        {
            try
            {
                if (endpoint == null || !endpoint.ContainsKey("channelIds"))
                {
                    return string.Empty;
                }

                var channelIdsValue = endpoint.GetNamedValue("channelIds");
                if (channelIdsValue == null || channelIdsValue.ValueType != JsonValueType.Array)
                {
                    return string.Empty;
                }

                var channelIds = channelIdsValue.GetArray();
                if (channelIds.Count == 0 || channelIds[0].ValueType != JsonValueType.String)
                {
                    return string.Empty;
                }

                return channelIds[0].GetString();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool HasSubscribeEndpoint(IJsonValue value, int depth)
        {
            return HasEndpointNamed(value, "subscribeEndpoint", depth);
        }

        private static bool HasUnsubscribeEndpoint(IJsonValue value, int depth)
        {
            return HasEndpointNamed(value, "unsubscribeEndpoint", depth);
        }

        private static bool HasEndpointNamed(IJsonValue value, string endpointName, int depth)
        {
            if (value == null || string.IsNullOrWhiteSpace(endpointName) || depth > 80)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey(endpointName))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        if (HasEndpointNamed(pair.Value, endpointName, depth + 1))
                        {
                            return true;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (HasEndpointNamed(array[(int)i], endpointName, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static ChannelNotificationState FindCurrentNotificationState(IJsonValue root, out bool found)
        {
            found = false;
            if (root == null)
            {
                return ChannelNotificationState.Unknown;
            }

            bool innerFound;
            var state = FindNotificationStateFromCurrentStateId(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            state = FindNotificationStateFromSelectedOption(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            state = FindNotificationStateFromExplicitCurrentFields(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromCurrentStateId(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path ?? string.Empty;

                    if (IsNotificationRelatedPath(lowerPath) && obj.ContainsKey("states"))
                    {
                        var currentStateId = GetJsonScalarStringSafe(obj, "currentStateId");
                        if (string.IsNullOrWhiteSpace(currentStateId))
                        {
                            currentStateId = GetJsonScalarStringSafe(obj, "current_state_id");
                        }

                        if (!string.IsNullOrWhiteSpace(currentStateId))
                        {
                            var statesValue = obj.GetNamedValue("states");
                            if (statesValue != null && statesValue.ValueType == JsonValueType.Array)
                            {
                                var states = statesValue.GetArray();
                                for (uint i = 0; i < states.Count; i++)
                                {
                                    var stateValue = states[(int)i];
                                    if (stateValue == null || stateValue.ValueType != JsonValueType.Object)
                                    {
                                        continue;
                                    }

                                    var stateObj = stateValue.GetObject();
                                    var stateId = GetJsonScalarStringSafe(stateObj, "stateId");
                                    if (string.IsNullOrWhiteSpace(stateId))
                                    {
                                        stateId = GetJsonScalarStringSafe(stateObj, "state_id");
                                    }

                                    if (!string.IsNullOrWhiteSpace(stateId)
                                        && string.Equals(stateId, currentStateId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        ChannelNotificationState parsedState;
                                        if (TryParseNotificationStateFromObject(stateValue, 0, out parsedState))
                                        {
                                            found = true;
                                            return parsedState;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();

                        bool nestedFound;
                        var nested = FindNotificationStateFromCurrentStateId(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindNotificationStateFromCurrentStateId(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromSelectedOption(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (ObjectHasSelectedTrue(obj))
                    {
                        ChannelNotificationState parsedState;
                        if (TryParseNotificationStateFromObject(value, 0, out parsedState))
                        {
                            found = true;
                            return parsedState;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();

                        bool nestedFound;
                        var nested = FindNotificationStateFromSelectedOption(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindNotificationStateFromSelectedOption(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromExplicitCurrentFields(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var compact = lowerKey.Replace("_", string.Empty).Replace("-", string.Empty);
                        var nextPath = string.IsNullOrEmpty(path)
                            ? lowerKey
                            : path + "." + lowerKey;

                        if (pair.Value != null
                            && pair.Value.ValueType == JsonValueType.String
                            && IsCurrentNotificationStateKey(compact, nextPath))
                        {
                            bool parsed;
                            var parsedState = ParseNotificationStateText(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return parsedState;
                            }
                        }

                        bool nestedFound;
                        var nested = FindNotificationStateFromExplicitCurrentFields(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindNotificationStateFromExplicitCurrentFields(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return ChannelNotificationState.Unknown;
        }

        private static bool IsNotificationRelatedPath(string lowerPath)
        {
            var path = lowerPath ?? string.Empty;
            return path.Contains("notification")
                || path.Contains("bell")
                || path.Contains("subscriptionnotification")
                || path.Contains("notificationpreference");
        }

        private static bool IsCurrentNotificationStateKey(string compactKey, string lowerPath)
        {
            var key = compactKey ?? string.Empty;
            var path = lowerPath ?? string.Empty;
            if (!IsNotificationRelatedPath(path) && !key.Contains("notification"))
            {
                return false;
            }

            if (key == "currentnotificationstate"
                || key == "currentnotificationpreference"
                || key == "currentstate")
            {
                return true;
            }

            if (path.Contains("current")
                && (key == "notificationstate"
                    || key == "notificationpreference"
                    || key == "subscriptionnotificationpreference"))
            {
                return true;
            }

            return false;
        }

        private static bool ObjectHasSelectedTrue(JsonObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            return GetJsonBooleanSafe(obj, "selected")
                || GetJsonBooleanSafe(obj, "isSelected")
                || GetJsonBooleanSafe(obj, "is_selected")
                || GetJsonBooleanSafe(obj, "checked")
                || GetJsonBooleanSafe(obj, "isChecked")
                || GetJsonBooleanSafe(obj, "is_checked");
        }

        private static bool GetJsonBooleanSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return false;
                }

                var value = obj.GetNamedValue(key);
                return value != null && value.ValueType == JsonValueType.Boolean && value.GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        private static string GetJsonScalarStringSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return string.Empty;
                }

                var value = obj.GetNamedValue(key);
                if (value == null)
                {
                    return string.Empty;
                }

                if (value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }

                if (value.ValueType == JsonValueType.Number)
                {
                    return ((int)Math.Round(value.GetNumber())).ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool TryParseNotificationStateFromObject(IJsonValue value, int depth, out ChannelNotificationState state)
        {
            state = ChannelNotificationState.Unknown;
            if (value == null || depth > 12)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();

                    // First read real labels/state fields. Do not let iconType win over text,
                    // because YouTube sometimes keeps a NOTIFICATIONS_NONE icon inside the
                    // selected/default bell state object.
                    if (TryParseNotificationStateFromObjectStrings(obj, false, out state))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (!IsPreferredNotificationTextContainerKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (IsNotificationIconKey(compact)
                            || IsPreferredNotificationTextContainerKey(compact)
                            || IsNotificationEndpointOrActionKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }

                    // Icon parsing is only a fallback. This fixes the case where the current
                    // state is personalized/default but the object also contains a none icon.
                    if (TryParseNotificationStateFromObjectStrings(obj, true, out state))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (!IsNotificationIconKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        ChannelNotificationState nestedState;
                        if (TryParseNotificationStateFromObject(array[(int)i], depth + 1, out nestedState))
                        {
                            state = nestedState;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryParseNotificationStateFromObjectStrings(
            JsonObject obj,
            bool iconsOnly,
            out ChannelNotificationState state
        )
        {
            state = ChannelNotificationState.Unknown;
            if (obj == null)
            {
                return false;
            }

            foreach (var pair in obj)
            {
                var key = pair.Key ?? string.Empty;
                var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);
                var isIconKey = IsNotificationIconKey(compact);

                if (iconsOnly != isIconKey)
                {
                    continue;
                }

                if (pair.Value != null
                    && pair.Value.ValueType == JsonValueType.String
                    && IsNotificationTextOrIconKey(compact))
                {
                    bool parsed;
                    var parsedState = ParseNotificationStateText(pair.Value.GetString(), out parsed);
                    if (parsed)
                    {
                        state = parsedState;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNotificationIconKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "icon" || key == "iconname" || key == "icontype";
        }

        private static bool IsPreferredNotificationTextContainerKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "title"
                || key == "text"
                || key == "simpletext"
                || key == "label"
                || key == "accessibility"
                || key == "accessibilitydata"
                || key == "accessibilitylabel"
                || key == "tooltip"
                || key == "subtitle"
                || key == "description";
        }

        private static bool IsNotificationEndpointOrActionKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key.Contains("endpoint")
                || key.Contains("command")
                || key.Contains("action")
                || key.Contains("service")
                || key.Contains("tracking")
                || key.Contains("menu")
                || key.Contains("params")
                || key.Contains("token");
        }

        private static bool IsNotificationTextOrIconKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "icon"
                || key == "iconname"
                || key == "icontype"
                || key == "accessibilitylabel"
                || key == "label"
                || key == "text"
                || key == "simpletext"
                || key == "title"
                || key == "tooltip"
                || key == "notificationpreference"
                || key == "notificationstate"
                || key == "state";
        }

        private static ChannelNotificationState ParseNotificationStateText(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return ChannelNotificationState.Unknown;
            }

            var normalized = value.Trim().ToUpperInvariant();
            var compact = normalized
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty);

            if (normalized == "ALL" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_ALL")
            {
                parsed = true;
                return ChannelNotificationState.All;
            }

            if (normalized == "NONE" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_NONE")
            {
                parsed = true;
                return ChannelNotificationState.None;
            }

            if (normalized == "PERSONALIZED" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_PERSONALIZED")
            {
                parsed = true;
                return ChannelNotificationState.Default;
            }

            // Check personalized/default before none. Some TV /next objects contain both
            // "personalized notifications" text and a nested off/none icon; the text is the
            // selected state, the icon can be only a menu asset.
            if (compact.Contains("PERSONALIZED")
                || compact.Contains("PERSONALISED")
                || compact.Contains("DEFAULT")
                || compact.Contains("NOTIFICATIONSPERSONALIZED")
                || compact == "NOTIFICATIONS"
                || compact == "NOTIFICATION"
                || compact.Contains("OCCASIONAL")
                || compact.Contains("SOMENOTIFICATION")
                || compact.Contains("HIGHLIGHT")
                || normalized.Contains("ПЕРСОНАЛ")
                || normalized.Contains("РЕКОМЕНД")
                || normalized.Contains("НЕКОТОР")
                || normalized.Contains("ВАЖН")
                || normalized.Contains("ИНТЕРЕС"))
            {
                parsed = true;
                return ChannelNotificationState.Default;
            }

            if (compact.Contains("ALLNOTIFICATION")
                || compact.Contains("NOTIFICATIONSALL")
                || compact.Contains("NOTIFICATIONALL")
                || compact.Contains("NOTIFICATIONSACTIVE")
                || compact.Contains("NOTIFICATIONACTIVE")
                || compact.Contains("RINGING")
                || normalized.Contains("ВСЕ УВЕДОМ")
                || normalized.Contains("ВСЕ ОПОВЕЩ"))
            {
                parsed = true;
                return ChannelNotificationState.All;
            }

            if (compact.Contains("NONOTIFICATION")
                || compact.Contains("NOTIFICATIONSNONE")
                || compact.Contains("NOTIFICATIONNONE")
                || compact.Contains("NOTIFICATIONSOFF")
                || compact.Contains("NOTIFICATIONOFF")
                || compact.Contains("NOTIFICATIONDISABLED")
                || compact.Contains("MUTED")
                || normalized.Contains("НЕ ПРИСЫЛАТЬ")
                || normalized.Contains("БЕЗ УВЕДОМ")
                || normalized.Contains("НЕТ УВЕДОМ")
                || normalized.Contains("НИКАКИХ УВЕДОМ")
                || normalized.Contains("УВЕДОМЛЕНИЯ ОТКЛЮЧ")
                || normalized.Contains("ОТКЛЮЧИТЬ УВЕДОМ"))
            {
                parsed = true;
                return ChannelNotificationState.None;
            }

            return ChannelNotificationState.Unknown;
        }

        private static string GetJsonStringSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return string.Empty;
                }

                var value = obj.GetNamedValue(key);
                if (value != null && value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string BuildSubscriptionPayload(string channelId, string parameters, string clickTrackingParams)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var user = new JsonObject();
            user["enableSafetyMode"] = JsonValue.CreateBooleanValue(false);
            context["user"] = user;

            var request = new JsonObject();
            request["internalExperimentFlags"] = new JsonArray();
            request["consistencyTokenJars"] = new JsonArray();
            context["request"] = request;

            if (!string.IsNullOrWhiteSpace(clickTrackingParams))
            {
                var clickTracking = new JsonObject();
                clickTracking["clickTrackingParams"] = JsonValue.CreateStringValue(clickTrackingParams);
                context["clickTracking"] = clickTracking;
            }

            var channelIds = new JsonArray();
            channelIds.Add(JsonValue.CreateStringValue(channelId));

            var payload = new JsonObject();
            payload["context"] = context;
            payload["channelIds"] = channelIds;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleUserVideoRatingAsync(UserVideoRating.Like);
        }

        private async void DislikeButton_Click(object sender, RoutedEventArgs e)
        {
            await ToggleUserVideoRatingAsync(UserVideoRating.Dislike);
        }

        private async Task ToggleUserVideoRatingAsync(UserVideoRating requestedRating)
        {
            if (_ratingRequestInProgress || string.IsNullOrWhiteSpace(currentVideoId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var videoIdAtClick = currentVideoId;
            var oldRating = _currentUserRating;
            var newRating = oldRating == requestedRating ? UserVideoRating.None : requestedRating;
            var stateGeneration = ++_ratingStateGeneration;

            _ratingRequestInProgress = true;
            _currentUserRating = newRating;
            UpdateRatingVisualState();

            try
            {
                var success = await SetUserVideoRatingAsync(videoIdAtClick, newRating, token);
                if (stateGeneration != _ratingStateGeneration || !string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentUserRating = oldRating;
                    UpdateRatingVisualState();
                    await ShowRatingMessageAsync(
                        Localization.GetString("RatingFailed"),
                        Localization.GetString("RatingRejectedDetailed")
                    );
                    return;
                }

                // Do not immediately reload /next here. YouTube can return the previous toggle
                // state for a short time after /like/removelike, which made the icon switch
                // back after a second click. The clicked state is the source of truth until
                // the next video load or manual refresh.
                _currentUserRating = newRating;
                UpdateRatingVisualState();
            }
            catch (Exception ex)
            {
                if (stateGeneration == _ratingStateGeneration && string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    _currentUserRating = oldRating;
                    UpdateRatingVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Rating] Error toggling rating: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("RatingFailed"), ex.Message);
            }
            finally
            {
                if (stateGeneration == _ratingStateGeneration && string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    _ratingRequestInProgress = false;
                    UpdateRatingVisualState();
                }
            }
        }

        private async Task<bool> SetUserVideoRatingAsync(string videoId, UserVideoRating rating, string accessToken)
        {
            var endpoint = rating == UserVideoRating.Like
                ? "like/like"
                : rating == UserVideoRating.Dislike
                    ? "like/dislike"
                    : "like/removelike";

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(BuildRatingPayload(videoId), Encoding.UTF8, "application/json");
                AddYouTubeAuthHeaders(request, accessToken, true);

                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Rating] Innertube rating update OK: " + rating);
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Rating] Innertube rating failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
            }

            return await SetUserVideoRatingWithDataApiAsync(videoId, rating, accessToken);
        }

        private async Task<bool> SetUserVideoRatingWithDataApiAsync(
            string videoId,
            UserVideoRating rating,
            string accessToken
        )
        {
            var ratingValue = rating == UserVideoRating.Like
                ? "like"
                : rating == UserVideoRating.Dislike
                    ? "dislike"
                    : "none";

            var url = YouTubeDataApiBaseUrl
                + "videos/rate?id="
                + Uri.EscapeDataString(videoId)
                + "&rating="
                + Uri.EscapeDataString(ratingValue);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                AddYouTubeAuthHeaders(request, accessToken, false);
                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Rating] Data API rating update OK: " + ratingValue);
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Rating] Data API rating failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
                return false;
            }
        }

        private async Task LoadUserVideoRatingAsync(string videoId, JsonObject alreadyLoadedNextRoot)
        {
            var loadGeneration = _ratingStateGeneration;

            if (string.IsNullOrWhiteSpace(videoId))
            {
                if (loadGeneration == _ratingStateGeneration)
                {
                    _currentUserRating = UserVideoRating.None;
                    UpdateRatingVisualState();
                }
                return;
            }

            var token = await GetTvAccessTokenAsync(false);
            if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                _currentUserRating = UserVideoRating.None;
                UpdateRatingVisualState();
                System.Diagnostics.Debug.WriteLine("[Rating] TV refresh/access token not available; rating state hidden");
                return;
            }

            try
            {
                // The unauthenticated /next response is useful for counts, but it usually cannot know
                // the current user's selected like/dislike. First ask authenticated Innertube /next,
                // mirroring TubeReplacer's /next flow, then fall back to the Data API only if needed.
                var nextResult = await TryLoadUserVideoRatingFromAuthenticatedNextAsync(videoId, token);
                if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (nextResult != null && nextResult.Found)
                {
                    _currentUserRating = nextResult.Rating;
                    System.Diagnostics.Debug.WriteLine("[Rating] Current user rating from " + nextResult.Source + ": " + _currentUserRating);
                    UpdateRatingVisualState();
                    return;
                }

                var dataApiResult = await TryLoadUserVideoRatingFromDataApiAsync(videoId, token);
                if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (dataApiResult != null && dataApiResult.Found)
                {
                    _currentUserRating = dataApiResult.Rating;
                    System.Diagnostics.Debug.WriteLine("[Rating] Current user rating from Data API: " + _currentUserRating);
                    UpdateRatingVisualState();
                    return;
                }

                _currentUserRating = UserVideoRating.None;
                UpdateRatingVisualState();
                System.Diagnostics.Debug.WriteLine("[Rating] Could not determine current user rating; using None");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Error loading rating state: " + ex.Message);
                if (loadGeneration == _ratingStateGeneration && string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    _currentUserRating = UserVideoRating.None;
                    UpdateRatingVisualState();
                }
            }
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromAuthenticatedNextAsync(string videoId, string accessToken)
        {
            var tvResult = await TryLoadUserVideoRatingFromNextClientAsync(
                videoId,
                accessToken,
                false,
                "authenticated /next TVHTML5"
            );

            if (tvResult != null && tvResult.Found)
            {
                return tvResult;
            }

            return await TryLoadUserVideoRatingFromNextClientAsync(
                videoId,
                accessToken,
                true,
                "authenticated /next MWEB"
            );
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromNextClientAsync(
            string videoId,
            string accessToken,
            bool mobileWebClient,
            string sourceName
        )
        {
            try
            {
                var url = BuildInnertubeUrl("next");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedNextPayload(videoId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json"
                    );

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeMwebClientHeaderName,
                            InnertubeMwebClientVersion,
                            InnertubeMwebUserAgent
                        );
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeTvClientHeaderName,
                            InnertubeTvClientVersion,
                            InnertubeTvUserAgent
                        );
                    }

                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Rating] "
                            + sourceName
                            + " failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + json
                        );
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    ApplyMainSaveStateFromAuthenticatedNext(root, videoId, sourceName);
                    bool found;
                    var rating = ExtractUserRatingFromNext(root, out found);
                    if (!found)
                    {
                        System.Diagnostics.Debug.WriteLine("[Rating] " + sourceName + " did not contain selected like/dislike state");
                        return null;
                    }

                    return new RatingLoadResult
                    {
                        Found = true,
                        Rating = rating,
                        Source = sourceName
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyMainSaveStateFromAuthenticatedNext(
            JsonObject root,
            string videoId,
            string sourceName)
        {
            bool isSaved;
            if (!TryExtractMainSaveStateFromNext(root, out isSaved))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SaveButton] " + sourceName + " did not contain an explicit Save state");
                return;
            }

            if (!string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            _mainSaveStateSupportedByNext = true;
            SetImageSource(
                SaveActionIcon,
                isSaved ? "Assets/save_clicked.png" : "Assets/save.png");
            System.Diagnostics.Debug.WriteLine(
                "[SaveButton] State from " + sourceName + ": " + (isSaved ? "saved" : "not saved"));
        }

        private static bool TryExtractMainSaveStateFromNext(JsonObject root, out bool isSaved)
        {
            isSaved = false;
            if (root == null)
            {
                return false;
            }

            JsonObject videoActions = null;
            foreach (var candidate in EnumerateObjects(root))
            {
                if (!candidate.ContainsKey("videoPrimaryInfoRenderer"))
                {
                    continue;
                }

                try
                {
                    var renderer = candidate.GetNamedObject("videoPrimaryInfoRenderer");
                    if (renderer.ContainsKey("videoActions"))
                    {
                        videoActions = renderer.GetNamedObject("videoActions");
                        break;
                    }
                }
                catch
                {
                }
            }

            if (videoActions == null)
            {
                return false;
            }

            foreach (var candidate in EnumerateObjects(videoActions))
            {
                string serialized;
                try
                {
                    serialized = candidate.Stringify().ToUpperInvariant();
                }
                catch
                {
                    continue;
                }

                if (serialized.IndexOf("GET_ADD_TO_PLAYLIST", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("ADDTOPLAYLISTSERVICEENDPOINT", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("PLAYLIST_ADD", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("WATCH-SAVE", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("SAVE_TO_PLAYLIST", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                bool boolState;
                if (TryGetDirectBoolean(candidate, "isToggled", out boolState)
                    || TryGetDirectBoolean(candidate, "isSelected", out boolState)
                    || TryGetDirectBoolean(candidate, "selected", out boolState))
                {
                    isSaved = boolState;
                    return true;
                }

                string state;
                if (TryGetDirectString(candidate, "state", out state)
                    || TryGetDirectString(candidate, "buttonState", out state)
                    || TryGetDirectString(candidate, "selectionState", out state))
                {
                    var normalized = (state ?? string.Empty).Trim().ToUpperInvariant();
                    if (normalized == "INACTIVE"
                        || normalized == "UNSELECTED"
                        || normalized == "OFF"
                        || normalized == "DEFAULT"
                        || normalized == "BUTTON_VIEW_MODEL_STATE_INACTIVE")
                    {
                        isSaved = false;
                        return true;
                    }

                    if (normalized == "ACTIVE"
                        || normalized == "SELECTED"
                        || normalized == "TOGGLED"
                        || normalized == "ON"
                        || normalized == "SAVED"
                        || normalized == "ADDED"
                        || normalized == "BUTTON_VIEW_MODEL_STATE_ACTIVE")
                    {
                        isSaved = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetDirectBoolean(JsonObject obj, string key, out bool value)
        {
            value = false;
            try
            {
                if (obj != null && obj.ContainsKey(key))
                {
                    var jsonValue = obj.GetNamedValue(key);
                    if (jsonValue != null && jsonValue.ValueType == JsonValueType.Boolean)
                    {
                        value = jsonValue.GetBoolean();
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryGetDirectString(JsonObject obj, string key, out string value)
        {
            value = string.Empty;
            try
            {
                if (obj != null && obj.ContainsKey(key))
                {
                    var jsonValue = obj.GetNamedValue(key);
                    if (jsonValue != null && jsonValue.ValueType == JsonValueType.String)
                    {
                        value = jsonValue.GetString();
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromDataApiAsync(string videoId, string accessToken)
        {
            try
            {
                var url = YouTubeDataApiBaseUrl
                    + "videos/getRating?id="
                    + Uri.EscapeDataString(videoId);

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    AddYouTubeAuthHeaders(request, accessToken, false);
                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Rating] getRating failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + json
                        );
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    var rating = ExtractRatingFromGetRatingResponse(root);
                    return new RatingLoadResult
                    {
                        Found = true,
                        Rating = ParseUserVideoRating(rating),
                        Source = "Data API getRating"
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Data API getRating error: " + ex.Message);
                return null;
            }
        }

        private static string ExtractRatingFromGetRatingResponse(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("items"))
                {
                    return string.Empty;
                }

                var itemsValue = root.GetNamedValue("items");
                if (itemsValue == null || itemsValue.ValueType != JsonValueType.Array)
                {
                    return string.Empty;
                }

                var items = itemsValue.GetArray();
                if (items.Count == 0 || items[0].ValueType != JsonValueType.Object)
                {
                    return string.Empty;
                }

                var item = items[0].GetObject();
                return item.GetNamedString("rating", string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static UserVideoRating ExtractUserRatingFromNext(JsonObject root, out bool foundSignal)
        {
            foundSignal = false;
            if (root == null)
            {
                return UserVideoRating.None;
            }

            UserVideoRating directRating;
            bool foundDirect;
            directRating = FindDirectRatingStatus(root, string.Empty, 0, out foundDirect);
            if (foundDirect)
            {
                foundSignal = true;
                return directRating;
            }

            var matches = new List<RatingToggleMatch>();
            CollectRatingToggleMatches(root, string.Empty, matches, 0);

            var disliked = matches.FirstOrDefault(m => m.IsToggled && m.Rating == UserVideoRating.Dislike);
            if (disliked != null)
            {
                foundSignal = true;
                return UserVideoRating.Dislike;
            }

            var liked = matches.FirstOrDefault(m => m.IsToggled && m.Rating == UserVideoRating.Like);
            if (liked != null)
            {
                foundSignal = true;
                return UserVideoRating.Like;
            }

            if (matches.Count > 0)
            {
                foundSignal = true;
                return UserVideoRating.None;
            }

            return UserVideoRating.None;
        }

        private static UserVideoRating FindDirectRatingStatus(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return UserVideoRating.None;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsDirectRatingStatusKey(lowerKey))
                        {
                            bool parsed;
                            var rating = ParseDirectRatingStatus(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return rating;
                            }
                        }

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.Boolean)
                        {
                            var boolValue = pair.Value.GetBoolean();
                            if (boolValue && IsLikeBooleanKey(lowerKey))
                            {
                                found = true;
                                return UserVideoRating.Like;
                            }

                            if (boolValue && IsDislikeBooleanKey(lowerKey))
                            {
                                found = true;
                                return UserVideoRating.Dislike;
                            }
                        }

                        bool nestedFound;
                        var nested = FindDirectRatingStatus(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindDirectRatingStatus(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return UserVideoRating.None;
        }

        private static bool IsDirectRatingStatusKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "likestatus"
                || compact == "ratingstatus"
                || compact == "userrating"
                || compact == "feedbackstate"
                || compact == "selectedrating"
                || compact == "selectionstate";
        }

        private static bool IsLikeBooleanKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "isliked" || compact == "likedbyviewer" || compact == "viewerliked";
        }

        private static bool IsDislikeBooleanKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "isdisliked" || compact == "dislikedbyviewer" || compact == "viewerdisliked";
        }

        private static UserVideoRating ParseDirectRatingStatus(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return UserVideoRating.None;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized == "LIKE" || normalized == "LIKED" || normalized == "LIKE_STATUS_LIKE")
            {
                parsed = true;
                return UserVideoRating.Like;
            }

            if (normalized == "DISLIKE" || normalized == "DISLIKED" || normalized == "LIKE_STATUS_DISLIKE")
            {
                parsed = true;
                return UserVideoRating.Dislike;
            }

            if (normalized == "NONE" || normalized == "NO_RATING" || normalized == "INDIFFERENT" || normalized == "LIKE_STATUS_INDIFFERENT" || normalized == "RATING_UNSPECIFIED")
            {
                parsed = true;
                return UserVideoRating.None;
            }

            return UserVideoRating.None;
        }

        private static void CollectRatingToggleMatches(
            IJsonValue value,
            string path,
            List<RatingToggleMatch> matches,
            int depth
        )
        {
            if (value == null || matches == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("isToggled"))
                    {
                        var rating = DetermineRatingKindForToggleObject(obj, path);
                        if (rating != UserVideoRating.None)
                        {
                            bool isToggled = false;
                            try
                            {
                                var toggledValue = obj.GetNamedValue("isToggled");
                                if (toggledValue != null && toggledValue.ValueType == JsonValueType.Boolean)
                                {
                                    isToggled = toggledValue.GetBoolean();
                                }
                            }
                            catch
                            {
                                isToggled = false;
                            }

                            matches.Add(new RatingToggleMatch
                            {
                                Rating = rating,
                                IsToggled = isToggled,
                                Path = path
                            });
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();
                        CollectRatingToggleMatches(pair.Value, nextPath, matches, depth + 1);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectRatingToggleMatches(array[(int)i], path + "[]", matches, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static UserVideoRating DetermineRatingKindForToggleObject(JsonObject obj, string path)
        {
            var lowerPath = (path ?? string.Empty).ToLowerInvariant();
            if (lowerPath.Contains("dislike"))
            {
                return UserVideoRating.Dislike;
            }

            if (lowerPath.Contains("likebutton") || lowerPath.Contains("like_button") || lowerPath.Contains("segmentedlikedislike"))
            {
                return UserVideoRating.Like;
            }

            bool foundIcon;
            var iconRating = FindRatingIconType(obj, 0, out foundIcon);
            if (foundIcon)
            {
                return iconRating;
            }

            return UserVideoRating.None;
        }

        private static UserVideoRating FindRatingIconType(IJsonValue value, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 8)
            {
                return UserVideoRating.None;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = (pair.Key ?? string.Empty).ToLowerInvariant();
                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String)
                        {
                            var text = pair.Value.GetString();
                            if ((key == "icontype" || key == "icon" || key == "accessibilitylabel") && IsDislikeText(text))
                            {
                                found = true;
                                return UserVideoRating.Dislike;
                            }

                            if ((key == "icontype" || key == "icon" || key == "accessibilitylabel") && IsLikeText(text))
                            {
                                found = true;
                                return UserVideoRating.Like;
                            }
                        }

                        bool nestedFound;
                        var nested = FindRatingIconType(pair.Value, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindRatingIconType(array[(int)i], depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return UserVideoRating.None;
        }

        private static bool IsLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim().ToUpperInvariant();
            return normalized == "LIKE" || normalized == "LIKE_SELECTED" || normalized == "LIKE_FILLED";
        }

        private static bool IsDislikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim().ToUpperInvariant();
            return normalized == "DISLIKE" || normalized == "DISLIKE_SELECTED" || normalized == "DISLIKE_FILLED";
        }

        private static UserVideoRating ParseUserVideoRating(string rating)
        {
            if (string.Equals(rating, "like", StringComparison.OrdinalIgnoreCase))
            {
                return UserVideoRating.Like;
            }

            if (string.Equals(rating, "dislike", StringComparison.OrdinalIgnoreCase))
            {
                return UserVideoRating.Dislike;
            }

            return UserVideoRating.None;
        }

        private static async Task<string> GetTvAccessTokenAsync(bool showErrors)
        {
            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                if (showErrors)
                {
                    await ShowStaticRatingMessageAsync(
                        Localization.GetString("SignInRequired"),
                        Localization.GetString("TvTokenMissingDetailed")
                    );
                }

                System.Diagnostics.Debug.WriteLine("[Rating] TV refresh token not found");
                return string.Empty;
            }

            var accessToken = await global::Config.RefreshAccessTokenAsync(refreshToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                if (showErrors)
                {
                    await ShowStaticRatingMessageAsync(
                        Localization.GetString("SignInRequired"),
                        Localization.GetString("TvTokenExchangeFailedDetailed")
                    );
                }

                System.Diagnostics.Debug.WriteLine("[Rating] Failed to exchange TV refresh token for access token");
                return string.Empty;
            }

            return accessToken;
        }

        private static async Task ShowStaticRatingMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = Localization.GetString("OK")
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + title + ": " + message);
            }
        }

        private static void AddYouTubeAuthHeaders(
            HttpRequestMessage request,
            string accessToken,
            bool innertubeRequest
        )
        {
            if (innertubeRequest)
            {
                AddInnertubeAuthHeadersForClient(
                    request,
                    accessToken,
                    InnertubeTvClientHeaderName,
                    InnertubeTvClientVersion,
                    InnertubeTvUserAgent
                );
                return;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
        }

        private static void AddInnertubeAuthHeadersForClient(
            HttpRequestMessage request,
            string accessToken,
            string clientNameHeader,
            string clientVersion,
            string userAgent
        )
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Config.ApplySelectedAccountHeader(request, true);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        private void ResetSubscriptionUiForNewVideo()
        {
            _subscriptionStateGeneration++;
            _currentSubscriptionState = ChannelSubscriptionState.Unknown;
            _currentNotificationState = ChannelNotificationState.Default;
            _subscriptionRequestInProgress = false;
            _subscribeParams = string.Empty;
            _unsubscribeParams = string.Empty;
            _subscribeClickTrackingParams = string.Empty;
            _unsubscribeClickTrackingParams = string.Empty;

            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
            }
            if (SubscriptionMenuBottomSheetTransform != null)
            {
                SubscriptionMenuBottomSheetTransform.Y = 440;
            }

            UpdateSubscriptionVisualState();
        }

        private void UpdateSubscriptionVisualState()
        {
            var isSubscribed = _currentSubscriptionState == ChannelSubscriptionState.Subscribed;

            if (SubscribeButton != null)
            {
                // Keep the button enabled visually while a request is in progress. The click handler
                // already ignores duplicate clicks through _subscriptionRequestInProgress. Disabling
                // the Button on older UWP builds can suppress image content in the custom template.
                SubscribeButton.IsEnabled = !string.IsNullOrWhiteSpace(currentChannelId);
                SubscribeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                SubscribeButton.Opacity = 1.0;
                SubscribeButton.Padding = isSubscribed ? new Thickness(14, 8, 15, 8) : new Thickness(14, 7, 14, 7);
                SubscribeButton.MinWidth = 0;
            }

            if (SubscribeButtonContainer != null)
            {
                var containerBrush = isSubscribed
                    ? (App.GetThemeBrush("AppSurfaceBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 39, 39, 39)))
                    : (App.GetThemeBrush("PrimaryActionBackgroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 241, 241)));
                SubscribeButtonContainer.Background = containerBrush;
                SubscribeButtonContainer.BorderBrush = containerBrush;
                SubscribeButtonContainer.Opacity = 1.0;
                SubscribeButtonContainer.MinWidth = 0;
                SubscribeButtonContainer.CornerRadius = new CornerRadius(18);
            }

            if (SubscribeButtonText != null)
            {
                SubscribeButtonText.Text = Localization.GetString("Subscribe");
                SubscribeButtonText.Visibility = isSubscribed ? Visibility.Collapsed : Visibility.Visible;
                SubscribeButtonText.Foreground = App.GetThemeBrush("PrimaryActionForegroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 15, 15, 15));
            }

            if (SubscribeSubscribedIconsPanel != null)
            {
                SubscribeSubscribedIconsPanel.Visibility = isSubscribed ? Visibility.Visible : Visibility.Collapsed;
                SubscribeSubscribedIconsPanel.Opacity = 1.0;
            }

            if (SubscribeNotificationIcon != null)
            {
                SubscribeNotificationIcon.Visibility = Visibility.Visible;
                SubscribeNotificationIcon.Opacity = 1.0;
                SubscribeNotificationIcon.Width = 22;
                SubscribeNotificationIcon.Height = 22;
                SubscribeNotificationIcon.Stretch = Windows.UI.Xaml.Media.Stretch.Uniform;
                SetImageSource(SubscribeNotificationIcon, GetNotificationIconAssetPath(_currentNotificationState));
            }

            if (SubscribeDownArrowIcon != null)
            {
                SubscribeDownArrowIcon.Visibility = Visibility.Visible;
                SubscribeDownArrowIcon.Opacity = 1.0;
                SubscribeDownArrowIcon.Width = 16;
                SubscribeDownArrowIcon.Height = 16;
                SubscribeDownArrowIcon.Margin = new Thickness(6, 0, 0, 0);
                SubscribeDownArrowIcon.Stretch = Windows.UI.Xaml.Media.Stretch.Uniform;
                SetImageSource(SubscribeDownArrowIcon, "Assets/down_arrow.png");
            }

            UpdateSubscriptionMenuVisualState();
        }

        private void UpdateSubscriptionMenuVisualState()
        {
            var effectiveState = _currentNotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : _currentNotificationState;

            if (NotificationAllCheckmark != null)
            {
                NotificationAllCheckmark.Visibility = effectiveState == ChannelNotificationState.All
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (NotificationPersonalizedCheckmark != null)
            {
                NotificationPersonalizedCheckmark.Visibility = effectiveState == ChannelNotificationState.Default
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (NotificationNoneCheckmark != null)
            {
                NotificationNoneCheckmark.Visibility = effectiveState == ChannelNotificationState.None
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private static string GetNotificationIconAssetPath(ChannelNotificationState state)
        {
            if (state == ChannelNotificationState.All)
            {
                return "Assets/all_notifications.png";
            }

            if (state == ChannelNotificationState.None)
            {
                return "Assets/none_notifications.png";
            }

            return "Assets/notifications.png";
        }

        private void ResetRatingUiForNewVideo()
        {
            _ratingStateGeneration++;
            _currentUserRating = UserVideoRating.None;
            _ratingRequestInProgress = false;
            _mainSaveStateSupportedByNext = false;
            SetImageSource(SaveActionIcon, "Assets/save.png");
            UpdateRatingVisualState();
        }

        private void UpdateRatingVisualState()
        {
            if (LikeButton != null)
            {
                LikeButton.IsEnabled = !_ratingRequestInProgress;
                LikeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                LikeButton.Opacity = 1.0;
            }

            if (DislikeButton != null)
            {
                DislikeButton.IsEnabled = !_ratingRequestInProgress;
                DislikeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                DislikeButton.Opacity = 1.0;
            }

            if (LikeIcon != null)
            {
                LikeIcon.Opacity = 1.0;
                SetImageSource(
                    LikeIcon,
                    _currentUserRating == UserVideoRating.Like
                        ? "Assets/player/like_clicked.png"
                        : "Assets/player/like.png"
                );
            }

            if (DislikeIcon != null)
            {
                DislikeIcon.Opacity = 1.0;
                SetImageSource(
                    DislikeIcon,
                    _currentUserRating == UserVideoRating.Dislike
                        ? "Assets/player/dislike_clicked.png"
                        : "Assets/player/dislike.png"
                );
            }
        }

        private static void SetImageSource(Image image, string assetPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            try
            {
                App.SetThemeImageSource(image, assetPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Failed to set icon source: " + ex.Message);
            }
        }

        private async Task ShowRatingMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = Localization.GetString("OK")
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + title + ": " + message);
            }
        }

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
            var height = SharePopupPanel != null ? SharePopupPanel.ActualHeight : 0;
            if (height <= 1)
            {
                height = 390;
            }
            return height + 20;
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
            var height = SaveBottomSheetPanel != null ? SaveBottomSheetPanel.ActualHeight : 0;
            return (height > 0 ? height : 540) + 20;
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
            double height = ShareBottomSheetPanel != null ? ShareBottomSheetPanel.ActualHeight : 0;
            if (height <= 0)
                height = 330;

            return height + 20;
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

                // Call /next endpoint to get related videos
                var nextJson = await PostPersonalizedTvNextAsync(videoId);
                var nextRoot = Windows.Data.Json.JsonValue.Parse(nextJson).GetObject();

                // Extract related videos by walking the JSON
                ExtractRelatedVideosFromJson(nextRoot, relatedVideos);

                // A successful TV response can still omit the recommendation shelf for a
                // particular account/video. Keep playback-page recommendations working by
                // falling back to the ordinary WEB shape only when TV yielded no cards at all.
                if (relatedVideos.Count == 0)
                {
                    var webJson = await PostInnertubeAsync("next", BuildNextPayload(videoId)).ConfigureAwait(false);
                    var webRoot = Windows.Data.Json.JsonValue.Parse(webJson).GetObject();
                    ExtractRelatedVideosFromJson(webRoot, relatedVideos);
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

        private async Task<string> PostPersonalizedTvNextAsync(string videoId)
        {
            Config.LoadUserToken();
            var refreshToken = Config.UserToken;
            if (!string.IsNullOrWhiteSpace(refreshToken))
            {
                var accessToken = await Config.RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(accessToken))
                {
                    var url = BuildInnertubeUrl("next");
                    using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                    {
                        request.Content = new StringContent(
                            BuildAuthenticatedNextPayload(videoId, false),
                            Encoding.UTF8,
                            "application/json");
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeTvClientHeaderName,
                            InnertubeTvClientVersion,
                            InnertubeTvUserAgent);

                        var response = await httpClient.SendAsync(request).ConfigureAwait(false);
                        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(json))
                        {
                            return json;
                        }

                        System.Diagnostics.Debug.WriteLine(
                            "[RelatedVideos] Authenticated TV /next failed: " + response.StatusCode);
                    }
                }
            }

            return await PostInnertubeAsync("next", BuildNextPayload(videoId)).ConfigureAwait(false);
        }

        private void ExtractRelatedVideosFromJson(
            Windows.Data.Json.IJsonValue value,
            List<RelatedVideoCardItem> videos
        )
        {
            int visitedNodes = 0;
            ExtractRelatedVideosFromJson(value, videos, ref visitedNodes);
        }

        private void ExtractRelatedVideosFromJson(
            Windows.Data.Json.IJsonValue value,
            List<RelatedVideoCardItem> videos,
            ref int visitedNodes
        )
        {
            if (value == null || videos == null || videos.Count >= MaxRelatedVideosToShow || visitedNodes >= MaxRelatedJsonNodesToScan)
            {
                return;
            }

            visitedNodes++;

            if (value.ValueType == Windows.Data.Json.JsonValueType.Object)
            {
                var obj = value.GetObject();

                // Modern /next can return related cards in several renderer shapes.
                // Keep lockupViewModel support, but also parse the common WEB compactVideoRenderer path.
                TryAddRelatedVideoFromRenderer(obj, videos);
                if (videos.Count >= MaxRelatedVideosToShow)
                {
                    return;
                }

                // Recursively walk through all properties, but stop early once enough cards are found.
                foreach (var pair in obj)
                {
                    ExtractRelatedVideosFromJson(pair.Value, videos, ref visitedNodes);
                    if (videos.Count >= MaxRelatedVideosToShow || visitedNodes >= MaxRelatedJsonNodesToScan)
                    {
                        return;
                    }
                }
            }
            else if (value.ValueType == Windows.Data.Json.JsonValueType.Array)
            {
                var arr = value.GetArray();
                for (int i = 0; i < arr.Count; i++)
                {
                    ExtractRelatedVideosFromJson(arr[i], videos, ref visitedNodes);
                    if (videos.Count >= MaxRelatedVideosToShow || visitedNodes >= MaxRelatedJsonNodesToScan)
                    {
                        return;
                    }
                }
            }
        }

        private void TryAddRelatedVideoFromRenderer(JsonObject obj, List<RelatedVideoCardItem> videos)
        {
            if (obj == null || videos == null || videos.Count >= MaxRelatedVideosToShow)
            {
                return;
            }

            RelatedVideoCardItem videoData = null;

            try
            {
                if (obj.ContainsKey("lockupViewModel"))
                {
                    videoData = ExtractVideoFromLockup(obj.GetNamedObject("lockupViewModel"));
                }
                else if (obj.ContainsKey("compactVideoRenderer"))
                {
                    videoData = ExtractVideoFromStandardRenderer(obj.GetNamedObject("compactVideoRenderer"));
                }
                else if (obj.ContainsKey("videoRenderer"))
                {
                    videoData = ExtractVideoFromStandardRenderer(obj.GetNamedObject("videoRenderer"));
                }
                else if (obj.ContainsKey("gridVideoRenderer"))
                {
                    videoData = ExtractVideoFromStandardRenderer(obj.GetNamedObject("gridVideoRenderer"));
                }
                else if (obj.ContainsKey("playlistPanelVideoRenderer"))
                {
                    videoData = ExtractVideoFromStandardRenderer(obj.GetNamedObject("playlistPanelVideoRenderer"));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[RelatedVideos] Renderer parse skipped: " + ex.Message);
                videoData = null;
            }

            AddRelatedVideoIfValid(videos, videoData);
        }

        private void ApplyPlaylistQueueUi()
        {
            if (PlaylistQueuePanel == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(currentPlaylistId) || _playlistQueue.Count == 0)
            {
                PlaylistQueuePanel.Visibility = Visibility.Collapsed;
                UpdatePlaylistTransportControls();
                return;
            }

            PlaylistQueuePanel.Visibility = Visibility.Visible;

            if (PlaylistQueueTitleText != null)
            {
                PlaylistQueueTitleText.Text = string.IsNullOrWhiteSpace(_playlistQueueTitle)
                    ? (Config.IsMixPlaylistId(currentPlaylistId) ? Localization.GetString("Mix") : Localization.GetString("Playlist"))
                    : _playlistQueueTitle;
            }

            var index = GetCurrentPlaylistIndex();
            if (PlaylistQueuePositionText != null)
            {
                PlaylistQueuePositionText.Text = index >= 0
                    ? (index + 1) + " / " + _playlistQueue.Count
                    : Localization.Format("VideosSuffixFormat", _playlistQueue.Count);
            }

            // Mark the item that is playing right now.
            for (int i = 0; i < _playlistQueue.Count; i++)
            {
                var queueItem = _playlistQueue[i];
                if (queueItem != null)
                {
                    queueItem.is_current = (i == index);
                }
            }

            if (PlaylistQueueContainer != null)
            {
                PlaylistQueueContainer.ItemsSource = null;
                PlaylistQueueContainer.ItemsSource = _playlistQueue;
            }

            UpdatePlaylistTransportControls();
        }

        private void UpdatePlaylistTransportControls()
        {
            if (CustomVideoPlayer == null)
            {
                return;
            }

            var index = GetCurrentPlaylistIndex();
            var canPrevious = !string.IsNullOrWhiteSpace(currentPlaylistId) && index > 0;
            var canNext = !string.IsNullOrWhiteSpace(currentPlaylistId)
                && index >= 0
                && index + 1 < _playlistQueue.Count;

            CustomVideoPlayer.SetSystemMediaNavigationEnabled(canPrevious, canNext);
        }

        // Walks a chain of nested objects, returning null if any link is missing.
        private static JsonObject GetObjectPath(JsonObject root, params string[] keys)
        {
            var current = root;
            for (var i = 0; i < keys.Length; i++)
            {
                if (current == null || !current.ContainsKey(keys[i]))
                {
                    return null;
                }

                try { current = current.GetNamedObject(keys[i]); }
                catch { return null; }
            }

            return current;
        }

        private int GetCurrentPlaylistIndex()
        {
            // Prefer the index YouTube reports for the queue; fall back to matching by id.
            if (_playlistCurrentIndex >= 0 && _playlistCurrentIndex < _playlistQueue.Count)
            {
                var atIndex = _playlistQueue[_playlistCurrentIndex];
                if (atIndex != null && string.Equals(atIndex.video_id, currentVideoId, StringComparison.Ordinal))
                {
                    return _playlistCurrentIndex;
                }
            }

            for (int i = 0; i < _playlistQueue.Count; i++)
            {
                var item = _playlistQueue[i];
                if (item != null && string.Equals(item.video_id, currentVideoId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        private void PlaylistQueueHeaderButton_Click(object sender, RoutedEventArgs e)
        {
            if (PlaylistQueueContainer == null)
            {
                return;
            }

            var wasExpanded = PlaylistQueueContainer.Visibility == Visibility.Visible;
            PlaylistQueueContainer.Visibility = wasExpanded ? Visibility.Collapsed : Visibility.Visible;

            if (PlaylistQueueChevron != null)
            {
                PlaylistQueueChevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
                PlaylistQueueChevron.RenderTransform = new RotateTransform
                {
                    Angle = wasExpanded ? 0 : 180
                };
            }
        }

        private void PlaylistQueueItem_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var item = button != null ? button.DataContext as RelatedVideoCardItem : null;
            if (item == null || string.IsNullOrWhiteSpace(item.video_id))
            {
                return;
            }

            if (string.Equals(item.video_id, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            NavigateToPlaylistVideo(item.video_id);
        }

        // Keeps the playlist / mix context while moving between its videos, so the queue (and,
        // for a jam, its endless continuation) survives the navigation.
        private async void NavigateToPlaylistVideo(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId) || _playlistSwitchInProgress)
            {
                return;
            }

            _playlistSwitchInProgress = true;
            try
            {
                await SwitchToVideoAsync(videoId, currentPlaylistId, _playlistQueueTitle);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Direct switch failed: " + ex.Message);
            }
            finally
            {
                _playlistSwitchInProgress = false;
                UpdatePlaylistTransportControls();
            }
        }

        private async Task SwitchPlaylistRelativeAsync(int offset, string reason)
        {
            if (_playlistSwitchInProgress || string.IsNullOrWhiteSpace(currentPlaylistId) || _playlistQueue.Count == 0)
            {
                return;
            }

            var index = GetCurrentPlaylistIndex();
            var targetIndex = index + offset;
            if (index < 0 || targetIndex < 0 || targetIndex >= _playlistQueue.Count)
            {
                UpdatePlaylistTransportControls();
                return;
            }

            var target = _playlistQueue[targetIndex];
            if (target == null || string.IsNullOrWhiteSpace(target.video_id))
            {
                return;
            }

            _playlistSwitchInProgress = true;
            try
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] " + reason + " -> " + target.video_id);
                await SwitchToVideoAsync(target.video_id, currentPlaylistId, _playlistQueueTitle);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] " + reason + " failed: " + ex.Message);
            }
            finally
            {
                _playlistSwitchInProgress = false;
                UpdatePlaylistTransportControls();
            }
        }

        // Playback cannot keep up with the current format — step down to the next lower height
        // this video offers (1080 -> 720 -> 480 -> 360). Mirrors what the official app does and,
        // more importantly, gets off a format the device is about to die on.
        private void CustomVideoPlayer_PlaybackStalling(object sender, object e)
        {
            // Do NOT change quality automatically here.
            //
            // MediaPlaybackSession enters Buffering during ordinary seeks, especially when the
            // user taps seek repeatedly. The player's buffering detector used to interpret three
            // such transitions as a bandwidth problem and this handler then reloaded the source at
            // a lower quality. Besides visibly changing the user's chosen quality, that source
            // replacement could be initiated by a Media Foundation callback thread and race the
            // seek that caused the buffering, producing RPC_E_WRONG_THREAD / native crashes.
            //
            // Manual quality selection and speed-driven source changes still use ChangeQualityAsync.
            System.Diagnostics.Debug.WriteLine(
                "[Video] PlaybackStalling ignored: automatic quality step-down is disabled");
        }

        // Records the video in the account's watch history once per opened video, shortly after
        // playback actually starts (mirrors what the official clients do with their stats pings).
        private async Task ReportWatchHistoryAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId) || _historyReportedVideoId == videoId)
            {
                return;
            }

            _historyReportedVideoId = videoId;

            try
            {
                Config.LoadUserToken();
                var refreshToken = Config.UserToken;
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    return; // Signed out — nothing to attribute the view to.
                }

                var position = CustomVideoPlayer != null
                    ? CustomVideoPlayer.CurrentPlaybackPosition.TotalSeconds : 0;
                var length = CustomVideoPlayer != null
                    ? CustomVideoPlayer.ParsedDuration.TotalSeconds : 0;

                await Config.ReportWatchHistoryAsync(videoId, refreshToken, position, length);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] History report failed: " + ex.Message);
            }
        }

        private async void CustomVideoPlayer_VideoEnded(object sender, object e)
        {
            // MediaEnded is raised directly by MediaPlayer now, so this path continues to work
            // while DispatcherTimer is throttled in the background / mini-player.
            await SwitchPlaylistRelativeAsync(1, "auto-advance");
        }

        private async void CustomVideoPlayer_NextRequested(object sender, object e)
        {
            await SwitchPlaylistRelativeAsync(1, "SMTC next");
        }

        private async void CustomVideoPlayer_PreviousRequested(object sender, object e)
        {
            await SwitchPlaylistRelativeAsync(-1, "SMTC previous");
        }

        // The watch queue for the current playlist / mix. YouTube returns it inside the same
        // /next response (playlistPanelRenderer) whenever the request carried a playlistId, so
        // no extra round-trip is needed. For auto-generated mixes ("jams", RD...) this panel is
        // the only place the queue exists.
        private void ExtractPlaylistQueueFromNext(JsonObject nextRoot)
        {
            ExtractPlaylistQueueFromNext(nextRoot, null);
        }

        // tvRoot, when present, is the authenticated TVHTML5 /next response. It is preferred over
        // the anonymous one because mixes are personalized: for the very same video the signed-in
        // queue and the anonymous queue share nothing but the seed.
        private void ExtractPlaylistQueueFromNext(JsonObject nextRoot, JsonObject tvRoot)
        {
            _playlistQueue.Clear();

            if ((nextRoot == null && tvRoot == null) || string.IsNullOrWhiteSpace(currentPlaylistId))
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Skipped (no playlist context on this video)");
                ResetJamQueue();
                return;
            }

            try
            {
                var fresh = new List<RelatedVideoCardItem>();
                _playlistCurrentIndex = -1;

                // TV shape first: contents.singleColumnWatchNextResults.playlist.playlist carries
                // the title/currentIndex, while the queue itself arrives as tileRenderer cards.
                if (tvRoot != null)
                {
                    var tvBox = GetObjectPath(tvRoot, "contents", "singleColumnWatchNextResults", "playlist", "playlist");

                    // Only the current index is trustworthy here: on the TV client
                    // playlist.playlist.title is the SEED VIDEO's title, not a queue label, so
                    // using it showed the music video's name where the official app shows the
                    // shelf header. Take that header ("Up next") instead.
                    if (tvBox != null && tvBox.ContainsKey("currentIndex"))
                    {
                        try { _playlistCurrentIndex = (int)tvBox.GetNamedNumber("currentIndex"); }
                        catch { _playlistCurrentIndex = -1; }
                    }

                    var shelfTitle = ExtractTvUpNextShelfTitle(tvRoot);
                    if (!string.IsNullOrWhiteSpace(shelfTitle))
                    {
                        _playlistQueueTitle = shelfTitle;
                    }

                    CollectTvPlaylistQueue(tvRoot, currentPlaylistId, 0, fresh);

                    System.Diagnostics.Debug.WriteLine(
                        "[PlaylistQueue] Authenticated TV queue: " + fresh.Count + " item(s), title="
                        + (string.IsNullOrWhiteSpace(_playlistQueueTitle) ? "(none)" : _playlistQueueTitle)
                    );
                }

                // Preferred path: contents.twoColumnWatchNextResults.playlist.playlist. That
                // object holds the queue itself plus the real title ("Mix - <video>"), the
                // isInfinite flag and currentIndex. There is NO playlistPanelRenderer wrapper in
                // this response, which is why the header used to fall back to a bare "Mix".
                var playlistBox = fresh.Count > 0 || nextRoot == null
                    ? null
                    : GetObjectPath(nextRoot, "contents", "twoColumnWatchNextResults", "playlist", "playlist");
                if (playlistBox != null)
                {
                    ApplyPlaylistBoxMetadata(playlistBox);

                    if (playlistBox.ContainsKey("contents"))
                    {
                        CollectPlaylistQueue(playlistBox.GetNamedValue("contents"), 0, fresh);
                    }
                }

                // Fallback: scan the whole response (older/other response shapes).
                if (fresh.Count == 0 && nextRoot != null)
                {
                    CollectPlaylistQueue(nextRoot, 0, fresh);
                }

                // A stored playlist comes back complete and in a stable order, so the fresh
                // response is authoritative. A mix ("jam") is different: YouTube regenerates a
                // sliding window around the current video, so taking it as-is drops everything
                // already watched. Keep our own growing list for mixes instead.
                if (!Config.IsMixPlaylistId(currentPlaylistId))
                {
                    ResetJamQueue();
                    _playlistQueue.AddRange(fresh);
                }
                else
                {
                    MergeJamQueue(fresh);
                    _playlistQueue.AddRange(_jamQueueItems);
                }

                System.Diagnostics.Debug.WriteLine(
                    "[PlaylistQueue] " + _playlistQueue.Count + " item(s) for " + currentPlaylistId
                    + " (fresh " + fresh.Count + ")"
                    + (string.IsNullOrWhiteSpace(_playlistQueueTitle) ? string.Empty : " (" + _playlistQueueTitle + ")")
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Parse failed: " + ex.Message);
            }
        }

        // Mixes keep their history: items already in the list stay and keep their order, and the
        // queue is only extended once the current video is the last one — the same way the
        // YouTube site grows a mix as you reach its end.
        private void MergeJamQueue(List<RelatedVideoCardItem> fresh)
        {
            if (!string.Equals(_jamQueuePlaylistId, currentPlaylistId, StringComparison.Ordinal))
            {
                // Switched to a different mix — start its history from scratch.
                _jamQueuePlaylistId = currentPlaylistId;
                _jamQueueItems.Clear();
                _jamQueueItems.AddRange(fresh);
                return;
            }

            var currentIndex = IndexOfVideo(_jamQueueItems, currentVideoId);

            // Not in the list yet (the user jumped somewhere else in the mix) — take what came
            // back so the current video is represented.
            var atEnd = currentIndex < 0 || currentIndex >= _jamQueueItems.Count - 1;
            if (!atEnd)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[PlaylistQueue] Keeping " + _jamQueueItems.Count + " item(s); not at the end yet ("
                    + (currentIndex + 1) + "/" + _jamQueueItems.Count + ")"
                );
                return;
            }

            var added = 0;
            for (int i = 0; i < fresh.Count; i++)
            {
                var item = fresh[i];
                if (item != null && IndexOfVideo(_jamQueueItems, item.video_id) < 0)
                {
                    _jamQueueItems.Add(item);
                    added++;
                }
            }

            System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Reached the end; extended mix by " + added + " item(s)");
        }

        private void ResetJamQueue()
        {
            _jamQueuePlaylistId = null;
            _jamQueueItems.Clear();
        }

        private static int IndexOfVideo(List<RelatedVideoCardItem> items, string videoId)
        {
            if (items == null || string.IsNullOrWhiteSpace(videoId))
            {
                return -1;
            }

            for (int i = 0; i < items.Count; i++)
            {
                var item = items[i];
                if (item != null && string.Equals(item.video_id, videoId, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        // The TV watch response labels the queue with a shelf header ("Up next"), separate from
        // the playlist box (whose title is the seed video). Returns the first non-blank
        // shelfHeaderRenderer title — that is the queue shelf's header.
        private string ExtractTvUpNextShelfTitle(JsonObject tvRoot)
        {
            try
            {
                return FindFirstShelfHeaderTitle(tvRoot, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Shelf title scan failed: " + ex.Message);
                return string.Empty;
            }
        }

        private string FindFirstShelfHeaderTitle(Windows.Data.Json.IJsonValue value, int depth)
        {
            if (value == null || depth > 24)
            {
                return string.Empty;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                if (obj.ContainsKey("shelfHeaderRenderer"))
                {
                    var header = obj.GetNamedObject("shelfHeaderRenderer");
                    var title = ExtractTextFromField(header, "title", string.Empty);
                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        return title;
                    }
                }

                foreach (var pair in obj)
                {
                    var found = FindFirstShelfHeaderTitle(pair.Value, depth + 1);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < array.Count; i++)
                {
                    var found = FindFirstShelfHeaderTitle(array[i], depth + 1);
                    if (!string.IsNullOrWhiteSpace(found))
                    {
                        return found;
                    }
                }
            }

            return string.Empty;
        }

        // Title / "now playing" index of the queue. Both watch shapes expose the same fields on
        // their playlist box, so one reader serves them.
        private void ApplyPlaylistBoxMetadata(JsonObject playlistBox)
        {
            if (playlistBox == null)
            {
                return;
            }

            var boxTitle = GetJsonString(playlistBox, "title");
            if (string.IsNullOrWhiteSpace(boxTitle) && playlistBox.ContainsKey("titleText"))
            {
                boxTitle = ExtractTextFromRunsOrSimpleText(playlistBox.GetNamedValue("titleText"));
            }
            if (!string.IsNullOrWhiteSpace(boxTitle))
            {
                _playlistQueueTitle = boxTitle;
            }

            if (playlistBox.ContainsKey("currentIndex"))
            {
                try { _playlistCurrentIndex = (int)playlistBox.GetNamedNumber("currentIndex"); }
                catch { _playlistCurrentIndex = -1; }
            }
        }

        // The TV watch response has no playlist.playlist.contents — the queue arrives as
        // tileRenderer cards in the "Up next" shelf, mixed into the same response as the ordinary
        // related-video shelves. A queue tile is told apart by carrying our playlistId in its
        // watch endpoint, and its "index" gives the real position, so the shelf order is not
        // trusted. Everything without that playlistId is a related video and must be ignored —
        // that is exactly what used to leak into the queue.
        private void CollectTvPlaylistQueue(
            Windows.Data.Json.IJsonValue value,
            string playlistId,
            int depth,
            List<RelatedVideoCardItem> target
        )
        {
            var found = new List<KeyValuePair<int, RelatedVideoCardItem>>();
            CollectTvPlaylistTiles(value, playlistId, depth, found);
            found.Sort((a, b) => a.Key.CompareTo(b.Key));

            for (int i = 0; i < found.Count; i++)
            {
                var item = found[i].Value;
                if (item != null && IndexOfVideo(target, item.video_id) < 0)
                {
                    target.Add(item);
                }
            }
        }

        private void CollectTvPlaylistTiles(
            Windows.Data.Json.IJsonValue value,
            string playlistId,
            int depth,
            List<KeyValuePair<int, RelatedVideoCardItem>> target
        )
        {
            if (value == null || depth > 24)
            {
                return;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                if (obj.ContainsKey("tileRenderer"))
                {
                    try
                    {
                        var tile = obj.GetNamedObject("tileRenderer");
                        var endpoint = GetTileWatchEndpoint(tile);
                        if (endpoint != null
                            && string.Equals(GetJsonString(endpoint, "playlistId"), playlistId, StringComparison.Ordinal))
                        {
                            var item = BuildQueueItemFromTile(tile, endpoint);
                            if (item != null)
                            {
                                var index = target.Count;
                                if (endpoint.ContainsKey("index"))
                                {
                                    try { index = (int)endpoint.GetNamedNumber("index"); }
                                    catch { }
                                }

                                target.Add(new KeyValuePair<int, RelatedVideoCardItem>(index, item));
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Tile skipped: " + ex.Message);
                    }
                }

                foreach (var pair in obj)
                {
                    CollectTvPlaylistTiles(pair.Value, playlistId, depth + 1, target);
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < array.Count; i++)
                {
                    CollectTvPlaylistTiles(array[i], playlistId, depth + 1, target);
                }
            }
        }

        private static JsonObject GetTileWatchEndpoint(JsonObject tile)
        {
            if (tile == null || !tile.ContainsKey("onSelectCommand"))
            {
                return null;
            }

            var onSelect = tile.GetNamedObject("onSelectCommand");
            if (onSelect.ContainsKey("watchEndpoint"))
            {
                return onSelect.GetNamedObject("watchEndpoint");
            }
            if (onSelect.ContainsKey("watchPlaylistEndpoint"))
            {
                return onSelect.GetNamedObject("watchPlaylistEndpoint");
            }

            return null;
        }

        private RelatedVideoCardItem BuildQueueItemFromTile(JsonObject tile, JsonObject endpoint)
        {
            var videoId = GetJsonString(endpoint, "videoId");
            if (string.IsNullOrWhiteSpace(videoId))
            {
                videoId = GetJsonString(tile, "contentId");
            }
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var item = new RelatedVideoCardItem();
            item.video_id = videoId;
            item.playlist_id = GetJsonString(endpoint, "playlistId");
            item.thumbnail = "https://i.ytimg.com/vi/" + videoId + "/hqdefault.jpg";
            item.channel_thumbnail = ExtractChannelThumbnailFromRenderer(tile);
            item.WatchedPercent = Config.ExtractWatchedPercent(tile);

            if (tile.ContainsKey("metadata"))
            {
                var metadata = tile.GetNamedObject("metadata");
                if (metadata.ContainsKey("tileMetadataRenderer"))
                {
                    var tileMetadata = metadata.GetNamedObject("tileMetadataRenderer");
                    item.title = ExtractTextFromField(tileMetadata, "title", string.Empty);

                    if (tileMetadata.ContainsKey("lines"))
                    {
                        var lines = tileMetadata.GetNamedArray("lines");
                        if (lines.Count > 0)
                        {
                            item.author = ExtractTileQueueLineText(lines[0].GetObject());
                        }
                    }
                }
            }

            if (tile.ContainsKey("header"))
            {
                var header = tile.GetNamedObject("header");
                if (header.ContainsKey("tileHeaderRenderer"))
                {
                    var tileHeader = header.GetNamedObject("tileHeaderRenderer");
                    if (tileHeader.ContainsKey("thumbnailOverlays"))
                    {
                        item.duration = ExtractDurationFromThumbnailOverlays(
                            tileHeader.GetNamedArray("thumbnailOverlays")
                        );
                    }
                }
            }

            return item;
        }

        private string ExtractTileQueueLineText(JsonObject line)
        {
            if (line == null || !line.ContainsKey("lineRenderer"))
            {
                return string.Empty;
            }

            var lineRenderer = line.GetNamedObject("lineRenderer");
            if (!lineRenderer.ContainsKey("items"))
            {
                return string.Empty;
            }

            var items = lineRenderer.GetNamedArray("items");
            if (items.Count == 0)
            {
                return string.Empty;
            }

            var first = items[0].GetObject();
            if (!first.ContainsKey("lineItemRenderer"))
            {
                return string.Empty;
            }

            return ExtractTextFromField(first.GetNamedObject("lineItemRenderer"), "text", string.Empty);
        }

        private void CollectPlaylistQueue(Windows.Data.Json.IJsonValue value, int depth, List<RelatedVideoCardItem> target)
        {
            if (value == null || depth > 24)
            {
                return;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();

                if (obj.ContainsKey("playlistPanelRenderer"))
                {
                    var panel = obj.GetNamedObject("playlistPanelRenderer");
                    var panelTitle = ExtractTextFromField(panel, "title", string.Empty);
                    if (!string.IsNullOrWhiteSpace(panelTitle))
                    {
                        _playlistQueueTitle = panelTitle;
                    }
                }

                if (obj.ContainsKey("playlistPanelVideoRenderer"))
                {
                    try
                    {
                        var item = ExtractVideoFromStandardRenderer(obj.GetNamedObject("playlistPanelVideoRenderer"));
                        if (item != null && !string.IsNullOrWhiteSpace(item.video_id))
                        {
                            if (IndexOfVideo(target, item.video_id) < 0)
                            {
                                target.Add(item);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Item skipped: " + ex.Message);
                    }
                }

                foreach (var pair in obj)
                {
                    CollectPlaylistQueue(pair.Value, depth + 1, target);
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (int i = 0; i < array.Count; i++)
                {
                    CollectPlaylistQueue(array[i], depth + 1, target);
                }
            }
        }

        private void AddRelatedVideoIfValid(List<RelatedVideoCardItem> videos, RelatedVideoCardItem videoData)
        {
            if (videos == null || videoData == null || string.IsNullOrWhiteSpace(videoData.video_id))
            {
                return;
            }

            for (int i = 0; i < videos.Count; i++)
            {
                var existing = videos[i];
                if (existing != null && string.Equals(existing.video_id, videoData.video_id, StringComparison.Ordinal))
                {
                    return;
                }
            }

            if (string.IsNullOrWhiteSpace(videoData.thumbnail))
            {
                videoData.thumbnail = "https://i.ytimg.com/vi/" + videoData.video_id + "/mqdefault.jpg";
            }

            if (string.IsNullOrWhiteSpace(videoData.title))
            {
                videoData.title = "Video";
            }

            videos.Add(videoData);
        }

        private RelatedVideoCardItem ExtractVideoFromStandardRenderer(JsonObject renderer)
        {
            if (renderer == null)
            {
                return null;
            }

            try
            {
                var videoId = GetJsonString(renderer, "videoId");
                if (string.IsNullOrWhiteSpace(videoId) && renderer.ContainsKey("navigationEndpoint"))
                {
                    var endpoint = renderer.GetNamedObject("navigationEndpoint");
                    videoId = ExtractVideoIdFromEndpoint(endpoint);
                }

                if (string.IsNullOrWhiteSpace(videoId))
                {
                    return null;
                }

                var videoItem = new RelatedVideoCardItem();
                videoItem.video_id = videoId;
                videoItem.playlist_id = ExtractPlaylistIdFromWatchEndpoint(renderer);
                videoItem.WatchedPercent = Config.ExtractWatchedPercent(renderer);
                videoItem.thumbnail = ExtractThumbnailFromRenderer(renderer);
                if (string.IsNullOrWhiteSpace(videoItem.thumbnail))
                {
                    videoItem.thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";
                }

                if (renderer.ContainsKey("title"))
                {
                    videoItem.title = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("title"));
                }
                if (string.IsNullOrWhiteSpace(videoItem.title) && renderer.ContainsKey("headline"))
                {
                    videoItem.title = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("headline"));
                }

                if (renderer.ContainsKey("shortBylineText"))
                {
                    videoItem.author = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("shortBylineText"));
                }
                if (string.IsNullOrWhiteSpace(videoItem.author) && renderer.ContainsKey("longBylineText"))
                {
                    videoItem.author = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("longBylineText"));
                }
                if (string.IsNullOrWhiteSpace(videoItem.author) && renderer.ContainsKey("ownerText"))
                {
                    videoItem.author = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("ownerText"));
                }

                videoItem.channel_thumbnail = ExtractChannelThumbnailFromRenderer(renderer);

                if (renderer.ContainsKey("lengthText"))
                {
                    videoItem.duration = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("lengthText"));
                }
                if (string.IsNullOrWhiteSpace(videoItem.duration) && renderer.ContainsKey("thumbnailOverlays"))
                {
                    videoItem.duration = ExtractDurationFromThumbnailOverlays(renderer.GetNamedArray("thumbnailOverlays"));
                }

                ExtractViewsAndPublishedFromRenderer(renderer, videoItem);

                System.Diagnostics.Debug.WriteLine("[RelatedVideos] Extracted renderer video: " + videoItem.video_id + " - " + videoItem.title);
                return videoItem;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[RelatedVideos] Error extracting renderer video: " + ex.Message);
                return null;
            }
        }

        // Playlist attached to a card's watch endpoint (mix / "jam" cards carry one).
        private static string ExtractPlaylistIdFromWatchEndpoint(JsonObject renderer)
        {
            try
            {
                if (renderer == null)
                {
                    return string.Empty;
                }

                if (renderer.ContainsKey("navigationEndpoint"))
                {
                    var endpoint = renderer.GetNamedObject("navigationEndpoint");
                    if (endpoint.ContainsKey("watchEndpoint"))
                    {
                        return GetJsonString(endpoint.GetNamedObject("watchEndpoint"), "playlistId");
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractVideoIdFromEndpoint(JsonObject endpoint)
        {
            try
            {
                if (endpoint == null)
                {
                    return string.Empty;
                }

                if (endpoint.ContainsKey("watchEndpoint"))
                {
                    var watchEndpoint = endpoint.GetNamedObject("watchEndpoint");
                    return GetJsonString(watchEndpoint, "videoId");
                }

                if (endpoint.ContainsKey("reelWatchEndpoint"))
                {
                    var reelEndpoint = endpoint.GetNamedObject("reelWatchEndpoint");
                    return GetJsonString(reelEndpoint, "videoId");
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractThumbnailFromRenderer(JsonObject renderer)
        {
            try
            {
                if (renderer == null || !renderer.ContainsKey("thumbnail"))
                {
                    return string.Empty;
                }

                var thumbnail = renderer.GetNamedObject("thumbnail");
                if (!thumbnail.ContainsKey("thumbnails"))
                {
                    return string.Empty;
                }

                var thumbs = thumbnail.GetNamedArray("thumbnails");
                for (int i = (int)thumbs.Count - 1; i >= 0; i--)
                {
                    var thumb = thumbs[i].GetObject();
                    var url = GetJsonString(thumb, "url");
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractChannelThumbnailFromRenderer(JsonObject renderer)
        {
            try
            {
                if (renderer == null)
                {
                    return string.Empty;
                }

                // Keep the same parser as every other video card so WEB compact/video renderer
                // changes are handled in one place.
                string url = Config.ExtractVideoCardChannelThumbnail(renderer);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return url;
                }

                if (renderer.ContainsKey("channelThumbnail"))
                {
                    url = ExtractBestImageUrl(renderer.GetNamedObject("channelThumbnail"));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }

                if (renderer.ContainsKey("channelThumbnailSupportedRenderers"))
                {
                    var supportedRenderers = renderer.GetNamedObject("channelThumbnailSupportedRenderers");
                    if (supportedRenderers.ContainsKey("channelThumbnailWithLinkRenderer"))
                    {
                        var channelThumbnailRenderer = supportedRenderers.GetNamedObject("channelThumbnailWithLinkRenderer");
                        if (channelThumbnailRenderer.ContainsKey("thumbnail"))
                        {
                            url = ExtractBestImageUrl(channelThumbnailRenderer.GetNamedObject("thumbnail"));
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                return url;
                            }
                        }
                    }
                }

                if (renderer.ContainsKey("owner"))
                {
                    var owner = renderer.GetNamedObject("owner");
                    if (owner.ContainsKey("videoOwnerRenderer"))
                    {
                        var ownerRenderer = owner.GetNamedObject("videoOwnerRenderer");
                        if (ownerRenderer.ContainsKey("thumbnail"))
                        {
                            url = ExtractBestImageUrl(ownerRenderer.GetNamedObject("thumbnail"));
                            if (!string.IsNullOrWhiteSpace(url))
                            {
                                return url;
                            }
                        }
                    }
                }

                if (renderer.ContainsKey("decoratedAvatarViewModel"))
                {
                    url = ExtractBestImageUrl(renderer.GetNamedObject("decoratedAvatarViewModel"));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }

                if (renderer.ContainsKey("avatar"))
                {
                    url = ExtractBestImageUrl(renderer.GetNamedObject("avatar"));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractChannelThumbnailFromLockup(JsonObject lockupVM)
        {
            try
            {
                if (lockupVM == null)
                {
                    return string.Empty;
                }

                var direct = Config.ExtractVideoCardChannelThumbnail(lockupVM);
                if (!string.IsNullOrWhiteSpace(direct))
                {
                    return direct;
                }

                if (!lockupVM.ContainsKey("metadata"))
                {
                    return string.Empty;
                }

                var metadata = lockupVM.GetNamedObject("metadata");
                return ExtractBestImageUrl(metadata);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractBestImageUrl(IJsonValue value)
        {
            int visitedNodes = 0;
            return ExtractBestImageUrl(value, ref visitedNodes);
        }

        private static string ExtractBestImageUrl(IJsonValue value, ref int visitedNodes)
        {
            if (value == null || visitedNodes > 700)
            {
                return string.Empty;
            }

            visitedNodes++;

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();

                    if (obj.ContainsKey("thumbnails"))
                    {
                        var url = ExtractBestImageUrlFromArray(obj.GetNamedArray("thumbnails"));
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }

                    if (obj.ContainsKey("sources"))
                    {
                        var url = ExtractBestImageUrlFromArray(obj.GetNamedArray("sources"));
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }

                    if (obj.ContainsKey("image"))
                    {
                        var url = ExtractBestImageUrl(obj.GetNamedValue("image"), ref visitedNodes);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }

                    if (obj.ContainsKey("thumbnail"))
                    {
                        var url = ExtractBestImageUrl(obj.GetNamedValue("thumbnail"), ref visitedNodes);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }

                    if (obj.ContainsKey("avatarViewModel"))
                    {
                        var url = ExtractBestImageUrl(obj.GetNamedValue("avatarViewModel"), ref visitedNodes);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var url = ExtractBestImageUrl(pair.Value, ref visitedNodes);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var arr = value.GetArray();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        var url = ExtractBestImageUrl(arr[i], ref visitedNodes);
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            return url;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractBestImageUrlFromArray(JsonArray array)
        {
            try
            {
                if (array == null || array.Count == 0)
                {
                    return string.Empty;
                }

                for (int i = (int)array.Count - 1; i >= 0; i--)
                {
                    var item = array[i].GetObject();
                    var url = GetJsonString(item, "url");
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        url = GetJsonString(item, "uri");
                    }

                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        return url;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractDurationFromLockupViewModel(JsonObject lockupVM)
        {
            if (lockupVM == null)
            {
                return string.Empty;
            }

            try
            {
                // Current lockupViewModel shape.
                var thumbnailViewModel = GetObjectPath(lockupVM, "contentImage", "thumbnailViewModel");
                if (thumbnailViewModel != null && thumbnailViewModel.ContainsKey("overlays"))
                {
                    var overlays = thumbnailViewModel.GetNamedArray("overlays");
                    for (int i = 0; i < overlays.Count; i++)
                    {
                        if (overlays[i].ValueType != JsonValueType.Object)
                        {
                            continue;
                        }

                        var overlay = overlays[i].GetObject();
                        if (!overlay.ContainsKey("thumbnailBottomOverlayViewModel"))
                        {
                            continue;
                        }

                        var bottomOverlay = overlay.GetNamedObject("thumbnailBottomOverlayViewModel");
                        if (!bottomOverlay.ContainsKey("badges"))
                        {
                            continue;
                        }

                        var badges = bottomOverlay.GetNamedArray("badges");
                        for (int b = 0; b < badges.Count; b++)
                        {
                            if (badges[b].ValueType != JsonValueType.Object)
                            {
                                continue;
                            }

                            var badgeContainer = badges[b].GetObject();
                            if (!badgeContainer.ContainsKey("thumbnailBadgeViewModel"))
                            {
                                continue;
                            }

                            var badge = badgeContainer.GetNamedObject("thumbnailBadgeViewModel");
                            if (!badge.ContainsKey("text"))
                            {
                                continue;
                            }

                            var duration = ExtractTextFromRunsOrSimpleText(badge.GetNamedValue("text"));
                            if (!string.IsNullOrWhiteSpace(duration))
                            {
                                return duration;
                            }
                        }
                    }
                }

                // Older lockupViewModel shape used by some TV/legacy clients.
                var oldOverlay = GetObjectPath(
                    lockupVM,
                    "contentImage",
                    "thumbnailOverlayViewModel",
                    "renderer",
                    "thumbnailOverlayTimeStatusViewModel"
                );
                if (oldOverlay != null && oldOverlay.ContainsKey("text"))
                {
                    var duration = ExtractTextFromRunsOrSimpleText(oldOverlay.GetNamedValue("text"));
                    if (!string.IsNullOrWhiteSpace(duration))
                    {
                        return duration;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractDurationFromThumbnailOverlays(JsonArray overlays)
        {
            try
            {
                if (overlays == null)
                {
                    return string.Empty;
                }

                for (int i = 0; i < overlays.Count; i++)
                {
                    var overlay = overlays[i].GetObject();
                    if (!overlay.ContainsKey("thumbnailOverlayTimeStatusRenderer"))
                    {
                        continue;
                    }

                    var timeRenderer = overlay.GetNamedObject("thumbnailOverlayTimeStatusRenderer");
                    if (timeRenderer.ContainsKey("text"))
                    {
                        var duration = ExtractTextFromRunsOrSimpleText(timeRenderer.GetNamedValue("text"));
                        if (!string.IsNullOrWhiteSpace(duration))
                        {
                            return duration;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static void ExtractViewsAndPublishedFromRenderer(JsonObject renderer, RelatedVideoCardItem videoItem)
        {
            if (renderer == null || videoItem == null)
            {
                return;
            }

            try
            {
                if (renderer.ContainsKey("viewCountText"))
                {
                    videoItem.views = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("viewCountText"));
                }

                if (renderer.ContainsKey("publishedTimeText"))
                {
                    videoItem.published = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("publishedTimeText"));
                }

                if ((string.IsNullOrWhiteSpace(videoItem.views) || string.IsNullOrWhiteSpace(videoItem.published)) && renderer.ContainsKey("metadataText"))
                {
                    var metadata = ExtractTextFromRunsOrSimpleText(renderer.GetNamedValue("metadataText"));
                    FillViewsAndPublishedFromMetadataText(videoItem, metadata);
                }
            }
            catch
            {
            }
        }

        private static void FillViewsAndPublishedFromMetadataText(RelatedVideoCardItem videoItem, string metadata)
        {
            if (videoItem == null || string.IsNullOrWhiteSpace(metadata))
            {
                return;
            }

            var parts = metadata.Split(new[] { '•', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                var part = parts[i] != null ? parts[i].Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(videoItem.views) && part.IndexOf("view", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    videoItem.views = part;
                }
                else if (string.IsNullOrWhiteSpace(videoItem.published))
                {
                    videoItem.published = part;
                }
            }
        }

        // metadataRows[row].metadataParts[part].text.content, guarded at every step.
        private static string GetLockupMetadataPart(JsonArray metadataRows, int rowIndex, int partIndex)
        {
            try
            {
                if (metadataRows == null || rowIndex < 0 || rowIndex >= metadataRows.Count)
                {
                    return string.Empty;
                }

                var row = metadataRows[rowIndex].GetObject();
                if (!row.ContainsKey("metadataParts"))
                {
                    return string.Empty;
                }

                var parts = row.GetNamedArray("metadataParts");
                if (partIndex < 0 || partIndex >= parts.Count)
                {
                    return string.Empty;
                }

                var part = parts[partIndex].GetObject();
                if (!part.ContainsKey("text"))
                {
                    return string.Empty;
                }

                var text = part.GetNamedObject("text");
                return text.ContainsKey("content") ? text.GetNamedString("content") : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private RelatedVideoCardItem ExtractVideoFromLockup(Windows.Data.Json.JsonObject lockupVM)
        {
            try
            {
                // Extract video ID from rendererContext.commandContext.onTap.innertubeCommand.watchEndpoint.videoId
                string videoId = "";
                string cardPlaylistId = "";
                if (lockupVM.ContainsKey("rendererContext"))
                {
                    var rendererContext = lockupVM.GetNamedObject("rendererContext");
                    if (rendererContext.ContainsKey("commandContext"))
                    {
                        var commandContext = rendererContext.GetNamedObject("commandContext");
                        if (commandContext.ContainsKey("onTap"))
                        {
                            var onTap = commandContext.GetNamedObject("onTap");
                            if (onTap.ContainsKey("innertubeCommand"))
                            {
                                var innertubeCommand = onTap.GetNamedObject("innertubeCommand");
                                if (innertubeCommand.ContainsKey("watchEndpoint"))
                                {
                                    var watchEndpoint = innertubeCommand.GetNamedObject(
                                        "watchEndpoint"
                                    );
                                    if (watchEndpoint.ContainsKey("videoId"))
                                    {
                                        videoId = watchEndpoint.GetNamedString("videoId");
                                    }

                                    // A mix / "jam" card points at a playlist as well; without
                                    // carrying it the queue is lost when the card is tapped.
                                    if (watchEndpoint.ContainsKey("playlistId"))
                                    {
                                        cardPlaylistId = watchEndpoint.GetNamedString("playlistId");
                                    }
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(videoId))
                {
                    return null;
                }

                // Create a RelatedVideoCardItem
                var videoItem = new RelatedVideoCardItem();
                videoItem.video_id = videoId;
                videoItem.playlist_id = cardPlaylistId;
                videoItem.WatchedPercent = Config.ExtractWatchedPercent(lockupVM);

                // Set thumbnail URL from YouTube
                videoItem.thumbnail = "https://i.ytimg.com/vi/" + videoId + "/mqdefault.jpg";
                videoItem.channel_thumbnail = ExtractChannelThumbnailFromLockup(lockupVM);

                // Extract title from metadata.lockupMetadataViewModel.title.content
                if (lockupVM.ContainsKey("metadata"))
                {
                    var metadata = lockupVM.GetNamedObject("metadata");
                    if (metadata.ContainsKey("lockupMetadataViewModel"))
                    {
                        var lockupMetaVM = metadata.GetNamedObject("lockupMetadataViewModel");
                        if (lockupMetaVM.ContainsKey("title"))
                        {
                            var titleObj = lockupMetaVM.GetNamedObject("title");
                            if (titleObj.ContainsKey("content"))
                            {
                                videoItem.title = titleObj.GetNamedString("content");
                            }
                        }
                    }
                }

                // Skip thumbnail extraction from API (we use YouTube URL instead)
                // Extract channel/author name from metadata rows
                if (lockupVM.ContainsKey("metadata"))
                {
                    var metadata = lockupVM.GetNamedObject("metadata");
                    if (metadata.ContainsKey("lockupMetadataViewModel"))
                    {
                        var lockupMetaVM = metadata.GetNamedObject("lockupMetadataViewModel");
                        if (lockupMetaVM.ContainsKey("metadata"))
                        {
                            var metaContent = lockupMetaVM.GetNamedObject("metadata");
                            if (metaContent.ContainsKey("contentMetadataViewModel"))
                            {
                                var contentMetaVM = metaContent.GetNamedObject(
                                    "contentMetadataViewModel"
                                );
                                if (contentMetaVM.ContainsKey("metadataRows"))
                                {
                                    // Layout of the lockup metadata (same as SymTube reads):
                                    //   row 0, part 0 -> channel name
                                    //   row 1, part 0 -> view count
                                    //   row 1, part 1 -> published time
                                    // Only row 0 was read before, which left the line under the
                                    // thumbnail without views/date.
                                    var metadataRows = contentMetaVM.GetNamedArray("metadataRows");
                                    videoItem.author = GetLockupMetadataPart(metadataRows, 0, 0);
                                    videoItem.views = GetLockupMetadataPart(metadataRows, 1, 0);
                                    videoItem.published = GetLockupMetadataPart(metadataRows, 1, 1);
                                }
                            }
                        }
                    }
                }

                // Modern lockups moved duration into
                // contentImage.thumbnailViewModel.overlays[].thumbnailBottomOverlayViewModel
                // .badges[].thumbnailBadgeViewModel.text. Keep the older
                // thumbnailOverlayTimeStatusViewModel path as a fallback.
                videoItem.duration = ExtractDurationFromLockupViewModel(lockupVM);

                if (lockupVM.ContainsKey("metadata"))
                {
                    var metadata = lockupVM.GetNamedObject("metadata");
                    if (metadata.ContainsKey("lockupMetadataViewModel"))
                    {
                        var lockupMetaVM = metadata.GetNamedObject("lockupMetadataViewModel");
                        if (lockupMetaVM.ContainsKey("metadata"))
                        {
                            var metaContent = lockupMetaVM.GetNamedObject("metadata");
                            if (metaContent.ContainsKey("contentMetadataViewModel"))
                            {
                                var contentMetaVM = metaContent.GetNamedObject(
                                    "contentMetadataViewModel"
                                );
                                if (contentMetaVM.ContainsKey("metadataRows"))
                                {
                                    var metadataRows = contentMetaVM.GetNamedArray("metadataRows");
                                    if (metadataRows.Count > 1)
                                    {
                                        var secondRow = metadataRows[1].GetObject();
                                        if (secondRow.ContainsKey("metadataParts"))
                                        {
                                            var metadataParts = secondRow.GetNamedArray(
                                                "metadataParts"
                                            );
                                            for (uint i = 0; i < metadataParts.Count; i++)
                                            {
                                                var part = metadataParts[(int)i].GetObject();
                                                if (part.ContainsKey("text"))
                                                {
                                                    var textObj = part.GetNamedObject("text");
                                                    if (textObj.ContainsKey("content"))
                                                    {
                                                        var content = textObj.GetNamedString("content");
                                                        if (i == 0)
                                                        {
                                                            videoItem.views = content;
                                                        }
                                                        else if (i == 1 && content != "•")
                                                        {
                                                            videoItem.published = content;
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[RelatedVideos] Extracted video: {videoItem.video_id} - {videoItem.title}"
                );
                return videoItem;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RelatedVideos] Error extracting video: {ex.Message}"
                );
                System.Diagnostics.Debug.WriteLine($"[RelatedVideos] Stack: {ex.StackTrace}");
                return null;
            }
        }

        private void ShowSubscriptionMenuBottomSheet()
        {
            if (_currentSubscriptionState != ChannelSubscriptionState.Subscribed)
            {
                return;
            }

            UpdateSubscriptionMenuVisualState();

            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }

            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            }

            AnimateSubscriptionMenuBottomSheet(true);
        }

        private void AnimateSubscriptionMenuBottomSheet(bool show)
        {
            if (SubscriptionMenuBottomSheetTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();
            animation.To = show ? 0 : 280;

            Storyboard.SetTarget(animation, SubscriptionMenuBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (SubscriptionMenuBottomSheetPanel != null)
                    {
                        SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    if (OverlayGrid != null)
                    {
                        OverlayGrid.Visibility = Visibility.Collapsed;
                    }
                };
            }

            storyboard.Begin();
        }

        private async void NotificationAllOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.All)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.All);
        }

        private async void NotificationPersonalizedOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.Default || _currentNotificationState == ChannelNotificationState.Unknown)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.Default);
        }

        private async void NotificationNoneOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentNotificationState == ChannelNotificationState.None)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.None);
        }

        private async void NotificationUnsubscribeOptionButton_Click(object sender, RoutedEventArgs e)
        {
            await SetChannelSubscriptionStateAsync(false);
        }

        private void SubscriptionMenuDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSubscriptionMenuBottomSheet(false);
        }

        private void SubscriptionMenuDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _subscriptionMenuInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _subscriptionMenuInitialTransformY = SubscriptionMenuBottomSheetTransform != null ? SubscriptionMenuBottomSheetTransform.Y : 0;
                _subscriptionMenuIsDragging = true;
                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_subscriptionMenuIsDragging && SubscriptionMenuBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _subscriptionMenuInitialY;
                double newY = _subscriptionMenuInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= 440)
                {
                    SubscriptionMenuBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_subscriptionMenuIsDragging)
            {
                _subscriptionMenuIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (SubscriptionMenuBottomSheetTransform != null && SubscriptionMenuBottomSheetTransform.Y > 140)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
                else
                {
                    AnimateSubscriptionMenuBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void OverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            AnimateSaveBottomSheet(false);
            AnimateCommentsBottomSheet(false);
            AnimateShareBottomSheet(false);
            AnimateSettingsBottomSheet(false);
            AnimateSubscriptionMenuBottomSheet(false);
            OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void ShareDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Close share bottom sheet when tapping drag area
            AnimateShareBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void SaveDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSaveBottomSheet(false);
            e.Handled = true;
        }

        private void SaveDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _saveInitialY = e.GetCurrentPoint(element).Position.Y;
                _saveInitialTransformY = SaveBottomSheetTransform != null ? SaveBottomSheetTransform.Y : 0;
                _saveIsDragging = true;
                e.Handled = true;
            }
        }

        private void SaveDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (!_saveIsDragging || element == null || SaveBottomSheetTransform == null)
            {
                return;
            }

            var dragOffset = e.GetCurrentPoint(element).Position.Y - _saveInitialY;
            var newY = _saveInitialTransformY + dragOffset;
            var dismissDistance = GetSaveSheetDismissDistance();
            if (newY >= 0 && newY <= dismissDistance)
            {
                SaveBottomSheetTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SaveDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_saveIsDragging)
            {
                return;
            }

            _saveIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SaveBottomSheetTransform != null
                && SaveBottomSheetTransform.Y > GetSaveSheetDismissDistance() * 0.5)
            {
                AnimateSaveBottomSheet(false);
            }
            else
            {
                AnimateSaveBottomSheet(true);
            }
            e.Handled = true;
        }

        private void ShareDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _shareInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _shareInitialTransformY = ShareBottomSheetTransform?.Y ?? 0;
                _shareIsDragging = true;
                e.Handled = true;
            }
        }

        private void ShareDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_shareIsDragging && ShareBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _shareInitialY;
                double newY = _shareInitialTransformY + dragOffset;

                double dismissDistance = GetShareSheetDismissDistance();
                if (newY >= 0 && newY <= dismissDistance)
                {
                    ShareBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void ShareDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_shareIsDragging)
            {
                _shareIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (ShareBottomSheetTransform.Y > GetShareSheetDismissDistance() * 0.5)
                {
                    // Close the sheet
                    AnimateShareBottomSheet(false);
                    if (OverlayGrid != null)
                        OverlayGrid.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Snap back to open position
                    AnimateShareBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentVideoId))
            {
                string shareUrl = BuildShareUrl();
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(shareUrl);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                AnimateShareBottomSheet(false);
                ShowSharePopup(Localization.GetString("LinkCopied"), Localization.GetString("VideoLinkCopied"));
            }
        }

        private void ShareViaSystemButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentVideoId))
            {
                string shareUrl = BuildShareUrl();
                string title = VideoTitleText?.Text ?? "YouTube Video";

                // Close the share bottom sheet first
                AnimateShareBottomSheet(false);

                // Use Windows Share UI
                var dataTransferManager =
                    Windows.ApplicationModel.DataTransfer.DataTransferManager.GetForCurrentView();
                dataTransferManager.DataRequested += (shareSender, args) =>
                {
                    var request = args.Request;
                    request.Data.Properties.Title = title;
                    request.Data.Properties.Description = Localization.GetString("ShareVideoDescription");
                    request.Data.SetWebLink(new Uri(shareUrl));
                };

                // Show share UI
                Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
            }
        }

        private void SharePopupDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            e.Handled = true;
        }

        private void SharePopupDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _sharePopupInitialY = e.GetCurrentPoint(element).Position.Y;
                _sharePopupInitialTransformY = SharePopupTransform != null ? SharePopupTransform.Y : 0;
                _sharePopupIsDragging = true;
                e.Handled = true;
            }
        }

        private void SharePopupDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_sharePopupIsDragging || SharePopupTransform == null)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var dragOffset = e.GetCurrentPoint(element).Position.Y - _sharePopupInitialY;
            var newY = _sharePopupInitialTransformY + dragOffset;
            var dismissDistance = GetSharePopupDismissDistance();
            if (newY >= 0 && newY <= dismissDistance)
            {
                SharePopupTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SharePopupDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_sharePopupIsDragging)
            {
                return;
            }

            _sharePopupIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SharePopupTransform != null && SharePopupTransform.Y > GetSharePopupDismissDistance() * 0.35)
            {
                HideSharePopup();
            }
            else
            {
                AnimateSharePopup(true);
            }
            e.Handled = true;
        }

        private void SharePopupOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            e.Handled = true;
        }

        private void SharePopupContainer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

        private async void DownloadButton_Click(object sender, RoutedEventArgs e)
        {
            if (_offlineMode || string.IsNullOrWhiteSpace(currentVideoId)) return;

            if (MainSettingsPanel != null) MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null) QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null) SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null) SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null) AudioTrackSettingsPanel.Visibility = Visibility.Collapsed;
            if (DownloadSettingsPanel != null) DownloadSettingsPanel.Visibility = Visibility.Visible;

            var populateTask = PopulateDownloadQualityOptionsAsync();
            UpdateSettingsBottomSheetHeight();

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                ShowSettingsBottomSheetInFullscreenPopup();
            }
            else
            {
                RestoreSettingsBottomSheetFromFullscreenPopup();
                if (OverlayGrid != null) OverlayGrid.Visibility = Visibility.Visible;
                if (SettingsBottomSheetPanel != null)
                {
                    SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                    AnimateSettingsBottomSheet(true);
                }
            }

            await populateTask;
            UpdateSettingsBottomSheetHeight();
        }

        private async Task PopulateDownloadQualityOptionsAsync()
        {
            if (DownloadQualityOptionsPanel == null) return;
            DownloadQualityOptionsPanel.Children.Clear();
            DownloadUnavailableText.Visibility = Visibility.Collapsed;
            if (DownloadQualityLoadingRing != null)
            {
                DownloadQualityLoadingRing.Visibility = Visibility.Visible;
                DownloadQualityLoadingRing.IsActive = true;
            }

            var formats = await GetDownloadFormatChoicesAsync(currentVideoId);
            if (DownloadQualityLoadingRing != null)
            {
                DownloadQualityLoadingRing.IsActive = false;
                DownloadQualityLoadingRing.Visibility = Visibility.Collapsed;
            }
            if (formats.Count == 0)
            {
                DownloadUnavailableText.Visibility = Visibility.Visible;
                return;
            }

            foreach (var format in formats)
            {
                var selected = format;
                var quality = selected.Video.QualityTier + "p";
                var existing = await DownloadManager.FindAsync(currentVideoId, quality);
                var label = quality;
                if (existing != null && existing.IsDownloading)
                    label += "  ·  " + existing.ProgressPercent + "%";
                else if (existing != null && existing.IsComplete)
                    label += "  ·  " + Localization.GetString("Downloaded");

                var button = MakeCheckableOptionButton(
                    label,
                    existing != null && existing.IsComplete,
                    async () => await StartDownloadAsync(selected));
                DownloadQualityOptionsPanel.Children.Add(button);
            }
        }

        private sealed class DownloadFormatChoice
        {
            public PlayerFormatModel Video { get; set; }
            public PlayerFormatModel Audio { get; set; }
            public string MediaUserAgent { get; set; }
        }

        private async Task<List<DownloadFormatChoice>> GetDownloadFormatChoicesAsync(string videoId)
        {
            var all = new List<PlayerFormatModel>();
            var mediaUserAgent = _playbackMediaUserAgent;
            try
            {
                // This is deliberately the same cached /player response used by the quality
                // settings and by playback. Its adaptive H.264 and AAC URLs are already proven
                // by DashDemuxer; downloads pair those exact links and mux them into one MP4.
                var json = await PostAndroidPlayerAsync(videoId).ConfigureAwait(false);
                mediaUserAgent = _playbackMediaUserAgent;
                AddDownloadFormatsFromPlayerJson(json, mediaUserAgent, all);

                // If the cached playback response has expired or carried only HLS, refresh the
                // normal player chain once. Do not fall back to a URL merely because it appears
                // in streamingData: both tracks must be usable by the existing demuxer.
                if (!ContainsDownloadableAdaptivePair(all))
                {
                    _lastAndroidPlayerVideoId = string.Empty;
                    _lastAndroidPlayerJson = string.Empty;
                    json = await PostAndroidPlayerAsync(videoId).ConfigureAwait(false);
                    mediaUserAgent = _playbackMediaUserAgent;
                    all.Clear();
                    AddDownloadFormatsFromPlayerJson(json, mediaUserAgent, all);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Format request failed: " + ex.Message);
            }

            if (!ContainsDownloadableAdaptivePair(all))
            {
                foreach (var format in availableFormats)
                {
                    if (format == null) continue;
                    format.MediaUserAgent = mediaUserAgent ?? string.Empty;
                    all.Add(format);
                }
            }

            var usableFormats = all.Where(f => f != null && IsDownloadMediaUrlFresh(f.Url)).ToList();
            var audio = SelectAudioOnlyAacFormat(usableFormats, _selectedAudioTrackId);
            if (audio == null) return new List<DownloadFormatChoice>();

            return usableFormats
                .Where(f => f != null && f.IsAdaptive && f.HasVideo && !f.HasAudio
                    && f.QualityTier > 0 && IsDownloadMediaUrlFresh(f.Url)
                    && !string.IsNullOrWhiteSpace(f.MimeType)
                    && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0)
                .GroupBy(f => f.QualityTier)
                .Select(g => new DownloadFormatChoice
                {
                    Video = g.OrderByDescending(f => Math.Max(f.AverageBitrate, f.Bitrate)).First(),
                    Audio = audio,
                    MediaUserAgent = mediaUserAgent ?? string.Empty
                })
                .OrderByDescending(f => f.Video.QualityTier)
                .ToList();
        }

        private static void AddDownloadFormatsFromPlayerJson(
            string json,
            string mediaUserAgent,
            IList<PlayerFormatModel> target)
        {
            if (string.IsNullOrWhiteSpace(json) || target == null) return;

            try
            {
                var parsed = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(JsonValue.Parse(json).GetObject(), parsed);
                foreach (var format in parsed)
                {
                    if (format == null) continue;
                    format.MediaUserAgent = mediaUserAgent ?? string.Empty;
                    target.Add(format);
                }
                System.Diagnostics.Debug.WriteLine(
                    "[Downloads] " + parsed.Count + " format(s) from "
                    + (string.IsNullOrWhiteSpace(mediaUserAgent) ? "player" : mediaUserAgent));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads] Player response parse failed: " + ex.Message);
            }
        }

        private static bool ContainsDownloadableAdaptivePair(
            IEnumerable<PlayerFormatModel> formats)
        {
            if (formats == null) return false;
            var hasVideo = formats.Any(f => f != null && f.IsAdaptive
                && f.HasVideo && !f.HasAudio && IsDownloadMediaUrlFresh(f.Url)
                && !string.IsNullOrWhiteSpace(f.MimeType)
                && f.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                && f.MimeType.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0);
            var hasAudio = formats.Any(f => f != null && f.IsAdaptive
                && f.HasAudio && !f.HasVideo && IsDownloadMediaUrlFresh(f.Url)
                && !string.IsNullOrWhiteSpace(f.MimeType)
                && (f.MimeType.IndexOf("audio/mp4", StringComparison.OrdinalIgnoreCase) >= 0
                    || f.MimeType.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0));
            return hasVideo && hasAudio;
        }

        private static bool IsDownloadMediaUrlFresh(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            try
            {
                var match = Regex.Match(url, @"(?:[?&])expire=(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success) return true;
                long expires;
                if (!long.TryParse(match.Groups[1].Value, out expires)) return true;
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                return expires > now + 90;
            }
            catch
            {
                return true;
            }
        }

        private async Task StartDownloadAsync(DownloadFormatChoice format)
        {
            if (format == null || format.Video == null || format.Audio == null) return;
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null) OverlayGrid.Visibility = Visibility.Collapsed;

            var item = await DownloadManager.StartAsync(
                currentVideoId,
                VideoTitleText == null ? string.Empty : VideoTitleText.Text,
                currentChannelName,
                currentChannelId,
                _currentVideoDescription,
                _currentVideoThumbnailUrl,
                _currentChannelThumbnailUrl,
                format.Video,
                format.Audio,
                format.MediaUserAgent);

            if (item == null)
                System.Diagnostics.Debug.WriteLine("[Downloads] Could not start transfer");
            await UpdateDownloadButtonAsync();
        }

        private async void DownloadManager_Changed(object sender, EventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, async () =>
            {
                await UpdateDownloadButtonAsync();
            });
        }

        private async Task UpdateDownloadButtonAsync()
        {
            if (DownloadActionText == null || DownloadActionIcon == null || DownloadProgressIcon == null)
                return;

            var item = await DownloadManager.FindAsync(currentVideoId);
            var active = item != null && item.IsDownloading;
            DownloadActionIcon.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            DownloadProgressIcon.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            DownloadActionText.Text = active
                ? item.ProgressPercent + "%"
                : Localization.GetString("Download");
            if (active) SetDownloadProgressArc(item.ProgressPercent);
        }

        private void SetDownloadProgressArc(int percent)
        {
            if (DownloadProgressArc == null) return;
            var value = Math.Max(0, Math.Min(100, percent));
            if (value >= 100)
            {
                DownloadProgressArc.Data = new EllipseGeometry
                {
                    Center = new Windows.Foundation.Point(10, 10),
                    RadiusX = 8,
                    RadiusY = 8
                };
                return;
            }

            var angle = value * Math.PI * 2.0 / 100.0;
            var end = new Windows.Foundation.Point(
                10 + 8 * Math.Sin(angle),
                10 - 8 * Math.Cos(angle));
            var figure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(10, 2),
                IsClosed = false
            };
            figure.Segments.Add(new ArcSegment
            {
                Point = end,
                Size = new Windows.Foundation.Size(8, 8),
                SweepDirection = SweepDirection.Clockwise,
                IsLargeArc = value > 50
            });
            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            DownloadProgressArc.Data = geometry;
        }

        private void SettingsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Close settings bottom sheet when tapping drag area
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void ShowSettingsBottomSheet()
        {
            // Reset to the Video page's single settings UI. The same live controls are moved
            // into a top-level popup when the player is fullscreen; CustomVideoPlayer no longer
            // owns a second settings implementation.
            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Visible;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null)
                AudioTrackSettingsPanel.Visibility = Visibility.Collapsed;
            if (DownloadSettingsPanel != null)
                DownloadSettingsPanel.Visibility = Visibility.Collapsed;

            UpdateSettingsRowValues();

            if (SubtitlesButton != null)
                SubtitlesButton.Visibility = (_subtitleTracks != null && _subtitleTracks.HasAny)
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (_offlineMode)
            {
                if (QualityButton != null) QualityButton.Visibility = Visibility.Collapsed;
                if (AudioTrackButton != null) AudioTrackButton.Visibility = Visibility.Collapsed;
                if (SubtitlesButton != null) SubtitlesButton.Visibility = Visibility.Collapsed;
            }

            if (AudioTrackButton != null)
                AudioTrackButton.Visibility = (!_offlineMode
                    && _availableAudioTracks != null && _availableAudioTracks.Count > 1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            UpdateSettingsBottomSheetHeight();
            if (!_offlineMode)
                UpdateAudioTrackButtonVisibilityAsync();

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                ShowSettingsBottomSheetInFullscreenPopup();
                return;
            }

            RestoreSettingsBottomSheetFromFullscreenPopup();

            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Visible;

            if (SettingsBottomSheetPanel != null)
            {
                SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                AnimateSettingsBottomSheet(true);
            }
        }

        private void ShowSettingsBottomSheetInFullscreenPopup()
        {
            if (SettingsBottomSheetPanel == null)
            {
                return;
            }

            if (_fullscreenSettingsPopup != null && _fullscreenSettingsPopup.IsOpen)
            {
                var currentBounds = Window.Current.Bounds;
                UpdateFullscreenSettingsPopupBounds(new Windows.Foundation.Size(currentBounds.Width, currentBounds.Height));
                SettingsBottomSheetPanel.Visibility = Visibility.Visible;
                AnimateSettingsBottomSheet(true);
                return;
            }

            var currentParent = SettingsBottomSheetPanel.Parent as Panel;
            if (currentParent == null)
            {
                return;
            }

            _settingsBottomSheetOriginalParent = currentParent;
            _settingsBottomSheetOriginalIndex = currentParent.Children.IndexOf(SettingsBottomSheetPanel);
            currentParent.Children.Remove(SettingsBottomSheetPanel);

            var bounds = Window.Current.Bounds;
            _fullscreenSettingsPopupRoot = new Grid
            {
                Width = bounds.Width,
                Height = bounds.Height,
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                RequestedTheme = App.GetCurrentElementTheme()
            };

            var dimOverlay = new Grid
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(128, 0, 0, 0))
            };
            dimOverlay.Tapped += FullscreenSettingsOverlay_Tapped;
            _fullscreenSettingsPopupRoot.Children.Add(dimOverlay);

            SettingsBottomSheetPanel.Visibility = Visibility.Visible;
            SettingsBottomSheetPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            SettingsBottomSheetPanel.VerticalAlignment = VerticalAlignment.Bottom;
            UpdateSettingsBottomSheetHeight();
            if (SettingsBottomSheetTransform != null)
            {
                SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
            }
            _fullscreenSettingsPopupRoot.Children.Add(SettingsBottomSheetPanel);

            _fullscreenSettingsPopup = new Popup
            {
                Child = _fullscreenSettingsPopupRoot,
                HorizontalOffset = 0,
                VerticalOffset = 0,
                Width = bounds.Width,
                Height = bounds.Height,
                IsHitTestVisible = true
            };
            _fullscreenSettingsPopup.IsOpen = true;

            AnimateSettingsBottomSheet(true);
        }

        private void FullscreenSettingsOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSettingsBottomSheet(false);
            e.Handled = true;
        }

        private void UpdateFullscreenSettingsPopupBounds(Windows.Foundation.Size size)
        {
            if (_fullscreenSettingsPopup == null || !_fullscreenSettingsPopup.IsOpen)
            {
                return;
            }

            _fullscreenSettingsPopup.Width = size.Width;
            _fullscreenSettingsPopup.Height = size.Height;
            if (_fullscreenSettingsPopupRoot != null)
            {
                _fullscreenSettingsPopupRoot.Width = size.Width;
                _fullscreenSettingsPopupRoot.Height = size.Height;
            }
        }

        private void RestoreSettingsBottomSheetFromFullscreenPopup()
        {
            if (_fullscreenSettingsPopup == null && _settingsBottomSheetOriginalParent == null)
            {
                return;
            }

            try
            {
                var currentParent = SettingsBottomSheetPanel != null
                    ? SettingsBottomSheetPanel.Parent as Panel
                    : null;
                if (currentParent != null)
                {
                    currentParent.Children.Remove(SettingsBottomSheetPanel);
                }

                if (SettingsBottomSheetPanel != null && _settingsBottomSheetOriginalParent != null)
                {
                    var insertIndex = _settingsBottomSheetOriginalIndex;
                    if (insertIndex < 0 || insertIndex > _settingsBottomSheetOriginalParent.Children.Count)
                    {
                        insertIndex = _settingsBottomSheetOriginalParent.Children.Count;
                    }
                    _settingsBottomSheetOriginalParent.Children.Insert(insertIndex, SettingsBottomSheetPanel);
                    SettingsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (SettingsBottomSheetTransform != null)
                    {
                        SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
                    }
                }
            }
            finally
            {
                if (_fullscreenSettingsPopup != null)
                {
                    _fullscreenSettingsPopup.IsOpen = false;
                    _fullscreenSettingsPopup.Child = null;
                }
                _fullscreenSettingsPopup = null;
                _fullscreenSettingsPopupRoot = null;
                _settingsBottomSheetOriginalParent = null;
                _settingsBottomSheetOriginalIndex = -1;
            }
        }

        private double GetSettingsBottomSheetHiddenOffset()
        {
            if (SettingsBottomSheetPanel == null)
            {
                return 214;
            }

            var height = SettingsBottomSheetPanel.Height;
            if (double.IsNaN(height) || height <= 0)
            {
                height = SettingsBottomSheetPanel.ActualHeight;
            }

            return (height > 0 ? height : 204) + 10;
        }

        // Same sizing rule as youtube-ios/YTSettingsSheet: grip + visible page content +
        // bottom padding, capped at 70% of the current window. The page ScrollViewers handle
        // lists that exceed the cap; the main page grows and shrinks with its visible rows.
        private void UpdateSettingsBottomSheetHeight()
        {
            if (SettingsBottomSheetPanel == null || Window.Current == null)
            {
                return;
            }

            FrameworkElement content = null;
            if (MainSettingsPanel != null && MainSettingsPanel.Visibility == Visibility.Visible)
            {
                content = MainSettingsPanel;
            }
            else if (QualitySettingsPanel != null && QualitySettingsPanel.Visibility == Visibility.Visible)
            {
                content = QualitySettingsPanel.Content as FrameworkElement;
            }
            else if (SpeedSettingsPanel != null && SpeedSettingsPanel.Visibility == Visibility.Visible)
            {
                content = SpeedSettingsPanel.Content as FrameworkElement;
            }
            else if (AudioTrackSettingsPanel != null && AudioTrackSettingsPanel.Visibility == Visibility.Visible)
            {
                content = AudioTrackSettingsPanel.Content as FrameworkElement;
            }
            else if (SubtitlesSettingsPanel != null && SubtitlesSettingsPanel.Visibility == Visibility.Visible)
            {
                content = SubtitlesSettingsPanel.Content as FrameworkElement;
            }
            else if (DownloadSettingsPanel != null && DownloadSettingsPanel.Visibility == Visibility.Visible)
            {
                content = DownloadSettingsPanel.Content as FrameworkElement;
            }

            if (content == null)
            {
                return;
            }

            var bounds = Window.Current.Bounds;
            var innerWidth = Math.Max(0, bounds.Width - 60);
            content.Measure(new Windows.Foundation.Size(innerWidth, double.PositiveInfinity));

            // XAML uses a 40px grip and 20px bottom content margin, exactly like youtube-ios.
            var contentHeight = 40 + content.DesiredSize.Height + 20;
            var maxHeight = Math.Max(100, bounds.Height * 0.70);
            var newHeight = Math.Min(contentHeight, maxHeight);
            var wasHidden = SettingsBottomSheetPanel.Visibility != Visibility.Visible;

            SettingsBottomSheetPanel.Height = Math.Max(60, Math.Ceiling(newHeight));
            if (wasHidden && SettingsBottomSheetTransform != null)
            {
                SettingsBottomSheetTransform.Y = GetSettingsBottomSheetHiddenOffset();
            }
        }

        private void AnimateSettingsBottomSheet(bool show)
        {
            if (SettingsBottomSheetTransform == null)
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
                animation.To = GetSettingsBottomSheetHiddenOffset();

                // Hide panel after animation completes
                animation.Completed += (s, args) =>
                {
                    if (SettingsBottomSheetPanel != null)
                        SettingsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    RestoreSettingsBottomSheetFromFullscreenPopup();
                };
            }

            Storyboard.SetTarget(animation, SettingsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");
            storyboard.Children.Add(animation);
            storyboard.Begin();
        }

        private void SettingsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var pointer = e.Pointer;
            if (element.CapturePointer(pointer))
            {
                _settingsInitialY = e.GetCurrentPoint(element).Position.Y;
                _settingsInitialTransformY = SettingsBottomSheetTransform != null ? SettingsBottomSheetTransform.Y : 0;
                _settingsIsDragging = true;
                e.Handled = true;
            }
        }

        private void SettingsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_settingsIsDragging || SettingsBottomSheetTransform == null)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var currentPoint = e.GetCurrentPoint(element);
            double dragOffset = currentPoint.Position.Y - _settingsInitialY;
            double newY = _settingsInitialTransformY + dragOffset;

            var hiddenOffset = GetSettingsBottomSheetHiddenOffset();
            if (newY >= 0 && newY <= hiddenOffset)
            {
                SettingsBottomSheetTransform.Y = newY;
            }

            e.Handled = true;
        }

        private void SettingsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_settingsIsDragging)
            {
                return;
            }

            _settingsIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SettingsBottomSheetTransform != null
                && SettingsBottomSheetTransform.Y > GetSettingsBottomSheetHiddenOffset() / 2)
            {
                AnimateSettingsBottomSheet(false);
                if (OverlayGrid != null)
                {
                    OverlayGrid.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                AnimateSettingsBottomSheet(true);
            }

            e.Handled = true;
        }

        private async void QualityButton_Click(object sender, RoutedEventArgs e)
        {
            if (QualityOptionsPanel == null)
            {
                return;
            }

            QualityOptionsPanel.Children.Clear();

            // Show the quality panel immediately; it is populated below.
            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Visible;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;

            // Highlight what is actually playing, not the raw per-video override. With a preferred
            // quality set in Settings the override is empty yet the video plays at that quality —
            // checkmarking "Auto" then was misleading. The effective tag reflects the real pick.
            var effective = GetEffectiveVideoQualityTag();

            // Auto is always offered (defaults to the reliable muxed/HLS pick).
            AddQualityOptionButton("Auto", string.IsNullOrWhiteSpace(effective));

            // Offer every height this video actually provides in H.264: muxed progressive
            // (itag 18 = 360p, itag 22 = 720p) AND adaptive video-only avc1 (up to 1080p),
            // which the on-the-fly DASH demuxer plays by pairing it with the AAC audio track.
            try
            {
                var heights = await GetAvailableQualityTagsAsync(currentVideoId);
                foreach (var height in heights)
                {
                    AddQualityOptionButton(height + "p", string.Equals(effective, height, StringComparison.Ordinal));
                }
                UpdateSettingsBottomSheetHeight();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to populate quality menu: " + ex.Message);
            }
        }

        // A settings-menu row with a leading checkmark column (like the Settings quality
        // picker): the check shows on the current item, the label sits to its right. Shared by the
        // quality and speed pickers so they look identical.
        private Button MakeCheckableOptionButton(string label, bool isCurrent, Action onClick)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var labelText = new TextBlock
            {
                Text = label,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(labelText, 1);
            grid.Children.Add(labelText);

            button.Content = grid;
            if (onClick != null)
            {
                button.Click += (s, args) => onClick();
            }

            return button;
        }

        private void AddQualityOptionButton(string quality, bool isCurrent)
        {
            if (QualityOptionsPanel == null)
            {
                return;
            }

            // A leading check column (like the Settings quality picker) marks the current pick;
            // the label sits to its right.
            var button = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14,
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var check = new FontIcon
            {
                Glyph = "",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var label = new TextBlock
            {
                Text = string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase) ? Localization.GetString("Auto") : quality,
                Foreground = (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White)),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            button.Content = grid;

            string selectedQuality = quality;
            button.Click += (s, args) =>
            {
                ChangeQuality(selectedQuality);

                AnimateSettingsBottomSheet(false);
                if (OverlayGrid != null)
                    OverlayGrid.Visibility = Visibility.Collapsed;
            };

            QualityOptionsPanel.Children.Add(button);
        }

        // Distinct H.264 heights this video offers (from the ANDROID_VR response), ascending:
        // muxed progressive (itag 18/22) plus adaptive video-only avc1 (up to 1080p). The
        // adaptive heights are playable because the DASH demuxer muxes them with AAC audio.
        private async Task<List<string>> GetAvailableQualityTagsAsync(string videoId)
        {
            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return result;
            }

            try
            {
                var androidJson = await PostAndroidPlayerAsync(videoId);
                if (string.IsNullOrWhiteSpace(androidJson))
                {
                    return result;
                }

                var androidRoot = JsonValue.Parse(androidJson).GetObject();
                var formats = new List<PlayerFormatModel>();
                CollectFormatsFromStreamingData(androidRoot, formats);

                var heights = new SortedSet<int>();
                foreach (var f in formats)
                {
                    if (f == null || string.IsNullOrWhiteSpace(f.Url) || f.QualityTier <= 0)
                    {
                        continue;
                    }

                    var mime = string.IsNullOrWhiteSpace(f.MimeType) ? string.Empty : f.MimeType.ToLowerInvariant();
                    if (mime.IndexOf("video/mp4") < 0)
                    {
                        continue;
                    }

                    // Muxed progressive (video+audio, H.264/AAC) — plays directly.
                    var isMuxed = !f.IsAdaptive && f.HasAudio && f.HasVideo;
                    // Adaptive video-only H.264 — plays via the DASH demuxer + AAC audio track.
                    var isAvcVideoOnly = f.IsAdaptive && f.HasVideo && !f.HasAudio && mime.IndexOf("avc1") >= 0;

                    if (isMuxed || isAvcVideoOnly)
                    {
                        heights.Add(f.QualityTier);
                    }
                }

                foreach (var h in heights)
                {
                    result.Add(h.ToString());
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] GetAvailableQualityTagsAsync failed: " + ex.Message);
            }

            return result;
        }

        private void SpeedButton_Click(object sender, RoutedEventArgs e)
        {
            // Show speed options
            if (SpeedOptionsPanel != null)
            {
                SpeedOptionsPanel.Children.Clear();

                // Add speed options with a leading checkmark on the current one, matching the
                // quality picker.
                var speeds = new[]
                {
                    0.25, 0.5, 0.75, 1.0
                    // Windows 10 Mobile ignores PlaybackRate > 1 for a MediaStreamSource.
                    // youtube-ios can offer these because AVPlayer changes its media clock:
                    // , 1.25, 1.5, 1.75, 2.0
                };
                foreach (var speed in speeds)
                {
                    var label = speed.ToString("0.##",
                        System.Globalization.CultureInfo.InvariantCulture) + "x";
                    var isCurrent = Math.Abs(speed - currentSpeed) < 0.001;

                    double selectedSpeed = speed;
                    var button = MakeCheckableOptionButton(label, isCurrent, () =>
                    {
                        ChangeSpeed(selectedSpeed);
                        AnimateSettingsBottomSheet(false);
                        if (OverlayGrid != null)
                            OverlayGrid.Visibility = Visibility.Collapsed;
                    });

                    SpeedOptionsPanel.Children.Add(button);
                }

                // Show speed panel, hide main panel
                if (MainSettingsPanel != null)
                    MainSettingsPanel.Visibility = Visibility.Collapsed;
                if (QualitySettingsPanel != null)
                    QualitySettingsPanel.Visibility = Visibility.Collapsed;
                if (SpeedSettingsPanel != null)
                    SpeedSettingsPanel.Visibility = Visibility.Visible;
                UpdateSettingsBottomSheetHeight();
            }
        }

        // --- Subtitles ------------------------------------------------------------------------

        private bool _showingSubtitleTranslations;

        // The player holds the list and the selection; the sheet only renders them.
        private Subtitles.TrackList _subtitleTracks
        {
            get
            {
                return CustomVideoPlayer != null
                    ? CustomVideoPlayer.SubtitleTracks
                    : new Subtitles.TrackList();
            }
        }

        private Subtitles.SubtitleTrack _activeSubtitleTrack
        {
            get { return CustomVideoPlayer != null ? CustomVideoPlayer.ActiveSubtitleTrack : null; }
        }

        private void LoadSubtitleTracks(JsonObject playerRoot)
        {
            _showingSubtitleTranslations = false;

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetSubtitleTracks(Subtitles.ParseTracks(playerRoot));
            }
        }

        // Fills the track list lazily; if it turns out there really are ≥2 languages, reveals the
        // menu entry (it may have been hidden when the sheet first opened).
        private async void UpdateAudioTrackButtonVisibilityAsync()
        {
            await EnsureAudioTracksLoadedAsync();
            if (AudioTrackButton != null)
            {
                AudioTrackButton.Visibility = (_availableAudioTracks != null && _availableAudioTracks.Count > 1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdateSettingsBottomSheetHeight();
            }
            UpdateSettingsRowValues();
        }

        private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateAudioTrackOptions();

            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null)
                AudioTrackSettingsPanel.Visibility = Visibility.Visible;
            UpdateSettingsBottomSheetHeight();
        }

        private void PopulateAudioTrackOptions()
        {
            if (AudioTrackOptionsPanel == null)
            {
                return;
            }

            AudioTrackOptionsPanel.Children.Clear();
            var currentTrack = GetCurrentAudioTrackInfo();
            var currentTrackId = currentTrack == null ? null : currentTrack.Id;

            foreach (var track in _availableAudioTracks)
            {
                var chosen = track;
                var isCurrent = string.IsNullOrEmpty(_selectedAudioTrackId)
                    ? string.Equals(currentTrackId, chosen.Id, StringComparison.Ordinal)
                    : string.Equals(_selectedAudioTrackId, chosen.Id, StringComparison.Ordinal);

                var button = MakeCheckableOptionButton(chosen.Name, isCurrent, () => ApplyAudioTrack(chosen.Id));
                AudioTrackOptionsPanel.Children.Add(button);
            }
        }

        private Config.AudioTrackInfo GetCurrentAudioTrackInfo()
        {
            if (_availableAudioTracks == null || _availableAudioTracks.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(_selectedAudioTrackId))
            {
                var explicitTrack = _availableAudioTracks.FirstOrDefault(t =>
                    string.Equals(t.Id, _selectedAudioTrackId, StringComparison.Ordinal));
                if (explicitTrack != null)
                {
                    return explicitTrack;
                }
            }

            Config.AudioTrackInfo systemTrack = null;
            var bestSystemScore = 0;
            foreach (var track in _availableAudioTracks)
            {
                var score = Config.SystemAudioTrackMatchScore(track.Id);
                if (score > bestSystemScore)
                {
                    bestSystemScore = score;
                    systemTrack = track;
                }
            }

            if (systemTrack != null)
            {
                return systemTrack;
            }

            return _availableAudioTracks.FirstOrDefault(t => t.IsDefault)
                ?? _availableAudioTracks[0];
        }

        private async void ApplyAudioTrack(string trackId)
        {
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;

            if (string.Equals(_selectedAudioTrackId, trackId, StringComparison.Ordinal))
            {
                return;
            }

            _selectedAudioTrackId = trackId;

            // youtube-ios rebuilds the same selected height (including Auto) with the new
            // preferred audio track and keeps the current playback position.
            var effective = GetEffectiveVideoQualityTag();
            await ChangeQualityAsync(string.IsNullOrWhiteSpace(effective) ? "Auto" : effective + "p");
        }

        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            _showingSubtitleTranslations = false;
            PopulateSubtitleOptions();

            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Visible;
            UpdateSettingsBottomSheetHeight();
        }

        // Two levels in the same panel: the author's tracks, and — behind one entry — the full
        // machine-translation language list, which runs to well over a hundred entries and would
        // bury the real tracks if shown inline.
        private void PopulateSubtitleOptions()
        {
            if (SubtitlesOptionsPanel == null)
            {
                return;
            }

            SubtitlesOptionsPanel.Children.Clear();

            if (_showingSubtitleTranslations)
            {
                var source = _subtitleTracks.TranslationSource;

                AddSubtitleOption(Localization.GetString("Back"), false, () =>
                {
                    _showingSubtitleTranslations = false;
                    PopulateSubtitleOptions();
                });

                foreach (var language in _subtitleTracks.TranslationLanguages)
                {
                    var target = language;
                    var isActive = _activeSubtitleTrack != null
                        && string.Equals(_activeSubtitleTrack.TranslationLanguageCode, target.LanguageCode, StringComparison.Ordinal);

                    AddSubtitleOption(target.Name, isActive,
                        () => ApplySubtitleTrack(CustomVideoPlayer.MakeTranslatedTrack(source, target)));
                }

                UpdateSettingsBottomSheetHeight();
                return;
            }

            // Sync adjustment, only meaningful while a track is on.
            if (_activeSubtitleTrack != null && CustomVideoPlayer != null)
            {
                AddSubtitleOption(Localization.Format("SyncFormat", CustomVideoPlayer.SubtitleOffsetDisplayText), false, null);
                AddSubtitleOption(Localization.GetString("SubtitleEarlier"), false, () =>
                {
                    CustomVideoPlayer.AdjustSubtitleOffset(250);
                    PopulateSubtitleOptions();
                });
                AddSubtitleOption(Localization.GetString("SubtitleLater"), false, () =>
                {
                    CustomVideoPlayer.AdjustSubtitleOffset(-250);
                    PopulateSubtitleOptions();
                });
                var resetTitle = Localization.GetString("SubtitleReset");
                if (string.Equals(resetTitle, "SubtitleReset", StringComparison.Ordinal))
                {
                    resetTitle = "Reset sync (0.00s)";
                }
                AddSubtitleOption(resetTitle, false, () =>
                {
                    CustomVideoPlayer.SetSubtitleOffset(0);
                    PopulateSubtitleOptions();
                });
            }

            AddSubtitleOption(Localization.GetString("Off"), _activeSubtitleTrack == null, () => ApplySubtitleTrack(null));

            foreach (var track in _subtitleTracks.Tracks)
            {
                var selected = track;
                var isActive = _activeSubtitleTrack != null
                    && string.IsNullOrEmpty(_activeSubtitleTrack.TranslationLanguageCode)
                    && string.Equals(_activeSubtitleTrack.BaseUrl, selected.BaseUrl, StringComparison.Ordinal);

                AddSubtitleOption(selected.DisplayName, isActive, () => ApplySubtitleTrack(selected));
            }

            if (_subtitleTracks.CanTranslate)
            {
                AddSubtitleOption(Localization.GetString("AutoTranslate"), false, () =>
                {
                    _showingSubtitleTranslations = true;
                    PopulateSubtitleOptions();
                });
            }
            UpdateSettingsBottomSheetHeight();
        }

        private void AddSubtitleOption(string label, bool isActive, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = new SolidColorBrush(isActive
                    ? Windows.UI.Color.FromArgb(255, 255, 0, 51)
                    : App.GetThemeColor("AppPrimaryTextBrush", Windows.UI.Colors.White)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14
            };

            if (onClick == null)
            {
                // A plain caption row, not a choice.
                button.IsHitTestVisible = false;
                button.Opacity = 0.7;
            }
            else
            {
                button.Click += (s, args) => onClick();
            }

            SubtitlesOptionsPanel.Children.Add(button);
        }

        private void ApplySubtitleTrack(Subtitles.SubtitleTrack track)
        {
            // Closed first: the download happens in the player and the sheet has nothing to wait for.
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SelectSubtitleTrack(track);
            }
        }

        private async void ChangeQuality(string quality)
        {
            // This is intentionally async void because it is used directly by UI handlers.
            // Never let an exception escape it: on UWP that becomes an unhandled dispatcher
            // exception and terminates the app.
            try
            {
                // MediaPlaybackSession / MediaStreamSource callbacks are not guaranteed to run on
                // the XAML UI thread. Quality changes touch CustomVideoPlayer and replace its
                // MediaPlayer source, so marshal the whole operation to the owning dispatcher.
                if (Dispatcher != null && !Dispatcher.HasThreadAccess)
                {
                    await Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => ChangeQuality(quality));
                    return;
                }

                // A manual quality pick takes precedence over the speed-driven Auto switch, so the
                // next drop to 1x must not undo it.
                _speedForcedAuto = false;
                await ChangeQualityAsync(quality);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] ChangeQuality failed safely for '" + quality + "': " + ex.Message);
                try
                {
                    if (CustomVideoPlayer != null)
                    {
                        CustomVideoPlayer.EndSourceLoading();
                    }
                }
                catch { }
            }
        }

        private async Task ChangeQualityAsync(string quality)
        {
            // Ignore a new quality switch while one is still building its source. Overlapping
            // source swaps (each an async void SetSource) race on the MediaPlayer and can crash,
            // especially combined with a rotation happening in the same window.
            if (_qualityChangeInProgress)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Quality change already in progress; ignoring '" + quality + "'");
                return;
            }

            _qualityChangeInProgress = true;
            try
            {
                currentQualityTag = NormalizeQualityTag(quality);
                // A per-video pick — including "Auto" — now overrides the Settings default until
                // the next video opens.
                _qualityExplicitlyChosen = true;
                ResetVideoOnlyDecodeFallbackState();

                // Capture the current position BEFORE the reload — ReloadPlayerOnlyAsync -> Stop()
                // resets it to zero. Arming the resume makes the new-quality source seek back to
                // this spot and keep playing (no manual tap on the play button).
                var resumePosition = TimeSpan.Zero;
                if (CustomVideoPlayer != null)
                {
                    resumePosition = CustomVideoPlayer.CurrentPlaybackPosition;
                    CustomVideoPlayer.CurrentQuality = string.IsNullOrWhiteSpace(currentQualityTag) ? null : currentQualityTag;
                    CustomVideoPlayer.PrepareResumeAfterSourceReload(resumePosition, true);
                    // Spinner + no play/seek until the new stream is actually ready.
                    CustomVideoPlayer.BeginSourceLoading();
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Changing quality to: "
                    + (string.IsNullOrWhiteSpace(currentQualityTag) ? "Auto" : currentQualityTag + "p")
                    + " @ " + resumePosition
                );

                await ReloadPlayerOnlyAsync(true);
            }
            finally
            {
                _qualityChangeInProgress = false;
            }
        }

        // Set when a >1x speed forced the progressive/Auto path; remembers the demuxer quality to
        // restore once the speed drops back to 1x or below.
        private bool _speedForcedAuto;
        private string _qualityBeforeSpeedForce;

        private async void ChangeSpeed(double speed)
        {
            System.Diagnostics.Debug.WriteLine($"[Video] Changing speed to: {speed}x");

            currentSpeed = speed;

            if (CustomVideoPlayer == null)
            {
                return;
            }

            // Arm the rate up front so it is applied whether or not the source is swapped below.
            CustomVideoPlayer.SetPlaybackRate(speed);

            if (_qualityChangeInProgress)
            {
                return;
            }

            if (speed > 1.0)
            {
                // The demuxer can't exceed 1x. Move to the progressive/Auto path and remember the
                // demuxer quality so 1x-and-below can bring it back.
                if (CustomVideoPlayer.IsUsingDemuxer)
                {
                    _qualityBeforeSpeedForce = GetEffectiveVideoQualityTag();
                    _speedForcedAuto = true;
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Speed >1x: switching to Auto (progressive); will restore "
                        + (string.IsNullOrWhiteSpace(_qualityBeforeSpeedForce) ? "Auto" : _qualityBeforeSpeedForce + "p"));
                    await ChangeQualityAsync("Auto");
                    // Re-arm after the reload so the new progressive source opens at the right rate.
                    CustomVideoPlayer.SetPlaybackRate(speed);
                }
            }
            else
            {
                // 1x or slower belongs on the demuxer again if a faster speed had forced Auto.
                if (_speedForcedAuto)
                {
                    _speedForcedAuto = false;
                    var restore = string.IsNullOrWhiteSpace(_qualityBeforeSpeedForce)
                        ? "Auto"
                        : _qualityBeforeSpeedForce + "p";
                    System.Diagnostics.Debug.WriteLine("[Video] Speed <=1x: restoring " + restore + " (demuxer)");
                    await ChangeQualityAsync(restore);
                    CustomVideoPlayer.SetPlaybackRate(speed);
                }
            }
        }

        // Fills the right-edge current-value labels on the main settings rows.
        private void UpdateSettingsRowValues()
        {
            if (QualityValueText != null)
            {
                var effective = GetEffectiveVideoQualityTag();
                var picked = string.IsNullOrWhiteSpace(effective)
                    ? Localization.GetString("Auto")
                    : effective + "p";
                var pickedHeight = ParseInt(effective);
                QualityValueText.Text = _readyHeight > 0 && _readyHeight != pickedHeight
                    ? picked + " · " + _readyHeight + "p"
                    : picked;
            }

            if (SpeedValueText != null)
            {
                SpeedValueText.Text = currentSpeed.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture) + "x";
            }

            if (AudioTrackValueText != null)
            {
                var audioTrack = GetCurrentAudioTrackInfo();
                AudioTrackValueText.Text = audioTrack == null
                    ? string.Empty
                    : (string.IsNullOrWhiteSpace(audioTrack.Name) ? audioTrack.Id : audioTrack.Name);
            }
        }

        private void ShowDescriptionButton_Click(object sender, RoutedEventArgs e)
        {
            ShowDescriptionBottomSheet();
        }

        private void CommentsContainerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowCommentsBottomSheet();
        }

        private async void CommentRepliesButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var comment = button != null ? button.DataContext as CommentItem : null;
            if (comment == null || string.IsNullOrWhiteSpace(currentVideoId)
                || !comment.TryBeginLoadingReplies())
            {
                return;
            }

            try
            {
                var replies = await Config.GetCommentsAsync(
                    currentVideoId,
                    comment.ReplyContinuationToken);

                // As in youtube-ios, the replies row is replaced by the returned branch.
                comment.FinishLoadingReplies(replies, true);
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] Replies opened: " + (replies != null ? replies.Count : 0));
            }
            catch (Exception ex)
            {
                comment.FinishLoadingReplies(null, false);
                System.Diagnostics.Debug.WriteLine("[Comments] Replies load failed: " + ex.Message);
            }
        }

        private void ShowDescriptionBottomSheet()
        {
            UpdateDescriptionSheetHeight();

            // Set the description text with link support
            if (!string.IsNullOrEmpty(_currentVideoDescription))
            {
                SetDescriptionWithLinks(_currentVideoDescription);
            }
            else
            {
                if (DescriptionTextBlock != null)
                {
                    DescriptionTextBlock.Blocks.Clear();
                    var paragraph = new Paragraph();
                    var run = new Run();
                    run.Text = Localization.GetString("DescriptionNotAvailable");
                    paragraph.Inlines.Add(run);
                    DescriptionTextBlock.Blocks.Add(paragraph);
                }
            }

            // Show the overlay and bottom sheet
            if (DescriptionOverlayGrid != null)
                DescriptionOverlayGrid.Visibility = Visibility.Visible;
            if (DescriptionBottomSheetPanel != null)
                DescriptionBottomSheetPanel.Visibility = Visibility.Visible;

            // Animate the bottom sheet up
            AnimateDescriptionBottomSheet(true);
        }

        private void SetDescriptionWithLinks(string description)
        {
            SetDescriptionWithLinks(DescriptionTextBlock, description);
        }

        private void UpdateLandscapeDescriptionText()
        {
            if (LandscapeDescriptionTextBlock == null)
                return;

            _landscapeDescriptionExpanded = false;
            _landscapeDescriptionNeedsToggle = false;
            UpdateLandscapeDescriptionExpansionState();

            var text = string.IsNullOrWhiteSpace(_currentVideoDescription)
                ? Localization.GetString("DescriptionNotAvailable")
                : _currentVideoDescription;
            SetDescriptionWithLinks(LandscapeDescriptionTextBlock, text);

            var ignored = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RefreshLandscapeDescriptionToggleVisibility);
        }

        private void LandscapeDescriptionTextBlock_LayoutUpdated(object sender, object e)
        {
            if (!_landscapeDescriptionExpanded)
                RefreshLandscapeDescriptionToggleVisibility();
        }

        private void RefreshLandscapeDescriptionToggleVisibility()
        {
            if (LandscapeDescriptionTextBlock == null || LandscapeDescriptionToggleButton == null)
                return;

            if (!_landscapeDescriptionExpanded)
                _landscapeDescriptionNeedsToggle = LandscapeDescriptionTextBlock.HasOverflowContent;

            LandscapeDescriptionToggleButton.Visibility = _landscapeDescriptionNeedsToggle
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LandscapeDescriptionToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _landscapeDescriptionExpanded = !_landscapeDescriptionExpanded;
            UpdateLandscapeDescriptionExpansionState();
        }

        private void UpdateLandscapeDescriptionExpansionState()
        {
            if (LandscapeDescriptionTextBlock != null)
                LandscapeDescriptionTextBlock.MaxHeight = _landscapeDescriptionExpanded
                    ? double.MaxValue
                    : 58.0;

            if (LandscapeDescriptionToggleText != null)
            {
                LandscapeDescriptionToggleText.Text = _landscapeDescriptionExpanded
                    ? Localization.GetString("ShowLess")
                    : "…" + Localization.GetString("ShowMore");
            }

            if (LandscapeDescriptionToggleButton != null)
                LandscapeDescriptionToggleButton.Visibility = _landscapeDescriptionNeedsToggle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SetDescriptionWithLinks(RichTextBlock target, string description)
        {
            if (target == null)
                return;

            target.Blocks.Clear();
            var paragraph = new Paragraph();
            var regex = new Regex(
                "(?<url>" + DescriptionUrlPattern + ")|(?<time>" + DescriptionTimecodePattern + ")",
                RegexOptions.IgnoreCase
            );

            var lastPos = 0;
            foreach (Match match in regex.Matches(description))
            {
                if (match.Index > lastPos)
                {
                    AddDescriptionTextRun(paragraph, description.Substring(lastPos, match.Index - lastPos));
                }

                if (match.Groups["url"].Success)
                {
                    var hyperlink = new Hyperlink();
                    var linkRun = new Run();
                    linkRun.Text = match.Value;
                    hyperlink.Inlines.Add(linkRun);
                    hyperlink.Click += Hyperlink_Click;
                    paragraph.Inlines.Add(hyperlink);
                }
                else if (match.Groups["time"].Success)
                {
                    TimeSpan timestamp;
                    if (TryParseDescriptionTimecode(match.Value, out timestamp))
                    {
                        var hyperlink = new Hyperlink();
                        var linkRun = new Run();
                        linkRun.Text = match.Value;
                        hyperlink.Inlines.Add(linkRun);
                        TimeSpan targetTimestamp = timestamp;
                        hyperlink.Click += (sender, args) => SeekToDescriptionTimestamp(targetTimestamp);
                        paragraph.Inlines.Add(hyperlink);
                    }
                    else
                    {
                        AddDescriptionTextRun(paragraph, match.Value);
                    }
                }
                else
                {
                    AddDescriptionTextRun(paragraph, match.Value);
                }

                lastPos = match.Index + match.Length;
            }

            if (lastPos < description.Length)
            {
                AddDescriptionTextRun(paragraph, description.Substring(lastPos));
            }

            if (paragraph.Inlines.Count == 0)
            {
                AddDescriptionTextRun(paragraph, description);
            }

            target.Blocks.Add(paragraph);
        }

        private void AddDescriptionTextRun(Paragraph paragraph, string text)
        {
            if (paragraph == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var run = new Run();
            run.Text = text;
            paragraph.Inlines.Add(run);
        }

        private void CommentTextRichTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            var richTextBlock = sender as RichTextBlock;
            if (richTextBlock == null)
            {
                return;
            }

            var text = richTextBlock.Tag as string;
            SetCommentTextWithTimecodeLinks(richTextBlock, text ?? string.Empty);
        }

        private void SetCommentTextWithTimecodeLinks(RichTextBlock richTextBlock, string text)
        {
            if (richTextBlock == null)
            {
                return;
            }

            richTextBlock.Blocks.Clear();

            var paragraph = new Paragraph();
            if (string.IsNullOrEmpty(text))
            {
                richTextBlock.Blocks.Add(paragraph);
                return;
            }

            var regex = new Regex(
                "(?<url>" + DescriptionUrlPattern + ")|(?<time>" + DescriptionTimecodePattern + ")",
                RegexOptions.IgnoreCase
            );

            var lastPos = 0;
            foreach (Match match in regex.Matches(text))
            {
                if (match.Index > lastPos)
                {
                    AddDescriptionTextRun(paragraph, text.Substring(lastPos, match.Index - lastPos));
                }

                if (match.Groups["time"].Success)
                {
                    TimeSpan timestamp;
                    if (TryParseDescriptionTimecode(match.Value, out timestamp))
                    {
                        var hyperlink = new Hyperlink();
                        var linkRun = new Run();
                        linkRun.Text = match.Value;
                        hyperlink.Inlines.Add(linkRun);
                        TimeSpan targetTimestamp = timestamp;
                        hyperlink.Click += (hyperlinkSender, args) => SeekToCommentTimestamp(targetTimestamp);
                        paragraph.Inlines.Add(hyperlink);
                    }
                    else
                    {
                        AddDescriptionTextRun(paragraph, match.Value);
                    }
                }
                else if (match.Groups["url"].Success)
                {
                    var hyperlink = new Hyperlink();
                    var linkRun = new Run();
                    linkRun.Text = match.Value;
                    hyperlink.Inlines.Add(linkRun);
                    hyperlink.Click += Hyperlink_Click;
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                {
                    AddDescriptionTextRun(paragraph, match.Value);
                }

                lastPos = match.Index + match.Length;
            }

            if (lastPos < text.Length)
            {
                AddDescriptionTextRun(paragraph, text.Substring(lastPos));
            }

            if (paragraph.Inlines.Count == 0)
            {
                AddDescriptionTextRun(paragraph, text);
            }

            richTextBlock.Blocks.Add(paragraph);
        }

        // Fires and forgets: SponsorBlock is a third-party service, so playback must never wait on
        // it. The answer arrives a moment after the video starts, which is fine — sponsor segments
        // at the very top of a video are rare, and the check runs continuously afterwards.
        private async void LoadSponsorBlockSegments(string videoId)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SetSkipSegments(null);
                }

                var segments = await SponsorBlock.GetSegmentsAsync(videoId);

                // The user may have moved on to another video while the request was in flight.
                if (CustomVideoPlayer == null
                    || !string.Equals(currentVideoId, videoId, StringComparison.Ordinal))
                {
                    return;
                }

                CustomVideoPlayer.SetSkipSegments(segments);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] SponsorBlock load failed: " + ex.Message);
            }
        }

        private void UpdateDescriptionChaptersFromDescription()
        {
            _descriptionChapterMarkers.Clear();

            if (!string.IsNullOrWhiteSpace(_currentVideoDescription))
            {
                var seenSeconds = new HashSet<int>();
                var timecodeRegex = new Regex(DescriptionTimecodePattern, RegexOptions.IgnoreCase);
                var lines = _currentVideoDescription.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    foreach (Match match in timecodeRegex.Matches(line))
                    {
                        TimeSpan position;
                        if (!TryParseDescriptionTimecode(match.Value, out position))
                        {
                            continue;
                        }

                        int totalSeconds = (int)Math.Round(position.TotalSeconds);
                        if (seenSeconds.Contains(totalSeconds))
                        {
                            continue;
                        }

                        seenSeconds.Add(totalSeconds);
                        _descriptionChapterMarkers.Add(new YouTube.CustomVideoPlayer.VideoChapterMarker
                        {
                            Position = position,
                            Title = ExtractDescriptionChapterTitle(line, match.Value)
                        });
                    }
                }
            }

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetChapters(_descriptionChapterMarkers);
            }
        }

        private string ExtractDescriptionChapterTitle(string line, string timecodeText)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            string title = line.Replace(timecodeText, string.Empty).Trim();
            title = title.Trim(' ', '-', '–', '—', ':', '|', '.', ')', '(');
            return title;
        }

        private bool TryParseDescriptionTimecode(string text, out TimeSpan position)
        {
            position = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return false;
            }

            int hours = 0;
            int minutes;
            int seconds;

            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0], out minutes) || !int.TryParse(parts[1], out seconds))
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(parts[0], out hours)
                    || !int.TryParse(parts[1], out minutes)
                    || !int.TryParse(parts[2], out seconds))
                {
                    return false;
                }

                if (minutes < 0 || minutes > 59)
                {
                    return false;
                }
            }

            if (hours < 0 || minutes < 0 || seconds < 0 || seconds > 59)
            {
                return false;
            }

            position = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        private void SeekToDescriptionTimestamp(TimeSpan timestamp)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SeekTo(timestamp);
                }

                AnimateDescriptionBottomSheet(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to seek to description timestamp: " + ex.Message);
            }
        }

        private void SeekToCommentTimestamp(TimeSpan timestamp)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SeekTo(timestamp);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to seek to comment timestamp: " + ex.Message);
            }
        }

        private async void Hyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            try
            {
                var url = "";
                foreach (var inline in sender.Inlines)
                {
                    var run = inline as Run;
                    if (run != null)
                    {
                        url += run.Text;
                    }
                }

                if (!string.IsNullOrEmpty(url))
                {
                    Uri uri;
                    if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                    {
                        await Windows.System.Launcher.LaunchUriAsync(uri);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error launching URL: {ex.Message}");
            }
        }

        private void AnimateDescriptionBottomSheet(bool show)
        {
            if (DescriptionBottomSheetTransform == null)
                return;

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0;
                if (DescriptionOverlayGrid != null)
                    DescriptionOverlayGrid.Visibility = Visibility.Visible;
            }
            else
            {
                animation.To = _descriptionSheetHiddenOffset;
            }

            Storyboard.SetTarget(animation, DescriptionBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (DescriptionBottomSheetPanel != null)
                        DescriptionBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (DescriptionOverlayGrid != null)
                        DescriptionOverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            storyboard.Begin();
        }

        private void UpdateDescriptionSheetHeight()
        {
            try
            {
                double playerHeight = VideoPlayerContainer?.ActualHeight ?? 0;
                if (playerHeight <= 0)
                {
                    playerHeight = CustomVideoPlayer?.ActualHeight ?? 0;
                }

                if (playerHeight > 0)
                {
                    _lastKnownPlayerHeight = playerHeight;
                }
                else if (_lastKnownPlayerHeight > 0)
                {
                    playerHeight = _lastKnownPlayerHeight;
                }
                else
                {
                    return;
                }

                double contentRowHeight = MainScrollViewer?.ActualHeight ?? 0;
                if (contentRowHeight <= 0)
                {
                    contentRowHeight = Window.Current.Bounds.Height;
                }

                double availableHeight = Math.Max(0, contentRowHeight - playerHeight);
                double desiredHeight = availableHeight;

                if (DescriptionBottomSheetPanel != null)
                    DescriptionBottomSheetPanel.Height = desiredHeight;
                _descriptionSheetHiddenOffset = desiredHeight;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to update description sheet height: {ex.Message}"
                );
            }
        }

        private void DescriptionCloseButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionOverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _descriptionInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _descriptionInitialTransformY = DescriptionBottomSheetTransform?.Y ?? 0;
                _descriptionIsDragging = true;
                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_descriptionIsDragging && DescriptionBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _descriptionInitialY;
                double newY = _descriptionInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= _descriptionSheetHiddenOffset)
                {
                    DescriptionBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_descriptionIsDragging)
            {
                _descriptionIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (DescriptionBottomSheetTransform.Y > (_descriptionSheetHiddenOffset * 0.45))
                {
                    AnimateDescriptionBottomSheet(false);
                }
                else
                {
                    AnimateDescriptionBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void ShowCommentsBottomSheet()
        {
            System.Diagnostics.Debug.WriteLine("[Comments] ShowCommentsBottomSheet called");

            // Set the comments data from CommentsList to CommentsItemsControl
            if (CommentsList != null && CommentsList.ItemsSource != null)
            {
                var items = CommentsList.ItemsSource;
                System.Diagnostics.Debug.WriteLine(
                    $"[Comments] Setting CommentsItemsControl.ItemsSource with items"
                );

                if (CommentsItemsControl != null)
                    CommentsItemsControl.ItemsSource = items;
                else
                    System.Diagnostics.Debug.WriteLine(
                        "[Comments] ERROR: CommentsItemsControl is null!"
                    );
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] WARNING: CommentsList or its ItemsSource is null"
                );
                if (CommentsList == null)
                    System.Diagnostics.Debug.WriteLine("[Comments] CommentsList is null");
                else
                    System.Diagnostics.Debug.WriteLine(
                        $"[Comments] CommentsList.ItemsSource is null, ItemsCount: {CommentsList.Items?.Count ?? 0}"
                    );
            }

            // Show the overlay and bottom sheet
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Visible;
            if (CommentsBottomSheetPanel != null)
            {
                CommentsBottomSheetPanel.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] CommentsBottomSheetPanel set to Visible"
                );
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] ERROR: CommentsBottomSheetPanel is null!"
                );
            }

            // Animate the bottom sheet up
            AnimateCommentsBottomSheet(true);
        }

        private void AnimateCommentsBottomSheet(bool show)
        {
            if (CommentsBottomSheetTransform == null)
                return;

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0;
            }
            else
            {
                animation.To = 410;
            }

            Storyboard.SetTarget(animation, CommentsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (CommentsBottomSheetPanel != null)
                        CommentsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (OverlayGrid != null)
                        OverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            storyboard.Begin();
        }

        private void CommentsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateCommentsBottomSheet(false);
        }

        private void CommentsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _commentsInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _commentsInitialTransformY = CommentsBottomSheetTransform?.Y ?? 0;
                _commentsIsDragging = true;
                e.Handled = true;
            }
        }

        private void CommentsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_commentsIsDragging && CommentsBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _commentsInitialY;
                double newY = _commentsInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= 410)
                {
                    CommentsBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void CommentsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_commentsIsDragging)
            {
                _commentsIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (CommentsBottomSheetTransform.Y > 205)
                {
                    AnimateCommentsBottomSheet(false);
                }
                else
                {
                    AnimateCommentsBottomSheet(true);
                }

                e.Handled = true;
            }
        }
    }

    internal sealed class RelatedVideoCardItem
    {
        public string video_id { get; set; }
        public string title { get; set; }
        public string author { get; set; }
        public string thumbnail { get; set; }

        // Full-resolution frame for the related-video cards. Upgraded from the API thumbnail (not
        // derived from the id), and falls back to `thumbnail` on ImageFailed.
        public string large_thumbnail
        {
            get { return global::Config.UpgradeThumbnailToMaxRes(thumbnail); }
        }
        public string channel_thumbnail { get; set; }
        public string views { get; set; }
        public string published { get; set; }
        public string duration { get; set; }
        public double WatchedPercent { get; set; }

        public Windows.UI.Xaml.Visibility WatchedProgressVisibility
        {
            get
            {
                return WatchedPercent > 0
                    ? Windows.UI.Xaml.Visibility.Visible
                    : Windows.UI.Xaml.Visibility.Collapsed;
            }
        }

        public string MetadataLine
        {
            get
            {
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(author)) parts.Add(author.Trim());
                if (!string.IsNullOrWhiteSpace(views)) parts.Add(views.Trim());
                if (!string.IsNullOrWhiteSpace(published)) parts.Add(published.Trim());
                return string.Join(" • ", parts);
            }
        }

        // Set when the card's watchEndpoint carries a playlist — this is what keeps an
        // auto-generated mix ("jam") alive when the card is tapped.
        public string playlist_id { get; set; }

        // "Now playing" styling for the playlist queue.
        public bool is_current { get; set; }

        public Brush row_background
        {
            get
            {
                return is_current
                    ? new SolidColorBrush(Windows.UI.Color.FromArgb(38, 255, 255, 255))
                    : new SolidColorBrush(Windows.UI.Colors.Transparent);
            }
        }

        public Windows.UI.Xaml.Visibility now_playing_visibility
        {
            get { return is_current ? Windows.UI.Xaml.Visibility.Visible : Windows.UI.Xaml.Visibility.Collapsed; }
        }
    }

    public sealed class PlayerFormatModel
    {
        public string Url { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        // Exact port of youtube-ios YTFormat.qualityTier: portrait 1080x1920 is 1080p,
        // not 1920p. Quality selection and menu labels must always use the short side.
        public int QualityTier
        {
            get
            {
                if (Width > 0 && Height > 0)
                {
                    return Math.Min(Width, Height);
                }

                return Height > 0 ? Height : Width;
            }
        }
        public string MimeType { get; set; }
        public int Itag { get; set; }
        public int Fps { get; set; }
        public int Bitrate { get; set; }
        public int AverageBitrate { get; set; }
        public string InitRangeStart { get; set; }
        public string InitRangeEnd { get; set; }
        public string IndexRangeStart { get; set; }
        public string IndexRangeEnd { get; set; }
        public bool HasAudio { get; set; }
        public bool HasVideo { get; set; }
        public bool IsAdaptive { get; set; }
        // The signed googlevideo URL must be fetched as the same client that requested it.
        // Downloads use this value when creating the BackgroundDownloader operation.
        public string MediaUserAgent { get; set; }

        // Multi-language audio (from the format's "audioTrack"). Empty on single-track videos.
        public string AudioTrackId { get; set; }
        public string AudioTrackName { get; set; }
        public bool AudioIsDefault { get; set; }
    }
}
