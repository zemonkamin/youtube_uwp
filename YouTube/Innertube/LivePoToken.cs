using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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
    // Troubadour YTPoToken + challenge_solver, using the real UWP WebView engine.
    // No account credentials are sent to the noauth attestation endpoints.
    internal sealed class LivePoToken
    {
        private const string RequestKey = "O43z0dpjhgX20SCx4KAo";
        private const string ApiKey = "AIzaSyDyT5W0Jh49F30Pqqtyfdf7pDLFKLJoAnw";
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, string> _tokens = new Dictionary<string, string>();
        private WebView _view;
        private Panel _host;
        private Task _preparing;
        private DateTime _renewAfter;
        private bool _ready;
        private int _generation;
        private string _tokenOwner, _stage;

        internal async Task<string> GetAsync(Panel host, string binding)
        {
            if (host == null || string.IsNullOrEmpty(binding)) return string.Empty;
            var complete = new TaskCompletionSource<string>();
            var generation = _generation;
            // Renewals may start on a media worker. Every WebView operation, including
            // minting, enters through the page dispatcher as in YTPoToken.tokenFor.
            await host.Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var entered = false;
                try
                {
                    await _gate.WaitAsync(); entered = true;
                    if (generation != _generation) { complete.TrySetResult(string.Empty); return; }
                    complete.TrySetResult(await GetCoreAsync(host, binding, generation));
                }
                catch (Exception ex)
                {
                    if (generation == _generation)
                    {
                        _ready = false; _tokens.Clear();
                        if (_preparing == null || _preparing.IsCompleted) _preparing = null;
                    }
                    System.Diagnostics.Debug.WriteLine("[Live][Proof] Failed at " + _stage + ": " + ex.GetType().Name + " - " + ex.Message);
                    complete.TrySetResult(string.Empty);
                }
                finally { if (entered) _gate.Release(); }
            });
            return await complete.Task;
        }

        private async Task<string> GetCoreAsync(Panel host, string binding, int generation)
        {
            var owner = PlaybackResolver.AccountKey;
            if (_tokenOwner != owner) { _tokenOwner = owner; _tokens.Clear(); }
            if (_preparing == null || (_ready && DateTime.UtcNow >= _renewAfter))
            {
                _tokens.Clear();
                _preparing = PrepareAsync(host, generation);
                // A caller can time out while preparation finishes in the background.
                var observed = ObservePreparationAsync(_preparing);
            }
            var preparing = _preparing;
            if (await Task.WhenAny(preparing, Task.Delay(20000)) != preparing)
            { System.Diagnostics.Debug.WriteLine("[Live][Proof] Still preparing at " + _stage); return string.Empty; }
            await preparing;
            if (generation != _generation || owner != PlaybackResolver.AccountKey) return string.Empty;
            if (!_ready) { _preparing = null; return string.Empty; }
            string token;
            if (_tokens.TryGetValue(binding, out token)) return token;
            _stage = "mint";
            System.Diagnostics.Debug.WriteLine("[Live][Proof] Minting " + (binding.Length == 11 ? "video" : "session") + " token");
            token = await EvaluateAsync(_view, "(function(){try{return mintPOToken(" + Quote(binding) + ")||'';}catch(e){return '';}})()", generation);
            if (owner != PlaybackResolver.AccountKey || generation != _generation) return string.Empty;
            if (!string.IsNullOrEmpty(token))
            {
                // Keep distinct unexpired tokens for the session and video bindings.
                if (_tokens.Count >= 32) _tokens.Clear();
                _tokens[binding] = token;
            }
            return token;
        }

        private async Task PrepareAsync(Panel host, int generation)
        {
            _ready = false;
            if (_view == null)
            {
                _view = new WebView(WebViewExecutionMode.SeparateThread) { Width = 1, Height = 1, Opacity = 0, IsHitTestVisible = false,
                    HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
                _host = host;
                var attached = new TaskCompletionSource<bool>();
                var attachingView = _view;
                RoutedEventHandler attachedHandler = null;
                attachedHandler = (sender, e) => { attachingView.Loaded -= attachedHandler; attached.TrySetResult(true); };
                attachingView.Loaded += attachedHandler;
                host.Children.Add(attachingView);
                _stage = "host";
                if (await Task.WhenAny(attached.Task, Task.Delay(5000)) != attached.Task)
                { attachingView.Loaded -= attachedHandler; return; }
            }
            if (generation != _generation) return;
            var view = _view;
            var loaded = new TaskCompletionSource<bool>();
            Windows.Foundation.TypedEventHandler<WebView, WebViewNavigationCompletedEventArgs> handler = null;
            handler = (sender, e) => { view.NavigationCompleted -= handler; loaded.TrySetResult(e.IsSuccess); };
            view.NavigationCompleted += handler;
            _stage = "origin";
            // BotGuard checks location.origin; NavigateToString would use about:blank.
            view.Navigate(new Uri("https://www.youtube.com/"));
            if (await Task.WhenAny(loaded.Task, Task.Delay(10000)) != loaded.Task || !await loaded.Task)
            { view.NavigationCompleted -= handler; return; }
            if (generation != _generation) return;
            _stage = "solver";
            var asset = await StorageFile.GetFileFromApplicationUriAsync(new Uri("ms-appx:///Assets/live_challenge_solver.js"));
            var solver = await FileIO.ReadTextAsync(asset);
            await EvaluateAsync(view, solver, generation);
            _stage = "challenge";
            var challenge = await AttestAsync("Create", new JsonObject { ["request_key"] = JsonValue.CreateStringValue(RequestKey) });
            if (generation != _generation) return;
            var scrambled = challenge == null ? null : challenge.Select(p => p.Value)
                .FirstOrDefault(v => v.ValueType == JsonValueType.String && v.GetString().Length > 100);
            if (scrambled == null) return;
            var raw = LiveSabrSession.Decode(scrambled.GetString());
            for (var i = 0; i < raw.Length; i++) raw[i] = unchecked((byte)(raw[i] + 97));
            JsonArray parts;
            if (!JsonArray.TryParse(Encoding.UTF8.GetString(raw, 0, raw.Length), out parts) || parts.Count < 6) return;
            var script = parts[1];
            if (script.ValueType == JsonValueType.Array)
                script = script.GetArray().FirstOrDefault(v => v.ValueType == JsonValueType.String && v.GetString().Length > 0);
            if (script == null || script.ValueType != JsonValueType.String || parts[4].ValueType != JsonValueType.String
                || parts[5].ValueType != JsonValueType.String) return;
            _stage = "botguard";
            await EvaluateAsync(view, script.GetString(), generation);
            await EvaluateAsync(view, "window.__uwpStage='';window.__uwpBotguard='';runBotguardChallenge(" + parts[4].Stringify() + "," + parts[5].Stringify() + ");", generation);
            if (!await WaitForAsync(view, "window.__uwpStage", "vmReady", generation)) return;
            await EvaluateAsync(view, "createPOSignalOutput();", generation);
            string signal = string.Empty;
            for (var i = 0; i < 40 && string.IsNullOrEmpty(signal); i++)
            { await Task.Delay(200); signal = await EvaluateAsync(view, "window.__uwpBotguard||''", generation); }
            if (string.IsNullOrEmpty(signal)) return;
            _stage = "integrity";
            var result = await AttestAsync("GenerateIT", new JsonObject
            { ["request_key"] = JsonValue.CreateStringValue(RequestKey), ["botguard_response"] = JsonValue.CreateStringValue(signal) });
            if (generation != _generation) return;
            var token = LiveJson.String(result, "integrityToken");
            if (string.IsNullOrEmpty(token)) return;
            await EvaluateAsync(view, "processIntegrityToken(" + Quote(token) + ");", generation);
            var ready = await WaitForAsync(view, "window.__uwpStage", "poReady", generation);
            if (generation != _generation) return;
            _ready = ready;
            var ttl = LiveJson.Number(result, "estimatedTtlSecs");
            _renewAfter = DateTime.UtcNow.AddSeconds((ttl > 0 ? ttl : 3600) * .8);
            _stage = ready ? "ready" : "integrity timeout";
        }

        private async Task<bool> WaitForAsync(WebView view, string expression, string expected, int generation)
        {
            for (var i = 0; i < 40; i++)
            { if (await EvaluateAsync(view, expression + "||''", generation) == expected) return true; await Task.Delay(200); }
            return false;
        }

        private async Task<string> EvaluateAsync(WebView view, string script, int generation)
        {
            if (generation != _generation || view == null || !ReferenceEquals(view, _view)) throw new OperationCanceledException();
            var result = await view.InvokeScriptAsync("eval", new[] { script });
            if (generation != _generation) throw new OperationCanceledException();
            return result;
        }

        private static async Task ObservePreparationAsync(Task preparing)
        { try { await preparing; } catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Live][Proof] Preparation ended: " + ex.GetType().Name); } }

        internal void Release()
        {
            // Invalidate old continuations immediately. Retire the captured WebView
            // after pending script execution returns, avoiding teardown mid-RPC.
            _generation++; _ready = false; _tokens.Clear();
            var view = _view; var host = _host; var preparing = _preparing;
            _view = null; _host = null; _preparing = null;
            if (view != null) { var retired = RetireViewAsync(view, host, preparing); }
        }

        private async Task RetireViewAsync(WebView view, Panel host, Task preparing)
        {
            if (preparing != null) await ObservePreparationAsync(preparing);
            await _gate.WaitAsync();
            try
            {
                view.Stop(); view.NavigateToString("<html></html>");
                if (host != null) host.Children.Remove(view);
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[Live][Proof] Release: " + ex.Message); }
            finally { _gate.Release(); }
        }

        private static string Quote(string value) { return JsonValue.CreateStringValue(value ?? string.Empty).Stringify(); }

        private static async Task<JsonObject> AttestAsync(string method, JsonObject body)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/api/jnn/v1/" + method + "?noauth=1"))
            {
                request.Content = new StringContent(body.Stringify(), Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("x-goog-api-key", ApiKey);
                request.Headers.TryAddWithoutValidation("x-user-agent", "grpc-web-javascript/0.1");
                using (var response = await YouTubeHttpClient.Shared.RawClient.SendAsync(request))
                {
                    if (!response.IsSuccessStatusCode) return null;
                    JsonObject json; return JsonObject.TryParse(await response.Content.ReadAsStringAsync(), out json) ? json : null;
                }
            }
        }
    }
}
