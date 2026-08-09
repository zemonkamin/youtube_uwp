using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Data;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Navigation;
using System.Threading.Tasks;
using Windows.ApplicationModel.Background;
using Windows.Data.Xml.Dom;
using Windows.Storage;
using Windows.UI.Notifications;

namespace YouTube
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    sealed partial class App : Application
    {
        private sealed class YouTubeNavigationTarget
        {
            public Type PageType { get; set; }
            public object Parameter { get; set; }
            public string DebugName { get; set; }
        }

        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
            this.Suspending += OnSuspending;

            // The process was dying with only "exited with code -1" in the log, which says nothing
            // about the cause. These two handlers name the actual exception before it takes the
            // app down — an unobserved task exception is just as fatal here and is easy to miss.
            this.UnhandledException += App_UnhandledException;
            TaskScheduler.UnobservedTaskException += App_UnobservedTaskException;
        }

        private void App_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine("[Crash] Unhandled: " + e.Message);

            if (e.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine("[Crash] " + e.Exception.GetType().FullName
                    + ": " + e.Exception.Message);
                System.Diagnostics.Debug.WriteLine("[Crash] Stack: " + e.Exception.StackTrace);

                var inner = e.Exception.InnerException;
                while (inner != null)
                {
                    System.Diagnostics.Debug.WriteLine("[Crash] Inner: " + inner.GetType().FullName
                        + ": " + inner.Message);
                    System.Diagnostics.Debug.WriteLine("[Crash] Inner stack: " + inner.StackTrace);
                    inner = inner.InnerException;
                }
            }
        }

        private void App_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception != null)
            {
                System.Diagnostics.Debug.WriteLine("[Crash] Unobserved task exception: "
                    + e.Exception.GetType().FullName + ": " + e.Exception.Message);
                System.Diagnostics.Debug.WriteLine("[Crash] Stack: " + e.Exception.StackTrace);

                foreach (var single in e.Exception.InnerExceptions)
                {
                    System.Diagnostics.Debug.WriteLine("[Crash] -> " + single.GetType().FullName
                        + ": " + single.Message);
                    System.Diagnostics.Debug.WriteLine("[Crash] -> stack: " + single.StackTrace);
                }
            }

            e.SetObserved();
        }

        /// <summary>
        /// Invoked when the application is launched normally by the end user.  Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">Details about the launch request and process.</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
#if DEBUG
            if (System.Diagnostics.Debugger.IsAttached)
            {
                this.DebugSettings.EnableFrameRateCounter = true;
            }
#endif
            var target = ParseYouTubeNavigationTarget(e != null ? e.Arguments : null);
            InitializeRootFrame(target, e != null ? e.PrelaunchActivated : false, e != null ? e.Arguments : null);
        }

        protected override void OnActivated(IActivatedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("[App] Activated: " + (args != null ? args.Kind.ToString() : "null"));

            YouTubeNavigationTarget target = null;
            if (args != null && args.Kind == ActivationKind.Protocol)
            {
                var protocolArgs = args as ProtocolActivatedEventArgs;
                if (protocolArgs != null && protocolArgs.Uri != null)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Protocol URI: " + protocolArgs.Uri.AbsoluteUri);
                    target = ParseYouTubeNavigationTarget(protocolArgs.Uri.AbsoluteUri);
                }
            }
            else if (args != null && args.Kind == ActivationKind.ToastNotification)
            {
                var toastArgs = args as ToastNotificationActivatedEventArgs;
                if (toastArgs != null)
                {
                    System.Diagnostics.Debug.WriteLine("[App] Toast arguments: " + toastArgs.Argument);
                    target = ParseYouTubeNavigationTarget(toastArgs.Argument);
                }
            }

            InitializeRootFrame(target, false, null);
            base.OnActivated(args);
        }

        private void InitializeRootFrame(YouTubeNavigationTarget target, bool prelaunchActivated, string launchArguments)
        {
            Frame rootFrame = Window.Current.Content as Frame;

            if (rootFrame == null)
            {
                rootFrame = new Frame();
                rootFrame.NavigationFailed += OnNavigationFailed;
                Window.Current.Content = rootFrame;
            }

            if (!prelaunchActivated)
            {
                Config.LoadUserToken();
#pragma warning disable 4014
                YouTubeNotificationService.InitializeAsync();
#pragma warning restore 4014

                if (target != null)
                {
                    if (string.IsNullOrEmpty(Config.UserToken) && rootFrame.Content == null)
                    {
                        System.Diagnostics.Debug.WriteLine("[App] Deep link received, but user is not authenticated. Navigating to Login.");
                        rootFrame.Navigate(typeof(Login));
                    }
                    else
                    {
                        NavigateToYouTubeTarget(rootFrame, target);
                    }
                }
                else if (rootFrame.Content == null)
                {
                    if (!string.IsNullOrEmpty(Config.UserToken))
                    {
                        rootFrame.Navigate(typeof(Home), launchArguments);
                    }
                    else
                    {
                        rootFrame.Navigate(typeof(Login), launchArguments);
                    }
                }

                Window.Current.Activate();
            }
        }

        private static void NavigateToYouTubeTarget(Frame rootFrame, YouTubeNavigationTarget target)
        {
            if (rootFrame == null || target == null || target.PageType == null)
            {
                return;
            }

            System.Diagnostics.Debug.WriteLine(
                "[App] Navigating from YouTube link to "
                + target.DebugName
                + " with parameter: "
                + (target.Parameter != null ? target.Parameter.ToString() : "null")
            );

            rootFrame.Navigate(target.PageType, target.Parameter);
        }

        private static YouTubeNavigationTarget ParseYouTubeNavigationTarget(string rawInput)
        {
            if (string.IsNullOrWhiteSpace(rawInput))
            {
                return null;
            }

            var directVideoId = ExtractDirectVideoActivationId(rawInput);
            if (!string.IsNullOrWhiteSpace(directVideoId))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Video),
                    Parameter = directVideoId,
                    DebugName = "Video"
                };
            }

            var url = NormalizeActivationUrl(rawInput);
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            System.Diagnostics.Debug.WriteLine("[App] Parsing YouTube URL: " + url);

            string videoId = ExtractShortsVideoId(url);
            if (!string.IsNullOrWhiteSpace(videoId))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Shorts),
                    Parameter = videoId,
                    DebugName = "Shorts"
                };
            }

            videoId = ExtractYouTubeVideoId(url);
            if (!string.IsNullOrWhiteSpace(videoId))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Video),
                    Parameter = videoId,
                    DebugName = "Video"
                };
            }

            string playlistId = ExtractYouTubePlaylistId(url);
            if (!string.IsNullOrWhiteSpace(playlistId))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Playlist),
                    Parameter = playlistId,
                    DebugName = "Playlist"
                };
            }

            string channelTarget = ExtractYouTubeChannelTarget(url);
            if (!string.IsNullOrWhiteSpace(channelTarget))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Channel),
                    Parameter = channelTarget,
                    DebugName = "Channel"
                };
            }

            string searchQuery = ExtractYouTubeSearchQuery(url);
            if (!string.IsNullOrWhiteSpace(searchQuery))
            {
                return new YouTubeNavigationTarget
                {
                    PageType = typeof(Search),
                    Parameter = searchQuery,
                    DebugName = "Search"
                };
            }

            return null;
        }

        private static string NormalizeActivationUrl(string rawInput)
        {
            var input = (rawInput ?? string.Empty).Trim();
            if (input.Length == 0)
            {
                return string.Empty;
            }

            // Custom protocol can pass the real URL as ?url=...
            var wrappedUrl = GetQueryParameter(input, "url");
            if (!string.IsNullOrWhiteSpace(wrappedUrl))
            {
                return Uri.UnescapeDataString(wrappedUrl).Replace("&amp;", "&");
            }

            // Support youtubehandler:https://www.youtube.com/watch?v=...
            var httpIndex = input.IndexOf("http", StringComparison.OrdinalIgnoreCase);
            if (input.StartsWith("youtubehandler", StringComparison.OrdinalIgnoreCase) && httpIndex >= 0)
            {
                return Uri.UnescapeDataString(input.Substring(httpIndex)).Replace("&amp;", "&");
            }

            return Uri.UnescapeDataString(input).Replace("&amp;", "&");
        }

        private static string ExtractDirectVideoActivationId(string rawInput)
        {
            try
            {
                var input = (rawInput ?? string.Empty).Trim();
                if (input.Length == 0)
                {
                    return string.Empty;
                }

                var videoId = GetQueryParameter(input, "videoId");
                if (IsYouTubeVideoId(videoId))
                {
                    return videoId;
                }

                videoId = GetQueryParameter(input, "v");
                if (IsYouTubeVideoId(videoId))
                {
                    return videoId;
                }

                var match = Regex.Match(
                    input,
                    @"(?:^|[?&;])openVideo=([A-Za-z0-9_-]{11})(?:$|[&#;])|^ytvideo:([A-Za-z0-9_-]{11})$",
                    RegexOptions.IgnoreCase
                );

                if (match.Success)
                {
                    videoId = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                    if (IsYouTubeVideoId(videoId))
                    {
                        return videoId;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractShortsVideoId(string url)
        {
            try
            {
                var match = Regex.Match(
                    url,
                    @"(?:youtube\.com|m\.youtube\.com|www\.youtube\.com)/shorts/([A-Za-z0-9_-]{11})",
                    RegexOptions.IgnoreCase
                );

                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractYouTubeVideoId(string url)
        {
            try
            {
                var videoId = GetQueryParameter(url, "v");
                if (IsYouTubeVideoId(videoId))
                {
                    return videoId;
                }

                var match = Regex.Match(
                    url,
                    @"(?:youtu\.be/|youtube\.com/(?:embed/|v/|live/)|m\.youtube\.com/(?:embed/|v/|live/)|www\.youtube\.com/(?:embed/|v/|live/))([A-Za-z0-9_-]{11})",
                    RegexOptions.IgnoreCase
                );

                if (match.Success)
                {
                    return match.Groups[1].Value;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractYouTubePlaylistId(string url)
        {
            try
            {
                var playlistId = GetQueryParameter(url, "list");
                if (!string.IsNullOrWhiteSpace(playlistId))
                {
                    return playlistId;
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        // https://www.youtube.com/results?search_query=...
        // Companion apps that only know a track name (and not a video id) can hand the query
        // over this way; the Search page takes the raw query string as its parameter.
        private static string ExtractYouTubeSearchQuery(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return string.Empty;
                }

                var isResultsPage = Regex.IsMatch(
                    url,
                    @"(?:youtube\.com|m\.youtube\.com|www\.youtube\.com)/results",
                    RegexOptions.IgnoreCase
                );

                if (!isResultsPage)
                {
                    return string.Empty;
                }

                var query = GetQueryParameter(url, "search_query");
                if (string.IsNullOrWhiteSpace(query))
                {
                    query = GetQueryParameter(url, "q");
                }

                return (query ?? string.Empty).Trim();
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string ExtractYouTubeChannelTarget(string url)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    return string.Empty;
                }

                // https://www.youtube.com/@GLITCH or https://youtube.com/@GLITCH/videos
                var handleMatch = Regex.Match(
                    url,
                    @"(?:youtube\.com|m\.youtube\.com|www\.youtube\.com)/@([^/?#&]+)",
                    RegexOptions.IgnoreCase
                );

                if (handleMatch.Success)
                {
                    var handle = CleanChannelHandle(handleMatch.Groups[1].Value);
                    if (!string.IsNullOrWhiteSpace(handle))
                    {
                        return "@" + handle;
                    }
                }

                // https://www.youtube.com/channel/UC...
                var channelIdMatch = Regex.Match(
                    url,
                    @"(?:youtube\.com|m\.youtube\.com|www\.youtube\.com)/channel/([^/?#&]+)",
                    RegexOptions.IgnoreCase
                );

                if (channelIdMatch.Success)
                {
                    var channelId = channelIdMatch.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(channelId))
                    {
                        return channelId;
                    }
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static string CleanChannelHandle(string rawHandle)
        {
            if (string.IsNullOrWhiteSpace(rawHandle))
            {
                return string.Empty;
            }

            var handle = rawHandle.Trim().TrimStart('@').Trim('/');
            try
            {
                handle = Uri.UnescapeDataString(handle);
            }
            catch
            {
            }

            return handle.Trim();
        }

        private static string GetQueryParameter(string url, string key)
        {
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            try
            {
                var questionIndex = url.IndexOf('?');
                if (questionIndex < 0 || questionIndex >= url.Length - 1)
                {
                    return string.Empty;
                }

                var fragmentIndex = url.IndexOf('#', questionIndex + 1);
                var query = fragmentIndex >= 0
                    ? url.Substring(questionIndex + 1, fragmentIndex - questionIndex - 1)
                    : url.Substring(questionIndex + 1);

                var parts = query.Split('&');
                for (int i = 0; i < parts.Length; i++)
                {
                    var part = parts[i];
                    if (string.IsNullOrWhiteSpace(part))
                    {
                        continue;
                    }

                    var equalsIndex = part.IndexOf('=');
                    var name = equalsIndex >= 0 ? part.Substring(0, equalsIndex) : part;
                    if (!string.Equals(Uri.UnescapeDataString(name), key, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var value = equalsIndex >= 0 ? part.Substring(equalsIndex + 1) : string.Empty;
                    return Uri.UnescapeDataString(value.Replace("+", " "));
                }
            }
            catch
            {
            }

            return string.Empty;
        }

        private static bool IsYouTubeVideoId(string value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 11)
            {
                return false;
            }

            for (int i = 0; i < value.Length; i++)
            {
                var c = value[i];
                if (!char.IsLetterOrDigit(c) && c != '_' && c != '-')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">The Frame which failed navigation</param>
        /// <param name="e">Details about the navigation failure</param>
        void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            throw new Exception("Failed to load Page " + e.SourcePageType.FullName);
        }

        /// <summary>
        /// Invoked when application execution is being suspended.  Application state is saved
        /// without knowing whether the application will be terminated or resumed with the contents
        /// of memory still intact.
        /// </summary>
        /// <param name="sender">The source of the suspend request.</param>
        /// <param name="e">Details about the suspend request.</param>
        private void OnSuspending(object sender, SuspendingEventArgs e)
        {
            var deferral = e.SuspendingOperation.GetDeferral();
            //TODO: Save application state and stop any background activity
            deferral.Complete();
        }
    }

    public sealed class NotificationBackgroundTask : IBackgroundTask
    {
        public async void Run(IBackgroundTaskInstance taskInstance)
        {
            var deferral = taskInstance.GetDeferral();
            try
            {
                Config.LoadUserToken();
                await YouTubeNotificationService.RefreshAndShowNewVideoNotificationsAsync("background");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background task error: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        }
    }

    internal static class YouTubeNotificationService
    {
        private const string BackgroundTaskName = "YouTubeNewVideoNotificationTask";
        private const string BackgroundTaskEntryPoint = "YouTube.NotificationBackgroundTask";
        private const string KnownNotificationVideoIdsKey = "yt_known_notification_video_ids";
        private const string LastToastCheckUtcKey = "yt_last_toast_check_utc";
        private const string FirstNotificationSeedDoneKey = "yt_first_notification_seed_done";
        public const string NotificationsEnabledSettingKey = "yt_notifications_enabled";
        private const string NotificationsDefaultOffMigrationKey = "yt_notifications_default_off_v1";
        public const string NotificationsIntervalMinutesSettingKey = "yt_notifications_interval_minutes";
        private const int DefaultNotificationIntervalMinutes = 60;
        private const int MinNotificationIntervalMinutes = 15;
        private const int BackgroundMinimumMinutesBetweenChecks = 55;
        private const int MaxNotificationsToFetch = 20;
        private const int MaxToastsPerCheck = 5;
        private const int HardMaxToastsPerBatch = 5;
        private const int MaxKnownVideoIds = 250;
        private const string FallbackToastImage = "ms-appx:///Assets/Square44x44Logo.png";
        private static bool _isChecking;
        private static bool _backgroundRegistrationStarted;
        private static DispatcherTimer _foregroundNotificationTimer;
        private static bool _foregroundNotificationTimerStarted;
        private static bool _toastNotifierUnavailable;

        public static bool AreNotificationsEnabled()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;

                // One-time migration: older builds created the setting as true automatically.
                // That makes Settings look enabled even though the new product default is OFF.
                // Force it off once after upgrading; all later user choices are preserved.
                if (!values.ContainsKey(NotificationsDefaultOffMigrationKey))
                {
                    values[NotificationsDefaultOffMigrationKey] = true;
                    values[NotificationsEnabledSettingKey] = false;
                    return false;
                }

                if (!values.ContainsKey(NotificationsEnabledSettingKey))
                {
                    values[NotificationsEnabledSettingKey] = false;
                    return false;
                }

                var value = values[NotificationsEnabledSettingKey];
                if (value is bool)
                {
                    return (bool)value;
                }

                bool parsed;
                if (value != null && bool.TryParse(value.ToString(), out parsed))
                {
                    return parsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[ToastNotifications] Read enabled setting failed: " + ex.Message);
            }

            return false;
        }

        public static void SetNotificationsEnabled(bool enabled)
        {
            ApplicationData.Current.LocalSettings.Values[NotificationsEnabledSettingKey] = enabled;
        }

        public static int GetNotificationIntervalMinutes()
        {
            try
            {
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.ContainsKey(NotificationsIntervalMinutesSettingKey))
                {
                    values[NotificationsIntervalMinutesSettingKey] = DefaultNotificationIntervalMinutes;
                    return DefaultNotificationIntervalMinutes;
                }

                var value = values[NotificationsIntervalMinutesSettingKey];
                if (value is int)
                {
                    return NormalizeNotificationInterval((int)value);
                }

                if (value is long)
                {
                    return NormalizeNotificationInterval((int)(long)value);
                }

                int parsed;
                if (int.TryParse(value == null ? string.Empty : value.ToString(), out parsed))
                {
                    return NormalizeNotificationInterval(parsed);
                }
            }
            catch
            {
            }

            return DefaultNotificationIntervalMinutes;
        }

        public static void SetNotificationIntervalMinutes(int minutes)
        {
            ApplicationData.Current.LocalSettings.Values[NotificationsIntervalMinutesSettingKey] = NormalizeNotificationInterval(minutes);
        }

        public static string GetNotificationFrequencyDisplayText()
        {
            if (!AreNotificationsEnabled())
            {
                return "Off";
            }

            var minutes = GetNotificationIntervalMinutes();
            if (minutes < 60)
            {
                return "Every " + minutes + " min";
            }

            if (minutes == 60)
            {
                return "Every hour";
            }

            if (minutes % 60 == 0)
            {
                var hours = minutes / 60;
                return hours == 1 ? "Every hour" : "Every " + hours + " hours";
            }

            return "Every " + minutes + " min";
        }

        private static int NormalizeNotificationInterval(int minutes)
        {
            if (minutes < MinNotificationIntervalMinutes)
            {
                return MinNotificationIntervalMinutes;
            }

            return minutes;
        }

        public static async Task ReconfigureBackgroundTaskAsync()
        {
            StopForegroundNotificationTimer();
            UnregisterBackgroundTask();
            _backgroundRegistrationStarted = false;
            await RegisterBackgroundTaskAsync();
        }

        private static void UnregisterBackgroundTask()
        {
            try
            {
                var tasks = BackgroundTaskRegistration.AllTasks.ToList();
                for (int i = 0; i < tasks.Count; i++)
                {
                    var task = tasks[i];
                    if (task.Value != null && string.Equals(task.Value.Name, BackgroundTaskName, StringComparison.Ordinal))
                    {
                        task.Value.Unregister(true);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background unregister error: " + ex.Message);
            }
        }

        public static async Task InitializeAsync()
        {
            try
            {
                // Register the periodic notification mechanism, but do NOT start a network fetch
                // here. App.OnLaunched starts this service at the same time Home is loading its
                // recommendations; fetching subscriptions/notifications here competes with the
                // first Home request and makes the feed appear much slower.
                //
                // Home triggers the first foreground fetch after its first recommendation page is
                // already visible. Background/foreground timers continue to work normally.
                await RegisterBackgroundTaskAsync();

                System.Diagnostics.Debug.WriteLine(
                    "[ToastNotifications] Service initialized; startup fetch deferred to Home");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Initialize error: " + ex.Message);
            }
        }

        public static async Task RegisterBackgroundTaskAsync()
        {
            if (_backgroundRegistrationStarted)
            {
                return;
            }

            _backgroundRegistrationStarted = true;

            if (!AreNotificationsEnabled())
            {
                StopForegroundNotificationTimer();
                UnregisterBackgroundTask();
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background notifications disabled");
                return;
            }

            try
            {
                foreach (var task in BackgroundTaskRegistration.AllTasks)
                {
                    if (task.Value != null && string.Equals(task.Value.Name, BackgroundTaskName, StringComparison.Ordinal))
                    {
                        System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background task already registered");
                        StopForegroundNotificationTimer();
                        return;
                    }
                }

                var accessStatus = await BackgroundExecutionManager.RequestAccessAsync();
                var accessStatusText = accessStatus.ToString();
                if (accessStatusText.IndexOf("Denied", StringComparison.OrdinalIgnoreCase) >= 0
                    || accessStatusText.IndexOf("Unspecified", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background access is not available: " + accessStatusText + ". Using foreground timer fallback.");
                    StartForegroundNotificationTimer("background access denied");
                    return;
                }

                var interval = GetNotificationIntervalMinutes();
                var builder = new BackgroundTaskBuilder();
                builder.Name = BackgroundTaskName;
                builder.TaskEntryPoint = BackgroundTaskEntryPoint;
                builder.SetTrigger(new TimeTrigger((uint)interval, false));
                builder.AddCondition(new SystemCondition(SystemConditionType.InternetAvailable));
                builder.Register();

                StopForegroundNotificationTimer();
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background task registered, interval=" + interval + " min");
            }
            catch (Exception ex)
            {
                _backgroundRegistrationStarted = false;

                if (IsBackgroundBrokerUnavailable(ex))
                {
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background task broker is unavailable. Using foreground timer fallback instead.");
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Background registration failed. Using foreground timer fallback: " + ex.Message);
                }

                StartForegroundNotificationTimer("background registration fallback");
            }
        }

        private static bool IsBackgroundBrokerUnavailable(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            // 0x80080005 / CO_E_SERVER_EXEC_FAILURE happens on some Windows 10 / Mobile
            // builds when the background task broker cannot start. It is not a notification
            // logic error, so we silently fall back to an in-app timer.
            return ex.HResult == unchecked((int)0x80080005);
        }

        private static void EnsureForegroundNotificationTimer()
        {
            if (!AreNotificationsEnabled())
            {
                StopForegroundNotificationTimer();
                return;
            }

            if (_foregroundNotificationTimerStarted)
            {
                ConfigureForegroundNotificationTimerInterval();
                return;
            }

            StartForegroundNotificationTimer("ensure");
        }

        private static void StartForegroundNotificationTimer(string reason)
        {
            try
            {
                if (!AreNotificationsEnabled())
                {
                    StopForegroundNotificationTimer();
                    return;
                }

                if (_foregroundNotificationTimer == null)
                {
                    _foregroundNotificationTimer = new DispatcherTimer();
                    _foregroundNotificationTimer.Tick += ForegroundNotificationTimer_Tick;
                }

                ConfigureForegroundNotificationTimerInterval();
                _foregroundNotificationTimerStarted = true;
                _foregroundNotificationTimer.Stop();
                _foregroundNotificationTimer.Start();
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Foreground notification timer started, interval=" + GetNotificationIntervalMinutes() + " min, reason=" + reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Foreground timer start error: " + ex.Message);
            }
        }

        private static void StopForegroundNotificationTimer()
        {
            try
            {
                if (_foregroundNotificationTimer != null)
                {
                    _foregroundNotificationTimer.Stop();
                }
            }
            catch
            {
            }

            _foregroundNotificationTimerStarted = false;
        }

        private static void ConfigureForegroundNotificationTimerInterval()
        {
            if (_foregroundNotificationTimer == null)
            {
                return;
            }

            var minutes = GetNotificationIntervalMinutes();
            if (minutes < MinNotificationIntervalMinutes)
            {
                minutes = MinNotificationIntervalMinutes;
            }

            _foregroundNotificationTimer.Interval = TimeSpan.FromMinutes(minutes);
        }

        private static async void ForegroundNotificationTimer_Tick(object sender, object e)
        {
            await RefreshAndShowNewVideoNotificationsAsync("foreground-timer");
        }

        public static async Task RefreshAndShowNewVideoNotificationsAsync(string reason)
        {
            if (_isChecking)
            {
                return;
            }

            if (!AreNotificationsEnabled())
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Skipped because notifications are disabled");
                return;
            }

            _isChecking = true;
            try
            {
                Config.LoadUserToken();
                if (string.IsNullOrWhiteSpace(Config.UserToken))
                {
                    return;
                }

                var items = await Config.GetNotificationsAsync(Config.UserToken, MaxNotificationsToFetch);
                var shown = 0;
                var skippedAlreadySent = 0;
                var knownVideoIds = LoadKnownVideoIds();

                var maxToastsThisBatch = Math.Min(MaxToastsPerCheck, HardMaxToastsPerBatch);

                // Notifications produced by the app opening should not interrupt the user with a
                // banner — they land silently in the Action Center. A check the user triggers while
                // the app is running (foreground timer, background task) still pops normally.
                var suppressPopup = string.Equals(reason, "app-startup", StringComparison.Ordinal);

                if (items != null && !_toastNotifierUnavailable)
                {
                    for (int i = 0; i < items.Count && shown < maxToastsThisBatch; i++)
                    {
                        var item = items[i];
                        if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
                        {
                            continue;
                        }

                        var videoId = item.VideoId.Trim();
                        if (knownVideoIds.Contains(videoId))
                        {
                            skippedAlreadySent++;
                            continue;
                        }

                        if (ShowNewVideoToast(item, suppressPopup))
                        {
                            shown++;
                            knownVideoIds.Insert(0, videoId);
                        }
                        else if (_toastNotifierUnavailable)
                        {
                            System.Diagnostics.Debug.WriteLine("[ToastNotifications] Toast broker is unavailable; stopping this notification batch.");
                            break;
                        }
                    }
                }
                else if (_toastNotifierUnavailable)
                {
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Toast broker is unavailable; skipped showing fetched notifications.");
                }

                SaveKnownVideoIds(knownVideoIds);
                SaveLastCheckUtc();

                var checkedCount = items == null ? 0 : items.Count;
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Sent " + shown + " toast(s) from " + checkedCount + " fetched notification(s), max per batch=" + maxToastsThisBatch + ", skipped already sent=" + skippedAlreadySent + ", reason=" + reason);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[ToastNotifications] Refresh error: " + ex.Message);
            }
            finally
            {
                _isChecking = false;
            }
        }

        private static bool ShowNewVideoToast(NotificationItem item, bool suppressPopup)
        {
            if (_toastNotifierUnavailable)
            {
                return false;
            }

            if (item == null || string.IsNullOrWhiteSpace(item.VideoId))
            {
                return false;
            }

            try
            {
                var rawVideoTitle = FirstNonEmpty(item.VideoTitle, ExtractVideoTitleFromMessage(item.Message), item.Message, "YouTube video");
                if (IsGenericNewVideoToastText(rawVideoTitle))
                {
                    rawVideoTitle = FirstNonEmpty(item.VideoTitle, ExtractVideoTitleFromMessage(item.Message), "YouTube video");
                }

                var videoTitle = TrimForToast(rawVideoTitle, 110);
                var author = TrimForToast(FirstNonEmpty(item.Author, item.Title, "YouTube"), 70);
                var image = EscapeXml(FirstNonEmpty(NormalizeImageUrl(item.ThumbnailUrl), NormalizeImageUrl(item.AvatarUrl), FallbackToastImage));
                var launch = EscapeXml("openVideo=" + item.VideoId);

                // ToastGeneric is more stable on Windows 10 Mobile for multiline text.
                // appLogoOverride keeps the preview/icon on the left while text lines stay visible.
                var xml = "<toast launch=\"" + launch + "\" activationType=\"foreground\">"
                    + "<visual>"
                    + "<binding template=\"ToastGeneric\">"
                    + "<image placement=\"appLogoOverride\" hint-crop=\"none\" src=\"" + image + "\"/>"
                    + "<text hint-maxLines=\"2\">" + EscapeXml(videoTitle) + "</text>"
                    + "<text hint-maxLines=\"1\">" + EscapeXml(author) + "</text>"
                    + "</binding>"
                    + "</visual>"
                    + "</toast>";

                var document = new XmlDocument();
                document.LoadXml(xml);

                var toast = new ToastNotification(document);
                toast.Tag = MakeToastTag(item.VideoId + "-" + DateTime.UtcNow.Ticks.ToString());
                toast.Group = "youtube-new-videos";
                toast.ExpirationTime = DateTimeOffset.Now.AddHours(6);

                // Straight to the Action Center, no banner and no sound.
                toast.SuppressPopup = suppressPopup;

                ToastNotificationManager.CreateToastNotifier().Show(toast);
                return true;
            }
            catch (Exception ex)
            {
                if (IsToastBrokerUnavailable(ex))
                {
                    _toastNotifierUnavailable = true;
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Toast broker is unavailable in this app session; system toast sending is disabled until restart. " + ex.Message);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("[ToastNotifications] Toast show error: " + ex.Message);
                }

                return false;
            }
        }

        private static bool IsToastBrokerUnavailable(Exception ex)
        {
            if (ex == null)
            {
                return false;
            }

            return ex.HResult == unchecked((int)0x80080005);
        }

        private static bool IsFirstNotificationSeedDone()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Values.ContainsKey(FirstNotificationSeedDoneKey))
            {
                return false;
            }

            var value = settings.Values[FirstNotificationSeedDoneKey];
            return value is bool && (bool)value;
        }

        private static void MarkFirstNotificationSeedDone()
        {
            ApplicationData.Current.LocalSettings.Values[FirstNotificationSeedDoneKey] = true;
        }

        private static bool HasEnoughTimePassedSinceLastCheck()
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (!settings.Values.ContainsKey(LastToastCheckUtcKey))
            {
                return true;
            }

            var raw = settings.Values[LastToastCheckUtcKey] as string;
            DateTime lastCheckUtc;
            if (string.IsNullOrWhiteSpace(raw) || !DateTime.TryParse(raw, out lastCheckUtc))
            {
                return true;
            }

            if (lastCheckUtc.Kind != DateTimeKind.Utc)
            {
                lastCheckUtc = lastCheckUtc.ToUniversalTime();
            }

            return (DateTime.UtcNow - lastCheckUtc).TotalMinutes >= BackgroundMinimumMinutesBetweenChecks;
        }

        private static List<string> LoadKnownVideoIds()
        {
            var settings = ApplicationData.Current.LocalSettings;
            var raw = settings.Values.ContainsKey(KnownNotificationVideoIdsKey)
                ? settings.Values[KnownNotificationVideoIdsKey] as string
                : string.Empty;

            var result = new List<string>();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return result;
            }

            var parts = raw.Split('|');
            for (int i = 0; i < parts.Length; i++)
            {
                var id = (parts[i] ?? string.Empty).Trim();
                if (id.Length > 0 && !result.Contains(id))
                {
                    result.Add(id);
                }
            }

            return result;
        }

        private static void SaveKnownVideoIds(List<string> ids)
        {
            if (ids == null)
            {
                ids = new List<string>();
            }

            var clean = new List<string>();
            for (int i = 0; i < ids.Count && clean.Count < MaxKnownVideoIds; i++)
            {
                var id = (ids[i] ?? string.Empty).Trim();
                if (id.Length > 0 && !clean.Contains(id))
                {
                    clean.Add(id);
                }
            }

            ApplicationData.Current.LocalSettings.Values[KnownNotificationVideoIdsKey] = string.Join("|", clean);
        }

        private static void SaveLastCheckUtc()
        {
            ApplicationData.Current.LocalSettings.Values[LastToastCheckUtcKey] = DateTime.UtcNow.ToString("o");
        }

        private static bool IsGenericNewVideoToastText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return true;
            }

            var normalized = value.Trim().ToLowerInvariant();

            return normalized == "new video"
                || normalized == "new video is out"
                || normalized == "uploaded a video"
                || normalized == "вышло новое видео";
        }

        private static string ExtractVideoTitleFromMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return string.Empty;
            }

            var value = message.Trim();
            var firstQuote = value.IndexOf('"');
            var lastQuote = value.LastIndexOf('"');
            if (firstQuote >= 0 && lastQuote > firstQuote)
            {
                return value.Substring(firstQuote + 1, lastQuote - firstQuote - 1).Trim();
            }

            return value
                .Replace("Uploaded a video", string.Empty)
                .Replace("uploaded a video", string.Empty)
                .Trim(' ', ':', '-', '—');
        }

        private static string NormalizeImageUrl(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return string.Empty;
            }

            var value = imageUrl.Trim();
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                return "https:" + value;
            }

            return value;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null)
            {
                return string.Empty;
            }

            for (int i = 0; i < values.Length; i++)
            {
                if (!string.IsNullOrWhiteSpace(values[i]))
                {
                    return values[i].Trim();
                }
            }

            return string.Empty;
        }

        private static string TrimForToast(string value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var trimmed = value.Trim();
            if (trimmed.Length <= maxLength)
            {
                return trimmed;
            }

            return trimmed.Substring(0, Math.Max(0, maxLength - 1)).TrimEnd() + "…";
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;")
                .Replace("'", "&apos;");
        }

        private static string MakeToastTag(string videoId)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return "video-" + DateTime.UtcNow.Ticks.ToString();
            }

            var value = videoId.Trim();
            if (value.Length > 64)
            {
                value = value.Substring(0, 64);
            }

            return value;
        }
    }


}
