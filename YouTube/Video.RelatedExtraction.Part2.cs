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

    }
}
