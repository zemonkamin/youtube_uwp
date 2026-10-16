using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace YouTube.Innertube
{
    // Troubadour YTNSig + Resources/nsig.js. Probe the URL object's behavior
    // instead of guessing a minified function name from the player script.
    internal sealed class PlayerNSig
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _answers = new Dictionary<string, string>();
        private WebView _view;
        private Panel _host;
        private string _solver, _preparedFor;
        private bool _ready;
        private Task<bool> _attached;
        private int _generation;

        internal async Task<string> TransformAsync(Panel host, string scriptUrl, string script, string value)
        {
            var complete = new TaskCompletionSource<string>();
            var generation = _generation;
            await host.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                await _gate.WaitAsync();
                try
                {
                    if (generation != _generation) { complete.TrySetResult(string.Empty); return; }
                    var key = scriptUrl + "\n" + value;
                    string answer;
                    if (_answers.TryGetValue(key, out answer)) { complete.TrySetResult(answer); return; }
                    if (!_ready || _preparedFor != scriptUrl)
                        await PrepareAsync(host, scriptUrl, script, generation);
                    if (!_ready || generation != _generation) { complete.TrySetResult(string.Empty); return; }
                    System.Diagnostics.Debug.WriteLine("[NSig] Applying prepared solver");
                    answer = await EvaluateAsync(_view,
                        "(function(){try{return window.YTn(" + Quote(value) + ")||'';}catch(e){return '';}})()");
                    if (generation != _generation || string.IsNullOrEmpty(answer) || answer == value)
                        answer = string.Empty;
                    else
                    {
                        if (_answers.Count >= 64) _answers.Clear();
                        _answers[key] = answer;
                    }
                    complete.TrySetResult(answer);
                }
                catch (Exception ex)
                {
                    _ready = false;
                    System.Diagnostics.Debug.WriteLine("[NSig] WebView failed: " + ex.Message);
                    complete.TrySetResult(string.Empty);
                }
                finally { _gate.Release(); }
            });
            return await complete.Task;
        }

        private async Task PrepareAsync(Panel host, string scriptUrl, string script, int generation)
        {
            _ready = false;
            _preparedFor = scriptUrl;
            if (_solver == null)
            {
                var asset = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/nsig.js"));
                _solver = await FileIO.ReadTextAsync(asset);
            }
            if (generation != _generation) return;
            if (_view == null)
            {
                _view = new WebView(WebViewExecutionMode.SeparateThread) { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                _host = host;
                var attached = new TaskCompletionSource<bool>();
                var attachingView = _view;
                RoutedEventHandler attachedHandler = null;
                attachedHandler = (sender, e) =>
                {
                    attachingView.Loaded -= attachedHandler;
                    attached.TrySetResult(true);
                };
                attachingView.Loaded += attachedHandler;
                _attached = attached.Task;
                host.Children.Add(_view);
            }
            // Navigation before the first Loaded event can miss NavigationCompleted
            // on the legacy WebView and cost a full timeout on every first boot.
            if (_attached != null && await Task.WhenAny(_attached, Task.Delay(5000)) != _attached)
            { System.Diagnostics.Debug.WriteLine("[NSig] Solver host did not load"); return; }
            if (generation != _generation) return;
            var view = _view;
            var loaded = new TaskCompletionSource<bool>();
            Windows.Foundation.TypedEventHandler<WebView, WebViewNavigationCompletedEventArgs> handler = null;
            handler = (sender, e) => { view.NavigationCompleted -= handler; loaded.TrySetResult(e.IsSuccess); };
            view.NavigationCompleted += handler;
            // Keep NavigateToString small; a player script can exceed its HTML size limit.
            view.NavigateToString("<html><head><meta charset=\"utf-8\"></head><body></body></html>");
            if (await Task.WhenAny(loaded.Task, Task.Delay(5000)) != loaded.Task || !await loaded.Task)
            {
                view.NavigationCompleted -= handler;
                System.Diagnostics.Debug.WriteLine("[NSig] Solver page did not load");
                return;
            }
            if (generation != _generation) return;
            await EvaluateAsync(view, _solver);
            await EvaluateAsync(view, "window.__ytPlayerScript='';'';");
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            // Transfer bounded chunks to the legacy WebView; never interpolate raw JS
            // in HTML. Troubadour's bootBase64 decodes the same player bytes.
            for (var offset = 0; offset < payload.Length; offset += 65536)
            {
                if (generation != _generation) return;
                await EvaluateAsync(view, "window.__ytPlayerScript+="
                    + Quote(payload.Substring(offset, Math.Min(65536, payload.Length - offset))) + ";'';");
            }
            var report = await EvaluateAsync(view,
                "(function(){var r;try{r=YTSolver.bootBase64(window.__ytPlayerScript);}"
                + "catch(e){r='solver failed: '+e;}window.__ytPlayerScript=null;return r;})()");
            System.Diagnostics.Debug.WriteLine("[NSig] " + scriptUrl + ": " + report);
            if (generation != _generation || string.IsNullOrEmpty(report)
                || !report.StartsWith("ok ", StringComparison.Ordinal)) return;
            const string sample = "DhpWuaCJRFiGbHK";
            var check = await EvaluateAsync(view,
                "(function(){try{return window.YTn(" + Quote(sample) + ")||'';}catch(e){return '';}})()");
            _ready = generation == _generation && !string.IsNullOrEmpty(check) && check != sample;
            if (!_ready) System.Diagnostics.Debug.WriteLine("[NSig] Sample transform failed");
        }

        private static async Task<string> EvaluateAsync(WebView view, string script)
        {
            return await view.InvokeScriptAsync("eval", new[] { script });
        }

        private static string Quote(string value) { return JsonValue.CreateStringValue(value ?? string.Empty).Stringify(); }

        internal void Release()
        {
            // Invalidate a pending boot now, but let an in-flight InvokeScript return
            // before tearing down its native WebView. Cached n answers stay valid.
            _generation++; _ready = false; _preparedFor = null;
            var view = _view; var host = _host;
            _view = null; _host = null; _attached = null;
            if (view != null) { var retired = RetireViewAsync(view, host); }
        }

        private async Task RetireViewAsync(WebView view, Panel host)
        {
            await _gate.WaitAsync();
            try
            {
                view.Stop(); view.NavigateToString("<html></html>");
                if (host != null) host.Children.Remove(view);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[NSig] Release: " + ex.Message); }
            finally { _gate.Release(); }
        }
    }
}
