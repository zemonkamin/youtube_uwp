using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media.Imaging;
using Windows.UI.Xaml.Navigation;

// The Blank Page item template is documented at http://go.microsoft.com/fwlink/?LinkId=234238

namespace YouTube
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class Login : Page
    {
        private const string OAuthClientId = "861556708454-d6dlm3lh05idd8npek18k6be8ba3oc68.apps.googleusercontent.com";
        private const string OAuthClientSecret = "SboVhoG9s0rNafixCSGGKXAT";
        private const string InnertubeApiKey = "AIzaSyAO_FJ2SlqU8Q4STEHLGCilw_Y9_11qcW8";
        private const string DeviceCodeScope = "http://gdata.youtube.com https://www.googleapis.com/auth/youtube-paid-content";
        private const string DeviceModel = "ytlr:samsung:smarttv";
        private const string TvUserAgent = "Mozilla/5.0 (SMART-TV; Linux; Tizen 6.0)";

        private readonly HttpClient httpClient = new HttpClient();
        private CancellationTokenSource authCancellation;

        public Login()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when this page is about to be displayed in a Frame.
        /// </summary>
        /// <param name="e">Event data that describes how this page was reached.
        /// This parameter is typically used to configure the page.</param>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            // Check if already authenticated
            Config.LoadUserToken();
            if (!string.IsNullOrEmpty(Config.UserToken))
            {
                Frame.Navigate(typeof(MainPage));
                return;
            }

            BeginAuthFlowAsync();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            if (authCancellation != null)
            {
                authCancellation.Cancel();
                authCancellation.Dispose();
                authCancellation = null;
            }
        }

        private async void RefreshButton_OnClick(object sender, RoutedEventArgs e)
        {
            await BeginAuthFlowAsync();
        }

        private async Task BeginAuthFlowAsync()
        {
            if (authCancellation != null)
            {
                authCancellation.Cancel();
                authCancellation.Dispose();
            }

            authCancellation = new CancellationTokenSource();
            var token = authCancellation.Token;
            QrImage.Source = null;
            StatusText.Text = Localization.GetString("LoginLoadingQr");

            try
            {
                var flowState = await StartDeviceFlowAsync(token);
                if (flowState == null || string.IsNullOrWhiteSpace(flowState.QrBase64))
                {
                    StatusText.Text = Localization.GetString("LoginQrFailed");
                    return;
                }

                await SetQrImageAsync(flowState.QrBase64);
                UserCodeText.Text = flowState.UserCode ?? "";
                StatusText.Text = Localization.GetString("LoginScanQr");
                await PollTokenAsync(flowState, token);
            }
            catch (OperationCanceledException)
            {
                // Navigation away cancels polling.
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Login error: " + ex.Message);
                StatusText.Text = Localization.Format("ErrorFormat", ex.Message);
            }
        }

        private async Task PollTokenAsync(DeviceFlowState flowState, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                var response = await CheckDeviceTokenAsync(flowState.DeviceCode, token);
                if (!string.IsNullOrWhiteSpace(response.Error))
                {
                    if (string.Equals(response.Error, "authorization_pending", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, flowState.IntervalSeconds)), token);
                        continue;
                    }

                    throw new InvalidOperationException("Sign-in error: " + response.Error);
                }

                if (!string.IsNullOrWhiteSpace(response.RefreshToken) || !string.IsNullOrWhiteSpace(response.AccessToken))
                {
                    var refreshToken = !string.IsNullOrWhiteSpace(response.RefreshToken)
                        ? response.RefreshToken
                        : response.AccessToken;
                    
                    // Save token using Config
                    Config.SetUserToken(refreshToken);
                    
                    StatusText.Text = Localization.GetString("LoginSuccess");
                    
                    // Navigate to Home page
                    Frame.Navigate(typeof(Home));
                    return;
                }

                await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, flowState.IntervalSeconds)), token);
            }
        }

        private async Task<DeviceFlowState> StartDeviceFlowAsync(CancellationToken token)
        {
            var deviceCodeResponse = await GetDeviceCodeAsync(token);
            var qrBase64 = await GetTvQrBase64Async(deviceCodeResponse.UserCode, token);

            return new DeviceFlowState
            {
                DeviceCode = deviceCodeResponse.DeviceCode,
                UserCode = deviceCodeResponse.UserCode,
                QrBase64 = qrBase64,
                IntervalSeconds = deviceCodeResponse.IntervalSeconds
            };
        }

        private async Task<DeviceCodeResponse> GetDeviceCodeAsync(CancellationToken token)
        {
            var body = new Dictionary<string, string>
            {
                { "client_id", OAuthClientId },
                { "scope", DeviceCodeScope },
                { "device_id", Guid.NewGuid().ToString() },
                { "device_model", DeviceModel }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/o/oauth2/device/code"))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
                request.Content = new FormUrlEncodedContent(body);

                var response = await httpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var root = JsonObject.Parse(json);

                return new DeviceCodeResponse
                {
                    DeviceCode = GetJsonString(root, "device_code"),
                    UserCode = GetJsonString(root, "user_code"),
                    IntervalSeconds = GetJsonNumberAsInt(root, "interval", 5)
                };
            }
        }

        private async Task<string> GetTvQrBase64Async(string userCode, CancellationToken token)
        {
            var payload = "{\"context\":{\"client\":{\"clientName\":\"TVHTML5\",\"clientVersion\":\"7.20251217.19.00\",\"deviceMake\":\"Samsung\",\"deviceModel\":\"SmartTV\",\"platform\":\"TV\",\"hl\":\"ru\",\"gl\":\"RU\"}},\"handoffQrParams\":{\"rapidQrParams\":{\"qrPresetStyle\":\"HANDOFF_QR_LIMITED_PRESET_STYLE_MODERN_BIG_DOTS_INVERT_WITH_YT_LOGO\",\"userCode\":\"" + JsonEscape(userCode) + "\",\"rapidQrFeature\":\"RAPID_QR_FEATURE_DEFAULT\"}}}";
            var url = "https://www.youtube.com/youtubei/v1/mdx/handoff?key=" + InnertubeApiKey;
            using (var request = new HttpRequestMessage(HttpMethod.Post, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
                request.Content = new StringContent(payload);
                request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
                var response = await httpClient.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var root = JsonObject.Parse(json);
                var qrUrl = root
                    .GetNamedObject("rapidQrRenderer")
                    .GetNamedObject("qrCodeRenderer")
                    .GetNamedObject("qrCodeImage")
                    .GetNamedArray("thumbnails")[0]
                    .GetObject()
                    .GetNamedString("url");
                if (string.IsNullOrWhiteSpace(qrUrl))
                {
                    throw new InvalidOperationException("YouTube did not return a QR code.");
                }
                const string prefix = "base64,";
                var markerIndex = qrUrl.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
                if (markerIndex >= 0)
                {
                    return qrUrl.Substring(markerIndex + prefix.Length);
                }
                var qrBytes = await httpClient.GetByteArrayAsync(qrUrl);
                return Convert.ToBase64String(qrBytes);
            }
        }

        /// <summary>
        /// Безопасно извлекает вложенное строковое значение из JsonObject по цепочке ключей
        /// </summary>
        private static string GetNestedString(JsonObject root, params string[] path)
        {
            if (root == null || path == null || path.Length == 0)
                return null;

            JsonObject current = root;

            for (int i = 0; i < path.Length - 1; i++)
            {
                if (current == null || !current.ContainsKey(path[i]))
                    return null;

                var value = current.GetNamedValue(path[i]);
                if (value?.ValueType != JsonValueType.Object)
                    return null;

                current = value.GetObject();
            }

            // Последний элемент — строка
            if (current == null || !current.ContainsKey(path[path.Length - 1]))
                return null;

            var lastValue = current.GetNamedValue(path[path.Length - 1]);
            return lastValue?.ValueType == JsonValueType.String ? lastValue.GetString() : null;
        }

        private async Task<DeviceTokenResponse> CheckDeviceTokenAsync(string deviceCode, CancellationToken token)
        {
            var body = new Dictionary<string, string>
            {
                { "client_id", OAuthClientId },
                { "client_secret", OAuthClientSecret },
                { "code", deviceCode },
                { "grant_type", "http://oauth.net/grant_type/device/1.0" }
            };

            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/o/oauth2/token"))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", TvUserAgent);
                request.Content = new FormUrlEncodedContent(body);

                var response = await httpClient.SendAsync(request, token);
                var json = await response.Content.ReadAsStringAsync();
                var root = JsonObject.Parse(json);

                return new DeviceTokenResponse
                {
                    AccessToken = GetJsonString(root, "access_token"),
                    RefreshToken = GetJsonString(root, "refresh_token"),
                    Error = GetJsonString(root, "error")
                };
            }
        }

        private static string GetJsonString(JsonObject obj, string key)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return string.Empty;
            }

            var value = obj.GetNamedValue(key);
            return value != null && value.ValueType == JsonValueType.String ? value.GetString() : string.Empty;
        }

        private static int GetJsonNumberAsInt(JsonObject obj, string key, int fallback)
        {
            if (obj == null || !obj.ContainsKey(key))
            {
                return fallback;
            }

            var value = obj.GetNamedValue(key);
            if (value == null || value.ValueType != JsonValueType.Number)
            {
                return fallback;
            }

            return (int)value.GetNumber();
        }

        private static string JsonEscape(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private async Task SetQrImageAsync(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64))
                return;

            try
            {
                var bytes = Convert.FromBase64String(base64);
                var bitmap = new BitmapImage();

                using (var stream = new InMemoryRandomAccessStream())
                {
                    await stream.WriteAsync(bytes.AsBuffer());
                    stream.Seek(0);
                    await bitmap.SetSourceAsync(stream);
                }

                QrImage.Source = bitmap;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SetQrImage error: " + ex.Message);
                StatusText.Text = Localization.Format("QrDisplayErrorFormat", ex.Message);
            }
        }

        private sealed class DeviceFlowState
        {
            public string DeviceCode { get; set; }
            public string UserCode { get; set; }
            public string QrBase64 { get; set; }
            public int IntervalSeconds { get; set; }
        }

        private sealed class DeviceCodeResponse
        {
            public string DeviceCode { get; set; }
            public string UserCode { get; set; }
            public int IntervalSeconds { get; set; }
        }

        private sealed class DeviceTokenResponse
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
            public string Error { get; set; }
        }
    }
}
