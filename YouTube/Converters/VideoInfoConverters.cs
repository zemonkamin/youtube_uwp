using System;
using Windows.UI.Xaml.Data;

namespace YouTube.Converters
{
    public class ViewsCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                string viewsString = value as string;
                if (string.IsNullOrEmpty(viewsString))
                    return "0 просмотров";

                // Remove " просмотров" suffix if present
                string cleanViews = viewsString.Replace(" просмотров", "").Replace(" ", "");
                
                // Handle different formats
                long views;
                if (cleanViews.EndsWith("K"))
                {
                    double valueNum = double.Parse(cleanViews.Substring(0, cleanViews.Length - 1));
                    views = (long)(valueNum * 1000);
                }
                else if (cleanViews.EndsWith("M"))
                {
                    double valueNum = double.Parse(cleanViews.Substring(0, cleanViews.Length - 1));
                    views = (long)(valueNum * 1000000);
                }
                else if (cleanViews.EndsWith("B"))
                {
                    double valueNum = double.Parse(cleanViews.Substring(0, cleanViews.Length - 1));
                    views = (long)(valueNum * 1000000000);
                }
                else
                {
                    views = long.Parse(cleanViews);
                }

                // Format the number
                string formattedNumber;
                if (views >= 1000000000)
                {
                    formattedNumber = string.Format("{0:F1} млрд", views / 1000000000.0);
                }
                else if (views >= 1000000)
                {
                    formattedNumber = string.Format("{0:F1} млн", views / 1000000.0);
                }
                else if (views >= 1000)
                {
                    formattedNumber = string.Format("{0:F1} тыс", views / 1000.0);
                }
                else
                {
                    formattedNumber = views.ToString();
                }

                return formattedNumber + " просмотров";
            }
            catch
            {
                return value;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class RelativeDateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                string dateString = value as string;
                if (string.IsNullOrEmpty(dateString))
                    return "";

                DateTime date;
                // Try to parse different date formats
                if (DateTime.TryParseExact(dateString, "dd.MM.yyyy, HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out date))
                {
                    return GetRelativeDateString(date);
                }
                // Handle ISO 8601 format (2017-12-25T00:06:07Z)
                else if (DateTime.TryParse(dateString, null, System.Globalization.DateTimeStyles.RoundtripKind, out date))
                {
                    return GetRelativeDateString(date);
                }
                // Handle other common formats
                else if (DateTime.TryParse(dateString, out date))
                {
                    return GetRelativeDateString(date);
                }
                else
                {
                    return dateString;
                }
            }
            catch
            {
                return value;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        // Helper method to get relative date string
        private string GetRelativeDateString(DateTime date)
        {
            var timeSpan = DateTime.Now - date;
            var totalDays = (int)timeSpan.TotalDays;

            if (totalDays == 0)
            {
                var hours = (int)timeSpan.TotalHours;
                var minutes = (int)timeSpan.TotalMinutes;

                if (hours == 0)
                {
                    if (minutes < 1)
                        return "только что";
                    else if (minutes == 1)
                        return "1 минуту назад";
                    else if (minutes < 5)
                        return string.Format("{0} минуты назад", minutes);
                    else
                        return string.Format("{0} минут назад", minutes);
                }
                else if (hours == 1)
                {
                    return "1 час назад";
                }
                else if (hours < 5)
                {
                    return string.Format("{0} часа назад", hours);
                }
                else
                {
                    return string.Format("{0} часов назад", hours);
                }
            }
            else if (totalDays == 1)
            {
                return "1 день назад";
            }
            else if (totalDays < 7)
            {
                if (totalDays < 5)
                    return string.Format("{0} дня назад", totalDays);
                else
                    return string.Format("{0} дней назад", totalDays);
            }
            else if (totalDays < 30)
            {
                var weeks = totalDays / 7;
                if (weeks == 1)
                    return "1 неделю назад";
                else if (weeks < 5)
                    return string.Format("{0} недели назад", weeks);
                else
                    return string.Format("{0} недель назад", weeks);
            }
            else if (totalDays < 365)
            {
                var months = totalDays / 30;
                if (months == 1)
                    return "1 месяц назад";
                else if (months < 5)
                    return string.Format("{0} месяца назад", months);
                else
                    return string.Format("{0} месяцев назад", months);
            }
            else
            {
                var years = totalDays / 365;
                if (years == 1)
                    return "1 год назад";
                else if (years < 5)
                    return string.Format("{0} года назад", years);
                else
                    return string.Format("{0} лет назад", years);
            }
        }
    }

    public class SubscriberCountConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            try
            {
                string subscriberString = value as string;
                if (string.IsNullOrEmpty(subscriberString))
                    return "0 подписчиков";

                // Remove " подписчиков" suffix if present
                string cleanSubscribers = subscriberString.Replace(" подписчиков", "").Replace(" ", "");
                
                // Handle different formats
                long subscribers;
                if (cleanSubscribers.EndsWith("K"))
                {
                    double valueNum = double.Parse(cleanSubscribers.Substring(0, cleanSubscribers.Length - 1));
                    subscribers = (long)(valueNum * 1000);
                }
                else if (cleanSubscribers.EndsWith("M"))
                {
                    double valueNum = double.Parse(cleanSubscribers.Substring(0, cleanSubscribers.Length - 1));
                    subscribers = (long)(valueNum * 1000000);
                }
                else if (cleanSubscribers.EndsWith("B"))
                {
                    double valueNum = double.Parse(cleanSubscribers.Substring(0, cleanSubscribers.Length - 1));
                    subscribers = (long)(valueNum * 1000000000);
                }
                else
                {
                    subscribers = long.Parse(cleanSubscribers);
                }

                // Format the number
                string formattedNumber;
                if (subscribers >= 1000000000)
                {
                    formattedNumber = string.Format("{0:F1} млрд", subscribers / 1000000000.0);
                }
                else if (subscribers >= 1000000)
                {
                    formattedNumber = string.Format("{0:F1} млн", subscribers / 1000000.0);
                }
                else if (subscribers >= 1000)
                {
                    formattedNumber = string.Format("{0:F1} тыс", subscribers / 1000.0);
                }
                else
                {
                    formattedNumber = subscribers.ToString();
                }

                return formattedNumber + " подписчиков";
            }
            catch
            {
                return value;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}