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
        private static string ExtractTextFromField(
            JsonObject obj,
            string fieldName,
            string fallback
        )
        {
            if (obj == null || !obj.ContainsKey(fieldName))
            {
                return fallback;
            }

            var field = obj.GetNamedValue(fieldName);
            if (field == null || field.ValueType != JsonValueType.Object)
            {
                return fallback;
            }

            var fieldObject = field.GetObject();
            if (fieldObject.ContainsKey("simpleText"))
            {
                return fieldObject.GetNamedString("simpleText", fallback);
            }

            if (fieldObject.ContainsKey("runs"))
            {
                var runs = fieldObject.GetNamedArray("runs");
                var sb = new StringBuilder();
                for (int i = 0; i < runs.Count; i++)
                {
                    var runObj = runs[i].GetObject();
                    var text = runObj.GetNamedString("text", string.Empty);
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        sb.Append(text);
                    }
                }

                var collected = sb.ToString().Trim();
                return string.IsNullOrWhiteSpace(collected) ? fallback : collected;
            }

            return fallback;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i];
                }
            }
            return string.Empty;
        }

    }
}
