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
        private void UpdateVideoPlayerHeight()
        {
            double width = 0;
            if (VideoPlayerContainer != null)
            {
                width = VideoPlayerContainer.ActualWidth;
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                if (PlayerInfoPanel != null)
                {
                    width = PlayerInfoPanel.ActualWidth;
                }
            }

            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                width = Window.Current.Bounds.Width;
                if (RelatedColumn != null && RelatedColumn.Width.Value > 0 && Window.Current.Bounds.Width > RelatedColumn.Width.Value)
                {
                    width = Window.Current.Bounds.Width - RelatedColumn.Width.Value;
                }
            }

            UpdateVideoPlayerHeight(width);
        }

        private void UpdateVideoPlayerHeight(double width)
        {
            if (width <= 0 || double.IsNaN(width) || double.IsInfinity(width))
            {
                return;
            }

            if (CustomVideoPlayer != null && CustomVideoPlayer.IsFullscreen)
            {
                return;
            }

            var aspectRatio = _currentVideoPlayerAspectRatio;
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            var targetHeight = Math.Round(width / aspectRatio);
            if (targetHeight < MinVideoPlayerHeight)
            {
                targetHeight = MinVideoPlayerHeight;
            }

            if (VideoPlayerContainer != null &&
                (double.IsNaN(VideoPlayerContainer.Height) || Math.Abs(VideoPlayerContainer.Height - targetHeight) > 0.5))
            {
                VideoPlayerContainer.Height = targetHeight;
            }

            if (CustomVideoPlayer != null &&
                (double.IsNaN(CustomVideoPlayer.Height) || Math.Abs(CustomVideoPlayer.Height - targetHeight) > 0.5))
            {
                CustomVideoPlayer.Height = targetHeight;
                CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
            }
        }

        private void SetVideoPlayerAspectRatioFromFormat(PlayerFormatModel format)
        {
            if (format == null || format.Width <= 0 || format.Height <= 0)
            {
                return;
            }

            SetVideoPlayerAspectRatio((double)format.Width / format.Height);
        }

        private void SetVideoPlayerAspectRatio(double aspectRatio)
        {
            if (aspectRatio <= 0 || double.IsNaN(aspectRatio) || double.IsInfinity(aspectRatio))
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            // Guard against broken metadata. YouTube mobile/Continuum can report unusual sizes,
            // but real video aspect ratios still stay in a sane range.
            if (aspectRatio < 0.45 || aspectRatio > 3.5)
            {
                aspectRatio = DefaultVideoPlayerAspectRatio;
            }

            if (Math.Abs(_currentVideoPlayerAspectRatio - aspectRatio) > 0.001)
            {
                _currentVideoPlayerAspectRatio = aspectRatio;
                System.Diagnostics.Debug.WriteLine("[Video] Player aspect ratio set to " + aspectRatio.ToString("0.###"));
            }

            if (_minimizedToMiniPlayer
                && ReferenceEquals(MiniPlayer.ActivePlayer, CustomVideoPlayer))
            {
                MiniPlayer.UpdateAspectRatio(aspectRatio);
            }

            UpdateVideoPlayerHeight();
        }

        private void UpdateVideoPlayerAspectRatioFromFormats(string requestedQualityTag)
        {
            var format = SelectBestVideoFormatForAspectRatio(availableFormats, requestedQualityTag);
            if (format != null)
            {
                SetVideoPlayerAspectRatioFromFormat(format);
            }
            else
            {
                SetVideoPlayerAspectRatio(DefaultVideoPlayerAspectRatio);
            }
        }

        private static PlayerFormatModel SelectBestVideoFormatForAspectRatio(IList<PlayerFormatModel> formats, string requestedQualityTag)
        {
            if (formats == null)
            {
                return null;
            }

            int requestedHeight = ParseInt(requestedQualityTag);
            PlayerFormatModel best = null;

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || !format.HasVideo || format.Width <= 0 || format.Height <= 0)
                {
                    continue;
                }

                if (requestedHeight > 0 && format.QualityTier > requestedHeight)
                {
                    continue;
                }

                if (best == null || format.QualityTier > best.QualityTier)
                {
                    best = format;
                }
            }

            if (best != null)
            {
                return best;
            }

            for (int i = 0; i < formats.Count; i++)
            {
                var format = formats[i];
                if (format == null || !format.HasVideo || format.Width <= 0 || format.Height <= 0)
                {
                    continue;
                }

                if (best == null || format.QualityTier > best.QualityTier)
                {
                    best = format;
                }
            }

            return best;
        }

        private void UpdateVideoPlayerLayout(
            bool updateOrientationState = true,
            bool? portraitOverride = null)
        {
            // While handing the player to the mini-player the window shrinks to the small
            // always-on-top size, which is technically landscape. Reacting to that would drag the
            // page into fullscreen on its way out.
            if (_minimizedToMiniPlayer)
            {
                return;
            }

            // SizeChanged supplies the new geometry before ApplicationView.Orientation catches up
            // on Windows 10 Mobile. Prefer that value during rotation so the page cannot briefly
            // rebuild itself for the old orientation.
            bool isPortrait = portraitOverride ?? IsCurrentViewPortrait();
            var layoutModeChanged = !_videoLayoutInitialized
                || _lastAppliedPortraitLayout != isPortrait;

            // Windows 10 Mobile produces several SizeChanged notifications during one rotation.
            // Only the first notification for the new orientation may reparent panels and swap
            // recommendation sources. Later notifications only adjust geometry.
            if (!layoutModeChanged)
            {
                if (!isPortrait && RelatedColumn != null)
                    RelatedColumn.Width = new GridLength(GetRelatedColumnWidth());

                UpdateVideoPlayerHeight();
                if (updateOrientationState)
                    _wasPortrait = isPortrait;
                return;
            }

            UpdateNavigationChrome(isPortrait);

            UpdateTitleDescriptionSkeletonLayout(isPortrait);
            MovePlaylistQueueForLayout(isPortrait);
            MoveVideoActionsForLayout(isPortrait);
            UpdateLandscapeDetailsVisibility(isPortrait);
            ApplyRelatedContentForOrientation(isPortrait);

            if (isPortrait)
            {
                // Portrait mode - player takes full width, related videos shown below
                if (PlayerColumn != null)
                    PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null)
                    RelatedColumn.Width = new GridLength(0);

                if (RelatedPanel != null)
                    RelatedPanel.Visibility = Visibility.Collapsed;
                if (RelatedPanelVertical != null)
                    RelatedPanelVertical.Visibility = Visibility.Visible;

                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                    CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                if (PlayerInfoPanel != null)
                    PlayerInfoPanel.Margin = new Thickness(0);

            }
            else
            {
                // Landscape keeps the video/details column on the left and the playlist plus
                // recommendations rail on the right.
                var relatedWidth = GetRelatedColumnWidth();

                if (PlayerColumn != null)
                    PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null)
                    RelatedColumn.Width = new GridLength(relatedWidth);

                if (RelatedPanel != null)
                    RelatedPanel.Visibility = Visibility.Visible;
                if (RelatedPanelVertical != null)
                    RelatedPanelVertical.Visibility = Visibility.Collapsed;

                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
                    CustomVideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                }

                if (PlayerInfoPanel != null)
                    PlayerInfoPanel.Margin = new Thickness(0);

            }

            UpdateVideoPlayerHeight();
            var ignored = Dispatcher.RunAsync(CoreDispatcherPriority.Low, () => UpdateVideoPlayerHeight());

            if (_offlineMode)
            {
                if (PlayerColumn != null) PlayerColumn.Width = new GridLength(1, GridUnitType.Star);
                if (RelatedColumn != null) RelatedColumn.Width = new GridLength(0);
                if (RelatedPanel != null) RelatedPanel.Visibility = Visibility.Collapsed;
                if (RelatedPanelVertical != null) RelatedPanelVertical.Visibility = Visibility.Collapsed;
                if (RelatedVideosFallback != null) RelatedVideosFallback.Visibility = Visibility.Collapsed;
                if (RelatedVideosFallbackVertical != null) RelatedVideosFallbackVertical.Visibility = Visibility.Collapsed;
            }

            // Only the normal layout path advances the orientation baseline. Mini-player restore
            // resyncs may run while the phone is physically rotating, so those must never swallow
            // a real portrait/landscape transition.
            if (updateOrientationState)
            {
                _wasPortrait = isPortrait;
            }

            _videoLayoutInitialized = true;
            _lastAppliedPortraitLayout = isPortrait;
        }

        private static double GetRelatedColumnWidth()
        {
            var windowWidth = Window.Current.Bounds.Width;
            double relatedWidth;
            if (windowWidth <= 700)
                relatedWidth = Math.Max(220, Math.Min(280, windowWidth * 0.30));
            else if (windowWidth <= 1000)
                relatedWidth = Math.Max(260, Math.Min(340, windowWidth * 0.32));
            else
                relatedWidth = Math.Max(320, Math.Min(400, windowWidth * 0.33));

            return Math.Min(relatedWidth, Math.Max(200, windowWidth * 0.45));
        }

        private void UpdateNavigationChrome(bool isPortrait)
        {
            var show = !isPortrait;
            if (VideoNavbarRow != null)
                VideoNavbarRow.Height = new GridLength(show ? 56 : 0);
            if (VideoTabbarRow != null)
                VideoTabbarRow.Height = new GridLength(show ? 52 : 0);
            if (navbar != null)
            {
                navbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                if (show)
                    navbar.SetHorizontalLayout();
            }
            if (tabbar != null)
                tabbar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void App_ThemeChanged(object sender, EventArgs e)
        {
            ApplyCurrentVideoTheme();
        }

        private void VideoAmbientEffect_EnabledChanged(object sender, EventArgs e)
        {
            UpdateVideoAmbientMaterialBrushes();
            UpdateSubscriptionVisualState();
        }

        private void ApplyCurrentVideoTheme()
        {
            RequestedTheme = App.GetCurrentElementTheme();

            if (_fullscreenSettingsPopupRoot != null)
                _fullscreenSettingsPopupRoot.RequestedTheme = RequestedTheme;

            UpdateVideoAmbientMaterialBrushes();

            // These controls receive concrete brushes because they are built in C# rather than
            // XAML. Refresh them when a cached Video page returns or the system theme changes.
            UpdateSubscriptionVisualState();
            ForceShareTimeToggleVisual(_shareWithTimestamp);
            RefreshRuntimeOptionPanelTheme(QualityOptionsPanel);
            RefreshRuntimeOptionPanelTheme(SpeedOptionsPanel);
            RefreshRuntimeOptionPanelTheme(AudioTrackOptionsPanel);
            RefreshRuntimeOptionPanelTheme(SubtitlesOptionsPanel);
        }

        private void UpdateVideoAmbientMaterialBrushes()
        {
            var enabled = VideoAmbientEffectController.IsEnabled();
            SetPageBrushOpacity("VideoAmbientSurfaceBrush", enabled ? 0.72 : 1.0);
            SetPageBrushOpacity("VideoAmbientPrimaryActionBrush", enabled ? 0.82 : 1.0);

            var pillBorder = ConfigureAmbientPillBorder(enabled);
            var normalSurface = Resources["VideoAmbientSurfaceBrush"] as Brush;
            var gradient = ConfigureAmbientPillGradient(enabled);

            SetAmbientPillMaterial(LikeDislikeContainer, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(ShareActionContainer, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(SaveActionContainer, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(DownloadActionContainer, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(LandscapeDescriptionContainer, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(PlaylistQueuePanel, normalSurface, gradient, pillBorder, enabled);
            SetAmbientPillMaterial(LastCommentContainer, normalSurface, gradient, pillBorder, enabled);
        }

        private LinearGradientBrush ConfigureAmbientPillGradient(bool enabled)
        {
            var gradient = Resources["VideoAmbientPillGradientBrush"] as LinearGradientBrush;
            if (gradient == null || gradient.GradientStops.Count < 3)
                return gradient;

            var isLight = RequestedTheme == ElementTheme.Light;
            var channel = isLight ? (byte)0 : (byte)255;
            gradient.GradientStops[0].Color = Windows.UI.Color.FromArgb(
                enabled ? (byte)(isLight ? 34 : 52) : (byte)0,
                channel, channel, channel);
            gradient.GradientStops[1].Color = Windows.UI.Color.FromArgb(
                enabled ? (byte)(isLight ? 24 : 38) : (byte)0,
                channel, channel, channel);
            gradient.GradientStops[2].Color = Windows.UI.Color.FromArgb(
                enabled ? (byte)(isLight ? 16 : 24) : (byte)0,
                channel, channel, channel);
            gradient.Opacity = enabled ? 1.0 : 0.0;
            return gradient;
        }

        private LinearGradientBrush ConfigureAmbientPillBorder(bool enabled)
        {
            var border = Resources["VideoAmbientPillBorderBrush"] as LinearGradientBrush;
            if (border == null || border.GradientStops.Count < 3)
                return border;

            var isLight = RequestedTheme == ElementTheme.Light;
            var channel = isLight ? (byte)0 : (byte)255;
            border.GradientStops[0].Color = Windows.UI.Color.FromArgb(
                enabled ? (byte)(isLight ? 48 : 72) : (byte)0,
                channel, channel, channel);
            border.GradientStops[1].Color = Windows.UI.Color.FromArgb(
                enabled ? (byte)(isLight ? 20 : 30) : (byte)0,
                channel, channel, channel);
            border.GradientStops[2].Color = Windows.UI.Color.FromArgb(
                0, channel, channel, channel);
            border.Opacity = enabled ? 1.0 : 0.0;
            return border;
        }

        private static void SetAmbientPillMaterial(
            Border border,
            Brush normalBackground,
            Brush ambientGradient,
            Brush borderBrush,
            bool enabled)
        {
            if (border == null)
                return;

            border.Background = enabled ? ambientGradient : normalBackground;
            border.BorderBrush = enabled ? borderBrush : null;
            border.BorderThickness = enabled ? new Thickness(1) : new Thickness(0);
        }

        private void SetPageBrushOpacity(string resourceKey, double opacity)
        {
            try
            {
                var brush = Resources[resourceKey] as SolidColorBrush;
                if (brush != null)
                    brush.Opacity = opacity;
            }
            catch
            {
            }
        }

        private static void RefreshRuntimeOptionPanelTheme(DependencyObject root)
        {
            if (root == null)
                return;

            var primary = App.GetThemeBrush("AppPrimaryTextBrush");
            var control = root as Control;
            if (control != null && primary != null && !IsVideoAccentBrush(control.Foreground))
                control.Foreground = primary;

            var text = root as TextBlock;
            if (text != null && primary != null && !IsVideoAccentBrush(text.Foreground))
                text.Foreground = primary;

            var icon = root as FontIcon;
            if (icon != null && primary != null && !IsVideoAccentBrush(icon.Foreground))
                icon.Foreground = primary;

            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
                RefreshRuntimeOptionPanelTheme(VisualTreeHelper.GetChild(root, i));
        }

        private static bool IsVideoAccentBrush(Brush brush)
        {
            var solid = brush as SolidColorBrush;
            return solid != null
                && solid.Color.R == 255
                && solid.Color.G == 0
                && solid.Color.B == 51;
        }

        // The queue is one live control so its expansion state, ItemsSource and current marker are
        // preserved. Portrait keeps the original placement in the main content; landscape moves
        // that same Border above the related-video rail. This only uses Panel reparenting/Grid.Row,
        // which works on the VS2015 / Windows 10 Mobile UWP target.
        private void MovePlaylistQueueForLayout(bool isPortrait)
        {
            if (PlaylistQueuePanel == null || PlayerInfoPanel == null || LandscapePlaylistHost == null)
            {
                return;
            }

            try
            {
                if (isPortrait)
                {
                    if (!PlayerInfoPanel.Children.Contains(PlaylistQueuePanel))
                    {
                        var oldParent = PlaylistQueuePanel.Parent as Panel;
                        if (oldParent != null)
                            oldParent.Children.Remove(PlaylistQueuePanel);
                        PlayerInfoPanel.Children.Add(PlaylistQueuePanel);
                    }

                    Grid.SetRow(PlaylistQueuePanel, 4);
                    PlaylistQueuePanel.Margin = new Thickness(16, 0, 16, 16);
                }
                else
                {
                    if (!LandscapePlaylistHost.Children.Contains(PlaylistQueuePanel))
                    {
                        var oldParent = PlaylistQueuePanel.Parent as Panel;
                        if (oldParent != null)
                            oldParent.Children.Remove(PlaylistQueuePanel);
                        LandscapePlaylistHost.Children.Add(PlaylistQueuePanel);
                    }

                    Grid.SetRow(PlaylistQueuePanel, 0);
                    PlaylistQueuePanel.Margin = new Thickness(8, 0, 8, 12);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[PlaylistQueue] Layout reparent failed: " + ex.Message);
            }
        }

        private void MoveVideoActionsForLayout(bool isPortrait)
        {
            if (VideoActionsScrollViewer == null || PlayerInfoPanel == null || LandscapeActionsHost == null)
                return;

            try
            {
                var target = isPortrait ? (Panel)PlayerInfoPanel : LandscapeActionsHost;
                if (!target.Children.Contains(VideoActionsScrollViewer))
                {
                    var oldParent = VideoActionsScrollViewer.Parent as Panel;
                    if (oldParent != null)
                        oldParent.Children.Remove(VideoActionsScrollViewer);
                    target.Children.Add(VideoActionsScrollViewer);
                }

                if (isPortrait)
                {
                    Grid.SetRow(VideoActionsScrollViewer, 3);
                    VideoActionsScrollViewer.Margin = new Thickness(0, 2, 0, 16);
                    if (VideoActionsPanel != null)
                        VideoActionsPanel.Margin = new Thickness(16, 0, 16, 0);
                    LandscapeActionsHost.Visibility = Visibility.Collapsed;
                }
                else
                {
                    Grid.SetRow(VideoActionsScrollViewer, 0);
                    VideoActionsScrollViewer.Margin = new Thickness(0);
                    if (VideoActionsPanel != null)
                        VideoActionsPanel.Margin = new Thickness(0);
                    LandscapeActionsHost.Visibility = Visibility.Visible;
                }

                ScheduleLandscapeActionLabelUpdate();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Action-row reparent failed: " + ex.Message);
            }
        }

        private void ChannelAndActionsRow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ScheduleLandscapeActionLabelUpdate();
        }

        private void ScheduleLandscapeActionLabelUpdate()
        {
            if (Dispatcher == null) return;
            var ignored = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                UpdateLandscapeActionLabelVisibility);
        }

        private void UpdateLandscapeActionLabelVisibility()
        {
            if (_updatingLandscapeActionLabels
                || ShareActionText == null
                || SaveActionText == null
                || DownloadActionText == null
                || ShareButton == null
                || SaveButton == null
                || DownloadButton == null)
            {
                return;
            }

            _updatingLandscapeActionLabels = true;
            try
            {
                if (IsCurrentViewPortrait())
                {
                    SetActionLabelVisible(ShareActionText, ShareButton, true);
                    SetActionLabelVisible(SaveActionText, SaveButton, true);
                    SetActionLabelVisible(DownloadActionText, DownloadButton, true);
                    return;
                }

                EnsureLandscapeActionWidthMeasurements();

                // Keep enough room for the avatar plus an ellipsized channel name. The
                // Subscribe button remains untouched; only Share/Save/Download lose labels.
                var subscribeWidth = SubscribeButtonContainer != null
                    && SubscribeButtonContainer.Visibility == Visibility.Visible
                    ? Math.Max(SubscribeButtonContainer.ActualWidth,
                        SubscribeButtonContainer.DesiredSize.Width)
                    : 0.0;
                var available = Math.Max(0.0,
                    (ChannelAndActionsRow == null ? 0.0 : ChannelAndActionsRow.ActualWidth)
                    - 124.0 - subscribeWidth - 12.0);

                var showDownload = _actionsWidthAllLabels <= available;
                var showSave = showDownload
                    || _actionsWidthWithoutDownloadLabel <= available;
                var showShare = showSave
                    || _actionsWidthWithoutSaveLabel <= available;

                if (_actionsWidthIconsOnly > available)
                {
                    showDownload = false;
                    showSave = false;
                    showShare = false;
                }

                SetActionLabelVisible(DownloadActionText, DownloadButton, showDownload);
                SetActionLabelVisible(SaveActionText, SaveButton, showSave);
                SetActionLabelVisible(ShareActionText, ShareButton, showShare);
            }
            finally
            {
                _updatingLandscapeActionLabels = false;
            }
        }

        private void EnsureLandscapeActionWidthMeasurements()
        {
            if (!double.IsNaN(_actionsWidthAllLabels) || VideoActionsPanel == null)
                return;

            SetActionLabelVisible(ShareActionText, ShareButton, true);
            SetActionLabelVisible(SaveActionText, SaveButton, true);
            SetActionLabelVisible(DownloadActionText, DownloadButton, true);
            _actionsWidthAllLabels = MeasureVideoActionsWidth();

            SetActionLabelVisible(DownloadActionText, DownloadButton, false);
            _actionsWidthWithoutDownloadLabel = MeasureVideoActionsWidth();

            SetActionLabelVisible(SaveActionText, SaveButton, false);
            _actionsWidthWithoutSaveLabel = MeasureVideoActionsWidth();

            SetActionLabelVisible(ShareActionText, ShareButton, false);
            _actionsWidthIconsOnly = MeasureVideoActionsWidth();
        }

        private double MeasureVideoActionsWidth()
        {
            VideoActionsPanel.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            return VideoActionsPanel.DesiredSize.Width;
        }

        private static void SetActionLabelVisible(
            TextBlock label,
            Button button,
            bool visible)
        {
            if (label != null)
                label.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (button != null)
                button.Padding = visible ? new Thickness(16, 8, 16, 8) : new Thickness(8);
        }

        private void UpdateLandscapeDetailsVisibility(bool isPortrait)
        {
            var hasComments = _currentComments != null && _currentComments.Count > 0;
            if (CommentsList != null)
            {
                var portraitComments = isPortrait && hasComments ? _currentComments : null;
                if (!ReferenceEquals(CommentsList.ItemsSource, portraitComments))
                    CommentsList.ItemsSource = portraitComments;
            }
            if (LandscapeCommentsList != null)
            {
                var landscapeComments = !isPortrait && hasComments ? _currentComments : null;
                if (!ReferenceEquals(LandscapeCommentsList.ItemsSource, landscapeComments))
                    LandscapeCommentsList.ItemsSource = landscapeComments;
            }
            if (LandscapeDescriptionContainer != null)
                LandscapeDescriptionContainer.Visibility = isPortrait ? Visibility.Collapsed : Visibility.Visible;
            if (LandscapeCommentsPanel != null)
                LandscapeCommentsPanel.Visibility = !isPortrait && hasComments
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            if (CommentsContainerButton != null)
                CommentsContainerButton.Visibility = isPortrait && hasComments
                    ? Visibility.Visible
                    : Visibility.Collapsed;

            if (!isPortrait)
            {
                var ignored = Dispatcher.RunAsync(
                    CoreDispatcherPriority.Low,
                    RefreshLandscapeDescriptionToggleVisibility);
            }
        }

    }
}
