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
        private static bool HasSubscribeEndpoint(IJsonValue value, int depth)
        {
            return HasEndpointNamed(value, "subscribeEndpoint", depth);
        }

        private static bool HasUnsubscribeEndpoint(IJsonValue value, int depth)
        {
            return HasEndpointNamed(value, "unsubscribeEndpoint", depth);
        }

        private static bool HasEndpointNamed(IJsonValue value, string endpointName, int depth)
        {
            if (value == null || string.IsNullOrWhiteSpace(endpointName) || depth > 80)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (obj.ContainsKey(endpointName))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        if (HasEndpointNamed(pair.Value, endpointName, depth + 1))
                        {
                            return true;
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        if (HasEndpointNamed(array[(int)i], endpointName, depth + 1))
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static ChannelNotificationState FindCurrentNotificationState(IJsonValue root, out bool found)
        {
            found = false;
            if (root == null)
            {
                return ChannelNotificationState.Unknown;
            }

            bool innerFound;
            var state = FindNotificationStateFromCurrentStateId(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            state = FindNotificationStateFromSelectedOption(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            state = FindNotificationStateFromExplicitCurrentFields(root, string.Empty, 0, out innerFound);
            if (innerFound)
            {
                found = true;
                return state;
            }

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromCurrentStateId(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    var lowerPath = path ?? string.Empty;

                    if (IsNotificationRelatedPath(lowerPath) && obj.ContainsKey("states"))
                    {
                        var currentStateId = GetJsonScalarStringSafe(obj, "currentStateId");
                        if (string.IsNullOrWhiteSpace(currentStateId))
                        {
                            currentStateId = GetJsonScalarStringSafe(obj, "current_state_id");
                        }

                        if (!string.IsNullOrWhiteSpace(currentStateId))
                        {
                            var statesValue = obj.GetNamedValue("states");
                            if (statesValue != null && statesValue.ValueType == JsonValueType.Array)
                            {
                                var states = statesValue.GetArray();
                                for (uint i = 0; i < states.Count; i++)
                                {
                                    var stateValue = states[(int)i];
                                    if (stateValue == null || stateValue.ValueType != JsonValueType.Object)
                                    {
                                        continue;
                                    }

                                    var stateObj = stateValue.GetObject();
                                    var stateId = GetJsonScalarStringSafe(stateObj, "stateId");
                                    if (string.IsNullOrWhiteSpace(stateId))
                                    {
                                        stateId = GetJsonScalarStringSafe(stateObj, "state_id");
                                    }

                                    if (!string.IsNullOrWhiteSpace(stateId)
                                        && string.Equals(stateId, currentStateId, StringComparison.OrdinalIgnoreCase))
                                    {
                                        ChannelNotificationState parsedState;
                                        if (TryParseNotificationStateFromObject(stateValue, 0, out parsedState))
                                        {
                                            found = true;
                                            return parsedState;
                                        }
                                    }
                                }
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();

                        bool nestedFound;
                        var nested = FindNotificationStateFromCurrentStateId(pair.Value, nextPath, depth + 1, out nestedFound);
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
                        var nested = FindNotificationStateFromCurrentStateId(array[(int)i], path + "[]", depth + 1, out nestedFound);
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

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromSelectedOption(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();
                    if (ObjectHasSelectedTrue(obj))
                    {
                        ChannelNotificationState parsedState;
                        if (TryParseNotificationStateFromObject(value, 0, out parsedState))
                        {
                            found = true;
                            return parsedState;
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var nextPath = string.IsNullOrEmpty(path)
                            ? key.ToLowerInvariant()
                            : path + "." + key.ToLowerInvariant();

                        bool nestedFound;
                        var nested = FindNotificationStateFromSelectedOption(pair.Value, nextPath, depth + 1, out nestedFound);
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
                        var nested = FindNotificationStateFromSelectedOption(array[(int)i], path + "[]", depth + 1, out nestedFound);
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

            return ChannelNotificationState.Unknown;
        }

        private static ChannelNotificationState FindNotificationStateFromExplicitCurrentFields(
            IJsonValue value,
            string path,
            int depth,
            out bool found
        )
        {
            found = false;
            if (value == null || depth > 80)
            {
                return ChannelNotificationState.Unknown;
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
                        var compact = lowerKey.Replace("_", string.Empty).Replace("-", string.Empty);
                        var nextPath = string.IsNullOrEmpty(path)
                            ? lowerKey
                            : path + "." + lowerKey;

                        if (pair.Value != null
                            && pair.Value.ValueType == JsonValueType.String
                            && IsCurrentNotificationStateKey(compact, nextPath))
                        {
                            bool parsed;
                            var parsedState = ParseNotificationStateText(pair.Value.GetString(), out parsed);
                            if (parsed)
                            {
                                found = true;
                                return parsedState;
                            }
                        }

                        bool nestedFound;
                        var nested = FindNotificationStateFromExplicitCurrentFields(pair.Value, nextPath, depth + 1, out nestedFound);
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
                        var nested = FindNotificationStateFromExplicitCurrentFields(array[(int)i], path + "[]", depth + 1, out nestedFound);
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

            return ChannelNotificationState.Unknown;
        }

        private static bool IsNotificationRelatedPath(string lowerPath)
        {
            var path = lowerPath ?? string.Empty;
            return path.Contains("notification")
                || path.Contains("bell")
                || path.Contains("subscriptionnotification")
                || path.Contains("notificationpreference");
        }

        private static bool IsCurrentNotificationStateKey(string compactKey, string lowerPath)
        {
            var key = compactKey ?? string.Empty;
            var path = lowerPath ?? string.Empty;
            if (!IsNotificationRelatedPath(path) && !key.Contains("notification"))
            {
                return false;
            }

            if (key == "currentnotificationstate"
                || key == "currentnotificationpreference"
                || key == "currentstate")
            {
                return true;
            }

            if (path.Contains("current")
                && (key == "notificationstate"
                    || key == "notificationpreference"
                    || key == "subscriptionnotificationpreference"))
            {
                return true;
            }

            return false;
        }

        private static bool ObjectHasSelectedTrue(JsonObject obj)
        {
            if (obj == null)
            {
                return false;
            }

            return GetJsonBooleanSafe(obj, "selected")
                || GetJsonBooleanSafe(obj, "isSelected")
                || GetJsonBooleanSafe(obj, "is_selected")
                || GetJsonBooleanSafe(obj, "checked")
                || GetJsonBooleanSafe(obj, "isChecked")
                || GetJsonBooleanSafe(obj, "is_checked");
        }

        private static bool GetJsonBooleanSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return false;
                }

                var value = obj.GetNamedValue(key);
                return value != null && value.ValueType == JsonValueType.Boolean && value.GetBoolean();
            }
            catch
            {
                return false;
            }
        }

        private static string GetJsonScalarStringSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return string.Empty;
                }

                var value = obj.GetNamedValue(key);
                if (value == null)
                {
                    return string.Empty;
                }

                if (value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }

                if (value.ValueType == JsonValueType.Number)
                {
                    return ((int)Math.Round(value.GetNumber())).ToString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool TryParseNotificationStateFromObject(IJsonValue value, int depth, out ChannelNotificationState state)
        {
            state = ChannelNotificationState.Unknown;
            if (value == null || depth > 12)
            {
                return false;
            }

            try
            {
                if (value.ValueType == JsonValueType.Object)
                {
                    var obj = value.GetObject();

                    // First read real labels/state fields. Do not let iconType win over text,
                    // because YouTube sometimes keeps a NOTIFICATIONS_NONE icon inside the
                    // selected/default bell state object.
                    if (TryParseNotificationStateFromObjectStrings(obj, false, out state))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (!IsPreferredNotificationTextContainerKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (IsNotificationIconKey(compact)
                            || IsPreferredNotificationTextContainerKey(compact)
                            || IsNotificationEndpointOrActionKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }

                    // Icon parsing is only a fallback. This fixes the case where the current
                    // state is personalized/default but the object also contains a none icon.
                    if (TryParseNotificationStateFromObjectStrings(obj, true, out state))
                    {
                        return true;
                    }

                    foreach (var pair in obj)
                    {
                        var key = pair.Key ?? string.Empty;
                        var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);

                        if (!IsNotificationIconKey(compact))
                        {
                            continue;
                        }

                        if (pair.Value != null
                            && (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array))
                        {
                            ChannelNotificationState nestedState;
                            if (TryParseNotificationStateFromObject(pair.Value, depth + 1, out nestedState))
                            {
                                state = nestedState;
                                return true;
                            }
                        }
                    }
                }
                else if (value.ValueType == JsonValueType.Array)
                {
                    var array = value.GetArray();
                    for (uint i = 0; i < array.Count; i++)
                    {
                        ChannelNotificationState nestedState;
                        if (TryParseNotificationStateFromObject(array[(int)i], depth + 1, out nestedState))
                        {
                            state = nestedState;
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        private static bool TryParseNotificationStateFromObjectStrings(
            JsonObject obj,
            bool iconsOnly,
            out ChannelNotificationState state
        )
        {
            state = ChannelNotificationState.Unknown;
            if (obj == null)
            {
                return false;
            }

            foreach (var pair in obj)
            {
                var key = pair.Key ?? string.Empty;
                var compact = key.ToLowerInvariant().Replace("_", string.Empty).Replace("-", string.Empty);
                var isIconKey = IsNotificationIconKey(compact);

                if (iconsOnly != isIconKey)
                {
                    continue;
                }

                if (pair.Value != null
                    && pair.Value.ValueType == JsonValueType.String
                    && IsNotificationTextOrIconKey(compact))
                {
                    bool parsed;
                    var parsedState = ParseNotificationStateText(pair.Value.GetString(), out parsed);
                    if (parsed)
                    {
                        state = parsedState;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool IsNotificationIconKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "icon" || key == "iconname" || key == "icontype";
        }

        private static bool IsPreferredNotificationTextContainerKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "title"
                || key == "text"
                || key == "simpletext"
                || key == "label"
                || key == "accessibility"
                || key == "accessibilitydata"
                || key == "accessibilitylabel"
                || key == "tooltip"
                || key == "subtitle"
                || key == "description";
        }

        private static bool IsNotificationEndpointOrActionKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key.Contains("endpoint")
                || key.Contains("command")
                || key.Contains("action")
                || key.Contains("service")
                || key.Contains("tracking")
                || key.Contains("menu")
                || key.Contains("params")
                || key.Contains("token");
        }

        private static bool IsNotificationTextOrIconKey(string compactKey)
        {
            var key = compactKey ?? string.Empty;
            return key == "icon"
                || key == "iconname"
                || key == "icontype"
                || key == "accessibilitylabel"
                || key == "label"
                || key == "text"
                || key == "simpletext"
                || key == "title"
                || key == "tooltip"
                || key == "notificationpreference"
                || key == "notificationstate"
                || key == "state";
        }

        private static ChannelNotificationState ParseNotificationStateText(string value, out bool parsed)
        {
            parsed = false;
            if (string.IsNullOrWhiteSpace(value))
            {
                return ChannelNotificationState.Unknown;
            }

            var normalized = value.Trim().ToUpperInvariant();
            var compact = normalized
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .Replace(" ", string.Empty);

            if (normalized == "ALL" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_ALL")
            {
                parsed = true;
                return ChannelNotificationState.All;
            }

            if (normalized == "NONE" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_NONE")
            {
                parsed = true;
                return ChannelNotificationState.None;
            }

            if (normalized == "PERSONALIZED" || normalized == "SUBSCRIPTION_NOTIFICATION_PREFERENCE_PERSONALIZED")
            {
                parsed = true;
                return ChannelNotificationState.Default;
            }

            // Check personalized/default before none. Some TV /next objects contain both
            // "personalized notifications" text and a nested off/none icon; the text is the
            // selected state, the icon can be only a menu asset.
            if (compact.Contains("PERSONALIZED")
                || compact.Contains("PERSONALISED")
                || compact.Contains("DEFAULT")
                || compact.Contains("NOTIFICATIONSPERSONALIZED")
                || compact == "NOTIFICATIONS"
                || compact == "NOTIFICATION"
                || compact.Contains("OCCASIONAL")
                || compact.Contains("SOMENOTIFICATION")
                || compact.Contains("HIGHLIGHT")
                || normalized.Contains("ПЕРСОНАЛ")
                || normalized.Contains("РЕКОМЕНД")
                || normalized.Contains("НЕКОТОР")
                || normalized.Contains("ВАЖН")
                || normalized.Contains("ИНТЕРЕС"))
            {
                parsed = true;
                return ChannelNotificationState.Default;
            }

            if (compact.Contains("ALLNOTIFICATION")
                || compact.Contains("NOTIFICATIONSALL")
                || compact.Contains("NOTIFICATIONALL")
                || compact.Contains("NOTIFICATIONSACTIVE")
                || compact.Contains("NOTIFICATIONACTIVE")
                || compact.Contains("RINGING")
                || normalized.Contains("ВСЕ УВЕДОМ")
                || normalized.Contains("ВСЕ ОПОВЕЩ"))
            {
                parsed = true;
                return ChannelNotificationState.All;
            }

            if (compact.Contains("NONOTIFICATION")
                || compact.Contains("NOTIFICATIONSNONE")
                || compact.Contains("NOTIFICATIONNONE")
                || compact.Contains("NOTIFICATIONSOFF")
                || compact.Contains("NOTIFICATIONOFF")
                || compact.Contains("NOTIFICATIONDISABLED")
                || compact.Contains("MUTED")
                || normalized.Contains("НЕ ПРИСЫЛАТЬ")
                || normalized.Contains("БЕЗ УВЕДОМ")
                || normalized.Contains("НЕТ УВЕДОМ")
                || normalized.Contains("НИКАКИХ УВЕДОМ")
                || normalized.Contains("УВЕДОМЛЕНИЯ ОТКЛЮЧ")
                || normalized.Contains("ОТКЛЮЧИТЬ УВЕДОМ"))
            {
                parsed = true;
                return ChannelNotificationState.None;
            }

            return ChannelNotificationState.Unknown;
        }

        private static string GetJsonStringSafe(JsonObject obj, string key)
        {
            try
            {
                if (obj == null || string.IsNullOrWhiteSpace(key) || !obj.ContainsKey(key))
                {
                    return string.Empty;
                }

                var value = obj.GetNamedValue(key);
                if (value != null && value.ValueType == JsonValueType.String)
                {
                    return value.GetString();
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string BuildSubscriptionPayload(string channelId, string parameters, string clickTrackingParams)
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

            if (!string.IsNullOrWhiteSpace(clickTrackingParams))
            {
                var clickTracking = new JsonObject();
                clickTracking["clickTrackingParams"] = JsonValue.CreateStringValue(clickTrackingParams);
                context["clickTracking"] = clickTracking;
            }

            var channelIds = new JsonArray();
            channelIds.Add(JsonValue.CreateStringValue(channelId));

            var payload = new JsonObject();
            payload["context"] = context;
            payload["channelIds"] = channelIds;
            if (!string.IsNullOrWhiteSpace(parameters))
            {
                payload["params"] = JsonValue.CreateStringValue(parameters);
            }

            return Config.ApplySelectedAccountContext(payload.Stringify(), true);
        }

    }
}
