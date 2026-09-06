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
        private static string ExtractRatingFromGetRatingResponse(JsonObject root)
        {
            try
            {
                if (root == null || !root.ContainsKey("items"))
                {
                    return string.Empty;
                }

                var itemsValue = root.GetNamedValue("items");
                if (itemsValue == null || itemsValue.ValueType != JsonValueType.Array)
                {
                    return string.Empty;
                }

                var items = itemsValue.GetArray();
                if (items.Count == 0 || items[0].ValueType != JsonValueType.Object)
                {
                    return string.Empty;
                }

                var item = items[0].GetObject();
                return item.GetNamedString("rating", string.Empty);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static UserVideoRating ExtractUserRatingFromNext(JsonObject root, out bool foundSignal)
        {
            foundSignal = false;
            if (root == null)
            {
                return UserVideoRating.None;
            }

            UserVideoRating directRating;
            bool foundDirect;
            directRating = FindDirectRatingStatus(root, string.Empty, 0, out foundDirect);
            if (foundDirect)
            {
                foundSignal = true;
                return directRating;
            }

            var matches = new List<RatingToggleMatch>();
            CollectRatingToggleMatches(root, string.Empty, matches, 0);

            var disliked = matches.FirstOrDefault(m => m.IsToggled && m.Rating == UserVideoRating.Dislike);
            if (disliked != null)
            {
                foundSignal = true;
                return UserVideoRating.Dislike;
            }

            var liked = matches.FirstOrDefault(m => m.IsToggled && m.Rating == UserVideoRating.Like);
            if (liked != null)
            {
                foundSignal = true;
                return UserVideoRating.Like;
            }

            if (matches.Count > 0)
            {
                foundSignal = true;
                return UserVideoRating.None;
            }

            return UserVideoRating.None;
        }

        private static UserVideoRating FindDirectRatingStatus(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return UserVideoRating.None;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var lowerKey = key.ToLowerInvariant();
                        var nextPath = string.IsNullOrEmpty(path) ? lowerKey : path + "." + lowerKey;

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsDirectRatingStatusKey(lowerKey))
                        {
                            bool parsed;
                            var rating = ParseDirectRatingStatus(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return rating;
                            }
                        }

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.Boolean)
                        {
                            var boolValue = pair.Value.GetBoolean();
                            if (boolValue && IsLikeBooleanKey(lowerKey))
                            {
                                found = true;
                                return UserVideoRating.Like;
                            }

                            if (boolValue && IsDislikeBooleanKey(lowerKey))
                            {
                                found = true;
                                return UserVideoRating.Dislike;
                            }
                        }

                        bool nestedFound;
                        var nested = FindDirectRatingStatus(pair.Value, nextPath, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindDirectRatingStatus(array[(int)i], path + "[]", depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return UserVideoRating.None;
        }

        private static bool IsDirectRatingStatusKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "likestatus"
                || compact == "ratingstatus"
                || compact == "userrating"
                || compact == "feedbackstate"
                || compact == "selectedrating"
                || compact == "selectionstate";
        }

        private static bool IsLikeBooleanKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "isliked" || compact == "likedbyviewer" || compact == "viewerliked";
        }

        private static bool IsDislikeBooleanKey(string lowerKey)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            return compact == "isdisliked" || compact == "dislikedbyviewer" || compact == "viewerdisliked";
        }

        private static UserVideoRating ParseDirectRatingStatus(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return UserVideoRating.None;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized == "LIKE" || normalized == "LIKED" || normalized == "LIKE_STATUS_LIKE")
            {
                parsed = true;
                return UserVideoRating.Like;
            }

            if (normalized == "DISLIKE" || normalized == "DISLIKED" || normalized == "LIKE_STATUS_DISLIKE")
            {
                parsed = true;
                return UserVideoRating.Dislike;
            }

            if (normalized == "NONE" || normalized == "NO_RATING" || normalized == "INDIFFERENT" || normalized == "LIKE_STATUS_INDIFFERENT" || normalized == "RATING_UNSPECIFIED")
            {
                parsed = true;
                return UserVideoRating.None;
            }

            return UserVideoRating.None;
        }

        private static void CollectRatingToggleMatches(
            IJsonValue value,
            string path,
            List<RatingToggleMatch> matches,
            int depth
        )
        {
            if (value == null || matches == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("isToggled"))
                    {
                        var rating = DetermineRatingKindForToggleObject(obj, path);
                        if (rating != UserVideoRating.None)
                        {
                            bool isToggled = false;
                            try
                            {
                                var toggledValue = obj.GetNamedValue("isToggled");
                                if (toggledValue != null && toggledValue.ValueType == JsonValueType.Boolean)
                                {
                                    isToggled = toggledValue.GetBoolean();
                                }
                            }
                            catch
                            {
                                isToggled = false;
                            }

                            matches.Add(new RatingToggleMatch
                            {
                                Rating = rating,
                                IsToggled = isToggled,
                                Path = path
                            });
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();
                        CollectRatingToggleMatches(pair.Value, nextPath, matches, depth + 1);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectRatingToggleMatches(array[(int)i], path + "[]", matches, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static UserVideoRating DetermineRatingKindForToggleObject(JsonObject obj, string path)
        {
            var lowerPath = (path ?? string.Empty).ToLowerInvariant();
            if (lowerPath.Contains("dislike"))
            {
                return UserVideoRating.Dislike;
            }

            if (lowerPath.Contains("likebutton") || lowerPath.Contains("like_button") || lowerPath.Contains("segmentedlikedislike"))
            {
                return UserVideoRating.Like;
            }

            bool foundIcon;
            var iconRating = FindRatingIconType(obj, 0, out foundIcon);
            if (foundIcon)
            {
                return iconRating;
            }

            return UserVideoRating.None;
        }

        private static UserVideoRating FindRatingIconType(IJsonValue value, int depth, out bool found)
        {
            found = false;
            if (value == null || depth > 8)
            {
                return UserVideoRating.None;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    foreach (var pair in obj)
                    {
                        var key = (pair.Key ?? string.Empty).ToLowerInvariant();
                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String)
                        {
                            var text = pair.Value.GetString();
                            if ((key == "icontype" || key == "icon" || key == "accessibilitylabel") && IsDislikeText(text))
                            {
                                found = true;
                                return UserVideoRating.Dislike;
                            }

                            if ((key == "icontype" || key == "icon" || key == "accessibilitylabel") && IsLikeText(text))
                            {
                                found = true;
                                return UserVideoRating.Like;
                            }
                        }

                        bool nestedFound;
                        var nested = FindRatingIconType(pair.Value, depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        bool nestedFound;
                        var nested = FindRatingIconType(array[(int)i], depth + 1, out nestedFound);
                        if (nestedFound)
                        {
                            found = true;
                            return nested;
                        }
                    }
                }
            }
            catch
            {
                found = false;
            }

            return UserVideoRating.None;
        }

        private static bool IsLikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim().ToUpperInvariant();
            return normalized == "LIKE" || normalized == "LIKE_SELECTED" || normalized == "LIKE_FILLED";
        }

        private static bool IsDislikeText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var normalized = text.Trim().ToUpperInvariant();
            return normalized == "DISLIKE" || normalized == "DISLIKE_SELECTED" || normalized == "DISLIKE_FILLED";
        }

        private static UserVideoRating ParseUserVideoRating(string rating)
        {
            if (string.Equals(rating, "like", StringComparison.OrdinalIgnoreCase))
            {
                return UserVideoRating.Like;
            }

            if (string.Equals(rating, "dislike", StringComparison.OrdinalIgnoreCase))
            {
                return UserVideoRating.Dislike;
            }

            return UserVideoRating.None;
        }

        private static async Task<string> GetTvAccessTokenAsync(bool showErrors)
        {
            global::Config.LoadUserToken();
            var refreshToken = global::Config.UserToken;

            if (string.IsNullOrWhiteSpace(refreshToken))
            {
                if (showErrors)
                {
                    await ShowStaticRatingMessageAsync(
                        Localization.GetString("SignInRequired"),
                        Localization.GetString("TvTokenMissingDetailed")
                    );
                }

                System.Diagnostics.Debug.WriteLine("[Rating] TV refresh token not found");
                return string.Empty;
            }

            var accessToken = await global::Config.RefreshAccessTokenAsync(refreshToken);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                if (showErrors)
                {
                    await ShowStaticRatingMessageAsync(
                        Localization.GetString("SignInRequired"),
                        Localization.GetString("TvTokenExchangeFailedDetailed")
                    );
                }

                System.Diagnostics.Debug.WriteLine("[Rating] Failed to exchange TV refresh token for access token");
                return string.Empty;
            }

            return accessToken;
        }

        private static async Task ShowStaticRatingMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = Localization.GetString("OK")
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + title + ": " + message);
            }
        }

        private static void AddYouTubeAuthHeaders(
            HttpRequestMessage request,
            string accessToken,
            bool innertubeRequest
        )
        {
            if (innertubeRequest)
            {
                AddInnertubeAuthHeadersForClient(
                    request,
                    accessToken,
                    InnertubeTvClientHeaderName,
                    InnertubeTvClientVersion,
                    InnertubeTvUserAgent
                );
                return;
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
        }

        private static void AddInnertubeAuthHeadersForClient(
            HttpRequestMessage request,
            string accessToken,
            string clientNameHeader,
            string clientVersion,
            string userAgent
        )
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            Config.ApplySelectedAccountHeader(request, true);
            request.Headers.TryAddWithoutValidation("Accept-Language", Localization.AcceptLanguageHeader);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Name", clientNameHeader);
            request.Headers.TryAddWithoutValidation("X-YouTube-Client-Version", clientVersion);
            request.Headers.TryAddWithoutValidation("X-Goog-AuthUser", "0");
            request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
            request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
        }

        private void ResetSubscriptionUiForNewVideo()
        {
            _subscriptionStateGeneration++;
            _currentSubscriptionState = ChannelSubscriptionState.Unknown;
            _currentNotificationState = ChannelNotificationState.Default;
            _subscriptionRequestInProgress = false;
            _subscribeParams = string.Empty;
            _unsubscribeParams = string.Empty;
            _subscribeClickTrackingParams = string.Empty;
            _unsubscribeClickTrackingParams = string.Empty;

            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
            }
            if (SubscriptionMenuBottomSheetTransform != null)
            {
                SubscriptionMenuBottomSheetTransform.Y = 440;
            }

            UpdateSubscriptionVisualState();
        }

        private void UpdateSubscriptionVisualState()
        {
            var isSubscribed = _currentSubscriptionState == ChannelSubscriptionState.Subscribed;
            var ambientEnabled = VideoAmbientEffectController.IsEnabled();

            if (SubscribeButtonContainer != null)
            {
                SubscribeButtonContainer.Visibility = !_offlineMode && _hasSignedInAccount
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (SubscribeButton != null)
            {
                // Keep the button enabled visually while a request is in progress. The click handler
                // already ignores duplicate clicks through _subscriptionRequestInProgress. Disabling
                // the Button on older UWP builds can suppress image content in the custom template.
                SubscribeButton.IsEnabled = _hasSignedInAccount
                    && !string.IsNullOrWhiteSpace(currentChannelId);
                SubscribeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                SubscribeButton.Opacity = 1.0;
                SubscribeButton.Height = 36;
                SubscribeButton.Padding = new Thickness(16, 0, 16, 0);
                SubscribeButton.MinWidth = 0;
            }

            if (SubscribeButtonContainer != null)
            {
                var ambientResourceKey = isSubscribed
                    ? "VideoAmbientSurfaceBrush"
                    : "VideoAmbientPrimaryActionBrush";
                var containerBrush = Resources[ambientResourceKey] as SolidColorBrush;
                if (containerBrush == null)
                {
                    containerBrush = isSubscribed
                        ? (App.GetThemeBrush("AppSurfaceBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 39, 39, 39)))
                        : (App.GetThemeBrush("PrimaryActionBackgroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 241, 241, 241)));
                }
                var ambientBorder = Resources["VideoAmbientPillBorderBrush"] as Brush;
                var ambientGradient = Resources["VideoAmbientPillGradientBrush"] as LinearGradientBrush;
                SubscribeButtonContainer.Background = ambientEnabled && ambientGradient != null
                    ? (Brush)ambientGradient
                    : containerBrush;
                SubscribeButtonContainer.BorderBrush = ambientEnabled && ambientBorder != null
                    ? (Brush)ambientBorder
                    : containerBrush;
                SubscribeButtonContainer.BorderThickness = new Thickness(1);
                SubscribeButtonContainer.Opacity = 1.0;
                SubscribeButtonContainer.Height = 36;
                SubscribeButtonContainer.MinWidth = 0;
                SubscribeButtonContainer.CornerRadius = new CornerRadius(18);
            }

            if (SubscribeButtonText != null)
            {
                SubscribeButtonText.Text = Localization.GetString("Subscribe");
                SubscribeButtonText.Visibility = isSubscribed ? Visibility.Collapsed : Visibility.Visible;
                SubscribeButtonText.Foreground = ambientEnabled
                    ? (App.GetThemeBrush("AppPrimaryTextBrush") ?? new SolidColorBrush(Windows.UI.Colors.White))
                    : (App.GetThemeBrush("PrimaryActionForegroundBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 15, 15, 15)));
            }

            if (SubscribeSubscribedIconsPanel != null)
            {
                SubscribeSubscribedIconsPanel.Visibility = isSubscribed ? Visibility.Visible : Visibility.Collapsed;
                SubscribeSubscribedIconsPanel.Opacity = 1.0;
            }

            if (SubscribeNotificationIcon != null)
            {
                SubscribeNotificationIcon.Visibility = Visibility.Visible;
                SubscribeNotificationIcon.Opacity = 1.0;
                SubscribeNotificationIcon.Width = 22;
                SubscribeNotificationIcon.Height = 22;
                SubscribeNotificationIcon.Stretch = Windows.UI.Xaml.Media.Stretch.Uniform;
                SetImageSource(SubscribeNotificationIcon, GetNotificationIconAssetPath(_currentNotificationState));
            }

            if (SubscribeDownArrowIcon != null)
            {
                SubscribeDownArrowIcon.Visibility = Visibility.Visible;
                SubscribeDownArrowIcon.Opacity = 1.0;
                SubscribeDownArrowIcon.Width = 16;
                SubscribeDownArrowIcon.Height = 16;
                SubscribeDownArrowIcon.Margin = new Thickness(6, 0, 0, 0);
                SubscribeDownArrowIcon.Stretch = Windows.UI.Xaml.Media.Stretch.Uniform;
                SetImageSource(SubscribeDownArrowIcon, "Assets/down_arrow.png");
            }

            UpdateSubscriptionMenuVisualState();
        }

        private void UpdateSubscriptionMenuVisualState()
        {
            var effectiveState = _currentNotificationState == ChannelNotificationState.Unknown
                ? ChannelNotificationState.Default
                : _currentNotificationState;

            if (NotificationAllCheckmark != null)
            {
                NotificationAllCheckmark.Visibility = effectiveState == ChannelNotificationState.All
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (NotificationPersonalizedCheckmark != null)
            {
                NotificationPersonalizedCheckmark.Visibility = effectiveState == ChannelNotificationState.Default
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (NotificationNoneCheckmark != null)
            {
                NotificationNoneCheckmark.Visibility = effectiveState == ChannelNotificationState.None
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        private static string GetNotificationIconAssetPath(ChannelNotificationState state)
        {
            if (state == ChannelNotificationState.All)
            {
                return "Assets/all_notifications.png";
            }

            if (state == ChannelNotificationState.None)
            {
                return "Assets/none_notifications.png";
            }

            return "Assets/notifications.png";
        }

        private void RefreshAuthenticatedVideoActions()
        {
            global::Config.LoadUserToken();
            _hasSignedInAccount = !string.IsNullOrWhiteSpace(global::Config.UserToken);

            if (SaveActionContainer != null)
            {
                SaveActionContainer.Visibility = _hasSignedInAccount && !_offlineMode
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            if (DownloadActionContainer != null)
            {
                DownloadActionContainer.Visibility = _hasSignedInAccount && !_offlineMode
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            if (!_hasSignedInAccount)
            {
                if (SubscriptionMenuBottomSheetPanel != null)
                    SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
                if (SaveBottomSheetPanel != null)
                    SaveBottomSheetPanel.Visibility = Visibility.Collapsed;
            }

            UpdateSubscriptionVisualState();
            UpdateRatingVisualState();
        }

        private void ResetRatingUiForNewVideo()
        {
            _ratingStateGeneration++;
            _currentUserRating = UserVideoRating.None;
            _ratingRequestInProgress = false;
            _mainSaveStateSupportedByNext = false;
            SetImageSource(SaveActionIcon, "Assets/save.png");
            UpdateRatingVisualState();
        }

        private void UpdateRatingVisualState()
        {
            if (LikeButton != null)
            {
                LikeButton.IsEnabled = !_ratingRequestInProgress;
                LikeButton.IsHitTestVisible = _hasSignedInAccount && !_ratingRequestInProgress;
                LikeButton.IsTabStop = _hasSignedInAccount;
                LikeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                LikeButton.Opacity = 1.0;
            }

            if (DislikeButton != null)
            {
                DislikeButton.IsEnabled = !_ratingRequestInProgress;
                DislikeButton.IsHitTestVisible = _hasSignedInAccount && !_ratingRequestInProgress;
                DislikeButton.IsTabStop = _hasSignedInAccount;
                DislikeButton.Background = new SolidColorBrush(Windows.UI.Colors.Transparent);
                DislikeButton.Opacity = 1.0;
            }

            if (LikeIcon != null)
            {
                LikeIcon.Opacity = 1.0;
                SetImageSource(
                    LikeIcon,
                    _currentUserRating == UserVideoRating.Like
                        ? "Assets/player/like_clicked.png"
                        : "Assets/player/like.png"
                );
            }

            if (DislikeIcon != null)
            {
                DislikeIcon.Opacity = 1.0;
                SetImageSource(
                    DislikeIcon,
                    _currentUserRating == UserVideoRating.Dislike
                        ? "Assets/player/dislike_clicked.png"
                        : "Assets/player/dislike.png"
                );
            }
        }

        private static void SetImageSource(Image image, string assetPath)
        {
            if (image == null || string.IsNullOrWhiteSpace(assetPath))
            {
                return;
            }

            try
            {
                App.SetThemeImageSource(image, assetPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Rating] Failed to set icon source: " + ex.Message);
            }
        }

        private async Task ShowRatingMessageAsync(string title, string message)
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    PrimaryButtonText = Localization.GetString("OK")
                };
                await dialog.ShowAsync();
            }
            catch
            {
                System.Diagnostics.Debug.WriteLine("[Rating] " + title + ": " + message);
            }
        }

    }
}
