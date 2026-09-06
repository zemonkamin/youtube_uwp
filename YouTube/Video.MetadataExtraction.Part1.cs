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

            // Authenticated MWEB may move the same action into a slim metadata renderer.
            try
            {
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    IJsonValue segmented;
                    if (!obj.TryGetValue("segmentedLikeDislikeButtonViewModel", out segmented)
                        && !obj.TryGetValue("segmentedLikeDislikeButtonRenderer", out segmented))
                        continue;

                    foreach (var nested in EnumerateObjects(segmented))
                    {
                        var text = FirstNonEmpty(
                            GetJsonString(nested, "title"),
                            GetJsonString(nested, "accessibilityText"),
                            ExtractTextFromField(nested, "defaultText", string.Empty));
                        if (!string.IsNullOrWhiteSpace(text)
                            && Regex.IsMatch(text, @"\d")
                            && text.IndexOf("dislike", StringComparison.OrdinalIgnoreCase) < 0
                            && text.IndexOf("не нравится", StringComparison.OrdinalIgnoreCase) < 0)
                            return text;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[LikeCount] MWEB fallback failed: " + ex.Message);
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

                // Mobile authenticated layouts use slimVideoMetadataRenderer/videoMetadataRenderer.
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    JsonObject metadata = null;
                    if (obj.ContainsKey("slimVideoMetadataRenderer"))
                        metadata = obj.GetNamedObject("slimVideoMetadataRenderer");
                    else if (obj.ContainsKey("videoMetadataRenderer"))
                        metadata = obj.GetNamedObject("videoMetadataRenderer");

                    if (metadata == null) continue;
                    var date = FirstNonEmpty(
                        ExtractTextFromField(metadata, "dateText", string.Empty),
                        ExtractTextFromField(metadata, "publishDateText", string.Empty),
                        ExtractTextFromField(metadata, "publishedTimeText", string.Empty),
                        ExtractTextFromField(metadata, "uploadDateText", string.Empty));
                    if (!string.IsNullOrWhiteSpace(date)) return date;
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

            // MWEB owner blocks use slimOwnerRenderer/channelThumbnailWithLinkRenderer.
            try
            {
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    JsonObject owner = null;
                    if (obj.ContainsKey("videoOwnerRenderer"))
                        owner = obj.GetNamedObject("videoOwnerRenderer");
                    else if (obj.ContainsKey("slimOwnerRenderer"))
                        owner = obj.GetNamedObject("slimOwnerRenderer");
                    else if (obj.ContainsKey("channelThumbnailWithLinkRenderer"))
                        owner = obj.GetNamedObject("channelThumbnailWithLinkRenderer");

                    if (owner == null) continue;
                    var url = ExtractChannelThumbnailFromRenderer(owner);
                    if (string.IsNullOrWhiteSpace(url))
                        url = ExtractBestImageUrl(owner);
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ChannelAvatar] MWEB fallback failed: " + ex.Message);
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

            try
            {
                foreach (var obj in EnumerateObjects(nextRoot))
                {
                    if (!obj.ContainsKey("subscriberCountText")) continue;
                    var count = ExtractTextFromField(obj, "subscriberCountText", string.Empty);
                    if (!string.IsNullOrWhiteSpace(count)) return count;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SubscriberCount] MWEB fallback failed: " + ex.Message);
            }

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

    }
}
