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
        private void VideoInfoButton_Click(object sender, RoutedEventArgs e)
        {
            // In landscape the complete inline description lives directly below the title.
            // The portrait bottom sheet must never be opened from that layout.
            if (!IsCurrentViewPortrait())
            {
                return;
            }
            ShowDescriptionBottomSheet();
        }

        private void ChannelPicture_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var channelTarget = FirstNonEmpty(currentChannelId, currentChannelName, VideoAuthorText != null ? VideoAuthorText.Text : string.Empty);
            if (string.IsNullOrWhiteSpace(channelTarget))
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine("[Video] Navigating to channel: " + channelTarget);
            Frame.Navigate(typeof(Channel), channelTarget);
        }

        private async void SubscribeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;

            if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                ShowSubscriptionMenuBottomSheet();
                return;
            }

            await SetChannelSubscriptionStateAsync(true);
        }

        private async Task SetChannelSubscriptionStateAsync(bool subscribe)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return;
            }

            var channelIdAtClick = currentChannelId;
            var oldState = _currentSubscriptionState;
            var oldNotificationState = _currentNotificationState;
            var newState = subscribe ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
            var newNotificationState = subscribe ? ChannelNotificationState.Default : ChannelNotificationState.Default;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentSubscriptionState = newState;
            _currentNotificationState = newNotificationState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var success = await SetChannelSubscriptionAsync(channelIdAtClick, subscribe, token);
                if (stateGeneration != _subscriptionStateGeneration || !string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    return;
                }

                if (!success)
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    await ShowRatingMessageAsync(
                        Localization.GetString("SubscriptionFailed"),
                        Localization.GetString("SubscriptionRejectedDetailed")
                    );
                    return;
                }

                _currentSubscriptionState = newState;
                _currentNotificationState = newNotificationState;
                UpdateSubscriptionVisualState();
                UpdateSubscriptionMenuVisualState();

                if (!subscribe)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentSubscriptionState = oldState;
                    _currentNotificationState = oldNotificationState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Subscription] Error updating subscription: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("SubscriptionFailed"), ex.Message);
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private async Task ToggleChannelSubscriptionAsync()
        {
            await SetChannelSubscriptionStateAsync(_currentSubscriptionState != ChannelSubscriptionState.Subscribed);
        }

        private async Task ChangeChannelSubscriptionAsync(bool subscribe)
        {
            await SetChannelSubscriptionStateAsync(subscribe);
        }


        private async Task<bool> SetChannelSubscriptionAsync(string channelId, bool subscribe, string accessToken)
        {
            var endpoint = subscribe ? "subscription/subscribe" : "subscription/unsubscribe";
            var parameters = subscribe ? _subscribeParams : _unsubscribeParams;
            var clickTrackingParams = subscribe ? _subscribeClickTrackingParams : _unsubscribeClickTrackingParams;

            if (string.IsNullOrWhiteSpace(parameters))
            {
                parameters = subscribe ? DefaultSubscribeParams : DefaultUnsubscribeParams;
            }

            var url = BuildInnertubeUrl(endpoint);
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Content = new StringContent(
                    BuildSubscriptionPayload(channelId, parameters, clickTrackingParams),
                    Encoding.UTF8,
                    "application/json"
                );
                AddYouTubeAuthHeaders(request, accessToken, true);

                var response = await httpClient.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    System.Diagnostics.Debug.WriteLine("[Subscription] Innertube subscription update OK: " + (subscribe ? "subscribe" : "unsubscribe"));
                    return true;
                }

                var errorBody = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine(
                    "[Subscription] Innertube subscription update failed: "
                    + (int)response.StatusCode
                    + " "
                    + response.ReasonPhrase
                    + " "
                    + errorBody
                );
                return false;
            }
        }

        private async Task<bool> ModifyChannelNotificationPreferenceAsync(ChannelNotificationState targetState)
        {
            if (_subscriptionRequestInProgress || string.IsNullOrWhiteSpace(currentChannelId))
            {
                return false;
            }

            var token = await GetTvAccessTokenAsync(true);
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var oldState = _currentNotificationState;
            var channelIdAtClick = currentChannelId;
            var stateGeneration = ++_subscriptionStateGeneration;

            _subscriptionRequestInProgress = true;
            _currentNotificationState = targetState;
            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();

            try
            {
                var paramsValue = BuildNotificationPreferenceParams(channelIdAtClick, targetState);
                var url = BuildInnertubeUrl("notification/modify_channel_preference");
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    request.Content = new StringContent(
                        BuildNotificationPreferencePayload(paramsValue),
                        Encoding.UTF8,
                        "application/json"
                    );
                    AddYouTubeAuthHeaders(request, token, true);

                    var response = await httpClient.SendAsync(request);
                    var body = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                    {
                        _currentNotificationState = oldState;
                        UpdateSubscriptionVisualState();
                        UpdateSubscriptionMenuVisualState();
                        System.Diagnostics.Debug.WriteLine(
                            "[Subscription] Notification preference update failed: "
                            + (int)response.StatusCode
                            + " "
                            + response.ReasonPhrase
                            + " "
                            + body
                        );
                        await ShowRatingMessageAsync(Localization.GetString("NotificationsFailed"), Localization.GetString("NotificationPreferenceFailed"));
                        return false;
                    }
                }

                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = targetState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                    AnimateSubscriptionMenuBottomSheet(false);
                }

                return true;
            }
            catch (Exception ex)
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _currentNotificationState = oldState;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }

                System.Diagnostics.Debug.WriteLine("[Subscription] Notification preference update error: " + ex.Message);
                await ShowRatingMessageAsync(Localization.GetString("NotificationsFailed"), ex.Message);
                return false;
            }
            finally
            {
                if (stateGeneration == _subscriptionStateGeneration && string.Equals(channelIdAtClick, currentChannelId, StringComparison.Ordinal))
                {
                    _subscriptionRequestInProgress = false;
                    UpdateSubscriptionVisualState();
                    UpdateSubscriptionMenuVisualState();
                }
            }
        }

        private static string BuildNotificationPreferencePayload(string parameters)
        {
            var context = new JsonObject();
            var client = new JsonObject();
            client["clientName"] = JsonValue.CreateStringValue(InnertubeTvClientName);
            client["clientVersion"] = JsonValue.CreateStringValue(InnertubeTvClientVersion);
            client["hl"] = JsonValue.CreateStringValue(Config.Hl);
            client["gl"] = JsonValue.CreateStringValue(Config.Gl);
            client["platform"] = JsonValue.CreateStringValue("TV");
            client["clientFormFactor"] = JsonValue.CreateStringValue("UNKNOWN_FORM_FACTOR");
            context["client"] = client;

            var user = new JsonObject();
            user["enableSafetyMode"] = JsonValue.CreateBooleanValue(false);
            context["user"] = user;

            var request = new JsonObject();
            request["internalExperimentFlags"] = new JsonArray();
            request["consistencyTokenJars"] = new JsonArray();
            context["request"] = request;

            var payload = new JsonObject();
            payload["context"] = context;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

        private static string BuildNotificationPreferenceParams(string channelId, ChannelNotificationState targetState)
        {
            if (string.IsNullOrWhiteSpace(channelId))
            {
                return string.Empty;
            }

            byte stateCode = 1;
            if (targetState == ChannelNotificationState.All)
            {
                stateCode = 2;
            }
            else if (targetState == ChannelNotificationState.None)
            {
                stateCode = 3;
            }

            var channelBytes = Encoding.UTF8.GetBytes(channelId);
            var bytes = new List<byte>();
            bytes.Add(0x0A);
            bytes.Add((byte)channelBytes.Length);
            bytes.AddRange(channelBytes);
            bytes.Add(0x12);
            bytes.Add(0x02);
            bytes.Add(0x08);
            bytes.Add(stateCode);
            bytes.Add(0x18);
            bytes.Add(0x00);
            bytes.Add(0x20);
            bytes.Add(0x04);

            return Uri.EscapeDataString(Convert.ToBase64String(bytes.ToArray()));
        }

        private async Task LoadChannelSubscriptionStateAsync(
            string videoId,
            JsonObject alreadyLoadedNextRoot,
            bool alreadyLoadedNextIsAuthenticated)
        {
            var loadGeneration = _subscriptionStateGeneration;

            if (string.IsNullOrWhiteSpace(videoId))
            {
                if (loadGeneration == _subscriptionStateGeneration)
                {
                    _currentSubscriptionState = ChannelSubscriptionState.Unknown;
                    UpdateSubscriptionVisualState();
                }
                return;
            }

            var preliminaryResult = ExtractSubscriptionStateFromNext(
                alreadyLoadedNextRoot,
                alreadyLoadedNextIsAuthenticated
                    ? "authenticated primary /next"
                    : "public /next");
            ApplySubscriptionEndpointData(preliminaryResult);
            if (string.IsNullOrWhiteSpace(currentChannelId) && preliminaryResult != null && !string.IsNullOrWhiteSpace(preliminaryResult.ChannelId))
            {
                currentChannelId = preliminaryResult.ChannelId;
            }

            if (alreadyLoadedNextIsAuthenticated
                && preliminaryResult != null && preliminaryResult.Found)
            {
                ApplyLoadedSubscriptionState(preliminaryResult);
                System.Diagnostics.Debug.WriteLine(
                    "[Subscription] Reused authenticated primary /next: "
                    + _currentSubscriptionState);
                return;
            }

            var token = await GetTvAccessTokenAsync(false);
            if (loadGeneration != _subscriptionStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                }
                else
                {
                    _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                    UpdateSubscriptionVisualState();
                }
                System.Diagnostics.Debug.WriteLine("[Subscription] TV refresh/access token not available; using public /next state");
                return;
            }

            try
            {
                var nextResult = await TryLoadChannelSubscriptionFromAuthenticatedNextAsync(videoId, token);
                if (loadGeneration != _subscriptionStateGeneration || !string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    return;
                }

                if (nextResult != null && nextResult.Found)
                {
                    ApplyLoadedSubscriptionState(nextResult);
                    System.Diagnostics.Debug.WriteLine("[Subscription] Current subscription state from " + nextResult.Source + ": " + _currentSubscriptionState);
                    return;
                }

                if (preliminaryResult != null && preliminaryResult.Found)
                {
                    ApplyLoadedSubscriptionState(preliminaryResult);
                    System.Diagnostics.Debug.WriteLine("[Subscription] Current subscription state from public /next fallback: " + _currentSubscriptionState);
                    return;
                }

                _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                UpdateSubscriptionVisualState();
                System.Diagnostics.Debug.WriteLine("[Subscription] Could not determine subscription state; using NotSubscribed");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subscription] Error loading subscription state: " + ex.Message);
                if (loadGeneration == _subscriptionStateGeneration && string.Equals(videoId, currentVideoId, StringComparison.Ordinal))
                {
                    if (preliminaryResult != null && preliminaryResult.Found)
                    {
                        ApplyLoadedSubscriptionState(preliminaryResult);
                    }
                    else
                    {
                        _currentSubscriptionState = ChannelSubscriptionState.NotSubscribed;
                        UpdateSubscriptionVisualState();
                    }
                }
            }
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromAuthenticatedNextAsync(string videoId, string accessToken)
        {
            var tvResult = await TryLoadChannelSubscriptionFromNextClientAsync(
                videoId,
                accessToken,
                false,
                "authenticated /next TVHTML5"
            );

            if (tvResult != null && tvResult.Found)
            {
                return tvResult;
            }

            return await TryLoadChannelSubscriptionFromNextClientAsync(
                videoId,
                accessToken,
                true,
                "authenticated /next MWEB"
            );
        }

        private async Task<SubscriptionLoadResult> TryLoadChannelSubscriptionFromNextClientAsync(
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
                return response == null
                    ? null
                    : ExtractSubscriptionStateFromNext(response.Root, sourceName);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Subscription] " + sourceName + " error: " + ex.Message);
                return null;
            }
        }

        private void ApplyLoadedSubscriptionState(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            ApplySubscriptionEndpointData(result);

            if (!string.IsNullOrWhiteSpace(result.ChannelId))
            {
                currentChannelId = result.ChannelId;
            }

            _currentSubscriptionState = result.State;
            if (result.NotificationState != ChannelNotificationState.Unknown)
            {
                _currentNotificationState = result.NotificationState;
            }
            else if (_currentSubscriptionState == ChannelSubscriptionState.Subscribed)
            {
                _currentNotificationState = ChannelNotificationState.Default;
            }
            else
            {
                _currentNotificationState = ChannelNotificationState.Default;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Subscription] Notification state: "
                + _currentNotificationState
                + " from "
                + result.Source
            );

            UpdateSubscriptionVisualState();
            UpdateSubscriptionMenuVisualState();
        }

        private void ApplySubscriptionEndpointData(SubscriptionLoadResult result)
        {
            if (result == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeParams))
            {
                _subscribeParams = result.SubscribeParams;
            }

            if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams))
            {
                _unsubscribeParams = result.UnsubscribeParams;
            }

            if (!string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
            {
                _subscribeClickTrackingParams = result.SubscribeClickTrackingParams;
            }

            if (!string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
            {
                _unsubscribeClickTrackingParams = result.UnsubscribeClickTrackingParams;
            }
        }

        private static SubscriptionLoadResult ExtractSubscriptionStateFromNext(JsonObject root, string sourceName)
        {
            var result = new SubscriptionLoadResult
            {
                Found = false,
                State = ChannelSubscriptionState.Unknown,
                NotificationState = ChannelNotificationState.Unknown,
                Source = sourceName
            };

            if (root == null)
            {
                return result;
            }

            bool foundDirect;
            var directState = FindDirectSubscriptionState(root, string.Empty, 0, out foundDirect);
            if (foundDirect)
            {
                result.Found = true;
                result.State = directState;
            }

            CollectSubscriptionEndpointData(root, string.Empty, result, 0);

            if (!result.Found)
            {
                if (!string.IsNullOrWhiteSpace(result.UnsubscribeParams) || HasUnsubscribeEndpoint(root, 0))
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.Subscribed;
                }
                else if (!string.IsNullOrWhiteSpace(result.SubscribeParams) || HasSubscribeEndpoint(root, 0))
                {
                    result.Found = true;
                    result.State = ChannelSubscriptionState.NotSubscribed;
                }
            }

            bool foundNotification;
            var notificationState = FindCurrentNotificationState(root, out foundNotification);
            if (foundNotification)
            {
                result.NotificationState = notificationState;
            }
            else if (result.State == ChannelSubscriptionState.Subscribed)
            {
                result.NotificationState = ChannelNotificationState.Default;
            }

            return result;
        }

        private static ChannelSubscriptionState FindDirectSubscriptionState(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelSubscriptionState.Unknown;
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

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.Boolean && IsSubscriptionBooleanKey(lowerKey, nextPath))
                        {
                            found = true;
                            return pair.Value.GetBoolean() ? ChannelSubscriptionState.Subscribed : ChannelSubscriptionState.NotSubscribed;
                        }

                        if (pair.Value != null && pair.Value.ValueType == JsonValueType.String && IsSubscriptionStatusKey(lowerKey, nextPath))
                        {
                            bool parsed;
                            var parsedState = ParseSubscriptionStateText(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return parsedState;
                            }
                        }

                        bool nestedFound;
                        var nested = FindDirectSubscriptionState(pair.Value, nextPath, depth + 1, out nestedFound);
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
                        var nested = FindDirectSubscriptionState(array[(int)i], path + "[]", depth + 1, out nestedFound);
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

            return ChannelSubscriptionState.Unknown;
        }

        private static bool IsSubscriptionBooleanKey(string lowerKey, string lowerPath)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            var path = lowerPath ?? string.Empty;
            if (!(path.Contains("subscribe") || path.Contains("subscription") || path.Contains("videoowner") || path.Contains("owner")))
            {
                return false;
            }

            return compact == "subscribed"
                || compact == "issubscribed"
                || compact == "subscribedtochannel"
                || compact == "issubscribedtochannel"
                || compact == "channelissubscribed"
                || compact == "iscurrentusersubscribed"
                || compact == "viewersubscribed";
        }

        private static bool IsSubscriptionStatusKey(string lowerKey, string lowerPath)
        {
            var compact = (lowerKey ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty);
            var path = lowerPath ?? string.Empty;
            if (!(path.Contains("subscribe") || path.Contains("subscription") || path.Contains("notification")))
            {
                return false;
            }

            return compact == "subscriptionstatus"
                || compact == "subscribestatus"
                || compact == "subscribebuttonstate"
                || compact == "state"
                || compact == "status";
        }

        private static ChannelSubscriptionState ParseSubscriptionStateText(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return ChannelSubscriptionState.Unknown;
            }

            var normalized = value.Trim().ToUpperInvariant();
            if (normalized == "SUBSCRIBED" || normalized == "SUBSCRIPTION_STATUS_SUBSCRIBED" || normalized == "CHANNEL_SUBSCRIBED")
            {
                parsed = true;
                return ChannelSubscriptionState.Subscribed;
            }

            if (normalized == "UNSUBSCRIBED" || normalized == "NOT_SUBSCRIBED" || normalized == "SUBSCRIPTION_STATUS_UNSUBSCRIBED" || normalized == "CHANNEL_NOT_SUBSCRIBED")
            {
                parsed = true;
                return ChannelSubscriptionState.NotSubscribed;
            }

            return ChannelSubscriptionState.Unknown;
        }

        private static void CollectSubscriptionEndpointData(
            IJsonValue value,
            string path,
            SubscriptionLoadResult result,
            int depth
        )
        {
            if (value == null || result == null || depth > 80)
            {
                return;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey("subscribeEndpoint"))
                    {
                        ReadSubscriptionEndpointData(
                            obj.GetNamedValue("subscribeEndpoint"),
                            obj,
                            true,
                            result
                        );
                    }

                    if (obj.ContainsKey("unsubscribeEndpoint"))
                    {
                        ReadSubscriptionEndpointData(
                            obj.GetNamedValue("unsubscribeEndpoint"),
                            obj,
                            false,
                            result
                        );
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();
                        CollectSubscriptionEndpointData(pair.Value, nextPath, result, depth + 1);
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        CollectSubscriptionEndpointData(array[(int)i], path + "[]", result, depth + 1);
                    }
                }
            }
            catch
            {
            }
        }

        private static void ReadSubscriptionEndpointData(
            IJsonValue endpointValue,
            JsonObject parentObject,
            bool subscribe,
            SubscriptionLoadResult result
        )
        {
            if (endpointValue == null || endpointValue.ValueType != JsonValueType.Object || result == null)
            {
                return;
            }

            try
            {
                var endpoint = endpointValue.GetObject();
                var parameters = GetJsonStringSafe(endpoint, "params");
                var clickTrackingParams = GetJsonStringSafe(parentObject, "clickTrackingParams");
                if (string.IsNullOrWhiteSpace(clickTrackingParams))
                {
                    clickTrackingParams = GetJsonStringSafe(endpoint, "clickTrackingParams");
                }

                var endpointChannelId = GetFirstChannelIdFromEndpoint(endpoint);
                if (!string.IsNullOrWhiteSpace(endpointChannelId) && string.IsNullOrWhiteSpace(result.ChannelId))
                {
                    result.ChannelId = endpointChannelId;
                }

                if (subscribe)
                {
                    if (!string.IsNullOrWhiteSpace(parameters) && string.IsNullOrWhiteSpace(result.SubscribeParams))
                    {
                        result.SubscribeParams = parameters;
                    }

                    if (!string.IsNullOrWhiteSpace(clickTrackingParams) && string.IsNullOrWhiteSpace(result.SubscribeClickTrackingParams))
                    {
                        result.SubscribeClickTrackingParams = clickTrackingParams;
                    }
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(parameters) && string.IsNullOrWhiteSpace(result.UnsubscribeParams))
                    {
                        result.UnsubscribeParams = parameters;
                    }

                    if (!string.IsNullOrWhiteSpace(clickTrackingParams) && string.IsNullOrWhiteSpace(result.UnsubscribeClickTrackingParams))
                    {
                        result.UnsubscribeClickTrackingParams = clickTrackingParams;
                    }
                }
            }
            catch
            {
            }
        }

        private static string GetFirstChannelIdFromEndpoint(JsonObject endpoint)
        {
            try
            {
                if (endpoint == null || !endpoint.ContainsKey("channelIds"))
                {
                    return string.Empty;
                }

                var channelIdsValue = endpoint.GetNamedValue("channelIds");
                if (channelIdsValue == null || channelIdsValue.ValueType != JsonValueType.Array)
                {
                    return string.Empty;
                }

                var channelIds = channelIdsValue.GetArray();
                if (channelIds.Count == 0 || channelIds[0].ValueType != JsonValueType.String)
                {
                    return string.Empty;
                }

                return channelIds[0].GetString();
            }
            catch
            {
                return string.Empty;
            }
        }

    }
}
