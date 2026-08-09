using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Data.Json;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Foundation.Metadata;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml.Navigation;

namespace YouTube
{
    public sealed partial class Shorts : Page
    {
        private readonly List<ShortsVideoItem> _shorts = new List<ShortsVideoItem>();
        private readonly HashSet<string> _seenVideoIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private string _sequenceToken = string.Empty;
        private int _currentIndex = -1;
        private bool _isLoading;
        private bool _isPointerDown;
        private bool _isPointerSwipe;
        // True only while the slide animation runs. Loading is deliberately not covered by it.
        private bool _isAnimating;
        // Bumped by every ShowShortAsync call so a slow load can tell it has been superseded.
        private int _showGeneration;
        // Serializes the stream-loading half of a transition. Input stays free; this only stops
        // two swipes in quick succession from driving the media player concurrently.
        private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);
        // The swipe animation currently running, so a new one can supersede it cleanly.
        private Storyboard _swipeStoryboard;
        private TaskCompletionSource<bool> _swipeAnimationTcs;
        private bool _ratingInProgress;
        private int _ratingStateGeneration;
        private readonly HttpClient _httpClient = new HttpClient();
        private Point _pointerStart;
        private int _swipeDirection;
        private bool _isAnimatingShort;

        private sealed class ShortRatingToggleMatch
        {
            public string Rating { get; set; }
            public bool IsToggled { get; set; }
        }

        private readonly Dictionary<string, List<CommentItem>> _commentsCache = new Dictionary<string, List<CommentItem>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _descriptionCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private string _commentsLoadedVideoId = string.Empty;
        private bool _commentsIsLoading;
        private double _commentsInitialY;
        private double _commentsInitialTransformY;
        private bool _commentsIsDragging;
        private double _shareInitialY;
        private double _shareInitialTransformY;
        private bool _shareIsDragging;
        private double _descriptionInitialY;
        private double _descriptionInitialTransformY;
        private bool _descriptionIsDragging;
        private bool _shareWithTimestamp;
        private Storyboard _shareToggleStoryboard;
        private MediaPlayerElement _cachedShortsMediaPlayerElement;
        private bool _keyboardShortcutsAttached;

        // Shorts settings-sheet drag state. Matches the Video page bottom-sheet gesture.
        private double _shortsSettingsInitialY;
        private double _shortsSettingsInitialTransformY;
        private bool _shortsSettingsIsDragging;

        private const double SwipeThreshold = 80.0;
        private const double SwipeStartThreshold = 8.0;
        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string InnertubeTvClientName = "TVHTML5";
        private const string InnertubeTvClientVersion = "7.20260429.11.00";
        private const string InnertubeTvClientHeaderName = "85";
        private const string InnertubeMwebClientName = "MWEB";
        private const string InnertubeMwebClientVersion = "2.20251222.01.00";
        private const string InnertubeMwebClientHeaderName = "2";
        private const string InnertubeMwebUserAgent = "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1";
        private const string InnertubeTvUserAgent = "Mozilla/5.0 (Web0S; Linux; SmartTV) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/79.0.3945.79 Safari/537.36 YouTube/7.20260429.11.00";
        private const string InnertubeWebClientName = "WEB";
        private const string InnertubeWebClientVersion = "2.20250101";
        private const string InnertubeWebClientHeaderName = "1";
        private const string InnertubeWebUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

        public Shorts()
        {
            this.InitializeComponent();
            ResetShareTimeToggleVisual(false);
            this.Loaded += Shorts_Loaded;
            this.Unloaded += Shorts_Unloaded;

            if (ShortsPlayer != null)
            {
                ShortsPlayer.PlaybackStarted += ShortsPlayer_PlaybackStarted;
            }
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            System.Diagnostics.Debug.WriteLine("[Shorts] OnNavigatedTo entered");

            if (tabbar != null)
            {
                tabbar.SetActiveTab(Tabbar.ActiveTab.Shorts);
            }

            ApplyShortsStatusBarStyle();
            System.Diagnostics.Debug.WriteLine("[Shorts] Status bar styled; starting initial load");

            await LoadInitialShortsAsync(e.Parameter as string);
            System.Diagnostics.Debug.WriteLine("[Shorts] Initial load returned");
        }

        private static string NormalizeInitialShortVideoId(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            value = value.Trim();
            if (value.Length != 11)
            {
                return string.Empty;
            }

            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                var ok = (c >= 'a' && c <= 'z')
                    || (c >= 'A' && c <= 'Z')
                    || (c >= '0' && c <= '9')
                    || c == '_'
                    || c == '-';
                if (!ok)
                {
                    return string.Empty;
                }
            }

            return value;
        }

        private async Task LoadInitialShortsAsync(string initialShortVideoId)
        {
            try
            {
                ShowMessage(string.Empty);
                SetLoading(true);

                // A direct navigation from Search passes the Short's raw video id. Handle
                // that seed before requiring a signed-in Shorts feed: the requested Short itself
                // can be opened by id even when there is no account feed available.
                initialShortVideoId = NormalizeInitialShortVideoId(initialShortVideoId);
                if (!string.IsNullOrWhiteSpace(initialShortVideoId))
                {
                    var seedShort = new ShortsVideoItem
                    {
                        VideoId = initialShortVideoId,
                        Title = "Shorts",
                        ChannelName = string.Empty,
                        ChannelThumbnailUrl = string.Empty,
                        ThumbnailUrl = "https://i.ytimg.com/vi/" + initialShortVideoId + "/oardefault.jpg",
                        LikeCount = "Like",
                        CommentCount = "Comments",
                        RatingState = "none"
                    };

                    // A direct-id Short does not come from the Shorts feed, so resolve its
                    // identity from /player first. Unlike the generic /next walker, videoDetails
                    // is scoped to this exact video id and cannot accidentally pick text from a
                    // recommendation, engagement panel or another renderer.
                    try
                    {
                        var directMetadata = await LoadDirectShortMetadataAsync(initialShortVideoId);
                        if (directMetadata != null)
                        {
                            if (!string.IsNullOrWhiteSpace(directMetadata.Title))
                            {
                                seedShort.Title = directMetadata.Title;
                            }

                            if (!string.IsNullOrWhiteSpace(directMetadata.ChannelName))
                            {
                                seedShort.ChannelName = directMetadata.ChannelName;
                            }

                            if (!string.IsNullOrWhiteSpace(directMetadata.ThumbnailUrl))
                            {
                                seedShort.ThumbnailUrl = directMetadata.ThumbnailUrl;
                            }

                            if (!string.IsNullOrWhiteSpace(directMetadata.ChannelThumbnailUrl))
                            {
                                seedShort.ChannelThumbnailUrl = directMetadata.ChannelThumbnailUrl;
                            }

                            System.Diagnostics.Debug.WriteLine(
                                "[Shorts] Direct-id exact metadata: title=" + seedShort.Title
                                + ", channel=" + seedShort.ChannelName
                                + ", avatar=" + (!string.IsNullOrWhiteSpace(seedShort.ChannelThumbnailUrl)));
                        }
                    }
                    catch (Exception ex)
                    {
                        // Metadata is supplementary. Playback by id must still work.
                        System.Diagnostics.Debug.WriteLine(
                            "[Shorts] Direct-id exact metadata load failed: " + ex.Message);
                    }

                    _seenVideoIds.Add(seedShort.VideoId);
                    _shorts.Add(seedShort);
                    await ShowShortAsync(0, 0);

                    // Fill the rest of the Shorts feed in the background so swiping still works.
                    // Only try to extend the feed when an account token is available.
                    Config.LoadUserToken();
                    if (!string.IsNullOrWhiteSpace(Config.UserToken))
                    {
                        var ignored = LoadMoreShortsAsync();
                    }
                    return;
                }

                Config.LoadUserToken();
                if (string.IsNullOrWhiteSpace(Config.UserToken))
                {
                    SetLoading(false);
                    ShowMessage("Sign in to watch Shorts.");
                    return;
                }

                var loaded = await LoadMoreShortsAsync();
                if (!loaded || _shorts.Count == 0)
                {
                    ShowMessage("No Shorts found.");
                    return;
                }

                await ShowShortAsync(0, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Initial load error: " + ex.Message);
                ShowMessage("Unable to load Shorts.");
            }
            finally
            {
                SetLoading(false);
            }
        }

        private async Task<bool> LoadMoreShortsAsync()
        {
            if (_isLoading)
            {
                return false;
            }

            _isLoading = true;
            try
            {
                var feed = await Config.GetShortsAsync(_sequenceToken);
                if (feed == null)
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(feed.SequenceToken))
                {
                    _sequenceToken = feed.SequenceToken;
                }

                var added = 0;
                if (feed.Items != null)
                {
                    foreach (var item in feed.Items)
                    {
                        if (item == null || string.IsNullOrWhiteSpace(item.VideoId) || _seenVideoIds.Contains(item.VideoId))
                        {
                            continue;
                        }

                        _seenVideoIds.Add(item.VideoId);
                        _shorts.Add(item);
                        added++;
                    }
                }

                System.Diagnostics.Debug.WriteLine("[Shorts] Added " + added + " shorts, total " + _shorts.Count);
                return added > 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] LoadMore error: " + ex.Message);
                return false;
            }
            finally
            {
                _isLoading = false;
            }
        }

        private async Task ShowShortAsync(int index, int direction)
        {
            if (index < 0 || index >= _shorts.Count)
            {
                return;
            }

            // Every call supersedes the previous one. The stream load below is a network round
            // trip, and the user may well swipe again before it finishes — the generation check
            // makes the late arrival drop its result instead of yanking the screen back.
            var generation = ++_showGeneration;
            System.Diagnostics.Debug.WriteLine(
                "[Shorts] Transition #" + generation + " -> index " + index + ", direction " + direction);

            // The slide is the only part that must own the screen; input is deliberately NOT
            // blocked during loading, which is what used to make swipes feel unresponsive.
            _isAnimating = true;
            try
            {
                if (direction != 0)
                {
                    await AnimateSwipeContentToAsync(-direction * GetSwipeDistance(), 155);
                }
            }
            finally
            {
                // Only the newest transition may lift the gesture block; an older, superseded one
                // must not unblock input on behalf of the animation still running.
                if (generation == _showGeneration)
                {
                    _isAnimating = false;
                }
            }

            if (generation != _showGeneration)
            {
                return;
            }

            ShortsVideoItem item;
            try
            {
                // Everything from here to the load gate used to run unguarded, and it is reached
                // from an async void touch handler — so a single exception (a bad avatar URL, an
                // index that moved, a preview that failed to build) killed the process outright,
                // too fast for the crash handler to log anything.
                if (index >= _shorts.Count)
                {
                    return;
                }

                _currentIndex = index;
                item = _shorts[index];
                if (item == null)
                {
                    return;
                }

                // The neighbour's preview is now filling the screen; keep it there under the guise
                // of the "current" cover so snapping the transform back to zero is invisible.
                ShowCurrentPreview(item);
                if (SwipeContentTransform != null)
                {
                    SwipeContentTransform.Y = 0;
                }

                UpdateMetadata(item);
                UpdateNeighbourPreviews();
                ShowMessage(string.Empty);
                SetLoading(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Transition setup failed: " + ex);
                SetLoading(false);
                return;
            }

            // Hard number rather than a guess: if the crash while swiping is memory exhaustion,
            // this climbs towards the limit right before it happens.
            try
            {
                var used = Windows.System.MemoryManager.AppMemoryUsage / (1024 * 1024);
                var limit = Windows.System.MemoryManager.AppMemoryUsageLimit / (1024 * 1024);
                System.Diagnostics.Debug.WriteLine(
                    "[Shorts][Mem] " + used + " MB of " + limit + " MB ("
                    + Windows.System.MemoryManager.AppMemoryUsageLevel + ")");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts][Mem] unavailable: " + ex.Message);
            }

            // Bounded wait: if one load ever wedges, later swipes must not queue behind it
            // forever — that would leave the feed permanently stuck on one video.
            if (!await _loadGate.WaitAsync(TimeSpan.FromSeconds(12)))
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Load gate timed out; skipping this transition");
                return;
            }

            try
            {
                // A newer swipe may have arrived while waiting for the gate.
                if (generation != _showGeneration)
                {
                    return;
                }

                // Settle before touching the media pipeline. During a burst of swipes only the
                // short the user actually stops on should ever reach the player: loading and
                // tearing down every intermediate one is wasted work, and that churn is the most
                // likely source of the native crash — it kills the process without raising a
                // managed exception, which is why the crash handlers stay silent.
                await Task.Delay(150);
                if (generation != _showGeneration)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Shorts] Transition #" + generation + " superseded before loading; player untouched");
                    return;
                }

                // No explicit Stop() here: it tears the source down separately, and assigning the
                // new source already replaces it. One state change instead of two.
                if (string.IsNullOrWhiteSpace(item.VideoUrl))
                {
                    System.Diagnostics.Debug.WriteLine("[Shorts] Resolving stream for " + item.VideoId);

                    // Bounded: this call has no timeout of its own, and a hung request here holds
                    // the load gate, which freezes the whole feed — swipes then do nothing.
                    var urlTask = Config.GetShortPlaybackUrlAsync(item.VideoId);
                    var finished = await Task.WhenAny(urlTask, Task.Delay(TimeSpan.FromSeconds(10)));
                    if (finished != urlTask)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Shorts] Stream resolve TIMED OUT for " + item.VideoId);
                        ShowMessage("Unable to play this Short.");
                        return;
                    }

                    item.VideoUrl = await urlTask;
                }

                if (generation != _showGeneration)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(item.VideoUrl))
                {
                    ShowMessage("Unable to play this Short.");
                    return;
                }

                System.Diagnostics.Debug.WriteLine("[Shorts] Handing source to player for " + item.VideoId);

                // A pinned (non-Auto) Shorts quality means the demuxer, which reaches past the
                // muxed-progressive ceiling to 1080p. If the adaptive streams can't be fetched or
                // built, fall through to the muxed URL so the short still plays.
                var demuxed = false;
                // Demux when a height is pinned (per-short menu override, or the Settings default)
                // or when a specific audio language is chosen — both need the adaptive path.
                var useMuxer = _shortsQualityOverride > 0
                    || (_shortsQualityOverride == 0 && Config.IsShortsMuxerPreferred())
                    || !string.IsNullOrEmpty(_shortsAudioTrackId);
                if (useMuxer)
                {
                    var formatsTask = Config.GetShortDemuxFormatsAsync(
                        item.VideoId, _shortsAudioTrackId, _shortsQualityOverride);
                    var finished = await Task.WhenAny(formatsTask, Task.Delay(TimeSpan.FromSeconds(10)));
                    if (finished == formatsTask && generation == _showGeneration)
                    {
                        var formats = await formatsTask;
                        if (formats != null)
                        {
                            _currentShortAudioTracks = formats.AudioTracks;
                            demuxed = await ShortsPlayer.SetDemuxedSourceAsync(formats.Video, formats.Audio, true);
                        }
                    }

                    if (generation != _showGeneration)
                    {
                        return;
                    }
                }

                if (!demuxed)
                {
                    // Muxed progressive carries a single baked-in track — no language choice.
                    _currentShortAudioTracks = new List<Config.AudioTrackInfo>();
                    await ShortsPlayer.SetSourceFromUriAsync(new Uri(item.VideoUrl), true);
                }
                System.Diagnostics.Debug.WriteLine("[Shorts] Source accepted for " + item.VideoId
                    + (demuxed ? " (demuxed)" : " (progressive)"));

                // Carry the chosen speed and subtitle language onto the new short.
                ShortsPlayer.SetPlaybackRate(_shortsSpeed);
                ApplyShortsSubtitleSelection(item.VideoId, generation);

                if (generation != _showGeneration)
                {
                    return;
                }

                // The preview stays up until the player reports a frame on screen; SetSource
                // returning only means the source was handed over, not that anything is visible.

                // Tell the account this short was watched. Fire-and-forget on purpose: it
                // makes an authenticated /player call, and awaiting it here would put that
                // round-trip in the way of the next swipe.
                var ignoredHistory = ReportShortWatchHistoryAsync(item.VideoId);

                var ignoredPrefetch = PrefetchNeighbourStreamAsync(generation);

                if (_currentIndex >= _shorts.Count - 2 && !string.IsNullOrWhiteSpace(_sequenceToken))
                {
                    var ignored = LoadMoreShortsAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] ShowShort error: " + ex.Message);
                if (generation == _showGeneration)
                {
                    ShowMessage("Unable to play this Short.");
                }
            }
            finally
            {
                _loadGate.Release();

                if (generation == _showGeneration)
                {
                    SetLoading(false);
                }
            }
        }

        // Paints the system status bar itself instead of trying to cover it from inside the page.
        // The earlier attempt assumed the page extends underneath it; it does not, so the black
        // strip landed on top of the video instead of on the stale area.
        //
        // Guarded by ApiInformation because the status bar only exists on mobile — on desktop the
        // type is simply absent and this must stay a no-op rather than throw.
        private void ApplyShortsStatusBarStyle()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.ViewManagement.StatusBar"))
            {
                return;
            }

            try
            {
                var statusBar = StatusBar.GetForCurrentView();
                if (statusBar == null)
                {
                    return;
                }

                statusBar.BackgroundColor = Colors.Black;
                statusBar.BackgroundOpacity = 1;
                statusBar.ForegroundColor = Colors.White;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Status bar styling failed: " + ex.Message);
            }
        }

        // The previews are parked exactly one screen away, so their offsets are only valid for
        // the current height — rotation or a resize has to re-park them.
        private void ShortsRoot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var distance = GetSwipeDistance();

            if (NextPreviewTransform != null)
            {
                NextPreviewTransform.Y = distance;
            }
            if (PrevPreviewTransform != null)
            {
                PrevPreviewTransform.Y = -distance;
            }

            UpdatePreviewStretch(e.NewSize.Width, e.NewSize.Height);
        }

        // Keep the still previews letterboxed the same way the video is, so nothing jumps between
        // the preview and the first frame on a wide (Continuum/desktop) surface. See
        // CustomShortsPlayer.UpdateStretchForSize.
        private void UpdatePreviewStretch(double width, double height)
        {
            if (height <= 0 || width <= 0)
            {
                return;
            }

            var stretch = CustomShortsPlayer.StretchForContainer(width, height);

            if (CurrentPreviewImage != null) CurrentPreviewImage.Stretch = stretch;
            if (PrevPreviewImage != null) PrevPreviewImage.Stretch = stretch;
            if (NextPreviewImage != null) NextPreviewImage.Stretch = stretch;
        }

        // Thumbnails come straight from the video id, so a neighbour can be shown before anything
        // about it has been fetched.
        // "oardefault" is the frame in the video's Original Aspect Ratio — 1080x1920 for a short,
        // against 480x360 for hqdefault, which is both far softer and a 4:3 letterbox that then
        // has to be cropped to fill a phone screen. Verified present for Shorts; a fallback is
        // still wired up below for anything that lacks it.
        private static string PreviewUrlFor(ShortsVideoItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return item == null ? string.Empty : (item.ThumbnailUrl ?? string.Empty);
            }

            return "https://i.ytimg.com/vi/" + item.VideoId + "/oardefault.jpg";
        }

        private static string FallbackPreviewUrlFor(ShortsVideoItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailUrl))
            {
                return item.ThumbnailUrl;
            }

            return string.IsNullOrWhiteSpace(item.VideoId)
                ? string.Empty
                : "https://i.ytimg.com/vi/" + item.VideoId + "/hqdefault.jpg";
        }

        private void SetPreviewSource(Image target, ShortsVideoItem item)
        {
            if (target == null)
            {
                return;
            }

            var url = PreviewUrlFor(item);
            if (string.IsNullOrWhiteSpace(url))
            {
                target.Tag = null;
                target.Source = null;
                return;
            }

            // Nothing to do if this control already shows that image. Without this the same
            // bitmap was decoded again on every transition, which on a phone is pure waste.
            if (string.Equals(target.Tag as string, url, StringComparison.Ordinal))
            {
                return;
            }

            target.Tag = url;

            try
            {
                var bitmap = new BitmapImage();

                // Decode to screen size instead of the source's 1080x1920. Full size is ~8 MB of
                // decoded pixels per image, and three of these are alive at once — enough to run
                // the app out of memory while swiping quickly.
                var decodeHeight = (int)GetSwipeDistance();
                if (decodeHeight > 0)
                {
                    bitmap.DecodePixelType = DecodePixelType.Logical;
                    bitmap.DecodePixelHeight = decodeHeight;
                }

                // Fall back to the old thumbnail if the high-resolution frame is missing. Guarded
                // by the tag: by the time this fires the control may already be showing a
                // different short, and it must not be overwritten with a stale image.
                var fallback = FallbackPreviewUrlFor(item);
                if (!string.IsNullOrWhiteSpace(fallback) && fallback != url)
                {
                    bitmap.ImageFailed += (s, e) =>
                    {
                        if (!string.Equals(target.Tag as string, url, StringComparison.Ordinal))
                        {
                            return;
                        }

                        System.Diagnostics.Debug.WriteLine(
                            "[Shorts] oardefault missing; falling back for " + item.VideoId);

                        try
                        {
                            target.Tag = fallback;
                            var replacement = new BitmapImage();
                            if (decodeHeight > 0)
                            {
                                replacement.DecodePixelType = DecodePixelType.Logical;
                                replacement.DecodePixelHeight = decodeHeight;
                            }
                            replacement.UriSource = new Uri(fallback);
                            target.Source = replacement;
                        }
                        catch
                        {
                            target.Source = null;
                        }
                    };
                }

                bitmap.UriSource = new Uri(url);
                target.Source = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Preview load failed: " + ex.Message);
                target.Tag = null;
                target.Source = null;
            }
        }

        private void ShowCurrentPreview(ShortsVideoItem item)
        {
            if (CurrentPreviewImage == null)
            {
                return;
            }

            SetPreviewSource(CurrentPreviewImage, item);
            CurrentPreviewImage.Visibility = CurrentPreviewImage.Source != null
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void HideCurrentPreview()
        {
            if (CurrentPreviewImage != null)
            {
                CurrentPreviewImage.Visibility = Visibility.Collapsed;
            }
        }

        // The still image is only removed once a real frame is up, so there is no flash of black
        // between the preview disappearing and the video appearing.
        private void ShortsPlayer_PlaybackStarted(object sender, EventArgs e)
        {
            HideCurrentPreview();
        }

        // Parks the neighbouring previews one screen above and below, so a drag reveals them
        // without any further work. Setting the source here also warms the image cache while the
        // current short is still playing.
        private void UpdateNeighbourPreviews()
        {
            var distance = GetSwipeDistance();

            if (NextPreviewTransform != null)
            {
                NextPreviewTransform.Y = distance;
            }
            if (PrevPreviewTransform != null)
            {
                PrevPreviewTransform.Y = -distance;
            }

            SetPreviewSource(
                NextPreviewImage,
                _currentIndex + 1 < _shorts.Count ? _shorts[_currentIndex + 1] : null);

            SetPreviewSource(
                PrevPreviewImage,
                _currentIndex - 1 >= 0 ? _shorts[_currentIndex - 1] : null);
        }

        // Pulls the first chunk of a stream and throws it away. The bytes themselves are not the
        // point — DNS, TLS and the CDN edge are, and they are what the player pays for at swipe
        // time. Deliberately small: this phone has little memory and the data is discarded.
        private async Task WarmUpStreamAsync(string url, int generation)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.Range = new RangeHeaderValue(0, 131071); // first 128 KB

                    using (var response = await _httpClient.SendAsync(
                        request, HttpCompletionOption.ResponseHeadersRead))
                    {
                        if (generation != _showGeneration)
                        {
                            return;
                        }

                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[16384];
                            var total = 0;
                            int read;
                            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                total += read;
                                if (total >= 131072 || generation != _showGeneration)
                                {
                                    break;
                                }
                            }

                            System.Diagnostics.Debug.WriteLine(
                                "[Shorts] Warmed up next stream (" + total + " bytes)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Stream warm-up failed: " + ex.Message);
            }
        }

        // Resolves the next short's stream URL ahead of time, so swiping forward does not have to
        // wait for it. Kept to the immediate neighbour only — this runs on a phone.
        private async Task PrefetchNeighbourStreamAsync(int generation)
        {
            try
            {
                var nextIndex = _currentIndex + 1;
                if (nextIndex >= _shorts.Count)
                {
                    return;
                }

                var next = _shorts[nextIndex];
                if (next == null)
                {
                    return;
                }

                // The URL may already be known from an earlier pass — in that case skip straight
                // to opening the stream rather than returning, which would leave it unprepared.
                if (string.IsNullOrWhiteSpace(next.VideoUrl))
                {
                    var url = await Config.GetShortPlaybackUrlAsync(next.VideoId);
                    if (generation != _showGeneration || string.IsNullOrWhiteSpace(url))
                    {
                        return;
                    }

                    next.VideoUrl = url;
                    System.Diagnostics.Debug.WriteLine("[Shorts] Prefetched stream for " + next.VideoId);
                }

                // Resolving the URL only removes the API round trip. The rest of the wait is
                // network setup to the CDN, so fetch the head of the stream now: it resolves DNS,
                // completes the TLS handshake and pulls the MP4 header through the edge, all of
                // which the player would otherwise do from cold at swipe time.
                if (generation == _showGeneration)
                {
                    await WarmUpStreamAsync(next.VideoUrl, generation);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Prefetch failed: " + ex.Message);
            }
        }

        private void UpdateMetadata(ShortsVideoItem item)
        {
            if (item == null)
            {
                return;
            }

            ShortTitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "Shorts" : item.Title;
            ChannelNameText.Text = string.IsNullOrWhiteSpace(item.ChannelName) ? "YouTube" : item.ChannelName;
            LikeCountText.Text = string.IsNullOrWhiteSpace(item.LikeCount) ? "Like" : item.LikeCount;
            if (CommentCountText != null)
            {
                CommentCountText.Text = string.IsNullOrWhiteSpace(item.CommentCount) ? "Comments" : item.CommentCount;
            }
            item.RatingState = NormalizeShortRatingState(item.RatingState);
            if (item.RatingState == "none")
            {
                // The Shorts feed can contain generic like/dislike commands whose ratingStatus is
                // the action target, not the current viewer state. Do not trust cached IsLiked flags
                // from the feed parser here; authenticated /next below is the source of truth.
                item.IsLiked = false;
                item.IsDisliked = false;
            }
            UpdateRatingVisualState(item);

            var ratingGeneration = ++_ratingStateGeneration;
            var ignoredRatingLoad = LoadCurrentShortRatingStateAsync(item, ratingGeneration);

            // Only a real channel avatar belongs here. This used to fall back to the video's own
            // thumbnail, and since the TV feed leaves the avatar out for most Shorts, the result
            // was a frame of the video sitting in the channel circle.
            var avatarUrl = item.ChannelThumbnailUrl;

            if (ChannelAvatarBrush != null)
            {
                // The URL comes straight from the API response and is not guaranteed to be a
                // well-formed absolute URI — new Uri() throws on a bad one, and this runs on the
                // swipe path where an exception is fatal.
                Uri avatarUri;
                if (!string.IsNullOrWhiteSpace(avatarUrl)
                    && Uri.TryCreate(avatarUrl, UriKind.Absolute, out avatarUri))
                {
                    try
                    {
                        ChannelAvatarBrush.ImageSource = new BitmapImage(avatarUri);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Shorts] Avatar load failed: " + ex.Message);
                        ChannelAvatarBrush.ImageSource = null;
                    }
                }
                else
                {
                    ChannelAvatarBrush.ImageSource = null;
                }
            }
        }

        // async void: an exception escaping here terminates the process with no usable log, so the
        // whole body is guarded. This is the path every swipe takes.
        private async void GestureLayer_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                await HandlePointerReleasedAsync(sender, e);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Swipe handling failed: " + ex);
                _isPointerSwipe = false;
                _swipeDirection = 0;
                _isAnimating = false;
            }
        }

        private async Task HandlePointerReleasedAsync(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPointerDown)
            {
                return;
            }

            _isPointerDown = false;
            var element = sender as UIElement;
            if (element != null)
            {
                try
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                catch
                {
                }
            }

            var point = e.GetCurrentPoint(ShortsRoot).Position;
            var dx = point.X - _pointerStart.X;
            var dy = point.Y - _pointerStart.Y;

            // Once a vertical drag starts, complete it in the remembered direction.
            // This prevents the gesture from snapping back when the mouse/pointer is
            // released outside of the transparent layer or the final delta is tiny.
            var verticalSwipeStarted = _isPointerSwipe
                || _swipeDirection != 0
                || (Math.Abs(dy) >= SwipeStartThreshold && Math.Abs(dy) >= Math.Abs(dx) * 0.45);

            if (verticalSwipeStarted)
            {
                var direction = _swipeDirection;
                if (direction == 0)
                {
                    direction = dy < 0 ? 1 : -1;
                }

                // Pause here rather than when the drag starts: a drag that gets pulled back is a
                // cancelled swipe, and pausing/resuming it would produce an audible stutter for
                // nothing. By release the direction is final.
                if (ShortsPlayer != null)
                {
                    try
                    {
                        ShortsPlayer.Pause();
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine("[Shorts] Pause on swipe failed: " + ex.Message);
                    }
                }

                if (direction > 0)
                {
                    await ShowNextShortAsync();
                }
                else
                {
                    await ShowPreviousShortAsync();
                }
            }
            else
            {
                if (_isPointerSwipe)
                {
                    await AnimateSwipeContentToAsync(0, 160);
                }
                else if (ShortsPlayer != null)
                {
                    ShortsPlayer.TogglePlayPause();
                }
            }

            _isPointerSwipe = false;
            _swipeDirection = 0;
            e.Handled = true;
        }

        private void GestureLayer_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // Only the brief slide animation owns the screen. Loading the next stream no longer
            // swallows touches — that was the real reason swipes had to be repeated.
            if (_isAnimating)
            {
                return;
            }

            _isPointerDown = true;
            _isPointerSwipe = false;
            _swipeDirection = 0;
            _pointerStart = e.GetCurrentPoint(ShortsRoot).Position;

            var element = sender as UIElement;
            if (element != null)
            {
                try
                {
                    element.CapturePointer(e.Pointer);
                }
                catch
                {
                }
            }

            e.Handled = true;
        }

        private void GestureLayer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isPointerDown || _isAnimating)
            {
                return;
            }

            var point = e.GetCurrentPoint(ShortsRoot).Position;
            var dx = point.X - _pointerStart.X;
            var dy = point.Y - _pointerStart.Y;

            if (!_isPointerSwipe && Math.Abs(dy) >= SwipeStartThreshold && Math.Abs(dy) >= Math.Abs(dx) * 0.45)
            {
                _isPointerSwipe = true;
                _swipeDirection = dy < 0 ? 1 : -1;
            }

            if (_isPointerSwipe)
            {
                if (SwipeContentTransform != null)
                {
                    var limit = GetSwipeDistance();
                    if (dy > limit)
                    {
                        dy = limit;
                    }
                    else if (dy < -limit)
                    {
                        dy = -limit;
                    }

                    SwipeContentTransform.Y = dy;
                }

                e.Handled = true;
            }
        }

        private void GestureLayer_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _isPointerDown = false;
            _isPointerSwipe = false;
            _swipeDirection = 0;

            var element = sender as UIElement;
            if (element != null)
            {
                try
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                catch
                {
                }
            }

            var ignored = AnimateSwipeContentToAsync(0, 160);
            e.Handled = true;
        }

        private async Task ShowNextShortAsync()
        {
            if (_currentIndex < _shorts.Count - 1)
            {
                await ShowShortAsync(_currentIndex + 1, 1);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_sequenceToken))
            {
                SetLoading(true);
                var loaded = await LoadMoreShortsAsync();
                SetLoading(false);
                if (loaded && _currentIndex < _shorts.Count - 1)
                {
                    await ShowShortAsync(_currentIndex + 1, 1);
                    return;
                }
            }

            await AnimateSwipeContentToAsync(0, 160);
        }

        private async Task ShowPreviousShortAsync()
        {
            if (_currentIndex > 0)
            {
                await ShowShortAsync(_currentIndex - 1, -1);
                return;
            }

            await AnimateSwipeContentToAsync(0, 160);
        }

        private ShortsVideoItem CurrentShort
        {
            get
            {
                if (_currentIndex < 0 || _currentIndex >= _shorts.Count)
                {
                    return null;
                }

                return _shorts[_currentIndex];
            }
        }

        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            var item = CurrentShort;
            if (item == null || _ratingInProgress)
            {
                return;
            }

            var oldRating = item.RatingState;
            var newRating = string.Equals(oldRating, "like", StringComparison.OrdinalIgnoreCase) ? "none" : "like";
            await ApplyShortRatingAsync(item, newRating, oldRating);
        }

        private async void DislikeButton_Click(object sender, RoutedEventArgs e)
        {
            var item = CurrentShort;
            if (item == null || _ratingInProgress)
            {
                return;
            }

            var oldRating = item.RatingState;
            var newRating = string.Equals(oldRating, "dislike", StringComparison.OrdinalIgnoreCase) ? "none" : "dislike";
            await ApplyShortRatingAsync(item, newRating, oldRating);
        }

        private async Task ApplyShortRatingAsync(ShortsVideoItem item, string newRating, string oldRating)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            var ratingGeneration = ++_ratingStateGeneration;
            _ratingInProgress = true;
            oldRating = NormalizeShortRatingState(oldRating);
            newRating = NormalizeShortRatingState(newRating);
            ApplyRatingStateToItem(item, newRating);
            UpdateRatingVisualState(item);

            try
            {
                var ok = await SetShortRatingViaInnertubeAsync(item.VideoId, newRating);
                if (!ok && ratingGeneration == _ratingStateGeneration)
                {
                    ApplyRatingStateToItem(item, oldRating);
                    UpdateRatingVisualState(item);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Rating error: " + ex.Message);
                if (ratingGeneration == _ratingStateGeneration)
                {
                    ApplyRatingStateToItem(item, oldRating);
                    UpdateRatingVisualState(item);
                }
            }
            finally
            {
                if (ratingGeneration == _ratingStateGeneration)
                {
                    _ratingInProgress = false;
                }
            }
        }

        private async Task<bool> SetShortRatingViaInnertubeAsync(string videoId, string rating)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return false;
            }

            var accessToken = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return false;
            }

            var normalized = NormalizeShortRatingState(rating);
            var endpoint = "like/removelike";
            if (normalized == "like")
            {
                endpoint = "like/like";
            }
            else if (normalized == "dislike")
            {
                endpoint = "like/dislike";
            }

            // Do the same thing as Video.xaml.cs first: TVHTML5 /like/* with context + target.videoId.
            // The previous Shorts command path could pick a reel-specific command and YouTube returned
            // INVALID_ARGUMENT. For Shorts rating we use /next only for reading current state / command fallback.
            var ok = await PostShortRatingInnertubeAsync(
                endpoint,
                BuildShortRatingPayload(videoId, false),
                accessToken,
                InnertubeTvClientHeaderName,
                InnertubeTvClientVersion,
                InnertubeTvUserAgent,
                "TVHTML5 same-as-Video");

            if (ok)
            {
                return true;
            }

            // Fallback: load the concrete action command from authenticated /next for this exact Short.
            return await TrySetShortRatingUsingNextCommandAsync(videoId, normalized, accessToken);
        }

        private async Task<bool> TrySetShortRatingUsingNextCommandAsync(string videoId, string rating, string accessToken)
        {
            var command = await TryLoadShortRatingCommandFromNextClientAsync(videoId, rating, accessToken, false);
            if (command == null)
            {
                command = await TryLoadShortRatingCommandFromNextClientAsync(videoId, rating, accessToken, true);
            }

            if (command == null)
            {
                return false;
            }

            var endpoint = GetRatingCommandEndpoint(command, rating);
            var endpointPayload = GetRatingCommandPayloadObject(command);
            if (string.IsNullOrWhiteSpace(endpoint) || endpointPayload == null)
            {
                return false;
            }

            var clickTrackingParams = ReadJsonString(command, "clickTrackingParams");
            var payload = BuildShortRatingPayloadFromEndpoint(endpointPayload, videoId, false, clickTrackingParams);

            var ok = await PostShortRatingInnertubeAsync(
                endpoint,
                payload,
                accessToken,
                InnertubeTvClientHeaderName,
                InnertubeTvClientVersion,
                InnertubeTvUserAgent,
                "TVHTML5 command");

            if (ok)
            {
                return true;
            }

            payload = BuildShortRatingPayloadFromEndpoint(endpointPayload, videoId, true, clickTrackingParams);
            return await PostShortRatingInnertubeAsync(
                endpoint,
                payload,
                accessToken,
                InnertubeMwebClientHeaderName,
                InnertubeMwebClientVersion,
                InnertubeMwebUserAgent,
                "MWEB command");
        }

        private async Task<JsonObject> TryLoadShortRatingCommandFromNextClientAsync(string videoId, string rating, string accessToken, bool mobileWebClient)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("next")))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedNextPayload(videoId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json");

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeMwebClientHeaderName,
                            InnertubeMwebClientVersion,
                            InnertubeMwebUserAgent);
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeTvClientHeaderName,
                            InnertubeTvClientVersion,
                            InnertubeTvUserAgent);
                    }

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json))
                    {
                        return null;
                    }

                    var root = JsonValue.Parse(json);
                    return FindRatingCommandInValue(root, rating);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Rating command load error: " + ex.Message);
                return null;
            }
        }

        private static JsonObject FindRatingCommandInValue(IJsonValue value, string rating)
        {
            if (value == null)
            {
                return null;
            }

            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                if (IsMatchingRatingCommand(obj, rating))
                {
                    return obj;
                }

                foreach (var kvp in obj)
                {
                    var found = FindRatingCommandInValue(kvp.Value, rating);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                for (uint i = 0; i < array.Count; i++)
                {
                    var found = FindRatingCommandInValue(array[(int)i], rating);
                    if (found != null)
                    {
                        return found;
                    }
                }
            }

            return null;
        }

        private static bool IsMatchingRatingCommand(JsonObject command, string rating)
        {
            if (command == null)
            {
                return false;
            }

            var endpoint = GetRatingCommandEndpoint(command, rating);
            if (!string.IsNullOrWhiteSpace(endpoint))
            {
                return true;
            }

            var endpointPayload = GetRatingCommandPayloadObject(command);
            if (endpointPayload == null)
            {
                return false;
            }

            var status = FirstNonEmpty(
                ReadJsonString(endpointPayload, "status"),
                ReadJsonString(endpointPayload, "likeStatus"),
                ReadJsonString(endpointPayload, "ratingStatus"));
            var normalizedStatus = (status ?? string.Empty).Trim().ToUpperInvariant();

            if (rating == "like")
            {
                return normalizedStatus == "LIKE" || normalizedStatus == "LIKED";
            }

            if (rating == "dislike")
            {
                return normalizedStatus == "DISLIKE" || normalizedStatus == "DISLIKED";
            }

            return normalizedStatus == "INDIFFERENT" || normalizedStatus == "NONE" || normalizedStatus == "REMOVED";
        }

        private static string GetRatingCommandEndpoint(JsonObject command, string rating)
        {
            var apiUrl = string.Empty;
            try
            {
                if (command != null
                    && command.ContainsKey("commandMetadata")
                    && command.GetNamedValue("commandMetadata").ValueType == JsonValueType.Object)
                {
                    var commandMetadata = command.GetNamedObject("commandMetadata");
                    if (commandMetadata.ContainsKey("webCommandMetadata")
                        && commandMetadata.GetNamedValue("webCommandMetadata").ValueType == JsonValueType.Object)
                    {
                        var webCommandMetadata = commandMetadata.GetNamedObject("webCommandMetadata");
                        apiUrl = ReadJsonString(webCommandMetadata, "apiUrl");
                    }
                }
            }
            catch
            {
            }

            if (!string.IsNullOrWhiteSpace(apiUrl))
            {
                var lower = apiUrl.ToLowerInvariant();
                if (rating == "like" && lower.Contains("/like/like"))
                {
                    return NormalizeInnertubeEndpointFromApiUrl(apiUrl);
                }

                if (rating == "dislike" && lower.Contains("/like/dislike"))
                {
                    return NormalizeInnertubeEndpointFromApiUrl(apiUrl);
                }

                if (rating == "none" && lower.Contains("/like/removelike"))
                {
                    return NormalizeInnertubeEndpointFromApiUrl(apiUrl);
                }
            }

            var endpointPayload = GetRatingCommandPayloadObject(command);
            if (endpointPayload != null)
            {
                var status = FirstNonEmpty(
                    ReadJsonString(endpointPayload, "status"),
                    ReadJsonString(endpointPayload, "likeStatus"),
                    ReadJsonString(endpointPayload, "ratingStatus"));
                var normalizedStatus = (status ?? string.Empty).Trim().ToUpperInvariant();
                if (rating == "like" && (normalizedStatus == "LIKE" || normalizedStatus == "LIKED"))
                {
                    return "like/like";
                }

                if (rating == "dislike" && (normalizedStatus == "DISLIKE" || normalizedStatus == "DISLIKED"))
                {
                    return "like/dislike";
                }

                if (rating == "none" && (normalizedStatus == "INDIFFERENT" || normalizedStatus == "NONE" || normalizedStatus == "REMOVED"))
                {
                    return "like/removelike";
                }
            }

            return string.Empty;
        }

        private static string NormalizeInnertubeEndpointFromApiUrl(string apiUrl)
        {
            if (string.IsNullOrWhiteSpace(apiUrl))
            {
                return string.Empty;
            }

            var endpoint = apiUrl.Trim();
            var marker = "/youtubei/v1/";
            var markerIndex = endpoint.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex >= 0)
            {
                endpoint = endpoint.Substring(markerIndex + marker.Length);
            }

            if (endpoint.StartsWith("/", StringComparison.OrdinalIgnoreCase))
            {
                endpoint = endpoint.Substring(1);
            }

            var queryIndex = endpoint.IndexOf("?", StringComparison.OrdinalIgnoreCase);
            if (queryIndex >= 0)
            {
                endpoint = endpoint.Substring(0, queryIndex);
            }

            return endpoint;
        }

        private static JsonObject GetRatingCommandPayloadObject(JsonObject command)
        {
            if (command == null)
            {
                return null;
            }

            foreach (var key in new[] { "likeEndpoint", "performLikeEndpoint", "dislikeEndpoint" })
            {
                if (command.ContainsKey(key) && command.GetNamedValue(key).ValueType == JsonValueType.Object)
                {
                    return command.GetNamedObject(key);
                }
            }

            return null;
        }

        private static string BuildShortRatingPayloadFromEndpoint(JsonObject endpointPayload, string videoId, bool mobileWebClient, string clickTrackingParams)
        {
            var context = BuildShortInnertubeContext(mobileWebClient, clickTrackingParams);
            var payload = new JsonObject();
            payload["context"] = context;

            if (endpointPayload != null)
            {
                foreach (var kvp in endpointPayload)
                {
                    if (string.Equals(kvp.Key, "clickTrackingParams", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    try
                    {
                        payload[kvp.Key] = JsonValue.Parse(kvp.Value.Stringify());
                    }
                    catch
                    {
                        payload[kvp.Key] = kvp.Value;
                    }
                }
            }

            if (!payload.ContainsKey("target"))
            {
                var target = new JsonObject();
                target["videoId"] = JsonValue.CreateStringValue(videoId);
                payload["target"] = target;
            }

            return payload.Stringify();
        }

        private async Task<bool> PostShortRatingInnertubeAsync(
            string endpoint,
            string payload,
            string accessToken,
            string clientNameHeader,
            string clientVersion,
            string userAgent,
            string sourceName)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl(endpoint)))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        clientNameHeader,
                        clientVersion,
                        userAgent);

                    var response = await _httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine("[Shorts] Innertube rating OK via " + sourceName + ": " + endpoint);
                        return true;
                    }

                    System.Diagnostics.Debug.WriteLine(
                        "[Shorts] Innertube rating failed via "
                        + sourceName
                        + ": "
                        + endpoint
                        + " "
                        + response.StatusCode
                        + " "
                        + body);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Innertube rating error via " + sourceName + ": " + ex.Message);
            }

            return false;
        }

        private void UpdateRatingVisualState(ShortsVideoItem item)
        {
            var rating = item == null ? string.Empty : item.RatingState;
            var liked = string.Equals(rating, "like", StringComparison.OrdinalIgnoreCase);
            var disliked = string.Equals(rating, "dislike", StringComparison.OrdinalIgnoreCase);

            if (LikeIconImage != null)
            {
                App.SetThemeImageSource(LikeIconImage, liked
                    ? "Assets/Dark/player/like_clicked.png"
                    : "Assets/Dark/player/like.png");
            }

            if (DislikeIconImage != null)
            {
                App.SetThemeImageSource(DislikeIconImage, disliked
                    ? "Assets/Dark/player/dislike_clicked.png"
                    : "Assets/Dark/player/dislike.png");
            }

            if (LikeButton != null)
            {
                LikeButton.Opacity = 1.0;
            }

            if (DislikeButton != null)
            {
                DislikeButton.Opacity = 1.0;
            }
        }

        private async void CommentsButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowCommentsBottomSheetAsync();
        }

        private void SendButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShareBottomSheet();
        }

        private void SearchTopButton_Click(object sender, RoutedEventArgs e)
        {
            if (Frame != null)
            {
                Frame.Navigate(typeof(Searching));
            }
        }

        private async void MoreTopButton_Click(object sender, RoutedEventArgs e)
        {
            await ShowDescriptionBottomSheetAsync();
        }

        // --- Shorts settings (quality / speed / subtitles / audio) ----------------------------

        private int _shortsQualityOverride;          // 0 = follow Settings, >0 = pinned height
        private double _shortsSpeed = 1.0;
        private string _shortsAudioTrackId;          // null = default/locale
        private Subtitles.SubtitleTrack _shortsSubtitleTrack;
        private List<Config.AudioTrackInfo> _currentShortAudioTracks = new List<Config.AudioTrackInfo>();

        // Cached per short so opening the sheet does not re-hit the network each time.
        private List<int> _shortHeightsCache = new List<int>();
        private Subtitles.TrackList _shortSubtitlesCache;
        private string _settingsForShortId;

        private async void ShortsSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var item = CurrentShort;
            if (item == null)
            {
                return;
            }

            ShowShortsSettingsSheet();

            // Fetch the height list and captions once per short, then refresh the open sheet.
            if (!string.Equals(_settingsForShortId, item.VideoId, StringComparison.Ordinal))
            {
                _settingsForShortId = item.VideoId;
                _shortHeightsCache = new List<int>();

                // The Shorts settings menu intentionally has no subtitles row, so opening it
                // should not waste a network request on caption tracks.
                var heights = await Config.GetShortAvailableHeightsAsync(item.VideoId);
                if (string.Equals(CurrentShort != null ? CurrentShort.VideoId : null, item.VideoId, StringComparison.Ordinal))
                {
                    _shortHeightsCache = heights;
                    UpdateShortsSettingsRowValues();
                }
            }
        }

        private void ShowShortsSettingsSheet()
        {
            // Always opens on the main list.
            if (ShortsMainSettingsPanel != null)
                ShortsMainSettingsPanel.Visibility = Visibility.Visible;
            if (ShortsSubSettingsPanel != null)
                ShortsSubSettingsPanel.Visibility = Visibility.Collapsed;

            UpdateShortsSettingsRowValues();

            AnimateShortsSettingsSheet(true);
        }

        private void CloseShortsSettingsSheet()
        {
            AnimateShortsSettingsSheet(false);
        }

        private void AnimateShortsSettingsSheet(bool show)
        {
            if (ShortsSettingsSheetTransform == null)
            {
                return;
            }

            if (show)
            {
                ShortsSettingsOverlay.Visibility = Visibility.Visible;
                ShortsSettingsSheet.Visibility = Visibility.Visible;
            }

            var storyboard = new Windows.UI.Xaml.Media.Animation.Storyboard();
            var slide = new Windows.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = show ? 0 : 270,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new Windows.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = show
                        ? Windows.UI.Xaml.Media.Animation.EasingMode.EaseOut
                        : Windows.UI.Xaml.Media.Animation.EasingMode.EaseIn
                }
            };

            if (!show)
            {
                slide.Completed += (s, e) =>
                {
                    if (!_shortsSettingsIsDragging)
                    {
                        ShortsSettingsOverlay.Visibility = Visibility.Collapsed;
                        ShortsSettingsSheet.Visibility = Visibility.Collapsed;
                    }
                };
            }

            Windows.UI.Xaml.Media.Animation.Storyboard.SetTarget(slide, ShortsSettingsSheetTransform);
            Windows.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(slide, "Y");
            storyboard.Children.Add(slide);
            storyboard.Begin();
        }

        private void ShortsSettingsOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseShortsSettingsSheet();
        }

        // Tapping the handle closes the sheet, same as on the video page.
        private void ShortsSettingsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            CloseShortsSettingsSheet();
            e.Handled = true;
        }

        private void ShortsSettingsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element == null || ShortsSettingsSheetTransform == null)
            {
                return;
            }

            if (element.CapturePointer(e.Pointer))
            {
                _shortsSettingsInitialY = e.GetCurrentPoint(element).Position.Y;
                _shortsSettingsInitialTransformY = ShortsSettingsSheetTransform.Y;
                _shortsSettingsIsDragging = true;
                e.Handled = true;
            }
        }

        private void ShortsSettingsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_shortsSettingsIsDragging || ShortsSettingsSheetTransform == null)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var dragOffset = e.GetCurrentPoint(element).Position.Y - _shortsSettingsInitialY;
            var newY = _shortsSettingsInitialTransformY + dragOffset;

            // Drag only downward, exactly like the Video settings sheet.
            if (newY < 0)
            {
                newY = 0;
            }
            else if (newY > 270)
            {
                newY = 270;
            }

            ShortsSettingsSheetTransform.Y = newY;
            e.Handled = true;
        }

        private void ShortsSettingsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_shortsSettingsIsDragging)
            {
                return;
            }

            _shortsSettingsIsDragging = false;

            var element = sender as UIElement;
            if (element != null)
            {
                try
                {
                    element.ReleasePointerCapture(e.Pointer);
                }
                catch
                {
                }
            }

            if (ShortsSettingsSheetTransform != null && ShortsSettingsSheetTransform.Y > 90)
            {
                AnimateShortsSettingsSheet(false);
            }
            else
            {
                AnimateShortsSettingsSheet(true);
            }

            e.Handled = true;
        }

        // Fills the right-edge current-value labels on the main rows.
        private void UpdateShortsSettingsRowValues()
        {
            if (ShortsQualityValueText != null)
            {
                ShortsQualityValueText.Text = _shortsQualityOverride == 0
                    ? "Auto"
                    : _shortsQualityOverride + "p";
            }

            if (ShortsSpeedValueText != null)
            {
                ShortsSpeedValueText.Text = _shortsSpeed.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture) + "x";
            }

            // Language choice only exists when the short actually carries several audio tracks.
            var hasAudioChoice = _currentShortAudioTracks != null && _currentShortAudioTracks.Count > 1;
            if (ShortsAudioTrackButton != null)
            {
                ShortsAudioTrackButton.Visibility = hasAudioChoice ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ShortsAudioValueText != null && hasAudioChoice)
            {
                ShortsAudioValueText.Text = CurrentAudioTrackName();
            }

        }

        private string CurrentAudioTrackName()
        {
            if (_currentShortAudioTracks == null)
            {
                return string.Empty;
            }
            foreach (var t in _currentShortAudioTracks)
            {
                var isCurrent = string.IsNullOrEmpty(_shortsAudioTrackId)
                    ? t.IsDefault || Config.AudioTrackMatchesLocale(t.Id)
                    : string.Equals(_shortsAudioTrackId, t.Id, StringComparison.Ordinal);
                if (isCurrent)
                {
                    return t.Name;
                }
            }
            return string.Empty;
        }

        // Switches the sheet from the main list to an options list, like the video page does.
        private void ShowShortsOptions(string title)
        {
            ShortsSubSettingsHeader.Text = title;
            ShortsSubOptionsPanel.Children.Clear();
            ShortsMainSettingsPanel.Visibility = Visibility.Collapsed;
            ShortsSubSettingsPanel.Visibility = Visibility.Visible;
        }

        private void ShortsQualityButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShortsOptions("Quality");

            ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton("Auto", _shortsQualityOverride == 0, () =>
            {
                _shortsQualityOverride = 0;
                CloseShortsSettingsSheet();
                ReloadCurrentShort();
            }));

            for (int i = _shortHeightsCache.Count - 1; i >= 0; i--)
            {
                var h = _shortHeightsCache[i];
                ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton(h + "p", _shortsQualityOverride == h, () =>
                {
                    _shortsQualityOverride = h;
                    CloseShortsSettingsSheet();
                    ReloadCurrentShort();
                }));
            }
        }

        private void ShortsSpeedButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShortsOptions("Playback speed");

            foreach (var s in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 })
            {
                var speed = s;
                var label = s.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "x";
                ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton(
                    label, Math.Abs(_shortsSpeed - s) < 0.001, () =>
                    {
                        _shortsSpeed = speed;
                        ShortsPlayer.SetPlaybackRate(speed);
                        CloseShortsSettingsSheet();
                    }));
            }
        }

        private void ShortsAudioTrackButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShortsOptions("Audio track");

            foreach (var track in _currentShortAudioTracks)
            {
                var chosen = track;
                var isCurrent = string.IsNullOrEmpty(_shortsAudioTrackId)
                    ? chosen.IsDefault || Config.AudioTrackMatchesLocale(chosen.Id)
                    : string.Equals(_shortsAudioTrackId, chosen.Id, StringComparison.Ordinal);

                ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton(chosen.Name, isCurrent, () =>
                {
                    _shortsAudioTrackId = chosen.Id;
                    CloseShortsSettingsSheet();
                    ReloadCurrentShort();
                }));
            }
        }

        private void ShortsSubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            ShowShortsOptions("Subtitles");
            var videoId = CurrentShort != null ? CurrentShort.VideoId : null;

            ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton("Off", _shortsSubtitleTrack == null, () =>
            {
                _shortsSubtitleTrack = null;
                ShortsPlayer.SetSubtitleCues(null);
                CloseShortsSettingsSheet();
            }));

            if (_shortSubtitlesCache != null)
            {
                foreach (var track in _shortSubtitlesCache.Tracks)
                {
                    var chosen = track;
                    var isCurrent = _shortsSubtitleTrack != null
                        && string.Equals(_shortsSubtitleTrack.BaseUrl, chosen.BaseUrl, StringComparison.Ordinal)
                        && string.IsNullOrEmpty(_shortsSubtitleTrack.TranslationLanguageCode);

                    ShortsSubOptionsPanel.Children.Add(MakeShortsOptionButton(chosen.DisplayName, isCurrent, () =>
                    {
                        _shortsSubtitleTrack = chosen;
                        ApplyShortsSubtitleSelection(videoId, _showGeneration);
                        CloseShortsSettingsSheet();
                    }));
                }
            }
        }

        // Same option row as the video page pickers: leading check column, then the label.
        private Button MakeShortsOptionButton(string label, bool isCurrent, Action onClick)
        {
            var button = new Button
            {
                Background = new SolidColorBrush(Colors.Transparent),
                Foreground = new SolidColorBrush(Colors.White),
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
                Foreground = new SolidColorBrush(Colors.White),
                Visibility = isCurrent ? Visibility.Visible : Visibility.Collapsed,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(check, 0);
            grid.Children.Add(check);

            var text = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(Colors.White),
                FontWeight = isCurrent ? Windows.UI.Text.FontWeights.SemiBold : Windows.UI.Text.FontWeights.Normal,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            button.Content = grid;
            button.Click += (s, e) => onClick();
            return button;
        }


        private async void ReloadCurrentShort()
        {
            if (_currentIndex >= 0)
            {
                await ShowShortAsync(_currentIndex, 0);
            }
        }

        // Downloads and shows the selected caption track on the current short. No-op when the
        // selection is off or the short changed underneath it.
        private async void ApplyShortsSubtitleSelection(string videoId, int generation)
        {
            var track = _shortsSubtitleTrack;
            if (track == null)
            {
                ShortsPlayer.SetSubtitleCues(null);
                return;
            }

            try
            {
                var cues = await Subtitles.GetCuesAsync(track);
                if (generation == _showGeneration
                    && CurrentShort != null
                    && string.Equals(CurrentShort.VideoId, videoId, StringComparison.Ordinal))
                {
                    ShortsPlayer.SetSubtitleCues(cues);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Subtitle apply failed: " + ex.Message);
            }
        }

        private string CurrentShortUrl
        {
            get
            {
                var item = CurrentShort;
                if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
                {
                    return string.Empty;
                }

                return "https://www.youtube.com/shorts/" + item.VideoId;
            }
        }

        private async void ChannelHeaderPanel_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            await NavigateToCurrentShortChannelAsync();
        }

        private async Task NavigateToCurrentShortChannelAsync()
        {
            var item = CurrentShort;
            if (item == null)
            {
                return;
            }

            var channelTarget = await ResolveShortChannelTargetAsync(item);
            if (string.IsNullOrWhiteSpace(channelTarget))
            {
                channelTarget = item.ChannelName;
            }

            if (string.IsNullOrWhiteSpace(channelTarget))
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[Shorts] Navigating to channel: " + channelTarget);
            Frame.Navigate(typeof(Channel), channelTarget);
        }

        private async Task<string> ResolveShortChannelTargetAsync(ShortsVideoItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            var channelId = await TryLoadShortChannelIdFromPlayerAsync(item.VideoId);
            if (!string.IsNullOrWhiteSpace(channelId))
            {
                return channelId;
            }

            var channelName = item.ChannelName == null ? string.Empty : item.ChannelName.Trim();
            if (channelName.StartsWith("@", StringComparison.OrdinalIgnoreCase) || channelName.StartsWith("UC", StringComparison.OrdinalIgnoreCase))
            {
                return channelName;
            }

            return channelName;
        }

        private async Task LoadCurrentShortRatingStateAsync(ShortsVideoItem item, int generation)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            try
            {
                var accessToken = await GetTvAccessTokenAsync(false);
                if (generation != _ratingStateGeneration || CurrentShort != item)
                {
                    return;
                }

                var rating = await TryLoadShortRatingFromAuthenticatedNextAsync(item.VideoId, accessToken);
                if (generation != _ratingStateGeneration || CurrentShort != item)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(rating))
                {
                    ApplyRatingStateToItem(item, rating);
                    UpdateRatingVisualState(item);
                    System.Diagnostics.Debug.WriteLine("[Shorts] Current user rating from authenticated /next: " + rating);
                }

                // The TV feed omits like counts, so fill them in from the same response. Guarded
                // by the checks above: this only ever writes to the short still on screen. This
                // overwrites a feed-provided count on purpose — only the first short of a session
                // gets one, and letting it keep a different format made the counter visibly change
                // shape between shorts.
                // The feed rarely carries an avatar, so fill it from the same response.
                if (!string.IsNullOrWhiteSpace(_lastNextChannelAvatar)
                    && string.IsNullOrWhiteSpace(item.ChannelThumbnailUrl))
                {
                    item.ChannelThumbnailUrl = _lastNextChannelAvatar;

                    Uri avatarUri;
                    if (ChannelAvatarBrush != null
                        && Uri.TryCreate(item.ChannelThumbnailUrl, UriKind.Absolute, out avatarUri))
                    {
                        try
                        {
                            ChannelAvatarBrush.ImageSource = new BitmapImage(avatarUri);
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine("[Shorts] Avatar from /next failed: " + ex.Message);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(_lastNextLikeCount))
                {
                    item.LikeCount = _lastNextLikeCount;
                    LikeCountText.Text = item.LikeCount;
                    System.Diagnostics.Debug.WriteLine("[Shorts] Like count from /next: " + item.LikeCount);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Rating state load error: " + ex.Message);
            }
        }

        // Like count picked up as a side effect of the rating request; consumed by the caller
        // right after, while it still knows which short the request was for.
        private string _lastNextLikeCount = string.Empty;
        private string _lastNextChannelAvatar = string.Empty;

        // Records the short in the account's watch history, once per short. Without this the feed
        // keeps serving the same videos — the server has no idea they were already seen.
        private string _historyReportedVideoId;

        private async Task ReportShortWatchHistoryAsync(string videoId)
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

                // Length is left at 0 on purpose: Config fills it in from the authenticated
                // player response it already fetches, and the Shorts player exposes no duration.
                var reported = await Config.ReportWatchHistoryAsync(videoId, refreshToken, 0, 0);
                System.Diagnostics.Debug.WriteLine(
                    "[Shorts] History report for " + videoId + ": " + (reported ? "ok" : "failed")
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] History report failed: " + ex.Message);
            }
        }

        private async Task<string> TryLoadShortRatingFromAuthenticatedNextAsync(string videoId, string accessToken)
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return string.Empty;
            }

            _lastNextLikeCount = string.Empty;
            _lastNextChannelAvatar = string.Empty;

            // TV first: our bearer is minted for the TV client and only that client is allowed to
            // use it — MWEB answers 400. MWEB stays purely as a fallback for the case where the
            // TV request itself fails.
            //
            // The second client is only tried when the FIRST REQUEST ITSELF failed. An empty
            // rating is a perfectly good answer meaning "not rated yet"; treating it as a failure
            // is what used to send a doomed MWEB request on every unrated short.
            var rating = await TryLoadShortRatingFromNextClientAsync(videoId, accessToken, false);
            if (rating != null)
            {
                return rating;
            }

            return await TryLoadShortRatingFromNextClientAsync(videoId, accessToken, true) ?? string.Empty;
        }

        private async Task<string> TryLoadShortRatingFromNextClientAsync(string videoId, string accessToken, bool mobileWebClient)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("next")))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedNextPayload(videoId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json");

                    if (mobileWebClient)
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeMwebClientHeaderName,
                            InnertubeMwebClientVersion,
                            InnertubeMwebUserAgent);
                    }
                    else
                    {
                        AddInnertubeAuthHeadersForClient(
                            request,
                            accessToken,
                            InnertubeTvClientHeaderName,
                            InnertubeTvClientVersion,
                            InnertubeTvUserAgent);
                    }

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json))
                    {
                        System.Diagnostics.Debug.WriteLine("[Shorts] /next rating load failed: " + (int)response.StatusCode + " " + response.ReasonPhrase);
                        // null means "the request failed" — distinct from "" which means
                        // "answered, this short is simply not rated".
                        return null;
                    }

                    // Same response also carries the like count and channel avatar the TV feed omits.
                    _lastNextLikeCount = Config.FormatCompactCount(Config.ExtractLikeCountFromNextJson(json));
                    _lastNextChannelAvatar = Config.ExtractChannelAvatarFromNextJson(json);

                    return ExtractRatingFromNextJson(json) ?? string.Empty;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] /next rating load error: " + ex.Message);
                return null;
            }
        }

        private sealed class DirectShortMetadata
        {
            public string Title { get; set; }
            public string ChannelName { get; set; }
            public string ThumbnailUrl { get; set; }
            public string ChannelThumbnailUrl { get; set; }
        }

        private async Task<DirectShortMetadata> LoadDirectShortMetadataAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return null;
            }

            var metadata = new DirectShortMetadata();
            string playerJson = string.Empty;

            // First source of truth: /player.videoDetails. title + author belong to the exact
            // requested id and are much safer than recursively walking /next.
            try
            {
                var payload = BuildPlayerPayload(videoId);
                var accessToken = await GetTvAccessTokenAsync(false);

                playerJson = await LoadDirectShortPlayerJsonAsync(payload, accessToken);
                if (string.IsNullOrWhiteSpace(playerJson))
                {
                    playerJson = await LoadDirectShortPlayerJsonAsync(payload, string.Empty);
                }

                if (!string.IsNullOrWhiteSpace(playerJson))
                {
                    var root = JsonObject.Parse(playerJson);
                    if (root.ContainsKey("videoDetails"))
                    {
                        var details = root.GetNamedObject("videoDetails");

                        metadata.Title = details.GetNamedString("title", string.Empty).Trim();
                        metadata.ChannelName = details.GetNamedString("author", string.Empty).Trim();

                        if (details.ContainsKey("thumbnail"))
                        {
                            metadata.ThumbnailUrl = ExtractLargestThumbnailUrl(details.GetNamedObject("thumbnail"));
                        }

                        System.Diagnostics.Debug.WriteLine(
                            "[Shorts] /player metadata for " + videoId
                            + ": title='" + metadata.Title
                            + "', author='" + metadata.ChannelName + "'");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] /player metadata parse failed: " + ex.Message);
            }

            // Channel avatars normally are not present in /player.videoDetails. Use the existing
            // exact-video /next request only for the avatar, never for title/author.
            try
            {
                var details = await Config.GetVideoDetailsAsync(videoId);
                if (details != null && !string.IsNullOrWhiteSpace(details.ChannelThumbnail))
                {
                    metadata.ChannelThumbnailUrl = details.ChannelThumbnail.Trim();
                }

                // Only use these as emergency fallbacks if /player itself omitted them.
                if (details != null
                    && string.IsNullOrWhiteSpace(metadata.Title)
                    && !string.IsNullOrWhiteSpace(details.Title))
                {
                    metadata.Title = details.Title.Trim();
                }

                if (details != null
                    && string.IsNullOrWhiteSpace(metadata.ChannelName)
                    && !string.IsNullOrWhiteSpace(details.ChannelName))
                {
                    metadata.ChannelName = details.ChannelName.Trim();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Direct-id avatar fallback failed: " + ex.Message);
            }

            return metadata;
        }

        private async Task<string> LoadDirectShortPlayerJsonAsync(string payload, string accessToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("player")))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeWebClientHeaderName,
                        InnertubeWebClientVersion,
                        InnertubeWebUserAgent);

                    using (var response = await _httpClient.SendAsync(request))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                "[Shorts] Direct-id /player metadata failed: "
                                + (int)response.StatusCode + " " + response.ReasonPhrase);
                            return string.Empty;
                        }

                        return await response.Content.ReadAsStringAsync();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Direct-id /player request failed: " + ex.Message);
                return string.Empty;
            }
        }

        private static string ExtractLargestThumbnailUrl(JsonObject thumbnailObject)
        {
            try
            {
                if (thumbnailObject == null || !thumbnailObject.ContainsKey("thumbnails"))
                {
                    return string.Empty;
                }

                var thumbnails = thumbnailObject.GetNamedArray("thumbnails");
                string bestUrl = string.Empty;
                double bestArea = -1;

                for (int i = 0; i < (int)thumbnails.Count; i++)
                {
                    if (thumbnails[i].ValueType != JsonValueType.Object)
                    {
                        continue;
                    }

                    var thumb = thumbnails[i].GetObject();
                    var url = thumb.GetNamedString("url", string.Empty);
                    var width = thumb.GetNamedNumber("width", 0);
                    var height = thumb.GetNamedNumber("height", 0);
                    var area = width * height;

                    if (!string.IsNullOrWhiteSpace(url) && area >= bestArea)
                    {
                        bestUrl = url;
                        bestArea = area;
                    }
                }

                return bestUrl;
            }
            catch
            {
                return string.Empty;
            }
        }

        private async Task<string> TryLoadShortChannelIdFromPlayerAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            var payload = BuildPlayerPayload(videoId);
            var accessToken = await GetTvAccessTokenAsync(false);
            var channelId = await TryLoadShortChannelIdFromPlayerClientAsync(payload, accessToken);
            if (!string.IsNullOrWhiteSpace(channelId))
            {
                return channelId;
            }

            return await TryLoadShortChannelIdFromPlayerClientAsync(payload, string.Empty);
        }

        private async Task<string> TryLoadShortChannelIdFromPlayerClientAsync(string payload, string accessToken)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("player")))
                {
                    request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        InnertubeWebClientHeaderName,
                        InnertubeWebClientVersion,
                        InnertubeWebUserAgent);

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json))
                    {
                        return string.Empty;
                    }

                    return ExtractChannelIdFromPlayerJson(json);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Channel id player load error: " + ex.Message);
                return string.Empty;
            }
        }

        private static string NormalizeShortRatingState(string rating)
        {
            if (string.IsNullOrWhiteSpace(rating))
            {
                return "none";
            }

            var normalized = rating.Trim().ToLowerInvariant();
            if (normalized == "liked")
            {
                return "like";
            }

            if (normalized == "disliked")
            {
                return "dislike";
            }

            if (normalized == "like" || normalized == "dislike")
            {
                return normalized;
            }

            return "none";
        }

        private static void ApplyRatingStateToItem(ShortsVideoItem item, string rating)
        {
            if (item == null)
            {
                return;
            }

            rating = NormalizeShortRatingState(rating);
            item.RatingState = rating;
            item.IsLiked = rating == "like";
            item.IsDisliked = rating == "dislike";
        }

        private static async Task<string> GetTvAccessTokenAsync(bool showErrors)
        {
            Config.LoadUserToken();
            var refreshToken = Config.UserToken;
            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                if (showErrors)
                {
                    await ShowStaticShortsMessageAsync("Sign in required", "TV refresh token was not found. Sign in again and retry.");
                }
                return string.Empty;
            }

            var accessToken = await Config.RefreshAccessTokenAsync(refreshToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                if (showErrors)
                {
                    await ShowStaticShortsMessageAsync("Sign in required", "Could not exchange refresh token for access token.");
                }
                return string.Empty;
            }

            return accessToken;
        }

        private static async Task ShowStaticShortsMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = "OK"
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] " + title + ": " + message);
            }
        }

        private static string BuildInnertubeUrl(string endpoint)
        {
            return "https://www.youtube.com/youtubei/v1/" + endpoint + "?key=" + InnertubeApiKey;
        }

        private static JsonObject BuildShortInnertubeContext(bool mobileWebClient, string clickTrackingParams)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(mobileWebClient ? InnertubeMwebClientName : InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(mobileWebClient ? InnertubeMwebClientVersion : InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue("ru");
            client["gl"] = JsonValue.CreateStringValue("RU");
            if (!mobileWebClient)
            {
                client["platform"] = JsonValue.CreateStringValue("TV");
                client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
                client["originalUrl"] = JsonValue.CreateStringValue("https://www.youtube.com/tv");
                client["theme"] = JsonValue.CreateStringValue("CLASSIC");
            }
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

            return context;
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
            return payload.Stringify();
        }

        private static string BuildShortRatingPayload(string videoId, bool mobileWebClient)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(mobileWebClient ? InnertubeMwebClientName : InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(mobileWebClient ? InnertubeMwebClientVersion : InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            if (!mobileWebClient)
            {
                client["platform"] = JsonValue.CreateStringValue("TV");
                client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            }
            context["client"] = client;

            var target = new JsonObject();
            target["videoId"] = JsonValue.CreateStringValue(videoId);

            var payload = new JsonObject();
            payload["context"] = context;
            payload["target"] = target;
            return payload.Stringify();
        }

        private static string BuildPlayerPayload(string videoId)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeWebClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeWebClientVersion);
            client["hl"] = JsonValue.CreateStringValue("ru");
            client["gl"] = JsonValue.CreateStringValue("RU");
            context["client"] = client;

            var payload = new JsonObject();
            payload["context"] = context;
            payload["videoId"] = JsonValue.CreateStringValue(videoId);
            payload["racyCheckOk"] = JsonValue.CreateBooleanValue(true);
            payload["contentCheckOk"] = JsonValue.CreateBooleanValue(true);
            return payload.Stringify();
        }

        private static void AddInnertubeAuthHeadersForClient(HttpRequestMessage request, string accessToken, string clientNameHeader, string clientVersion, string userAgent)
        {
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }

            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            request.Headers.TryAddWithoutValidation("X-Origin", "https://www.youtube.com");
            if (clientNameHeader == InnertubeMwebClientHeaderName)
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://m.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://m.youtube.com/shorts/");
            }
            else
            {
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            }
        }

        private static string ExtractRatingFromNextJson(string json)
        {
            try
            {
                var root = JsonValue.Parse(json);
                bool found;
                var rating = ExtractCurrentRatingFromNext(root, out found);
                if (!found)
                {
                    return string.Empty;
                }

                return NormalizeShortRatingState(rating);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Rating JSON parse error: " + ex.Message);
                return string.Empty;
            }
        }

        private static string ExtractCurrentRatingFromNext(IJsonValue root, out bool found)
        {
            found = false;
            if (root == null)
            {
                return string.Empty;
            }

            var matches = new List<ShortRatingToggleMatch>();
            CollectShortRatingToggleMatches(root, string.Empty, matches, 0);

            var disliked = matches.FirstOrDefault(m => m != null && m.IsToggled && string.Equals(m.Rating, "dislike", StringComparison.OrdinalIgnoreCase));
            if (disliked != null)
            {
                found = true;
                return "dislike";
            }

            var liked = matches.FirstOrDefault(m => m != null && m.IsToggled && string.Equals(m.Rating, "like", StringComparison.OrdinalIgnoreCase));
            if (liked != null)
            {
                found = true;
                return "like";
            }

            if (matches.Count > 0)
            {
                found = true;
                return "none";
            }

            bool directFound;
            var direct = FindDirectCurrentRatingStatus(root, string.Empty, 0, out directFound);
            if (directFound)
            {
                found = true;
                return direct;
            }

            return string.Empty;
        }

        private static void CollectShortRatingToggleMatches(IJsonValue value, string path, List<ShortRatingToggleMatch> matches, int depth)
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
                    if (obj.ContainsKey("isToggled") || obj.ContainsKey("toggled") || obj.ContainsKey("selected") || obj.ContainsKey("isSelected") || obj.ContainsKey("checked") || obj.ContainsKey("isChecked"))
                    {
                        var rating = DetermineShortRatingKindForToggleObject(obj, path);
                        if (!string.IsNullOrWhiteSpace(rating))
                        {
                            bool toggled;
                            if (TryReadToggleState(obj, out toggled))
                            {
                                matches.Add(new ShortRatingToggleMatch { Rating = rating, IsToggled = toggled });
                            }
                        }
                    }

                    foreach (var kvp in obj)
                    {
                        var key = kvp.Key == null ? string.Empty : kvp.Key.ToLowerInvariant();
                        var nextPath = string.IsNullOrWhiteSpace(path) ? key : path + "." + key;
                        CollectShortRatingToggleMatches(kvp.Value, nextPath, matches, depth + 1);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectShortRatingToggleMatches(array[(int)i], path + "[]", matches, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool TryReadToggleState(JsonObject obj, out bool toggled)
        {
            toggled = false;
            if (obj == null)
            {
                return false;
            }

            foreach (var key in new[] { "isToggled", "toggled", "selected", "isSelected", "checked", "isChecked" })
            {
                if (!obj.ContainsKey(key))
                {
                    continue;
                }

                var value = obj.GetNamedValue(key);
                if (value == null)
                {
                    continue;
                }

                if (value.ValueType == JsonValueType.Boolean)
                {
                    toggled = value.GetBoolean();
                    return true;
                }

                if (value.ValueType == JsonValueType.String)
                {
                    var text = value.GetString();
                    toggled = string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "selected", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "toggled", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(text, "checked", StringComparison.OrdinalIgnoreCase);
                    return true;
                }
            }

            return false;
        }

        private static string DetermineShortRatingKindForToggleObject(JsonObject obj, string path)
        {
            var lowerPath = (path ?? string.Empty).ToLowerInvariant();
            if (lowerPath.Contains("dislike"))
            {
                return "dislike";
            }

            if ((lowerPath.Contains("likebutton") || lowerPath.Contains("like_button") || lowerPath.Contains("segmentedlikedislike") || lowerPath.Contains("likebuttonviewmodel"))
                && !lowerPath.Contains("dislike"))
            {
                return "like";
            }

            var objectText = ExtractTextFromAnyValue(obj).ToLowerInvariant();
            if (objectText.Contains("dislike"))
            {
                return "dislike";
            }

            if ((objectText.Contains("like") || objectText.Contains("нравится")) && !objectText.Contains("dislike") && !objectText.Contains("не нравится"))
            {
                return "like";
            }

            return string.Empty;
        }

        private static string FindDirectCurrentRatingStatus(IJsonValue value, string path, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 80)
            {
                return string.Empty;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var kvp in obj)
                    {
                        var key = kvp.Key == null ? string.Empty : kvp.Key.ToLowerInvariant();
                        var nextPath = string.IsNullOrWhiteSpace(path) ? key : path + "." + key;

                        if (kvp.Value != null && kvp.Value.ValueType == JsonValueType.String && IsDirectCurrentRatingKey(key, nextPath))
                        {
                            var normalized = NormalizeRatingText(kvp.Value.GetString());
                            if (!string.IsNullOrWhiteSpace(normalized))
                            {
                                found = true;
                                return normalized;
                            }
                        }

                        bool nestedFound;
                        var nested = FindDirectCurrentRatingStatus(kvp.Value, nextPath, depth + 1, out nestedFound);
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
                        var nested = FindDirectCurrentRatingStatus(array[(int)i], path + "[]", depth + 1, out nestedFound);
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

            return string.Empty;
        }

        private static bool IsDirectCurrentRatingKey(string lowerKey, string lowerPath)
        {
            var compactKey = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            var path = (lowerPath ?? string.Empty).ToLowerInvariant();

            if (path.Contains("endpoint")
                || path.Contains("command")
                || path.Contains("menu")
                || path.Contains("service")
                || path.Contains("likeendpoint")
                || path.Contains("dislikeendpoint"))
            {
                return false;
            }

            if (compactKey == "likestatus" || compactKey == "ratingstatus" || compactKey == "userrating" || compactKey == "feedbackstate" || compactKey == "selectedrating" || compactKey == "selectionstate")
            {
                return path.Contains("current")
                    || path.Contains("viewer")
                    || path.Contains("user")
                    || path.Contains("selected")
                    || path.Contains("feedback");
            }

            return false;
        }

        private static string NormalizeRatingText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lower = text.Trim().ToLowerInvariant();
            if (lower == "none" || lower == "no_rating" || lower == "indifferent" || lower == "like_status_indifferent" || lower == "rating_unspecified")
            {
                return "none";
            }

            if (lower == "like" || lower == "liked" || lower == "like_selected" || lower == "like_filled" || lower == "like_status_like" || lower.Contains("like status liked"))
            {
                return "like";
            }

            if (lower == "dislike" || lower == "disliked" || lower == "dislike_selected" || lower == "dislike_filled" || lower == "like_status_dislike" || lower.Contains("dislike status disliked"))
            {
                return "dislike";
            }

            return string.Empty;
        }

        private static bool JsonObjectContainsToggledState(JsonObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            bool toggled;
            return TryReadToggleState(obj, out toggled) && toggled;
        }

        private static string ExtractTextFromAnyValue(IJsonValue value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            try
            {
                if (value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }

                if (value.ValueType == JsonValueType.Number)
                {
                    return value.GetNumber().ToString();
                }

                if (value.ValueType == JsonValueType.Boolean)
                {
                    return value.GetBoolean() ? "true" : "false";
                }

                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var direct = FirstNonEmpty(
                        ReadJsonString(obj, "simpleText"),
                        ReadJsonString(obj, "text"),
                        ReadJsonString(obj, "label"),
                        ReadJsonString(obj, "iconType"));
                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }

                    var builder = new StringBuilder();
                    if (obj.ContainsKey("runs"))
                    {
                        var runs = obj.GetNamedArray("runs");
                        for (uint i = 0; i < runs.Count; i++)
                        {
                            var text = ExtractTextFromAnyValue(runs[(int)i]);
                            if (!string.IsNullOrWhiteSpace(text))
                            {
                                builder.Append(text);
                            }
                        }
                    }

                    if (obj.ContainsKey("accessibility"))
                    {
                        var accessibility = ExtractTextFromAnyValue(obj.GetNamedValue("accessibility"));
                        if (!string.IsNullOrWhiteSpace(accessibility))
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(" ");
                            }
                            builder.Append(accessibility);
                        }
                    }

                    if (obj.ContainsKey("accessibilityData"))
                    {
                        var accessibilityData = ExtractTextFromAnyValue(obj.GetNamedValue("accessibilityData"));
                        if (!string.IsNullOrWhiteSpace(accessibilityData))
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(" ");
                            }
                            builder.Append(accessibilityData);
                        }
                    }

                    return builder.ToString();
                }

                if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    var builder = new StringBuilder();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        var text = ExtractTextFromAnyValue(array[(int)i]);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (builder.Length > 0)
                            {
                                builder.Append(" ");
                            }
                            builder.Append(text);
                        }
                    }
                    return builder.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ReadJsonString(JsonObject obj, string key)
        {
            if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            try
            {
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
                    return value.GetNumber().ToString();
                }

                if (value.ValueType == JsonValueType.Boolean)
                {
                    return value.GetBoolean() ? "true" : "false";
                }

                if (value.ValueType == JsonValueType.Object)
                {
                    var nested = value.GetObject();
                    var simple = ReadJsonString(nested, "simpleText");
                    if (!string.IsNullOrWhiteSpace(simple))
                    {
                        return simple;
                    }

                    if (nested.ContainsKey("accessibility")
                        && nested.GetNamedValue("accessibility").ValueType == JsonValueType.Object)
                    {
                        var accessibility = nested.GetNamedObject("accessibility");
                        if (accessibility.ContainsKey("accessibilityData")
                            && accessibility.GetNamedValue("accessibilityData").ValueType == JsonValueType.Object)
                        {
                            var accessibilityData = accessibility.GetNamedObject("accessibilityData");
                            var label = ReadJsonString(accessibilityData, "label");
                            if (!string.IsNullOrWhiteSpace(label))
                            {
                                return label;
                            }
                        }
                    }

                    if (nested.ContainsKey("runs")
                        && nested.GetNamedValue("runs").ValueType == JsonValueType.Array)
                    {
                        var runs = nested.GetNamedArray("runs");
                        var builder = new StringBuilder();
                        for (uint i = 0; i < runs.Count; i++)
                        {
                            var run = runs[(int)i];
                            if (run != null && run.ValueType == JsonValueType.Object)
                            {
                                var text = ReadJsonString(run.GetObject(), "text");
                                if (!string.IsNullOrWhiteSpace(text))
                                {
                                    builder.Append(text);
                                }
                            }
                        }

                        if (builder.Length > 0)
                        {
                            return builder.ToString();
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractChannelIdFromPlayerJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                var rootValue = JsonValue.Parse(json);
                if (rootValue == null || rootValue.ValueType != JsonValueType.Object)
                {
                    return string.Empty;
                }

                var root = rootValue.GetObject();
                if (root.ContainsKey("videoDetails")
                    && root.GetNamedValue("videoDetails").ValueType == JsonValueType.Object)
                {
                    var videoDetails = root.GetNamedObject("videoDetails");
                    var channelId = FirstNonEmpty(
                        ReadJsonString(videoDetails, "channelId"),
                        ReadJsonString(videoDetails, "externalChannelId"));
                    if (!string.IsNullOrWhiteSpace(channelId))
                    {
                        return channelId;
                    }
                }

                if (root.ContainsKey("microformat")
                    && root.GetNamedValue("microformat").ValueType == JsonValueType.Object)
                {
                    var microformat = root.GetNamedObject("microformat");
                    if (microformat.ContainsKey("playerMicroformatRenderer")
                        && microformat.GetNamedValue("playerMicroformatRenderer").ValueType == JsonValueType.Object)
                    {
                        var renderer = microformat.GetNamedObject("playerMicroformatRenderer");
                        var channelId = FirstNonEmpty(
                            ReadJsonString(renderer, "externalChannelId"),
                            ReadJsonString(renderer, "channelId"));
                        if (!string.IsNullOrWhiteSpace(channelId))
                        {
                            return channelId;
                        }
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            for (var i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }

            return string.Empty;
        }

        private async Task ShowCommentsBottomSheetAsync()
        {
            var item = CurrentShort;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            if (ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Visible;
            }

            if (CommentsBottomSheetPanel != null)
            {
                CommentsBottomSheetPanel.Visibility = Visibility.Visible;
            }

            AnimateCommentsBottomSheet(true);

            if (CommentsItemsControl != null)
            {
                CommentsItemsControl.ItemsSource = null;
            }

            SetCommentsLoading(true);
            SetCommentsEmpty(false, string.Empty);

            List<CommentItem> comments;
            if (_commentsCache.TryGetValue(item.VideoId, out comments))
            {
                BindComments(item.VideoId, comments);
                return;
            }

            if (_commentsIsLoading)
            {
                return;
            }

            _commentsIsLoading = true;
            try
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Loading comments for: " + item.VideoId);
                comments = await Config.GetCommentsAsync(item.VideoId);
                if (comments == null)
                {
                    comments = new List<CommentItem>();
                }

                _commentsCache[item.VideoId] = comments;
                BindComments(item.VideoId, comments);
                System.Diagnostics.Debug.WriteLine("[Shorts] Loaded " + comments.Count + " comments for: " + item.VideoId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Comments load error: " + ex.Message);
                SetCommentsLoading(false);
                SetCommentsEmpty(true, "Unable to load comments.");
            }
            finally
            {
                _commentsIsLoading = false;
            }
        }

        private void BindComments(string videoId, List<CommentItem> comments)
        {
            _commentsLoadedVideoId = videoId ?? string.Empty;
            SetCommentsLoading(false);

            if (CommentsItemsControl != null)
            {
                CommentsItemsControl.ItemsSource = comments;
            }

            if (comments == null || comments.Count == 0)
            {
                SetCommentsEmpty(true, "No comments found.");
            }
            else
            {
                SetCommentsEmpty(false, string.Empty);
            }
        }

        private void SetCommentsLoading(bool isLoading)
        {
            if (CommentsLoadingPanel != null)
            {
                CommentsLoadingPanel.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }

            if (CommentsLoadingRing != null)
            {
                CommentsLoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
                CommentsLoadingRing.IsActive = isLoading;
            }

            if (CommentsScrollViewer != null)
            {
                CommentsScrollViewer.Visibility = isLoading ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SetCommentsEmpty(bool isVisible, string text)
        {
            if (CommentsEmptyText != null)
            {
                CommentsEmptyText.Text = string.IsNullOrWhiteSpace(text) ? "No comments found." : text;
                CommentsEmptyText.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void AnimateCommentsBottomSheet(bool show)
        {
            if (CommentsBottomSheetTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();
            animation.To = show ? 0 : 400;

            Storyboard.SetTarget(animation, CommentsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, args) =>
                {
                    if (CommentsBottomSheetPanel != null)
                    {
                        CommentsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }

                    HideOverlayIfNoPanelsOpen();
                };
            }

            storyboard.Begin();
        }

        private void CommentsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateCommentsBottomSheet(false);
            e.Handled = true;
        }

        private void CommentsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _commentsInitialY = e.GetCurrentPoint(element).Position.Y;
                _commentsInitialTransformY = CommentsBottomSheetTransform != null ? CommentsBottomSheetTransform.Y : 0;
                _commentsIsDragging = true;
                e.Handled = true;
            }
        }

        private void CommentsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_commentsIsDragging && CommentsBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
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
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (CommentsBottomSheetTransform != null && CommentsBottomSheetTransform.Y > 190)
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

        private void ShowShareBottomSheet()
        {
            var item = CurrentShort;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            _shareWithTimestamp = false;
            ResetShareTimeToggleVisual(false);

            if (ShareVideoTitleText != null)
            {
                ShareVideoTitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "YouTube Shorts" : item.Title;
            }

            if (ShareChannelText != null)
            {
                ShareChannelText.Text = string.IsNullOrWhiteSpace(item.ChannelName) ? "YouTube" : item.ChannelName;
            }

            if (ShareUrlTextBlock != null)
            {
                ShareUrlTextBlock.Text = BuildCurrentShareUrl();
            }

            if (ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Visible;
            }

            if (ShareBottomSheetPanel != null)
            {
                ShareBottomSheetPanel.Visibility = Visibility.Visible;
            }

            AnimateShareBottomSheet(true);
        }

        private string BuildCurrentShareUrl()
        {
            var item = CurrentShort;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return string.Empty;
            }

            var baseUrl = "https://youtu.be/" + item.VideoId;
            if (!_shareWithTimestamp)
            {
                return baseUrl;
            }

            var seconds = GetCurrentShortPositionSeconds();
            if (seconds <= 0)
            {
                return baseUrl;
            }

            return baseUrl + "?t=" + seconds;
        }

        private void RefreshShareUrlPreview()
        {
            if (ShareUrlTextBlock != null)
            {
                ShareUrlTextBlock.Text = BuildCurrentShareUrl();
            }
        }

        private int GetCurrentShortPositionSeconds()
        {
            try
            {
                var mediaPlayerElement = FindShortsMediaPlayerElement();
                if (mediaPlayerElement == null || mediaPlayerElement.MediaPlayer == null)
                {
                    return 0;
                }

                var seconds = (int)Math.Floor(mediaPlayerElement.MediaPlayer.PlaybackSession.Position.TotalSeconds);
                return seconds < 0 ? 0 : seconds;
            }
            catch
            {
                return 0;
            }
        }

        private MediaPlayerElement FindShortsMediaPlayerElement()
        {
            if (_cachedShortsMediaPlayerElement != null)
            {
                return _cachedShortsMediaPlayerElement;
            }

            _cachedShortsMediaPlayerElement = FindDescendant<MediaPlayerElement>(ShortsPlayer);
            return _cachedShortsMediaPlayerElement;
        }

        private static T FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            if (root == null)
            {
                return null;
            }

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var typed = child as T;
                if (typed != null)
                {
                    return typed;
                }

                var nested = FindDescendant<T>(child);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void ShareTimeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _shareWithTimestamp = !_shareWithTimestamp;
            AnimateShareTimeToggleVisual(_shareWithTimestamp);
            RefreshShareUrlPreview();
        }

        private void ResetShareTimeToggleVisual(bool stopAnimations)
        {
            if (stopAnimations && _shareToggleStoryboard != null)
            {
                _shareToggleStoryboard.Stop();
                _shareToggleStoryboard = null;
            }

            if (ShareTimeToggleTrack != null)
            {
                var brush = ShareTimeToggleTrack.Background as SolidColorBrush;
                if (brush == null)
                {
                    brush = new SolidColorBrush(ColorFromHex("#9B9B9B"));
                    ShareTimeToggleTrack.Background = brush;
                }
                else
                {
                    brush.Color = ColorFromHex("#9B9B9B");
                }
            }

            if (ShareTimeToggleThumbTransform != null)
            {
                ShareTimeToggleThumbTransform.X = 0;
            }
        }

        private void AnimateShareTimeToggleVisual(bool isOn)
        {
            if (ShareTimeToggleTrack == null || ShareTimeToggleThumbTransform == null)
            {
                return;
            }

            if (_shareToggleStoryboard != null)
            {
                _shareToggleStoryboard.Stop();
            }

            var brush = ShareTimeToggleTrack.Background as SolidColorBrush;
            if (brush == null)
            {
                brush = new SolidColorBrush(isOn ? Colors.White : ColorFromHex("#9B9B9B"));
                ShareTimeToggleTrack.Background = brush;
            }

            var thumbAnimation = new DoubleAnimation();
            thumbAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(180));
            thumbAnimation.To = isOn ? 18 : 0;
            thumbAnimation.EnableDependentAnimation = true;

            var colorAnimation = new ColorAnimation();
            colorAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(180));
            colorAnimation.To = isOn ? Colors.White : ColorFromHex("#9B9B9B");

            Storyboard.SetTarget(thumbAnimation, ShareTimeToggleThumbTransform);
            Storyboard.SetTargetProperty(thumbAnimation, "X");
            Storyboard.SetTarget(colorAnimation, brush);
            Storyboard.SetTargetProperty(colorAnimation, "Color");

            _shareToggleStoryboard = new Storyboard();
            _shareToggleStoryboard.Children.Add(thumbAnimation);
            _shareToggleStoryboard.Children.Add(colorAnimation);
            _shareToggleStoryboard.Begin();
        }

        private static Color ColorFromHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
            {
                return Colors.Transparent;
            }

            hex = hex.TrimStart('#');
            byte a = 255;
            int start = 0;
            if (hex.Length == 8)
            {
                a = Convert.ToByte(hex.Substring(0, 2), 16);
                start = 2;
            }

            var r = Convert.ToByte(hex.Substring(start, 2), 16);
            var g = Convert.ToByte(hex.Substring(start + 2, 2), 16);
            var b = Convert.ToByte(hex.Substring(start + 4, 2), 16);
            return Color.FromArgb(a, r, g, b);
        }

        private void AnimateShareBottomSheet(bool show)
        {
            if (ShareBottomSheetTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(280));
            animation.EasingFunction = new CircleEase();
            animation.To = show ? 0 : 366;

            Storyboard.SetTarget(animation, ShareBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, args) =>
                {
                    if (ShareBottomSheetPanel != null)
                    {
                        ShareBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }

                    _shareWithTimestamp = false;
                    ResetShareTimeToggleVisual(true);
                    RefreshShareUrlPreview();
                    HideOverlayIfNoPanelsOpen();
                };
            }

            storyboard.Begin();
        }

        private void ShareDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateShareBottomSheet(false);
            e.Handled = true;
        }

        private void ShareDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _shareInitialY = e.GetCurrentPoint(element).Position.Y;
                _shareInitialTransformY = ShareBottomSheetTransform != null ? ShareBottomSheetTransform.Y : 0;
                _shareIsDragging = true;
                e.Handled = true;
            }
        }

        private void ShareDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_shareIsDragging && ShareBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                double dragOffset = currentPoint.Position.Y - _shareInitialY;
                double newY = _shareInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= 366)
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
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (ShareBottomSheetTransform != null && ShareBottomSheetTransform.Y > 160)
                {
                    AnimateShareBottomSheet(false);
                }
                else
                {
                    AnimateShareBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private async Task ShowDescriptionBottomSheetAsync()
        {
            var item = CurrentShort;
            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return;
            }

            if (DescriptionTitleText != null)
            {
                DescriptionTitleText.Text = string.IsNullOrWhiteSpace(item.Title) ? "YouTube Shorts" : item.Title;
            }

            if (DescriptionChannelText != null)
            {
                DescriptionChannelText.Text = string.IsNullOrWhiteSpace(item.ChannelName) ? "YouTube" : item.ChannelName;
            }

            if (DescriptionBodyText != null)
            {
                DescriptionBodyText.Text = "Loading description...";
            }

            if (ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Visible;
            }

            if (DescriptionBottomSheetPanel != null)
            {
                DescriptionBottomSheetPanel.Visibility = Visibility.Visible;
            }

            AnimateDescriptionBottomSheet(true);

            var targetVideoId = item.VideoId;
            var description = await LoadShortDescriptionFromNextAsync(targetVideoId);
            var current = CurrentShort;
            if (current == null || !string.Equals(current.VideoId, targetVideoId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (DescriptionBodyText != null)
            {
                DescriptionBodyText.Text = string.IsNullOrWhiteSpace(description) ? "No description." : description;
            }
        }

        private async Task<string> LoadShortDescriptionFromNextAsync(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return string.Empty;
            }

            string cached;
            if (_descriptionCache.TryGetValue(videoId, out cached))
            {
                return cached;
            }

            string accessToken = string.Empty;
            try
            {
                accessToken = await GetTvAccessTokenAsync(false);
            }
            catch
            {
                accessToken = string.Empty;
            }

            var description = await TryLoadShortDescriptionFromNextClientAsync(videoId, accessToken, true);
            if (string.IsNullOrWhiteSpace(description))
            {
                description = await TryLoadShortDescriptionFromNextClientAsync(videoId, accessToken, false);
            }
            if (string.IsNullOrWhiteSpace(description))
            {
                description = await TryLoadShortDescriptionFromNextClientAsync(videoId, string.Empty, true);
            }
            if (string.IsNullOrWhiteSpace(description))
            {
                description = await TryLoadShortDescriptionFromNextClientAsync(videoId, string.Empty, false);
            }

            description = CleanShortDescription(description, videoId);
            _descriptionCache[videoId] = description ?? string.Empty;
            return _descriptionCache[videoId];
        }

        private async Task<string> TryLoadShortDescriptionFromNextClientAsync(string videoId, string accessToken, bool mobileWebClient)
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, BuildInnertubeUrl("next")))
                {
                    request.Content = new StringContent(
                        BuildAuthenticatedNextPayload(videoId, mobileWebClient),
                        Encoding.UTF8,
                        "application/json");

                    AddInnertubeAuthHeadersForClient(
                        request,
                        accessToken,
                        mobileWebClient ? InnertubeMwebClientHeaderName : InnertubeTvClientHeaderName,
                        mobileWebClient ? InnertubeMwebClientVersion : InnertubeTvClientVersion,
                        mobileWebClient ? InnertubeMwebUserAgent : InnertubeTvUserAgent);

                    var response = await _httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json))
                    {
                        System.Diagnostics.Debug.WriteLine("[Shorts] /next description failed: " + (int)response.StatusCode + " " + response.ReasonPhrase);
                        return string.Empty;
                    }

                    return ExtractShortDescriptionFromNextJson(json, videoId);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] /next description error: " + ex.Message);
                return string.Empty;
            }
        }

        private static string ExtractShortDescriptionFromNextJson(string json, string videoId)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return string.Empty;
            }

            try
            {
                var root = JsonValue.Parse(json);
                var candidates = new List<string>();
                CollectDescriptionCandidates(root, string.Empty, 0, candidates);
                var best = candidates
                    .Select(value => CleanShortDescription(value, videoId))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .OrderByDescending(value => value.Length)
                    .FirstOrDefault();
                return best ?? string.Empty;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Description JSON parse error: " + ex.Message);
                return string.Empty;
            }
        }

        private static void CollectDescriptionCandidates(IJsonValue value, string path, int depth, List<string> candidates)
        {
            if (value == null || candidates == null || depth > 80)
            {
                return;
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
                        var nextPath = string.IsNullOrWhiteSpace(path) ? lowerKey : path + "." + lowerKey;

                        if (IsDescriptionKey(lowerKey, nextPath))
                        {
                            var text = ExtractDescriptionText(pair.Value);
                            if (IsUsefulDescriptionCandidate(text))
                            {
                                candidates.Add(text);
                            }
                        }

                        CollectDescriptionCandidates(pair.Value, nextPath, depth + 1, candidates);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectDescriptionCandidates(array[(int)i], path, depth + 1, candidates);
                    }
                }
            }
            catch
            {
            }
        }

        private static bool IsDescriptionKey(string lowerKey, string lowerPath)
        {
            if (string.IsNullOrWhiteSpace(lowerKey))
            {
                return false;
            }

            if (lowerPath != null && (lowerPath.Contains("thumbnail") || lowerPath.Contains("endpoint") || lowerPath.Contains("command") || lowerPath.Contains("accessibility")))
            {
                return false;
            }

            return lowerKey == "shortdescription"
                || lowerKey == "description"
                || lowerKey == "descriptiontext"
                || lowerKey == "descriptionbodytext"
                || lowerKey == "attributeddescription"
                || lowerKey == "attributeddescriptionbodytext"
                || lowerKey == "attributeddescriptiontext"
                || lowerKey.Contains("descriptionbody")
                || lowerKey.Contains("attributeddescription");
        }

        private static string ExtractDescriptionText(IJsonValue value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            try
            {
                if (value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }

                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var direct = FirstNonEmpty(
                        ReadJsonString(obj, "content"),
                        ReadJsonString(obj, "simpleText"),
                        ReadJsonString(obj, "text"));
                    if (!string.IsNullOrWhiteSpace(direct))
                    {
                        return direct;
                    }

                    if (obj.ContainsKey("runs") && obj.GetNamedValue("runs").ValueType == JsonValueType.Array)
                    {
                        var runs = obj.GetNamedArray("runs");
                        var builder = new StringBuilder();
                        for (uint i = 0; i < runs.Count; i++)
                        {
                            var runText = ExtractTextFromAnyValue(runs[(int)i]);
                            if (!string.IsNullOrEmpty(runText))
                            {
                                builder.Append(runText);
                            }
                        }

                        return builder.ToString();
                    }

                    var nested = new StringBuilder();
                    foreach (var pair in obj)
                    {
                        var text = ExtractDescriptionText(pair.Value);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (nested.Length > 0)
                            {
                                nested.AppendLine();
                            }
                            nested.Append(text);
                        }
                    }
                    return nested.ToString();
                }

                if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    var builder = new StringBuilder();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        var text = ExtractDescriptionText(array[(int)i]);
                        if (!string.IsNullOrWhiteSpace(text))
                        {
                            if (builder.Length > 0)
                            {
                                builder.AppendLine();
                            }
                            builder.Append(text);
                        }
                    }
                    return builder.ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsUsefulDescriptionCandidate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim();
            if (normalized.Length < 3)
            {
                return false;
            }

            if (string.Equals(normalized, "Description", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string CleanShortDescription(string text, string videoId)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var result = text
                .Replace("\\r\\n", "\n")
                .Replace("\\n", "\n")
                .Replace("\r\n", "\n")
                .Replace("\r", "\n")
                .Trim();

            if (!string.IsNullOrWhiteSpace(videoId))
            {
                var escapedId = Regex.Escape(videoId.Trim());
                result = Regex.Replace(result, @"https?://(?:www\.)?(?:youtube\.com/(?:watch\?v=|shorts/)|youtu\.be/)" + escapedId + @"[^\s]*", string.Empty, RegexOptions.IgnoreCase);
            }

            result = Regex.Replace(result, @"https?://(?:www\.)?youtube\.com/shorts/[^\s]+", string.Empty, RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"https?://youtu\.be/[^\s]+", string.Empty, RegexOptions.IgnoreCase);
            result = Regex.Replace(result, @"[ \t]+\n", "\n");
            result = Regex.Replace(result, @"\n{3,}", "\n\n");
            return result.Trim();
        }

        private void AnimateDescriptionBottomSheet(bool show)
        {
            if (DescriptionBottomSheetTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(280));
            animation.EasingFunction = new CircleEase();
            animation.To = show ? 0 : 330;

            Storyboard.SetTarget(animation, DescriptionBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, args) =>
                {
                    if (DescriptionBottomSheetPanel != null)
                    {
                        DescriptionBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }

                    HideOverlayIfNoPanelsOpen();
                };
            }

            storyboard.Begin();
        }

        private void DescriptionDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
            e.Handled = true;
        }

        private void DescriptionDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _descriptionInitialY = e.GetCurrentPoint(element).Position.Y;
                _descriptionInitialTransformY = DescriptionBottomSheetTransform != null ? DescriptionBottomSheetTransform.Y : 0;
                _descriptionIsDragging = true;
                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_descriptionIsDragging && DescriptionBottomSheetTransform != null)
            {
                var element = sender as UIElement;
                var currentPoint = e.GetCurrentPoint(element);
                var dragOffset = currentPoint.Position.Y - _descriptionInitialY;
                var newY = _descriptionInitialTransformY + dragOffset;
                if (newY >= 0 && newY <= 330)
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
                var element = sender as UIElement;
                if (element != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                if (DescriptionBottomSheetTransform != null && DescriptionBottomSheetTransform.Y > 150)
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

        private void ShowCopiedPopup()
        {
            if (ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Visible;
            }

            if (CopiedPopupPanel != null)
            {
                CopiedPopupPanel.Visibility = Visibility.Visible;
            }
        }

        private void HideCopiedPopup()
        {
            if (CopiedPopupPanel != null)
            {
                CopiedPopupPanel.Visibility = Visibility.Collapsed;
            }

            HideOverlayIfNoPanelsOpen();
        }

        private void ShowQrPopup()
        {
            if (QrCodeImage != null)
            {
                var url = BuildCurrentShareUrl();
                var escaped = Uri.EscapeDataString(url);
                QrCodeImage.Source = new BitmapImage(new Uri("https://api.qrserver.com/v1/create-qr-code/?size=220x220&data=" + escaped));
            }

            if (ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Visible;
            }

            if (QrPopupPanel != null)
            {
                QrPopupPanel.Visibility = Visibility.Visible;
            }
        }

        private void HideQrPopup()
        {
            if (QrPopupPanel != null)
            {
                QrPopupPanel.Visibility = Visibility.Collapsed;
            }

            HideOverlayIfNoPanelsOpen();
        }

        private void ShortsOverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (CopiedPopupPanel != null && CopiedPopupPanel.Visibility == Visibility.Visible)
            {
                HideCopiedPopup();
            }

            if (QrPopupPanel != null && QrPopupPanel.Visibility == Visibility.Visible)
            {
                HideQrPopup();
            }

            AnimateCommentsBottomSheet(false);
            AnimateShareBottomSheet(false);
            AnimateDescriptionBottomSheet(false);
            e.Handled = true;
        }

        private void HideOverlayIfNoPanelsOpen()
        {
            bool commentsVisible = CommentsBottomSheetPanel != null && CommentsBottomSheetPanel.Visibility == Visibility.Visible;
            bool shareVisible = ShareBottomSheetPanel != null && ShareBottomSheetPanel.Visibility == Visibility.Visible;
            bool descriptionVisible = DescriptionBottomSheetPanel != null && DescriptionBottomSheetPanel.Visibility == Visibility.Visible;
            bool copiedVisible = CopiedPopupPanel != null && CopiedPopupPanel.Visibility == Visibility.Visible;
            bool qrVisible = QrPopupPanel != null && QrPopupPanel.Visibility == Visibility.Visible;

            if (!commentsVisible && !shareVisible && !descriptionVisible && !copiedVisible && !qrVisible && ShortsOverlayGrid != null)
            {
                ShortsOverlayGrid.Visibility = Visibility.Collapsed;
            }
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            var url = BuildCurrentShareUrl();
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(url);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            ShowCopiedPopup();
        }

        private void CopiedPopupOkButton_Click(object sender, RoutedEventArgs e)
        {
            HideCopiedPopup();
        }

        private void ShowQrButton_Click(object sender, RoutedEventArgs e)
        {
            ShowQrPopup();
        }

        private void QrPopupCloseButton_Click(object sender, RoutedEventArgs e)
        {
            HideQrPopup();
        }

        private void ShareViaSystemButton_Click(object sender, RoutedEventArgs e)
        {
            var item = CurrentShort;
            var url = BuildCurrentShareUrl();
            if (item == null || string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            var title = string.IsNullOrWhiteSpace(item.Title) ? "YouTube Shorts" : item.Title;

            var dataTransferManager = Windows.ApplicationModel.DataTransfer.DataTransferManager.GetForCurrentView();
            Windows.Foundation.TypedEventHandler<Windows.ApplicationModel.DataTransfer.DataTransferManager, Windows.ApplicationModel.DataTransfer.DataRequestedEventArgs> handler = null;
            handler = (shareSender, args) =>
            {
                shareSender.DataRequested -= handler;
                var request = args.Request;
                request.Data.Properties.Title = title;
                request.Data.Properties.Description = "Share this YouTube Short";
                request.Data.SetWebLink(new Uri(url));
            };
            dataTransferManager.DataRequested += handler;

            Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
        }

        private void Shorts_Loaded(object sender, RoutedEventArgs e)
        {
            AttachKeyboardShortcuts();
        }

        private void AttachKeyboardShortcuts()
        {
            if (_keyboardShortcutsAttached)
            {
                return;
            }

            try
            {
                var coreWindow = CoreWindow.GetForCurrentThread();
                if (coreWindow != null)
                {
                    coreWindow.KeyDown += Shorts_CoreWindow_KeyDown;
                    _keyboardShortcutsAttached = true;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Keyboard attach failed: " + ex.Message);
            }
        }

        private void DetachKeyboardShortcuts()
        {
            if (!_keyboardShortcutsAttached)
            {
                return;
            }

            try
            {
                var coreWindow = CoreWindow.GetForCurrentThread();
                if (coreWindow != null)
                {
                    coreWindow.KeyDown -= Shorts_CoreWindow_KeyDown;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Keyboard detach failed: " + ex.Message);
            }
            finally
            {
                _keyboardShortcutsAttached = false;
            }
        }

        private bool IsShortsModalOpen()
        {
            return ShortsOverlayGrid != null && ShortsOverlayGrid.Visibility == Visibility.Visible;
        }

        private async void Shorts_CoreWindow_KeyDown(CoreWindow sender, KeyEventArgs args)
        {
            try
            {
                if (IsShortsModalOpen())
                {
                    return;
                }

                if (args.VirtualKey == VirtualKey.Space)
                {
                    // Ignore auto-repeat so holding Space cannot rapidly flip play/pause.
                    if (args.KeyStatus.WasKeyDown)
                    {
                        return;
                    }

                    args.Handled = true;
                    if (ShortsPlayer != null)
                    {
                        ShortsPlayer.TogglePlayPause();
                    }
                    return;
                }

                if (args.VirtualKey != VirtualKey.Up && args.VirtualKey != VirtualKey.Down)
                {
                    return;
                }

                args.Handled = true;
                if (_isAnimating)
                {
                    return;
                }

                if (ShortsPlayer != null)
                {
                    ShortsPlayer.Pause();
                }

                if (args.VirtualKey == VirtualKey.Down)
                {
                    await ShowNextShortAsync();
                }
                else
                {
                    await ShowPreviousShortAsync();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Shorts] Keyboard shortcut failed: " + ex.Message);
            }
        }

        private double GetSwipeDistance()
        {
            if (ShortsRoot != null && ShortsRoot.ActualHeight > 0)
            {
                return ShortsRoot.ActualHeight;
            }

            return 640.0;
        }

        // Finishes whatever swipe animation is already running. Two storyboards animating the same
        // property is not just a visual conflict: the superseded one never raises Completed, so
        // its awaiter never resumes. The caller's finally block then never runs, _isAnimating
        // stays true, and every later touch is dropped — the page appears frozen.
        private void CompleteRunningSwipeAnimation()
        {
            var storyboard = _swipeStoryboard;
            var pending = _swipeAnimationTcs;
            _swipeStoryboard = null;
            _swipeAnimationTcs = null;

            if (storyboard != null)
            {
                try { storyboard.Stop(); }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine("[Shorts] Storyboard stop failed: " + ex.Message);
                }
            }

            if (pending != null)
            {
                pending.TrySetResult(true);
            }
        }

        private Task AnimateSwipeContentToAsync(double to, int milliseconds)
        {
            CompleteRunningSwipeAnimation();

            var tcs = new TaskCompletionSource<bool>();

            if (SwipeContentTransform == null)
            {
                tcs.SetResult(true);
                return tcs.Task;
            }

            var animation = new DoubleAnimation();
            animation.To = to;
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds));
            animation.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };

            Storyboard.SetTarget(animation, SwipeContentTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            storyboard.Completed += (s, args) =>
            {
                // Only the animation that is still the current one may commit its end value; a
                // superseded storyboard must not drag the transform back.
                if (_swipeStoryboard == storyboard)
                {
                    SwipeContentTransform.Y = to;
                    _swipeStoryboard = null;
                    _swipeAnimationTcs = null;
                }

                tcs.TrySetResult(true);
            };

            _swipeStoryboard = storyboard;
            _swipeAnimationTcs = tcs;
            storyboard.Begin();

            return tcs.Task;
        }

        private void SetLoading(bool isLoading)
        {
            LoadingOverlay.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = isLoading;
        }

        private void ShowMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                EmptyMessageText.Text = string.Empty;
                EmptyMessageText.Visibility = Visibility.Collapsed;
            }
            else
            {
                EmptyMessageText.Text = message;
                EmptyMessageText.Visibility = Visibility.Visible;
            }
        }

        private void Shorts_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachKeyboardShortcuts();

            if (ShortsPlayer != null)
            {
                ShortsPlayer.DisposePlayer();
            }
        }
    }
}
