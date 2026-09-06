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
        private static string ExtractHlsManifestUrl(JsonObject root)
        {
            if (root == null || !root.ContainsKey("streamingData"))
            {
                return string.Empty;
            }

            var streamingData = root.GetNamedObject("streamingData");
            return GetJsonString(streamingData, "hlsManifestUrl");
        }

        private PlayerFormatModel SelectPreferredFormat()
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Total available formats: {availableFormats.Count}"
            );
            return SelectPreferredProgressiveFormat(availableFormats, GetEffectiveVideoQualityTag(), false);
        }

        // storyboards.playerStoryboardSpecRenderer.spec — the sprite-sheet scrubbing frames.
        private static string ExtractStoryboardSpec(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("storyboards"))
                {
                    return string.Empty;
                }

                var storyboards = root.GetNamedObject("storyboards");
                if (!storyboards.ContainsKey("playerStoryboardSpecRenderer"))
                {
                    return string.Empty;
                }

                var renderer = storyboards.GetNamedObject("playerStoryboardSpecRenderer");
                return GetJsonString(renderer, "spec");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Storyboard spec extract failed: " + ex.Message);
                return string.Empty;
            }
        }

        private string GetEffectiveVideoQualityTag()
        {
            // During URL recovery Auto must reopen the exact tier that was actually playing,
            // rather than running screen/network selection again and silently changing quality.
            if (!string.IsNullOrWhiteSpace(_playbackRecoveryQualityOverride))
            {
                return _playbackRecoveryQualityOverride;
            }

            var explicitQuality = NormalizeQualityTag(currentQualityTag);
            if (!string.IsNullOrWhiteSpace(explicitQuality))
            {
                return explicitQuality;
            }

            // The override is empty. That means one of two things, and they must behave
            // differently: if the user actively chose "Auto" in this session it is a real Auto
            // (device-capped adaptive pick); otherwise the video just opened and should inherit the Settings
            // default. Without this distinction, picking Auto would bounce straight back to the
            // preferred quality and be impossible to select.
            if (_qualityExplicitlyChosen)
            {
                return string.Empty;
            }

            var preferredRaw = GetPreferredVideoQualitySetting();
            if (string.Equals(preferredRaw, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Using preferred video quality from Settings: Auto");
                return string.Empty;
            }

            var preferredQuality = NormalizeQualityTag(preferredRaw);
            if (!string.IsNullOrWhiteSpace(preferredQuality))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Using preferred video quality from Settings: "
                    + preferredQuality + "p");
                return preferredQuality;
            }

            // Invalid/corrupt setting never means "pick maximum"; fall back deterministically.
            try
            {
                ApplicationData.Current.LocalSettings.Values[
                    PreferredVideoQualitySettingKey] = "Auto";
            }
            catch
            {
            }
            return string.Empty;
        }

        private static string GetPreferredVideoQualitySetting()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;

                // There is exactly one source of truth for video quality. Older builds looked at
                // several legacy keys; a stale "1080" in one of them could unexpectedly override
                // what Settings actually showed.
                if (!values.ContainsKey(PreferredVideoQualitySettingKey))
                {
                    values[PreferredVideoQualitySettingKey] = "Auto";
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Preferred video quality was unset; initialized to Auto");
                    return "Auto";
                }

                var raw = values[PreferredVideoQualitySettingKey];
                var value = raw != null ? raw.ToString() : string.Empty;

                if (string.IsNullOrWhiteSpace(value))
                {
                    values[PreferredVideoQualitySettingKey] = "Auto";
                    return "Auto";
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Preferred video quality: " + value);
                return value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] Preferred quality read failed; using Auto: " + ex.Message);
                return "Auto";
            }
        }

        private static PlayerFormatModel SelectPreferredAudioOnlyTrackFormat(
            IList<PlayerFormatModel> formats
        )
        {
            if (formats == null)
            {
                return null;
            }

            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (!format.HasAudio || format.HasVideo)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();

                // Windows 10 Mobile is much happier with AAC in MP4 than Opus/WebM.
                if (mime.IndexOf("audio/mp4") < 0 && mime.IndexOf("mp4a") < 0)
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = new[] { 140, 139, 141 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            return candidates[0];
        }

        private static PlayerFormatModel SelectPreferredMobileAudioCarrierFormat(
            IList<PlayerFormatModel> formats
        )
        {
            if (formats == null)
            {
                return null;
            }

            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (format.IsAdaptive || !format.HasAudio || !format.HasVideo)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();

                // For Windows 10 Mobile the second stream is only an audio carrier.
                // Prefer tiny 3GP/H.263 carriers first: they are much cheaper than
                // decoding another MP4/H.264 360p stream while the visible video is high-res.
                if (
                    mime.IndexOf("video/3gpp") < 0
                    && mime.IndexOf("video/mp4") < 0
                    && mime.IndexOf("3gp") < 0
                )
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = new[] { 18, 59, 22, 17, 36 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            PlayerFormatModel best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (best == null)
                {
                    best = current;
                    continue;
                }

                var bestHeight = best.QualityTier > 0 ? best.QualityTier : 9999;
                var currentHeight = current.QualityTier > 0 ? current.QualityTier : 9999;
                if (currentHeight < bestHeight)
                {
                    best = current;
                }
            }

            return best;
        }

        private static PlayerFormatModel SelectItag18ProgressiveFormat(IList<PlayerFormatModel> formats)
        {
            if (formats == null)
            {
                return null;
            }

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (format.Itag == 18 && IsProgressiveMp4(format) && format.HasAudio && format.HasVideo)
                {
                    return format;
                }
            }

            return null;
        }

        private static PlayerFormatModel SelectPreferredH264VideoOnlyFormat(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag,
            ISet<int> excludedItags = null
        )
        {
            if (formats == null)
            {
                return null;
            }

            var requestedHeight = ParseInt(requestedQualityTag);
            var candidates = new List<PlayerFormatModel>();
            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (excludedItags != null && excludedItags.Contains(format.Itag))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (!format.IsAdaptive || !format.HasVideo || format.HasAudio)
                {
                    continue;
                }

                var mime = string.IsNullOrWhiteSpace(format.MimeType)
                    ? string.Empty
                    : format.MimeType.ToLowerInvariant();
                if (mime.IndexOf("video/mp4") < 0 || mime.IndexOf("avc1") < 0)
                {
                    continue;
                }

                // As in youtube-ios chooseVideo:maxHeight:, a requested height is a ceiling.
                // This also lets Auto retry the next lower tier after a decoder failure.
                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                candidates.Add(format);
            }

            if (candidates.Count == 0)
            {
                return null;
            }

            var preferredItags = GetPreferredH264VideoOnlyItags(requestedHeight);
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (candidates[i].Itag == preferredItags[p])
                    {
                        return candidates[i];
                    }
                }
            }

            PlayerFormatModel best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (!IsUwpPreferredVideoOnlyFormat(current))
                {
                    continue;
                }

                if (best == null || current.QualityTier > best.QualityTier)
                {
                    best = current;
                }
            }

            if (best != null)
            {
                return best;
            }

            best = null;
            for (int i = 0; i < candidates.Count; i++)
            {
                var current = candidates[i];
                if (best == null || current.QualityTier > best.QualityTier)
                {
                    best = current;
                }
            }

            return best;
        }

        private static bool IsUwpPreferredVideoOnlyFormat(PlayerFormatModel format)
        {
            if (format == null)
            {
                return false;
            }

            // UWP on older/mobile devices is much happier with 30fps H.264 streams.
            return format.Fps <= 0 || format.Fps <= 30;
        }

        private static int[] GetPreferredH264VideoOnlyItags(int requestedHeight)
        {
            if (requestedHeight >= 1080)
            {
                return new[] { 137, 299, 136, 298, 135, 134, 133, 160 };
            }

            if (requestedHeight >= 720)
            {
                return new[] { 136, 298, 135, 134, 133, 160 };
            }

            if (requestedHeight >= 480)
            {
                return new[] { 135, 134, 133, 160 };
            }

            if (requestedHeight >= 360)
            {
                return new[] { 134, 133, 160 };
            }

            if (requestedHeight >= 240)
            {
                return new[] { 133, 160 };
            }

            return new[] { 299, 137, 298, 136, 135, 134, 133, 160 };
        }

        private static PlayerFormatModel SelectPreferredAudioVideoTrackFormat(
            IList<PlayerFormatModel> formats
        )
        {
            // This source is used only as hidden audio. Prefer the lightest stable
            // progressive MP4 video that still carries AAC audio.
            var lowQuality = SelectPreferredProgressiveFormat(formats, "360", true);
            if (lowQuality != null)
            {
                return lowQuality;
            }

            return SelectPreferredProgressiveFormat(formats, string.Empty, true);
        }

        private static PlayerFormatModel SelectPreferredProgressiveFormat(
            IList<PlayerFormatModel> formats,
            string requestedQualityTag,
            bool preferSmallest
        )
        {
            PlayerFormatModel best = null;
            int requestedHeight = ParseInt(requestedQualityTag);
            var progressiveCandidates = new List<PlayerFormatModel>();

            if (formats == null)
            {
                return null;
            }

            foreach (var format in formats)
            {
                if (format == null || string.IsNullOrWhiteSpace(format.Url))
                {
                    continue;
                }

                if (
                    format.Url.IndexOf("googlevideo.com", StringComparison.OrdinalIgnoreCase) < 0
                    && format.Url.IndexOf(
                        "manifest.googlevideo.com",
                        StringComparison.OrdinalIgnoreCase
                    ) < 0
                )
                {
                    continue;
                }

                if (
                    !string.IsNullOrWhiteSpace(format.MimeType)
                    && format.MimeType.IndexOf("video/mp4", StringComparison.OrdinalIgnoreCase) < 0
                )
                {
                    continue;
                }

                if (!IsProgressiveMp4(format))
                {
                    continue;
                }

                if (!format.HasAudio || !format.HasVideo)
                {
                    continue;
                }

                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Video] Added progressive candidate: itag={format.Itag}, width={format.Width}, height={format.Height}, mime={format.MimeType}"
                );
                progressiveCandidates.Add(format);
            }

            if (progressiveCandidates.Count == 0)
            {
                return null;
            }

            var preferredItags = preferSmallest
                ? new[] { 18, 59, 22 }
                : new[] { 18, 22, 59 };
            for (int p = 0; p < preferredItags.Length; p++)
            {
                for (int i = 0; i < progressiveCandidates.Count; i++)
                {
                    if (progressiveCandidates[i].Itag == preferredItags[p])
                    {
                        return progressiveCandidates[i];
                    }
                }
            }

            for (int i = 0; i < progressiveCandidates.Count; i++)
            {
                var format = progressiveCandidates[i];
                if (best == null)
                {
                    best = format;
                    continue;
                }

                var bestHeight = best.QualityTier > 0 ? best.QualityTier : 0;
                var currentHeight = format.QualityTier > 0 ? format.QualityTier : 0;
                if (preferSmallest)
                {
                    if (bestHeight == 0 || (currentHeight > 0 && currentHeight < bestHeight))
                    {
                        best = format;
                    }
                }
                else if (currentHeight > bestHeight)
                {
                    best = format;
                }
            }

            return best;
        }

        private static bool FormatHasAudio(JsonObject formatObj, string mimeType)
        {
            var mime = string.IsNullOrWhiteSpace(mimeType)
                ? string.Empty
                : mimeType.ToLowerInvariant();

            return mime.IndexOf("audio/") >= 0
                || mime.IndexOf("mp4a") >= 0
                || mime.IndexOf("opus") >= 0
                || mime.IndexOf("vorbis") >= 0
                || !string.IsNullOrWhiteSpace(GetJsonString(formatObj, "audioQuality"))
                || !string.IsNullOrWhiteSpace(GetJsonString(formatObj, "audioSampleRate"))
                || (formatObj != null && formatObj.ContainsKey("audioChannels"));
        }

        private static bool FormatHasVideo(JsonObject formatObj, string mimeType)
        {
            var mime = string.IsNullOrWhiteSpace(mimeType)
                ? string.Empty
                : mimeType.ToLowerInvariant();

            return mime.IndexOf("video/") >= 0
                || mime.IndexOf("avc1") >= 0
                || mime.IndexOf("vp9") >= 0
                || mime.IndexOf("av01") >= 0
                || ParseIntOrDefault(formatObj, "height") > 0
                || ParseIntOrDefault(formatObj, "width") > 0;
        }

        private static bool IsProgressiveMp4(PlayerFormatModel format)
        {
            if (format == null || string.IsNullOrWhiteSpace(format.MimeType))
            {
                return false;
            }

            var mime = format.MimeType.ToLowerInvariant();
            if (mime.IndexOf("video/mp4") < 0)
            {
                return false;
            }

            if (format.IsAdaptive)
            {
                return false;
            }

            // Windows 10 Mobile supports H.264 + AAC, but we should be less restrictive
            // Allow any video/mp4 format (most will have compatible codecs)
            var hasAacAudio = mime.IndexOf("mp4a") >= 0 || format.HasAudio;
            var hasH264Video = mime.IndexOf("avc1") >= 0 || format.HasVideo;

            // Log for debugging
            System.Diagnostics.Debug.WriteLine(
                $"[Video] Format check: itag={format.Itag}, mime={format.MimeType}, hasAAC={hasAacAudio}, hasH264={hasH264Video}"
            );

            // Relaxed check: allow video/mp4 with any audio codec
            // Most progressive MP4s from YouTube should work on Windows 10 Mobile
            bool hasAudio = hasAacAudio || mime.IndexOf("audio") >= 0;
            if (!hasAudio && !hasH264Video)
            {
                // Only reject if it clearly has no audio and no video codec info
                return false;
            }

            // Exclude adaptive formats (video-only or audio-only)
            // These itags are known to be adaptive (separate video/audio streams)
            if (
                format.Itag == 299
                || format.Itag == 298
                || format.Itag == 137
                || format.Itag == 136
                || format.Itag == 135
                || format.Itag == 134
                || format.Itag == 133
                || format.Itag == 160
                || format.Itag == 140
                || format.Itag == 141
                || format.Itag == 251
                || format.Itag == 250
                || format.Itag == 249
                || format.Itag == 171
            )
            {
                return false;
            }

            return true;
        }

        private static int ParseIntOrDefault(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return 0;
            }

            var value = obj.GetNamedValue(key);
            if (value == null)
            {
                return 0;
            }

            if (value.ValueType == JsonValueType.Number)
            {
                return (int)value.GetNumber();
            }

            if (value.ValueType == JsonValueType.String)
            {
                return ParseInt(value.GetString());
            }

            return 0;
        }

        private static long ParseLongOrDefault(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key)) return 0;
            try
            {
                var value = obj.GetNamedValue(key);
                if (value == null) return 0;
                if (value.ValueType == JsonValueType.Number)
                    return (long)value.GetNumber();
                if (value.ValueType == JsonValueType.String)
                {
                    long parsed;
                    return long.TryParse(value.GetString(), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
                }
            }
            catch { }
            return 0;
        }

        private static string GetRangeValue(JsonObject obj, string rangeKey, string valueKey)
        {
            if (
                obj == null
                || string.IsNullOrWhiteSpace(rangeKey)
                || string.IsNullOrWhiteSpace(valueKey)
                || !obj.ContainsKey(rangeKey)
            )
            {
                return string.Empty;
            }

            try
            {
                var range = obj.GetNamedObject(rangeKey);
                return GetJsonString(range, valueKey);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string NormalizeQualityTag(string quality)
        {
            if (string.IsNullOrWhiteSpace(quality))
            {
                return string.Empty;
            }

            quality = quality.Trim();
            if (string.Equals(quality, "Auto", StringComparison.OrdinalIgnoreCase)
                || string.Equals(quality, "Стандарт", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (quality.EndsWith("p", StringComparison.OrdinalIgnoreCase))
            {
                quality = quality.Substring(0, quality.Length - 1);
            }

            return quality.Trim();
        }

        private bool ShouldUseItag18AsMainVideo(string qualityTag)
        {
            var normalized = NormalizeQualityTag(qualityTag);
            return string.IsNullOrWhiteSpace(_selectedAudioTrackId)
                && string.Equals(normalized, "360", StringComparison.OrdinalIgnoreCase);
        }

        private static int ParseInt(string value)
        {
            int result;
            return int.TryParse(value, out result) ? result : 0;
        }

        private static string FormatCount(string value)
        {
            long number;
            if (!long.TryParse(value, out number))
            {
                return value;
            }

            if (number >= 1000000000)
            {
                return (number / 1000000000.0).ToString("F1") + "B";
            }

            if (number >= 1000000)
            {
                return (number / 1000000.0).ToString("F1") + "M";
            }

            if (number >= 1000)
            {
                return (number / 1000.0).ToString("F1") + "K";
            }

            return number.ToString();
        }

        private static string FormatPublishDate(string publishDate)
        {
            try
            {
                DateTime date;
                if (DateTime.TryParse(publishDate, out date))
                {
                    var now = DateTime.Now;
                    var diff = now - date;

                    if (diff.TotalDays < 1)
                    {
                        return Localization.GetString("Today");
                    }
                    else if (diff.TotalDays < 7)
                    {
                        return Localization.Format("DaysAgo", (int)diff.TotalDays);
                    }
                    else if (diff.TotalDays < 30)
                    {
                        return Localization.Format("WeeksAgo", (int)(diff.TotalDays / 7));
                    }
                    else if (diff.TotalDays < 365)
                    {
                        return Localization.Format("MonthsAgo", (int)(diff.TotalDays / 30));
                    }
                    else
                    {
                        return Localization.Format("YearsAgo", (int)(diff.TotalDays / 365));
                    }
                }
                return publishDate;
            }
            catch
            {
                return publishDate;
            }
        }

        private static IEnumerable<JsonObject> EnumerateObjects(IJsonValue value)
        {
            var stack = new Stack<IJsonValue>();
            stack.Push(value);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == null)
                {
                    continue;
                }

                if (current.ValueType == JsonValueType.Object)
                {
                    var obj = current.GetObject();
                    yield return obj;
                    foreach (var pair in obj)
                    {
                        stack.Push(pair.Value);
                    }
                }
                else if (current.ValueType == JsonValueType.Array)
                {
                    var arr = current.GetArray();
                    for (int i = 0; i < arr.Count; i++)
                    {
                        stack.Push(arr[i]);
                    }
                }
            }
        }

    }
}
