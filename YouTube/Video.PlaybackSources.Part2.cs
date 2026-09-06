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

    }
}
