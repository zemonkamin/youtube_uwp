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
    public sealed partial class Video : Page
    {        private enum UserVideoRating
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

        private const string InnertubeApiKey = YouTubeApiConfig.ApiKey;
        private const string PreferredVideoQualitySettingKey = "PreferredVideoQuality";
        private readonly YouTubeHttpClient httpClient = YouTubeHttpClient.Shared;
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
        private readonly object _playerJsonParseGate = new object();
        private string _lastParsedPlayerJson = string.Empty;
        private JsonObject _lastParsedPlayerRoot;
        private readonly HashSet<int> _excludedH264VideoOnlyItags = new HashSet<int>();
        private int _currentH264VideoOnlyItag = -1;
        // Actual short-side tier selected for playback. youtube-ios keeps this separately from
        // the user's pick so Auto can be displayed as e.g. "Auto · 1080p".
        private int _readyHeight;
        private bool _qualityChangeInProgress;
        private bool _playbackRecoveryInProgress;
        private DateTime _lastPlaybackRecoveryUtc = DateTime.MinValue;
        private string _playbackRecoveryQualityOverride = string.Empty;
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
        private bool _videoLayoutInitialized;
        private bool _lastAppliedPortraitLayout;
        private bool _hasSignedInAccount;
        // Invalidates stale orientation work when the page is unloaded/restored. Fullscreen itself
        // is switched synchronously from SizeChanged so Windows 10 Mobile has no settle delay.
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
        private readonly List<DownloadOptionRowState> _downloadOptionRows =
            new List<DownloadOptionRowState>();
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
        // The old phone ItemsControl is not as aggressive about virtualization as modern UWP.
        // Avoid parsing and materializing content that is several screens away on W10M.
        private static int MaxRelatedVideosToShow
        {
            get { return ResponsiveLayout.IsPhoneDevice ? 16 : 32; }
        }
        private static int MaxRelatedJsonNodesToScan
        {
            get { return ResponsiveLayout.IsPhoneDevice ? 18000 : 60000; }
        }
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
        private const string YouTubeDataApiBaseUrl = YouTubeApiConfig.DataApiBaseUrl;
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
        private List<RelatedVideoCardItem> _currentRelatedVideos = new List<RelatedVideoCardItem>();
        private bool _relatedFallbackVisible;
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
            CustomVideoPlayer.SetAmbientHosts(
                VideoAmbientGlowHost,
                VideoAmbientPageHost,
                VideoNavbarGlassSourceHost,
                VideoTabbarGlassSourceHost);
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
        public long ContentLength { get; set; }
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
