using System;
using System.Collections.Generic;
using System.Net.Http;
using Windows.Data.Json;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;

namespace YouTube
{
    // Client for the community SponsorBlock database (https://sponsor.ajay.app), used to skip
    // sponsor reads and other non-content segments automatically.
    //
    // Queries go through the hash-prefix endpoint: only the first four hex characters of the
    // video id's SHA-256 are sent, so the server never learns which video is being watched. The
    // answer covers every video sharing that prefix and is filtered locally.
    public static class SponsorBlock
    {
        public const string ShowTimelineMarkersSettingKey = "ShowSponsorBlockTimelineMarkers";
        public static event Action TimelineMarkerVisibilityChanged;

        // Marker visibility is independent from automatic skipping. It defaults to enabled and
        // the Settings switch only hides the coloured ranges on the seek bar.
        public static bool AreTimelineMarkersEnabled()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(
                    ShowTimelineMarkersSettingKey, out raw) || raw == null)
                {
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

        public static void SetTimelineMarkersEnabled(bool enabled)
        {
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    ShowTimelineMarkersSettingKey] = enabled;
            }
            catch
            {
            }

            var changed = TimelineMarkerVisibilityChanged;
            if (changed != null)
            {
                changed();
            }
        }

        public sealed class Segment
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
            public string Category { get; set; }
        }

        private static readonly HttpClient _http = new HttpClient();

        // Categories worth skipping without asking. "sponsor" is paid promotion, "selfpromo" is
        // the author's own merch/Patreon plug, "interaction" is the "like and subscribe" ask.
        // Intro/outro and offtopic music are deliberately left out: those are part of the video.
        private const string Categories = "[\"sponsor\",\"selfpromo\",\"interaction\"]";

        private const string ApiBase = "https://sponsor.ajay.app/api/skipSegments/";

        private static string HashPrefix(string videoId)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var input = CryptographicBuffer.ConvertStringToBinary(videoId, BinaryStringEncoding.Utf8);
            var hashed = provider.HashData(input);
            var hex = CryptographicBuffer.EncodeToHexString(hashed);
            return hex.Substring(0, 4).ToLowerInvariant();
        }

        // Returns the skippable segments for the video, or an empty list when there are none, the
        // service is unreachable, or anything about the answer is unexpected. Never throws: a
        // missing SponsorBlock answer must not disturb playback.
        public static async System.Threading.Tasks.Task<List<Segment>> GetSegmentsAsync(string videoId)
        {
            var result = new List<Segment>();

            if (string.IsNullOrWhiteSpace(videoId))
            {
                return result;
            }

            try
            {
                var url = ApiBase + HashPrefix(videoId) + "?categories=" + Uri.EscapeDataString(Categories);
                var response = await _http.GetAsync(new Uri(url));

                if (!response.IsSuccessStatusCode)
                {
                    // 404 simply means no video with this prefix has submitted segments.
                    System.Diagnostics.Debug.WriteLine(
                        "[SponsorBlock] Lookup returned " + (int)response.StatusCode);
                    return result;
                }

                var body = await response.Content.ReadAsStringAsync();

                JsonArray videos;
                if (!JsonArray.TryParse(body, out videos))
                {
                    return result;
                }

                foreach (var videoValue in videos)
                {
                    var video = videoValue.GetObject();
                    if (!video.ContainsKey("videoID")
                        || !string.Equals(video.GetNamedString("videoID"), videoId, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!video.ContainsKey("segments"))
                    {
                        continue;
                    }

                    foreach (var segmentValue in video.GetNamedArray("segments"))
                    {
                        var segment = ParseSegment(segmentValue.GetObject());
                        if (segment != null)
                        {
                            result.Add(segment);
                        }
                    }
                }

                result.Sort((a, b) => a.Start.CompareTo(b.Start));
                System.Diagnostics.Debug.WriteLine(
                    "[SponsorBlock] " + result.Count + " skippable segment(s) for " + videoId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SponsorBlock] Lookup failed: " + ex.Message);
            }

            return result;
        }

        private static Segment ParseSegment(JsonObject segment)
        {
            try
            {
                // "mute" and "full" entries are not skips — only "skip" means "jump over this".
                if (segment.ContainsKey("actionType")
                    && !string.Equals(segment.GetNamedString("actionType"), "skip", StringComparison.Ordinal))
                {
                    return null;
                }

                var bounds = segment.GetNamedArray("segment");
                if (bounds.Count < 2)
                {
                    return null;
                }

                var start = bounds[0].GetNumber();
                var end = bounds[1].GetNumber();

                // A zero-length or inverted range would make the skip loop on itself.
                if (end - start < 0.5)
                {
                    return null;
                }

                return new Segment
                {
                    Start = TimeSpan.FromSeconds(start),
                    End = TimeSpan.FromSeconds(end),
                    Category = segment.ContainsKey("category") ? segment.GetNamedString("category") : "sponsor"
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[SponsorBlock] Bad segment: " + ex.Message);
                return null;
            }
        }
    }
}
