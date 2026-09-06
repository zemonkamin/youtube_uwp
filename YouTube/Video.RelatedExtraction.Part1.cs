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

    }
}
