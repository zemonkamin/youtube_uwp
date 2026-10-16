using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas.Effects;
using Windows.Foundation.Metadata;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.UI.Composition;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Hosting;

namespace YouTube
{
    internal static class VideoAmbientEffectController
    {
        private const string EnabledSettingKey = "VideoAmbientEffectEnabled";

        public static event EventHandler EnabledChanged;

        public static void EnsureDefault()
        {
            try
            {
                object raw;
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.TryGetValue(EnabledSettingKey, out raw) || raw == null)
                    values[EnabledSettingKey] = false;
            }
            catch
            {
            }
        }

        public static bool IsEnabled()
        {
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(EnabledSettingKey, out raw)
                    || raw == null)
                    return false;

                if (raw is bool)
                    return (bool)raw;

                bool parsed;
                return bool.TryParse(raw.ToString(), out parsed) && parsed;
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            var changed = IsEnabled() != enabled;
            try
            {
                ApplicationData.Current.LocalSettings.Values[EnabledSettingKey] = enabled;
            }
            catch
            {
            }

            if (changed && EnabledChanged != null)
                EnabledChanged(null, EventArgs.Empty);
        }
    }

    // Renders additional, low-resolution views of the active MediaPlayer surface. The player
    // still decodes only once; Composition stretches and blurs the duplicate views on the GPU.
    // This is considerably cheaper than reading SoftwareBitmap pixels for every video frame.
    internal sealed class VideoAmbientRenderer : IDisposable
    {
        private sealed class Attachment : IDisposable
        {
            public FrameworkElement Host;
            public CompositionSurfaceBrush SurfaceBrush;
            public CompositionEffectBrush EffectBrush;
            public SpriteVisual Visual;

            public void Dispose()
            {
                if (Host != null)
                {
                    try { ElementCompositionPreview.SetElementChildVisual(Host, null); }
                    catch { }
                }

                if (Visual != null) { try { Visual.Dispose(); } catch { } }
                if (EffectBrush != null) { try { EffectBrush.Dispose(); } catch { } }
                if (SurfaceBrush != null) { try { SurfaceBrush.Dispose(); } catch { } }
                Host = null;
                SurfaceBrush = null;
                EffectBrush = null;
                Visual = null;
            }
        }

        private const string VideoSourceName = "videoSource";
        private readonly List<Attachment> _attachments = new List<Attachment>();
        private FrameworkElement _playerBackgroundHost;
        private FrameworkElement _topBarHost;
        private FrameworkElement _bottomBarHost;
        private FrameworkElement _leftBarHost;
        private FrameworkElement _rightBarHost;
        private FrameworkElement _pageBackdropHost;
        private MediaPlayer _player;
        private MediaPlayerSurface _sharedSurface;
        private Compositor _sharedSurfaceCompositor;

        public void SetHosts(
            FrameworkElement playerBackgroundHost,
            FrameworkElement topBarHost,
            FrameworkElement bottomBarHost,
            FrameworkElement leftBarHost,
            FrameworkElement rightBarHost,
            FrameworkElement pageBackdropHost)
        {
            _playerBackgroundHost = playerBackgroundHost;
            _topBarHost = topBarHost;
            _bottomBarHost = bottomBarHost;
            _leftBarHost = leftBarHost;
            _rightBarHost = rightBarHost;
            _pageBackdropHost = pageBackdropHost;
        }

        public void Refresh(MediaPlayer player, bool isFullscreen)
        {
            // MediaPlayerSurface is fragile on the original Windows 10 compositor: disposing it
            // and immediately calling GetSurface again after every Popup reparent can return a
            // permanently blank surface. Keep it alive while the same MediaPlayer is in use and
            // rebuild only the lightweight brushes/visuals.
            var playerChanged = !ReferenceEquals(_player, player);
            ClearAttachments(playerChanged);
            _player = player;

            if (player == null || !CanUseMediaSurface())
                return;

            if (VideoAmbientEffectController.IsEnabled())
            {
                var mobile = ResponsiveLayout.IsPhoneDevice;
                // MediaPlayerElement paints opaque bars on W10M, so the full duplicate directly
                // behind it cannot be seen there. The bar/page surfaces below provide the
                // same visible result without spending another continuously rendered effect.
                if (!mobile)
                    Attach(_playerBackgroundHost, 26.0f, 0.92f);
                // Fullscreen gets a deliberately much darker cinematic fill. 184 is exactly four
                // times the previous fullscreen blur (46); the lower opacity blends that surface
                // with MediaPlayerElement's black bars instead of producing a bright color wash.
                var barBlur = mobile
                    ? (isFullscreen ? 64.0f : 28.0f)
                    : (isFullscreen ? 184.0f : 46.0f);
                var barOpacity = isFullscreen ? 0.26f : 0.68f;
                Attach(_topBarHost, barBlur, barOpacity);
                Attach(_bottomBarHost, barBlur, barOpacity);
                Attach(_leftBarHost, barBlur, barOpacity);
                Attach(_rightBarHost, barBlur, barOpacity);
                // One crop, blur and opacity cover the entire page, including both navigation
                // bars. Transparent navigation chrome exposes this same surface without a second
                // tint or blur. Local glow/strip copies caused visible seams at their boundaries.
                Attach(_pageBackdropHost, mobile ? 30.0f : 52.0f, 0.13f);
            }
        }

        public void ClearVisuals()
        {
            ClearAttachments(true);
        }

        private void ClearAttachments(bool releaseSurface)
        {
            for (var i = 0; i < _attachments.Count; i++)
            {
                try { _attachments[i].Dispose(); }
                catch { }
            }
            _attachments.Clear();
            if (releaseSurface && _sharedSurface != null)
            {
                try { _sharedSurface.Dispose(); }
                catch { }
                _sharedSurface = null;
                _sharedSurfaceCompositor = null;
            }
        }

        private void Attach(FrameworkElement host, float blurAmount, float opacity)
        {
            // Collapsed and zero-sized hosts used to allocate a MediaPlayerSurface, effect brush
            // and animation despite producing no pixels. On the phone this was a major source of
            // frame drops during layout and rotation.
            if (host == null || _player == null
                || host.Visibility != Visibility.Visible
                || host.ActualWidth <= 1.0
                || host.ActualHeight <= 1.0)
                return;

            Attachment attachment = null;
            try
            {
                var targetVisual = ElementCompositionPreview.GetElementVisual(host);
                var compositor = targetVisual.Compositor;
                if (_sharedSurface == null || !ReferenceEquals(_sharedSurfaceCompositor, compositor))
                {
                    if (_sharedSurface != null)
                    {
                        try { _sharedSurface.Dispose(); }
                        catch { }
                    }
                    _sharedSurface = _player.GetSurface(compositor);
                    _sharedSurfaceCompositor = compositor;
                }
                var surface = _sharedSurface;
                var surfaceBrush = compositor.CreateSurfaceBrush(surface.CompositionSurface);
                surfaceBrush.Stretch = CompositionStretch.UniformToFill;
                surfaceBrush.HorizontalAlignmentRatio = 0.5f;
                surfaceBrush.VerticalAlignmentRatio = 0.5f;

                var blur = new GaussianBlurEffect
                {
                    Name = "VideoAmbientBlur",
                    BlurAmount = blurAmount,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Speed,
                    Source = new CompositionEffectSourceParameter(VideoSourceName)
                };
                var saturation = new SaturationEffect
                {
                    Name = "VideoAmbientSaturation",
                    Saturation = 1.18f,
                    Source = blur
                };
                var factory = compositor.CreateEffectFactory(saturation);
                var effectBrush = factory.CreateBrush();
                effectBrush.SetSourceParameter(VideoSourceName, surfaceBrush);

                var visual = compositor.CreateSpriteVisual();
                visual.Brush = effectBrush;
                visual.Opacity = 0;

                var sizeExpression = compositor.CreateExpressionAnimation("target.Size");
                sizeExpression.SetReferenceParameter("target", targetVisual);
                visual.StartAnimation("Size", sizeExpression);
                var fade = compositor.CreateScalarKeyFrameAnimation();
                fade.Duration = TimeSpan.FromMilliseconds(520);
                fade.InsertKeyFrame(1.0f, opacity);
                visual.StartAnimation("Opacity", fade);

                attachment = new Attachment
                {
                    Host = host,
                    SurfaceBrush = surfaceBrush,
                    EffectBrush = effectBrush,
                    Visual = visual
                };
                _attachments.Add(attachment);
                ElementCompositionPreview.SetElementChildVisual(host, visual);
            }
            catch (Exception ex)
            {
                if (attachment != null)
                    attachment.Dispose();
                System.Diagnostics.Debug.WriteLine("[VideoAmbient] Surface attach failed: " + ex.Message);
            }
        }

        private static bool CanUseMediaSurface()
        {
            try
            {
                return ApiInformation.IsMethodPresent(
                    "Windows.Media.Playback.MediaPlayer",
                    "GetSurface");
            }
            catch
            {
                return false;
            }
        }

        public void Dispose()
        {
            ClearVisuals();
            _player = null;
            _playerBackgroundHost = null;
            _topBarHost = null;
            _bottomBarHost = null;
            _leftBarHost = null;
            _rightBarHost = null;
            _pageBackdropHost = null;
        }
    }
}
