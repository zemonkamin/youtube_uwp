using System;
using Microsoft.Graphics.Canvas.Effects;
using Windows.UI;
using Windows.UI.Composition;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Hosting;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    // Same Composition backdrop implementation used by telegram_uwp. The compositor owns
    // the blur, so scrolling content remains live beneath the navigation chrome on W10M.
    internal static class FluentGlassEffectHelper
    {
        private const string SourceParameterName = "source";
        private const string EnabledSettingKey = "GlassEffectEnabled";
        private const float TopBlurAmount = 10.0f;
        private const float BottomBlurAmount = 11.0f;
        private const float SearchBlurAmount = 13.0f;
        private const float LightTintOpacity = 0.88f;
        private const float DarkTintOpacity = 0.84f;
        private const float LightSearchTintOpacity = 0.70f;
        private const float DarkSearchTintOpacity = 0.64f;
        private const double MinimumSize = 1.0;

        public static event EventHandler EnabledChanged;

        public static void EnsureDefault()
        {
            try
            {
                object raw;
                var values = ApplicationData.Current.LocalSettings.Values;
                if (!values.TryGetValue(EnabledSettingKey, out raw) || raw == null)
                    values[EnabledSettingKey] = !ResponsiveLayout.IsPhoneDevice;
            }
            catch
            {
            }
        }

        public static bool IsEnabled()
        {
            // Keep the same device default in settings and on every glass surface,
            // including before the settings page has ever been opened.
            var defaultEnabled = !ResponsiveLayout.IsPhoneDevice;
            try
            {
                object raw;
                if (!ApplicationData.Current.LocalSettings.Values.TryGetValue(EnabledSettingKey, out raw) || raw == null)
                    return defaultEnabled;

                if (raw is bool)
                    return (bool)raw;

                bool parsed;
                return bool.TryParse(raw.ToString(), out parsed) ? parsed : defaultEnabled;
            }
            catch
            {
                return defaultEnabled;
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

        public static void AttachTopBar(FrameworkElement target, Brush baseBrush, bool usePageAmbientBackground = false)
        {
            Apply(target, TopBlurAmount, ResolveBrushColor(baseBrush, GetDefaultTopBarColor()), baseBrush, null, usePageAmbientBackground);
        }

        public static void AttachBottomBar(FrameworkElement target, Brush baseBrush, bool usePageAmbientBackground = false)
        {
            Apply(target, BottomBlurAmount, ResolveBrushColor(baseBrush, GetDefaultBottomBarColor()), baseBrush, null, usePageAmbientBackground);
        }

        public static void AttachSearchBar(FrameworkElement target, Brush baseBrush)
        {
            var tintColor = ResolveBrushColor(baseBrush, GetDefaultTopBarColor());
            Apply(
                target,
                SearchBlurAmount,
                tintColor,
                baseBrush,
                IsLightColor(tintColor) ? LightSearchTintOpacity : DarkSearchTintOpacity);
        }

        public static void Detach(UIElement target)
        {
            if (target == null) return;

            try
            {
                ElementCompositionPreview.SetElementChildVisual(target, null);
            }
            catch
            {
            }
        }

        private static void Apply(FrameworkElement target, float blurAmount, Color tintColor, Brush baseBrush, float? tintOpacity, bool usePageAmbientBackground = false)
        {
            if (target == null) return;

            // Video already renders its blurred background once across the full page. Another
            // backdrop blur/tint here would darken only the chrome (twice at navbar/sidebar
            // intersections). The shared ambient layer also works in compact layouts and when
            // the independent glass setting is disabled.
            if (usePageAmbientBackground && VideoAmbientEffectController.IsEnabled())
            {
                Detach(target);
                SetBackground(target, new SolidColorBrush(Colors.Transparent));
                return;
            }

            // Backdrop blur is one of the most expensive always-on effects on Lumia-era
            // GPUs. A solid themed surface is visually cleaner and much cheaper when the
            // phone is landscape and vertical space is already constrained.
            if (!IsEnabled() || ResponsiveLayout.IsCompactLandscape)
            {
                Detach(target);
                SetBackground(target, baseBrush ?? new SolidColorBrush(tintColor));
                return;
            }

            SetBackground(target, new SolidColorBrush(Colors.Transparent));

            var width = Math.Max(target.ActualWidth, MinimumSize);
            var height = Math.Max(target.ActualHeight, MinimumSize);
            if (width <= MinimumSize || height <= MinimumSize)
            {
                SetBackground(target, baseBrush ?? new SolidColorBrush(tintColor));
                return;
            }

            try
            {
                var targetVisual = ElementCompositionPreview.GetElementVisual(target);
                var compositor = targetVisual.Compositor;
                var backdropBrush = compositor.CreateBackdropBrush();

                var blurEffect = new GaussianBlurEffect
                {
                    Name = "YouTubeNavigationGlass",
                    BlurAmount = blurAmount * 4.0f,
                    BorderMode = EffectBorderMode.Hard,
                    Optimization = EffectOptimization.Balanced,
                    Source = new CompositionEffectSourceParameter(SourceParameterName)
                };

                var effectFactory = compositor.CreateEffectFactory(blurEffect);
                var effectBrush = effectFactory.CreateBrush();
                effectBrush.SetSourceParameter(SourceParameterName, backdropBrush);

                var containerVisual = compositor.CreateContainerVisual();
                var blurVisual = compositor.CreateSpriteVisual();
                var tintVisual = compositor.CreateSpriteVisual();

                blurVisual.Brush = effectBrush;
                blurVisual.Opacity = 1.0f;
                tintVisual.Brush = compositor.CreateColorBrush(tintColor);
                tintVisual.Opacity = tintOpacity.HasValue
                    ? tintOpacity.Value
                    : (IsLightColor(tintColor) ? LightTintOpacity : DarkTintOpacity);

                var sizeAnimation = compositor.CreateExpressionAnimation("target.Size");
                sizeAnimation.SetReferenceParameter("target", targetVisual);
                containerVisual.StartAnimation("Size", sizeAnimation);
                blurVisual.StartAnimation("Size", sizeAnimation);
                tintVisual.StartAnimation("Size", sizeAnimation);

                containerVisual.Children.InsertAtBottom(blurVisual);
                containerVisual.Children.InsertAtTop(tintVisual);
                ElementCompositionPreview.SetElementChildVisual(target, containerVisual);
            }
            catch
            {
                Detach(target);
                SetBackground(target, baseBrush ?? new SolidColorBrush(tintColor));
            }
        }

        private static void SetBackground(FrameworkElement target, Brush brush)
        {
            var border = target as Border;
            if (border != null)
            {
                border.Background = brush;
                return;
            }

            var panel = target as Panel;
            if (panel != null)
            {
                panel.Background = brush;
                return;
            }

            var control = target as Control;
            if (control != null)
                control.Background = brush;
        }

        private static Color ResolveBrushColor(Brush brush, Color fallback)
        {
            var solid = brush as SolidColorBrush;
            if (solid == null) return fallback;

            var color = solid.Color;
            return Color.FromArgb(255, color.R, color.G, color.B);
        }

        private static bool IsLightColor(Color color)
        {
            return (color.R * 0.2126f + color.G * 0.7152f + color.B * 0.0722f) / 255.0f >= 0.5f;
        }

        private static Color GetDefaultTopBarColor()
        {
            return IsDarkTheme()
                ? Color.FromArgb(255, 32, 32, 32)
                : Color.FromArgb(255, 243, 243, 243);
        }

        private static Color GetDefaultBottomBarColor()
        {
            return IsDarkTheme()
                ? Color.FromArgb(255, 31, 31, 31)
                : Color.FromArgb(255, 247, 247, 247);
        }

        private static bool IsDarkTheme()
        {
            try
            {
                return Application.Current.RequestedTheme == ApplicationTheme.Dark;
            }
            catch
            {
                return true;
            }
        }
    }
}
