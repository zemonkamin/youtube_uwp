using System;
using System.Text;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    // Windows.Data.Json is native to UWP and avoids reflection/converter overhead.
    // These helpers also avoid exceptions for optional fields, which are common in Innertube.
    internal static class FastJson
    {
        internal static bool TryParseObject(string json, out JsonObject value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            return JsonObject.TryParse(json, out value);
        }

        internal static string GetString(JsonObject value, string name)
        {
            if (value == null || string.IsNullOrEmpty(name))
            {
                return string.Empty;
            }

            IJsonValue field;
            if (!value.TryGetValue(name, out field) || field == null || field.ValueType != JsonValueType.String)
            {
                return string.Empty;
            }

            return field.GetString();
        }

        internal static int GetInt32(JsonObject value, string name, int fallback)
        {
            if (value == null || string.IsNullOrEmpty(name))
            {
                return fallback;
            }

            IJsonValue field;
            if (!value.TryGetValue(name, out field) || field == null || field.ValueType != JsonValueType.Number)
            {
                return fallback;
            }

            var number = field.GetNumber();
            if (number < int.MinValue || number > int.MaxValue)
            {
                return fallback;
            }

            return (int)number;
        }

        internal static string Escape(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            StringBuilder builder = null;
            for (var i = 0; i < value.Length; i++)
            {
                var c = value[i];
                string replacement = null;
                switch (c)
                {
                    case '\\': replacement = "\\\\"; break;
                    case '"': replacement = "\\\""; break;
                    case '\b': replacement = "\\b"; break;
                    case '\f': replacement = "\\f"; break;
                    case '\n': replacement = "\\n"; break;
                    case '\r': replacement = "\\r"; break;
                    case '\t': replacement = "\\t"; break;
                    default:
                        if (c < 32)
                        {
                            replacement = "\\u" + ((int)c).ToString("x4");
                        }
                        break;
                }

                if (replacement == null)
                {
                    if (builder != null)
                    {
                        builder.Append(c);
                    }
                    continue;
                }

                if (builder == null)
                {
                    builder = new StringBuilder(value.Length + 16);
                    builder.Append(value, 0, i);
                }
                builder.Append(replacement);
            }

            return builder == null ? value : builder.ToString();
        }
    }
}
