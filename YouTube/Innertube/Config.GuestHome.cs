using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using YouTube.Innertube;

public static partial class Config
{
        // TubeReplacer guest Home mapping:
        // browseId VLPL-p0-Yh03xpi2AsCiyuafMeQrMF6czMoL -> stored playlist id without VL.
        private const string GuestHomePlaylistId = "PL-p0-Yh03xpi2AsCiyuafMeQrMF6czMoL";
        private static readonly object GuestHomePageCacheGate = new object();
        private static HomeRecommendationsPage _guestHomeFirstPageCache;
        private static DateTime _guestHomeFirstPageCacheExpiresUtc = DateTime.MinValue;

        public static async Task<HomeRecommendationsPage> GetGuestHomePageAsync(
            string continuationToken,
            int count)
        {
            var page = new HomeRecommendationsPage();
            if (count <= 0)
                count = 12;

            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                lock (GuestHomePageCacheGate)
                {
                    if (_guestHomeFirstPageCache != null
                        && DateTime.UtcNow < _guestHomeFirstPageCacheExpiresUtc)
                    {
                        return CloneHomePage(_guestHomeFirstPageCache);
                    }
                }
            }

            var requestKey = string.IsNullOrWhiteSpace(continuationToken)
                ? "home:guest:trending:" + Hl + ":" + Gl
                : "home:guest:continuation:" + continuationToken;
            InnertubeRequestCoordinator.JsonResponse response = null;
            try
            {
                response = await InnertubeRequestCoordinator.GetJsonAsync(
                    requestKey,
                    delegate
                    {
                        return string.IsNullOrWhiteSpace(continuationToken)
                            ? GetPlaylistBrowseJsonAsync(GuestHomePlaylistId, string.Empty)
                            : PostWebBrowseAsync(null, null, null, continuationToken);
                    },
                    string.IsNullOrWhiteSpace(continuationToken)
                        ? TimeSpan.FromMinutes(5)
                        : TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Home][Guest] Trending playlist request failed: " + ex.Message);
            }

            if (response == null || string.IsNullOrWhiteSpace(response.Text))
            {
                // Keep a guest Home useful even if the TubeReplacer playlist is temporarily
                // unavailable. The primary source above remains the requested Trending playlist.
                if (string.IsNullOrWhiteSpace(continuationToken))
                    page.Videos = await GetAnonymousSearchVideosAsync("Trending", count).ConfigureAwait(false);
                return page;
            }

            // Home only needs cards and the tail continuation. Do not run the full playlist
            // metadata parser or block first paint on per-channel avatar network requests.
            page.Videos = ParsePlaylistVideoCards(response.Root, count);
            page.ContinuationToken = ExtractHistoryContinuationToken(response.Root);

            foreach (var video in page.Videos)
            {
                if (video != null)
                    video.PlaylistId = GuestHomePlaylistId;
            }

            if (string.IsNullOrWhiteSpace(continuationToken))
            {
                lock (GuestHomePageCacheGate)
                {
                    _guestHomeFirstPageCache = CloneHomePage(page);
                    _guestHomeFirstPageCacheExpiresUtc = DateTime.UtcNow.AddMinutes(5);
                }
            }

            System.Diagnostics.Debug.WriteLine(
                "[Home][Guest] Fast playlist page videos=" + page.Videos.Count
                + ", continuation=" + (!string.IsNullOrWhiteSpace(page.ContinuationToken)));
            return page;
        }

        private static HomeRecommendationsPage CloneHomePage(HomeRecommendationsPage source)
        {
            return new HomeRecommendationsPage
            {
                Videos = source == null || source.Videos == null
                    ? new List<VideoCardItem>()
                    : new List<VideoCardItem>(source.Videos),
                ContinuationToken = source == null
                    ? string.Empty
                    : (source.ContinuationToken ?? string.Empty)
            };
        }
}
