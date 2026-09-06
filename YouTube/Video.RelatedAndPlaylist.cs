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
    public sealed partial class Video
    {
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
                else if (obj.ContainsKey("tileRenderer"))
                {
                    // Authenticated TVHTML5 /next uses tileRenderer for its personalized
                    // recommendation shelves. These cards carry the watch target in
                    // onSelectCommand instead of navigationEndpoint.
                    var tile = obj.GetNamedObject("tileRenderer");
                    var endpoint = GetTileWatchEndpoint(tile) ?? new JsonObject();
                    var endpointPlaylistId = GetJsonString(endpoint, "playlistId");
                    if (string.IsNullOrWhiteSpace(currentPlaylistId)
                        || !string.Equals(endpointPlaylistId, currentPlaylistId,
                            StringComparison.Ordinal))
                    {
                        videoData = BuildQueueItemFromTile(tile, endpoint);
                    }
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

        private async void CustomVideoPlayer_PlaybackRecoveryRequested(object sender, object e)
        {
            if (_offlineMode || _playbackRecoveryInProgress || _qualityChangeInProgress
                || string.IsNullOrWhiteSpace(currentVideoId) || CustomVideoPlayer == null)
            {
                return;
            }

            // A dead CDN host can emit several state changes while the replacement is opening.
            // Keep those events from starting overlapping MediaPlayer source swaps.
            if ((DateTime.UtcNow - _lastPlaybackRecoveryUtc) < TimeSpan.FromSeconds(30))
            {
                System.Diagnostics.Debug.WriteLine("[Video] Playback URL recovery suppressed by cooldown");
                return;
            }

            _playbackRecoveryInProgress = true;
            _lastPlaybackRecoveryUtc = DateTime.UtcNow;
            var recoveryVideoId = currentVideoId;
            var resumePosition = CustomVideoPlayer.CurrentPlaybackPosition;
            var actualHeight = _readyHeight;

            try
            {
                if (actualHeight > 0)
                {
                    _playbackRecoveryQualityOverride = actualHeight.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Refreshing stalled playback URLs at "
                    + resumePosition.TotalSeconds.ToString("F1",
                        System.Globalization.CultureInfo.InvariantCulture)
                    + "s, quality="
                    + (actualHeight > 0 ? actualHeight + "p" : "current"));

                CustomVideoPlayer.PrepareResumeAfterSourceReload(resumePosition, true);
                CustomVideoPlayer.BeginSourceLoading();
                InvalidatePlaybackUrlCaches(recoveryVideoId);

                var freshResponse = await PostInnertubeJsonAsync(
                    "player", BuildPlayerPayload(recoveryVideoId));
                if (!IsStillCurrentVideo(recoveryVideoId))
                    return;

                _lastPlayerJson = freshResponse.Text;
                RememberParsedPlayerRoot(freshResponse.Text, freshResponse.Root);
                CaptureVisitorData(freshResponse.Root);
                await BindPlayerSourceAsync(freshResponse.Root, false);

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Stalled playback reopened with fresh URLs at the saved position");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Playback URL recovery failed: " + ex.Message);
                if (CustomVideoPlayer != null)
                    CustomVideoPlayer.EndSourceLoading();
            }
            finally
            {
                _playbackRecoveryQualityOverride = string.Empty;
                _playbackRecoveryInProgress = false;
            }
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

    }
}
