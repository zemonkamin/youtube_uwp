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
        private async Task LoadVideoDetailsAsync(string videoId)
        {
            Task<InnertubeRequestCoordinator.JsonResponse> playerTask = null;
            Task<WatchNextResponse> nextTask = null;
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
                playerTask = PostInnertubeJsonAsync("player", BuildPlayerPayload(videoId));

                var playerResponse = await playerTask;
                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                _lastPlayerJson = playerResponse.Text;
                var playerRoot = playerResponse.Root;
                RememberParsedPlayerRoot(playerResponse.Text, playerRoot);
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
                nextTask = GetWatchNextResponseAsync(videoId, currentPlaylistId);
                // The Android response is only a quality-menu warmup. On a phone it consumed a
                // full extra request immediately after playback became ready; fetch it lazily if
                // the user actually opens quality settings.
                warmupAndroidPlayerTask = ResponsiveLayout.IsPhoneDevice
                    ? null
                    : WarmUpAndroidPlayerAsync(videoId);
                sideDataTask = LoadSideDataAsync(videoId, nextTask);

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
                await ObserveBackgroundTaskAsync(warmupAndroidPlayerTask, "Android player warmup after main load failure");
            }
        }

        private async Task LoadSideDataAsync(
            string videoId,
            Task<WatchNextResponse> nextTask)
        {
            Task<List<RelatedVideoCardItem>> relatedVideosTask = null;
            Task relatedApplyTask = null;
            Task<List<CommentItem>> commentsTask = null;
            Task ratingTask = null;
            Task subscriptionTask = null;

            try
            {
                var nextResponse = await nextTask;
                var nextRoot = nextResponse.Response.Root;
                var authenticatedNextRoot = nextResponse.AuthenticatedResponse != null
                    ? nextResponse.AuthenticatedResponse.Root
                    : null;
                var personalizedNextRoot = authenticatedNextRoot ?? nextRoot;
                if (!IsStillCurrentVideo(videoId))
                {
                    return;
                }

                ApplyNextMetadata(nextRoot);

                // Related cards are already in this parsed /next. Start their CPU extraction and
                // binding before comments open a large continuation response or a playlist asks
                // for an authenticated queue. This makes the visible lower half fill much sooner.
                relatedVideosTask = ExtractRelatedVideosFromNextRootAsync(personalizedNextRoot);
                relatedApplyTask = ApplyRelatedVideosWhenReadyAsync(videoId, relatedVideosTask);
                commentsTask = LoadCommentsFromNextSafeAsync(
                    videoId, nextRoot, nextResponse.PageIsAuthenticatedMweb);

                // Watch queue for the current playlist / mix (jam), if we were opened with one.
                // Mixes are personalized, so the queue is refetched signed in; without that the
                // anonymous /next above answers with a generic mix built from the seed video only.
                // The primary TV response already contains the personalized queue. Do not issue
                // the old second authenticated /next just for playlist data.
                if (nextResponse.IsTvLayout && authenticatedNextRoot != null)
                    ExtractPlaylistQueueFromNext(null, authenticatedNextRoot);
                else if (authenticatedNextRoot != null)
                    ExtractPlaylistQueueFromNext(authenticatedNextRoot, null);
                else
                    ExtractPlaylistQueueFromNext(nextRoot, null);
                ApplyPlaylistQueueUi();

                // These may do authenticated calls. Start both immediately; Config.RefreshAccessTokenAsync
                // already serializes token refreshes so duplicate OAuth calls are avoided.
                ratingTask = LoadUserVideoRatingAsync(
                    videoId, authenticatedNextRoot ?? nextRoot,
                    authenticatedNextRoot != null || nextResponse.PageIsAuthenticatedMweb);
                subscriptionTask = LoadChannelSubscriptionStateAsync(
                    videoId, authenticatedNextRoot ?? nextRoot,
                    authenticatedNextRoot != null || nextResponse.PageIsAuthenticatedMweb);

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] /next side data failed: " + ex.Message);
                if (commentsTask == null)
                    commentsTask = LoadCommentsSafeAsync(videoId);
                if (relatedVideosTask == null)
                    relatedVideosTask = LoadRelatedVideosSafeAsync(videoId);
            }

            // Bind comments and related independently so a slow comments request cannot hold back related videos.
            var commentsApplyTask = ApplyCommentsWhenReadyAsync(videoId, commentsTask);
            if (relatedApplyTask == null)
                relatedApplyTask = ApplyRelatedVideosWhenReadyAsync(videoId, relatedVideosTask);
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

        private async Task<List<CommentItem>> LoadCommentsFromNextSafeAsync(
            string videoId,
            JsonObject nextRoot,
            bool authenticatedMwebContext)
        {
            try
            {
                var comments = await Config.GetCommentsFromNextAsync(
                        videoId, nextRoot, authenticatedMwebContext)
                    .ConfigureAwait(false);
                return comments ?? new List<CommentItem>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Shared comments loading failed: " + ex.Message);
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
            if (CustomVideoPlayer != null)
                CustomVideoPlayer.SetRelatedVideos(null);

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
            _currentRelatedVideos = new List<RelatedVideoCardItem>();
            _relatedFallbackVisible = true;
            ApplyRelatedContentForOrientation(IsCurrentViewPortrait());
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

                YouTube.Discord.DiscordPresenceService.SetVideo(
                    currentVideoId,
                    VideoTitleText != null ? VideoTitleText.Text : string.Empty,
                    currentChannelName,
                    _currentVideoThumbnailUrl);

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
            if (CustomVideoPlayer != null)
                CustomVideoPlayer.SetRelatedVideos(relatedVideos);

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

        private async void CustomVideoPlayer_RelatedVideoRequested(
            object sender,
            CustomVideoPlayer.FullscreenRelatedVideoRequestedEventArgs e)
        {
            if (e == null || string.IsNullOrWhiteSpace(e.VideoId))
                return;

            // SwitchToVideoAsync expects the player back in its page host. Exit the Popup first;
            // the selected related video then follows the same complete reset/load path as a page card.
            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
                CustomVideoPlayer.ToggleFullscreen();

            await SwitchToVideoAsync(e.VideoId, e.PlaylistId, null);
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

            UpdateLandscapeDetailsVisibility(IsCurrentViewPortrait());
        }

    }
}
