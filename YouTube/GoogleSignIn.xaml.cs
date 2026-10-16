using System;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class GoogleSignIn : Page
    {
        private bool _saving;
        private bool _isActive;
        private bool _showingError;

        public GoogleSignIn()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            _isActive = true;
            tabbar.SetActiveTab(Tabbar.ActiveTab.None);
            SystemNavigationManager.GetForCurrentView().BackRequested += BackRequested;
            await WebAccountSession.InitializeAsync();
            if (_isActive) NavigateToSignIn();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            _isActive = false;
            SystemNavigationManager.GetForCurrentView().BackRequested -= BackRequested;
            Browser.Stop();
            base.OnNavigatedFrom(e);
        }

        private void NavigateToSignIn()
        {
            // Keep EdgeHTML's native browser identity through redirects and form submissions.
            Browser.Navigate(new Uri(WebAccountSession.LoginUrl));
        }

        private void Browser_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            if (args.Uri == null || args.Uri.Scheme != "https")
            {
                args.Cancel = true;
                return;
            }
            LoadingBar.Visibility = Visibility.Visible;
        }

        private async void Browser_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            if (!_isActive || _saving || _showingError) return;
            if (!args.IsSuccess) await ShowErrorAndRetryAsync();
            else await CompleteSignInIfReadyAsync(args.Uri);
        }

        private void Browser_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            if (args.Uri != null && args.Uri.Scheme == "https") Browser.Navigate(args.Uri);
        }

        private async Task CompleteSignInIfReadyAsync(Uri uri)
        {
            if (_saving || !WebAccountSession.IsYouTubeUri(uri)
                || (uri.Host != "www.youtube.com" && uri.Host != "m.youtube.com" && uri.Host != "youtube.com")) return;
            _saving = true;
            LoadingBar.Visibility = Visibility.Visible;
            var failed = false;
            try
            {
                // Consent redirects can already have SAPISID without a completed YouTube
                // session. Confirm the signed-in page and keep its selected account context.
                var json = await Browser.InvokeScriptAsync("eval", new[] {
                    "(function(){var c=window.ytcfg;function g(k){return c&&c.get?c.get(k):null;}"
                    + "var r=window.ytInitialData&&window.ytInitialData.responseContext;"
                    + "var m=r&&r.mainAppWebResponseContext;return JSON.stringify({"
                    + "LOGGED_IN:g('LOGGED_IN')===true||(m&&m.loggedOut===false)||false,"
                    + "SESSION_INDEX:g('SESSION_INDEX'),DELEGATED_SESSION_ID:g('DELEGATED_SESSION_ID'),"
                    + "VISITOR_DATA:g('VISITOR_DATA'),INNERTUBE_CLIENT_VERSION:g('INNERTUBE_CLIENT_VERSION'),"
                    + "INNERTUBE_CONTEXT:g('INNERTUBE_CONTEXT')});})()" });
                JsonObject configuration;
                if (!JsonObject.TryParse(json, out configuration)
                    || !WebAccountSession.IsLoggedInConfiguration(configuration)
                    || !WebAccountSession.HasSignInCookies) return;
                await WebAccountSession.CompleteSignInAsync(configuration);
                if (_isActive) LeavePage();
            }
            catch (Exception)
            {
                failed = true;
            }
            finally
            {
                _saving = false;
                LoadingBar.Visibility = Visibility.Collapsed;
            }
            if (failed && _isActive) await ShowErrorAndRetryAsync();
        }

        private async Task ShowErrorAndRetryAsync()
        {
            if (!_isActive || _showingError) return;
            _showingError = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = Localization.GetString("WebAccountSignIn"),
                    Content = Localization.GetString("WebAccountSignInFailed"),
                    PrimaryButtonText = Localization.GetString("Loc_Retry_Content/Content"),
                    SecondaryButtonText = Localization.GetString("Cancel")
                };
                var result = await dialog.ShowAsync();
                if (_isActive && result == ContentDialogResult.Primary) NavigateToSignIn();
            }
            catch (Exception)
            {
                System.Diagnostics.Debug.WriteLine("[WebAccount] Could not show the sign-in error dialog.");
            }
            finally { _showingError = false; }
        }

        private void BackRequested(object sender, BackRequestedEventArgs e)
        {
            e.Handled = true;
            if (_saving) return;
            if (Browser.CanGoBack) Browser.GoBack();
            else LeavePage();
        }
        private void LeavePage()
        {
            if (Frame == null) return;
            if (Frame.CanGoBack) Frame.GoBack();
            else Frame.Navigate(typeof(Settings));
        }
    }
}
