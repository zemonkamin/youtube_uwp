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
        private async void LikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            await ToggleUserVideoRatingAsync(UserVideoRating.Like);
        }

        private async void DislikeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            await ToggleUserVideoRatingAsync(UserVideoRating.Dislike);
        }

        private async Task ToggleUserVideoRatingAsync(UserVideoRating requestedRating)
        {
            if (_ratingRequestInProgress || string.IsNullOrWhiteSpace(currentVideoId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var videoIdAtClick = currentVideoId;
            var oldRating = _currentUserRating;
            var newRating = oldRating == requestedRating ? UserVideoRating.None : requestedRating;
            var stateGeneration = ++_ratingStateGeneration;

            _ratingRequestInProgress = true;
            _currentUserRating = newRating;
            UpdateRatingVisualState();

            try
            {
                var success = await SetUserVideoRatingAsync(videoIdAtClick, newRating, token);
                if (stateGeneration != _ratingStateGeneration || !string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentUserRating = oldRating;
                    UpdateRatingVisualState();
                    await ShowRatingMessageAsync(
                        Localization.GetString("RatingFailed"),
                        Localization.GetString("RatingRejectedDetailed")
                    );
                    return;
                }

                // Do not immediately reload /next here. YouTube can return the previous toggle
                // state for a short time after /like/removelike, which made the icon switch
                // back after a second click. The clicked state is the source of truth until
                // the next video load or manual refresh.
                _currentUserRating = newRating;
                UpdateRatingVisualState();
            }
            catch (Exception ex)
            {
                if (stateGeneration == _ratingStateGeneration && string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    _currentUserRating = oldRating;
                    UpdateRatingVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Rating] Error toggling rating: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("RatingFailed"), ex.Message);
            }
            finally
            {
                if (stateGeneration == _ratingStateGeneration && string.Equals(videoIdAtClick, currentVideoId, StringComparison.Ordinal))
                {
                    _ratingRequestInProgress = false;
                    UpdateRatingVisualState();
                }
            }
        }

        private async Task<bool> SetUserVideoRatingAsync(string videoId, UserVideoRating rating, string accessToken)
        {
            var endpoint = rating == UserVideoRating.Like
                ? "like/like"
                : rating == UserVideoRating.Dislike
                    ? "like/dislike"
                    : "like/removelike";

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(BuildRatingPayload(videoId), Encoding.UTF8, "application/json");
                AddYouTubeAuthHeaders(request, accessToken, true);

                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Rating] Innertube rating update OK: " + rating);
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Rating] Innertube rating failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
            }

            return await SetUserVideoRatingWithDataApiAsync(videoId, rating, accessToken);
        }

        private async Task<bool> SetUserVideoRatingWithDataApiAsync(
            string videoId,
            UserVideoRating rating,
            string accessToken
        )
        {
            var ratingValue = rating == UserVideoRating.Like
                ? "like"
                : rating == UserVideoRating.Dislike
                    ? "dislike"
                    : "none";

            var url = YouTubeDataApiBaseUrl
                + "videos/rate?id="
                + Uri.EscapeDataString(videoId)
                + "&rating="
                + Uri.EscapeDataString(ratingValue);

            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                AddYouTubeAuthHeaders(request, accessToken, false);
                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Rating] Data API rating update OK: " + ratingValue);
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Rating] Data API rating failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
                return false;
            }
        }

        private async Task LoadUserVideoRatingAsync(
            string videoId,
            JsonObject alreadyLoadedNextRoot,
            bool alreadyLoadedNextIsAuthenticated)
        {
            var loadGeneration = _ratingStateGeneration;

            if (string.IsNullOrWhiteSpace(videoId))
            {
                if (loadGeneration == _ratingStateGeneration)
                {
                    _currentUserRating = UserVideoRating.None;
                    UpdateRatingVisualState();
                }
                return;
            }

            var preliminaryRatingFound = false;
            var preliminaryRating = UserVideoRating.None;
            if (alreadyLoadedNextIsAuthenticated)
            {
                ApplyMainSaveStateFromAuthenticatedNext(
                    alreadyLoadedNextRoot, videoId, "authenticated primary /next");
                preliminaryRating = ExtractUserRatingFromNext(
                    alreadyLoadedNextRoot, out preliminaryRatingFound);
            }

            var token = await GetTvAccessTokenAsync(false);
            if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                _currentUserRating = UserVideoRating.None;
                UpdateRatingVisualState();
                System.Diagnostics.Debug.WriteLine("[Rating] TV refresh/access token not available; rating state hidden");
                return;
            }

            try
            {
                // getRating is the authoritative per-user state. TV/MWEB button view-models can
                // contain both inactive actions and look like an explicit "None", so trusting
                // their first toggle caused existing likes/dislikes to appear unselected.
                var dataApiResult = await TryLoadUserVideoRatingFromDataApiAsync(videoId, token);
                if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (dataApiResult != null && dataApiResult.Found)
                {
                    _currentUserRating = dataApiResult.Rating;
                    System.Diagnostics.Debug.WriteLine("[Rating] Current user rating from Data API: " + _currentUserRating);
                    UpdateRatingVisualState();
                    return;
                }

                if (preliminaryRatingFound)
                {
                    _currentUserRating = preliminaryRating;
                    UpdateRatingVisualState();
                    System.Diagnostics.Debug.WriteLine(
                        "[Rating] Data API unavailable; using authenticated /next: "
                        + preliminaryRating);
                    return;
                }

                var nextResult = await TryLoadUserVideoRatingFromAuthenticatedNextAsync(videoId, token);
                if (loadGeneration != _ratingStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (nextResult != null && nextResult.Found)
                {
                    _currentUserRating = nextResult.Rating;
                    System.Diagnostics.Debug.WriteLine("[Rating] Current user rating from " + nextResult.Source + ": " + _currentUserRating);
                    UpdateRatingVisualState();
                    return;
                }

                _currentUserRating = UserVideoRating.None;
                UpdateRatingVisualState();
                System.Diagnostics.Debug.WriteLine("[Rating] Could not determine current user rating; using None");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Error loading rating state: " + ex.Message);
                if (loadGeneration == _ratingStateGeneration && string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    _currentUserRating = UserVideoRating.None;
                    UpdateRatingVisualState();
                }
            }
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromAuthenticatedNextAsync(string videoId, string accessToken)
        {
            var tvResult = await TryLoadUserVideoRatingFromNextClientAsync(
                videoId,
                accessToken,
                false,
                "authenticated /next TVHTML5"
            );

            if (tvResult != null && tvResult.Found)
            {
                return tvResult;
            }

            return await TryLoadUserVideoRatingFromNextClientAsync(
                videoId,
                accessToken,
                true,
                "authenticated /next MWEB"
            );
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromNextClientAsync(
            string videoId,
            string accessToken,
            bool mobileWebClient,
            string sourceName
        )
        {
            try
            {
                var response = await GetAuthenticatedNextResponseAsync(
                    videoId, accessToken, mobileWebClient).ConfigureAwait(false);
                if (response == null) return null;

                var root = response.Root;
                ApplyMainSaveStateFromAuthenticatedNext(root, videoId, sourceName);
                bool found;
                var rating = ExtractUserRatingFromNext(root, out found);
                if (!found)
                {
                    System.Diagnostics.Debug.WriteLine("[Rating] " + sourceName + " did not contain selected like/dislike state");
                    return null;
                }

                return new RatingLoadResult
                {
                    Found = true,
                    Rating = rating,
                    Source = sourceName
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyMainSaveStateFromAuthenticatedNext(
            JsonObject root,
            string videoId,
            string sourceName)
        {
            bool isSaved;
            if (!TryExtractMainSaveStateFromNext(root, out isSaved))
            {
                System.Diagnostics.Debug.WriteLine(
                    "[SaveButton] " + sourceName + " did not contain an explicit Save state");
                return;
            }

            if (!string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            _mainSaveStateSupportedByNext = true;
            SetImageSource(
                SaveActionIcon,
                isSaved ? "Assets/save_clicked.png" : "Assets/save.png");
            System.Diagnostics.Debug.WriteLine(
                "[SaveButton] State from " + sourceName + ": " + (isSaved ? "saved" : "not saved"));
        }

        private static bool TryExtractMainSaveStateFromNext(JsonObject root, out bool isSaved)
        {
            isSaved = false;
            if (root == null)
            {
                return false;
            }

            JsonObject videoActions = null;
            foreach (var candidate in EnumerateObjects(root))
            {
                if (!candidate.ContainsKey("videoPrimaryInfoRenderer"))
                {
                    continue;
                }

                try
                {
                    var renderer = candidate.GetNamedObject("videoPrimaryInfoRenderer");
                    if (renderer.ContainsKey("videoActions"))
                    {
                        videoActions = renderer.GetNamedObject("videoActions");
                        break;
                    }
                }
                catch
                {
                }
            }

            if (videoActions == null)
            {
                return false;
            }

            foreach (var candidate in EnumerateObjects(videoActions))
            {
                string serialized;
                try
                {
                    serialized = candidate.Stringify().ToUpperInvariant();
                }
                catch
                {
                    continue;
                }

                if (serialized.IndexOf("GET_ADD_TO_PLAYLIST", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("ADDTOPLAYLISTSERVICEENDPOINT", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("PLAYLIST_ADD", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("WATCH-SAVE", StringComparison.Ordinal) < 0
                    && serialized.IndexOf("SAVE_TO_PLAYLIST", StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                bool boolState;
                if (TryGetDirectBoolean(candidate, "isToggled", out boolState)
                    || TryGetDirectBoolean(candidate, "isSelected", out boolState)
                    || TryGetDirectBoolean(candidate, "selected", out boolState))
                {
                    isSaved = boolState;
                    return true;
                }

                string state;
                if (TryGetDirectString(candidate, "state", out state)
                    || TryGetDirectString(candidate, "buttonState", out state)
                    || TryGetDirectString(candidate, "selectionState", out state))
                {
                    var normalized = (state ?? string.Empty).Trim().ToUpperInvariant();
                    if (normalized == "INACTIVE"
                        || normalized == "UNSELECTED"
                        || normalized == "OFF"
                        || normalized == "DEFAULT"
                        || normalized == "BUTTON_VIEW_MODEL_STATE_INACTIVE")
                    {
                        isSaved = false;
                        return true;
                    }

                    if (normalized == "ACTIVE"
                        || normalized == "SELECTED"
                        || normalized == "TOGGLED"
                        || normalized == "ON"
                        || normalized == "SAVED"
                        || normalized == "ADDED"
                        || normalized == "BUTTON_VIEW_MODEL_STATE_ACTIVE")
                    {
                        isSaved = true;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool TryGetDirectBoolean(JsonObject obj, string key, out bool value)
        {
            value = false;
            try
            {
                if (obj != null && obj.ContainsKey(key))
                {
                    var jsonValue = obj.GetNamedValue(key);
                    if (jsonValue != null && jsonValue.ValueType == JsonValueType.Boolean)
                    {
                        value = jsonValue.GetBoolean();
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private static bool TryGetDirectString(JsonObject obj, string key, out string value)
        {
            value = string.Empty;
            try
            {
                if (obj != null && obj.ContainsKey(key))
                {
                    var jsonValue = obj.GetNamedValue(key);
                    if (jsonValue != null && jsonValue.ValueType == JsonValueType.String)
                    {
                        value = jsonValue.GetString();
                        return true;
                    }
                }
            }
            catch
            {
            }
            return false;
        }

        private async Task<RatingLoadResult> TryLoadUserVideoRatingFromDataApiAsync(string videoId, string accessToken)
        {
            try
            {
                var url = YouTubeDataApiBaseUrl
                    + "videos/getRating?id="
                    + Uri.EscapeDataString(videoId);

                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    AddYouTubeAuthHeaders(request, accessToken, false);
                    var response = await httpClient.SendAsync(request);
                    var json = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Rating] getRating failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + json
                        );
                        return null;
                    }

                    var root = JsonValue.Parse(json).GetObject();
                    var rating = ExtractRatingFromGetRatingResponse(root);
                    if (string.IsNullOrWhiteSpace(rating))
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[Rating] Data API getRating returned no item for this video");
                        return null;
                    }
                    return new RatingLoadResult
                    {
                        Found = true,
                        Rating = ParseUserVideoRating(rating),
                        Source = "Data API getRating"
                    };
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Data API getRating error: " + ex.Message);
                return null;
            }
        }

    }
}
