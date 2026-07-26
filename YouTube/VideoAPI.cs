using System;
using System.Collections.Generic;
using System.Text;
using Windows.Data.Json;

namespace YouTube
{
    public static class VideoParser
    {
        public static VideoDetails ParseVideoDetails(string json, string videoId)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return null;

                var root = JsonObject.Parse(json);
                var details = new VideoDetails
                {
                    VideoId = videoId,
                    Comments = new List<CommentItem>(),
                    RelatedVideos = new List<VideoCardItem>()
                };

                // Try to extract from twoColumnWatchNextResults
                if (!root.ContainsKey("contents")) return details;

                var contents = root.GetNamedObject("contents");
                if (!contents.ContainsKey("twoColumnWatchNextResults")) return details;

                var twoColumn = contents.GetNamedObject("twoColumnWatchNextResults");
                
                // Extract video info from results
                if (twoColumn.ContainsKey("results"))
                {
                    var results = twoColumn.GetNamedObject("results");
                    if (results.ContainsKey("results"))
                    {
                        var resultsRenderer = results.GetNamedObject("results");
                        ExtractVideoInfoFromRenderer(resultsRenderer, details);
                    }
                }

                // Extract comments continuation token
                if (twoColumn.ContainsKey("engagementPanels"))
                {
                    var panels = twoColumn.GetNamedArray("engagementPanels");
                    details.CommentsContinuationToken = FindCommentsContinuation(panels);
                }

                // Extract related videos
                if (twoColumn.ContainsKey("secondaryResults"))
                {
                    var secondaryResults = twoColumn.GetNamedObject("secondaryResults");
                    ExtractRelatedVideos(secondaryResults, details.RelatedVideos);
                }

                return details;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ParseVideoDetails] Error: {ex.Message}");
                return null;
            }
        }

        private static void ExtractVideoInfoFromRenderer(JsonObject resultsRenderer, VideoDetails details)
        {
            if (!resultsRenderer.ContainsKey("contents")) return;

            var contents = resultsRenderer.GetNamedArray("contents");
            
            foreach (var contentToken in contents)
            {
                if (contentToken.ValueType != JsonValueType.Object) continue;
                
                var content = contentToken.GetObject();
                
                // Video metadata
                if (content.ContainsKey("videoMetadataRenderer"))
                {
                    var metadata = content.GetNamedObject("videoMetadataRenderer");
                    details.Title = ExtractTextFromField(metadata, "title", "Unknown");
                    details.Description = ExtractTextFromField(metadata, "description", "");
                    
                    if (metadata.ContainsKey("publishDate"))
                    {
                        details.PublishedAt = metadata.GetNamedString("publishDate", "");
                    }
                }
                
                // Video primary info (views, likes)
                if (content.ContainsKey("videoPrimaryInfoRenderer"))
                {
                    var primaryInfo = content.GetNamedObject("videoPrimaryInfoRenderer");
                    
                    if (primaryInfo.ContainsKey("viewCount"))
                    {
                        details.ViewCount = ExtractTextFromField(primaryInfo, "viewCount", "0");
                    }
                }
                
                // Video secondary info (channel, subscribers)
                if (content.ContainsKey("videoSecondaryInfoRenderer"))
                {
                    var secondaryInfo = content.GetNamedObject("videoSecondaryInfoRenderer");
                    
                    if (secondaryInfo.ContainsKey("owner"))
                    {
                        var owner = secondaryInfo.GetNamedObject("owner");
                        if (owner.ContainsKey("videoOwnerRenderer"))
                        {
                            var ownerRenderer = owner.GetNamedObject("videoOwnerRenderer");
                            details.ChannelName = ExtractTextFromField(ownerRenderer, "title", "Unknown");
                            details.ChannelThumbnail = ExtractThumbnailUrl(ownerRenderer, "thumbnail");
                            
                            if (ownerRenderer.ContainsKey("navigationEndpoint"))
                            {
                                var navEndpoint = ownerRenderer.GetNamedObject("navigationEndpoint");
                                if (navEndpoint.ContainsKey("browseEndpoint"))
                                {
                                    details.ChannelId = navEndpoint.GetNamedObject("browseEndpoint").GetNamedString("browseId", "");
                                }
                            }
                        }
                    }
                }
            }
        }

        private static string FindCommentsContinuation(JsonArray panels)
        {
            foreach (var panelToken in panels)
            {
                if (panelToken.ValueType != JsonValueType.Object) continue;
                
                var panel = panelToken.GetObject();
                if (!panel.ContainsKey("engagementPanelSectionListRenderer")) continue;
                
                var sectionList = panel.GetNamedObject("engagementPanelSectionListRenderer");
                if (sectionList.GetNamedString("panelIdentifier", "") != "engagement-panel-comments-section") continue;
                
                if (sectionList.ContainsKey("content"))
                {
                    var content = sectionList.GetNamedObject("content");
                    return FindContinuationTokenInObject(content);
                }
            }
            
            return null;
        }

        private static string FindContinuationTokenInObject(JsonObject obj)
        {
            foreach (var item in obj)
            {
                if (item.Value.ValueType == JsonValueType.Object)
                {
                    var childObj = item.Value.GetObject();
                    
                    if (childObj.ContainsKey("continuationItemRenderer"))
                    {
                        var continuationItem = childObj.GetNamedObject("continuationItemRenderer");
                        if (continuationItem.ContainsKey("continuationEndpoint"))
                        {
                            var endpoint = continuationItem.GetNamedObject("continuationEndpoint");
                            if (endpoint.ContainsKey("continuationCommand"))
                            {
                                return endpoint.GetNamedObject("continuationCommand").GetNamedString("token", "");
                            }
                        }
                    }
                    
                    var token = FindContinuationTokenInObject(childObj);
                    if (!string.IsNullOrEmpty(token)) return token;
                }
                else if (item.Value.ValueType == JsonValueType.Array)
                {
                    var array = item.Value.GetArray();
                    foreach (var arrayItem in array)
                    {
                        if (arrayItem.ValueType == JsonValueType.Object)
                        {
                            var token = FindContinuationTokenInObject(arrayItem.GetObject());
                            if (!string.IsNullOrEmpty(token)) return token;
                        }
                    }
                }
            }
            
            return null;
        }

        private static void ExtractRelatedVideos(JsonObject secondaryResults, List<VideoCardItem> relatedVideos)
        {
            if (!secondaryResults.ContainsKey("secondaryVideoResults")) return;
            
            var videoResults = secondaryResults.GetNamedObject("secondaryVideoResults");
            if (!videoResults.ContainsKey("contents")) return;
            
            var contents = videoResults.GetNamedArray("contents");
            
            foreach (var contentToken in contents)
            {
                if (contentToken.ValueType != JsonValueType.Object) continue;
                
                var content = contentToken.GetObject();
                
                if (content.ContainsKey("compactVideoRenderer"))
                {
                    var compactVideo = content.GetNamedObject("compactVideoRenderer");
                    var video = ParseCompactVideoRenderer(compactVideo);
                    if (video != null)
                    {
                        relatedVideos.Add(video);
                    }
                }
            }
        }

        private static VideoCardItem ParseCompactVideoRenderer(JsonObject renderer)
        {
            var videoId = renderer.GetNamedString("videoId", "");
            if (string.IsNullOrEmpty(videoId)) return null;
            
            return new VideoCardItem
            {
                VideoId = videoId,
                Title = ExtractTextFromField(renderer, "title", "Unknown"),
                ChannelTitle = ExtractTextFromField(renderer, "longBylineText", "Unknown"),
                Duration = ExtractTextFromField(renderer, "lengthText", ""),
                ThumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/mqdefault.jpg"
            };
        }

        private static string ExtractTextFromField(JsonObject obj, string fieldName, string fallback)
        {
            if (obj == null || !obj.ContainsKey(fieldName)) return fallback;
            
            var field = obj.GetNamedValue(fieldName);
            if (field.ValueType != JsonValueType.Object) return fallback;
            
            var fieldObj = field.GetObject();
            
            if (fieldObj.ContainsKey("simpleText"))
            {
                return fieldObj.GetNamedString("simpleText", fallback);
            }
            
            if (fieldObj.ContainsKey("runs"))
            {
                var runs = fieldObj.GetNamedArray("runs");
                var sb = new StringBuilder();
                foreach (var runToken in runs)
                {
                    var run = runToken.GetObject();
                    sb.Append(run.GetNamedString("text", ""));
                }
                return sb.ToString();
            }
            
            return fallback;
        }

        private static string ExtractThumbnailUrl(JsonObject obj, string fieldName)
        {
            if (obj == null || !obj.ContainsKey(fieldName)) return "";
            
            var photoObj = obj.GetNamedObject(fieldName);
            if (photoObj.ContainsKey("thumbnails"))
            {
                var thumbnails = photoObj.GetNamedArray("thumbnails");
                if (thumbnails.Count > 0)
                {
                    return thumbnails[0].GetObject().GetNamedString("url", "");
                }
            }
            
            return "";
        }

        public static List<CommentItem> ParseComments(string json)
        {
            var comments = new List<CommentItem>();
            
            try
            {
                if (string.IsNullOrWhiteSpace(json)) return comments;
                
                var root = JsonValue.Parse(json);
                WalkForComments(root, comments);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ParseComments] Error: {ex.Message}");
            }
            
            return comments;
        }

        private static void WalkForComments(Windows.Data.Json.IJsonValue value, List<CommentItem> comments)
        {
            if (value == null) return;
            
            if (value.ValueType == JsonValueType.Object)
            {
                var obj = value.GetObject();
                
                // Check for commentEntityPayload (new format)
                if (obj.ContainsKey("commentEntityPayload"))
                {
                    var comment = ParseCommentEntityPayload(obj.GetNamedObject("commentEntityPayload"));
                    if (comment != null)
                    {
                        comments.Add(comment);
                    }
                }
                
                // Recurse
                foreach (var item in obj)
                {
                    WalkForComments(item.Value, comments);
                }
            }
            else if (value.ValueType == JsonValueType.Array)
            {
                var array = value.GetArray();
                foreach (var item in array)
                {
                    WalkForComments(item, comments);
                }
            }
        }

        private static CommentItem ParseCommentEntityPayload(JsonObject payload)
        {
            try
            {
                var authorData = payload.GetNamedObject("author", null);
                var props = payload.GetNamedObject("properties", null);
                
                if (authorData == null || props == null) return null;
                
                var displayName = authorData.GetNamedString("displayName", "").Trim();
                var author = string.IsNullOrEmpty(displayName) ? "Unknown" : 
                             (displayName.StartsWith("@") ? displayName : $"@{displayName}");
                
                var content = props.GetNamedObject("content", null);
                var text = "";
                if (content != null)
                {
                    if (content.ContainsKey("content"))
                    {
                        text = content.GetNamedString("content", "");
                    }
                    else if (content.ContainsKey("runs"))
                    {
                        var runs = content.GetNamedArray("runs");
                        var sb = new StringBuilder();
                        foreach (var runToken in runs)
                        {
                            sb.Append(runToken.GetObject().GetNamedString("text", ""));
                        }
                        text = sb.ToString();
                    }
                }
                
                if (string.IsNullOrWhiteSpace(text)) return null;
                
                var publishedTime = props.GetNamedString("publishedTime", "unknown");
                
                var avatarSources = payload.GetNamedObject("avatar", null)?
                    .GetNamedObject("image", null)?
                    .GetNamedArray("sources", null);
                
                var thumbnail = "";
                if (avatarSources != null && avatarSources.Count > 0)
                {
                    thumbnail = avatarSources[0].GetObject().GetNamedString("url", "");
                }
                
                return new CommentItem
                {
                    Author = author,
                    Text = text.Trim(),
                    PublishedAt = publishedTime,
                    AuthorThumbnail = thumbnail
                };
            }
            catch
            {
                return null;
            }
        }
    }
}
