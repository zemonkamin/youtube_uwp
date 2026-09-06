using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Data.Json;
using YouTube;
using YouTube.Innertube;

public static partial class Config
{
        public static async Task<PlaylistDetails> GetPlaylistDetailsAsync(
            string playlistId,
            string refreshToken,
            int maxVideos)
        {
            if (string.IsNullOrWhiteSpace(playlistId))
                return null;

            if (maxVideos <= 0)
                maxVideos = 80;
            maxVideos = Math.Min(maxVideos, 120);

            var accessToken = string.Empty;
            if (!string.IsNullOrWhiteSpace(refreshToken))
                accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);

            var json = await GetPlaylistBrowseJsonAsync(playlistId, accessToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(accessToken))
                json = await GetPlaylistBrowseJsonAsync(playlistId, string.Empty).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(json))
                return null;

            var details = ParsePlaylistDetails(json, playlistId, maxVideos);
            if (details != null
                && (details.Videos == null || details.Videos.Count == 0)
                && !string.IsNullOrWhiteSpace(accessToken))
            {
                var dataApiVideos = await GetPlaylistVideosViaDataApiAsync(
                    playlistId,
                    accessToken,
                    maxVideos).ConfigureAwait(false);
                if (dataApiVideos.Count > 0)
                {
                    details.Videos = dataApiVideos;
                    if (string.IsNullOrWhiteSpace(details.ThumbnailUrl))
                        details.ThumbnailUrl = dataApiVideos[0].ThumbnailUrl;
                    if (string.IsNullOrWhiteSpace(details.MetadataText))
                    {
                        details.MetadataText = Localization.Format(
                            "VideosSuffixFormat",
                            dataApiVideos.Count);
                    }
                }
            }

            if (details != null && details.Videos != null)
            {
                RememberChannelAvatar(
                    string.Empty,
                    details.OwnerName,
                    details.OwnerThumbnailUrl);
                // The browse response already contains everything needed to show the page.
                // Data API metadata and missing channel avatars are decorative enrichment and
                // must not hold the first frame hostage on Windows 10 Mobile.
                details.DeferredEnrichment = EnrichPlaylistDetailsAsync(
                    details,
                    playlistId,
                    accessToken);
            }

            return details;
        }

        public static async Task<PlaylistDetails> GetPlaylistContinuationAsync(
            string refreshToken,
            string continuationToken,
            int maxVideos)
        {
            if (string.IsNullOrWhiteSpace(continuationToken))
                return null;

            if (maxVideos <= 0)
                maxVideos = 40;
            maxVideos = Math.Min(maxVideos, 80);

            var accessToken = string.Empty;
            if (!string.IsNullOrWhiteSpace(refreshToken))
                accessToken = await RefreshAccessTokenAsync(refreshToken).ConfigureAwait(false);

            var json = string.Empty;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                json = await PostTvBrowseAsync(
                    accessToken,
                    null,
                    null,
                    continuationToken).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(json))
                json = await PostWebBrowseAsync(null, null, null, continuationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                json = await PostAndroidBrowseAsync(
                    accessToken,
                    null,
                    null,
                    continuationToken).ConfigureAwait(false);
            }
            if (string.IsNullOrWhiteSpace(json))
                return null;

            var details = new PlaylistDetails
            {
                PlaylistId = string.Empty,
                Title = string.Empty,
                OwnerName = string.Empty,
                OwnerThumbnailUrl = string.Empty,
                Description = string.Empty,
                ThumbnailUrl = string.Empty,
                MetadataText = string.Empty,
                ContinuationToken = string.Empty,
                Videos = ParsePlaylistVideoCards(json, maxVideos)
            };

            try
            {
                details.ContinuationToken = ExtractHistoryContinuationToken(JsonValue.Parse(json));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Playlist] Continuation token parse error: " + ex.Message);
            }

            details.DeferredEnrichment = HydrateMissingChannelThumbnailsAsync(
                details.Videos,
                accessToken);
            System.Diagnostics.Debug.WriteLine(
                "[Playlist] Continuation parsed videos: "
                + (details.Videos != null ? details.Videos.Count : 0)
                + ", has more: "
                + (!string.IsNullOrWhiteSpace(details.ContinuationToken)));
            return details;
        }

        private static async Task EnrichPlaylistDetailsAsync(
            PlaylistDetails details,
            string playlistId,
            string accessToken)
        {
            if (details == null)
                return;

            Task<PlaylistDetails> metadataTask = null;
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                metadataTask = GetPlaylistMetadataViaDataApiAsync(playlistId, accessToken);
            }

            var avatarTask = HydrateMissingChannelThumbnailsAsync(
                details.Videos,
                accessToken);

            if (metadataTask != null)
            {
                try
                {
                    var dataApiDetails = await metadataTask.ConfigureAwait(false);
                    MergePlaylistMetadata(details, dataApiDetails);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        "[Playlist] Deferred metadata failed: " + ex.Message);
                }
            }

            try
            {
                await avatarTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Playlist] Deferred avatars failed: " + ex.Message);
            }
        }
}
