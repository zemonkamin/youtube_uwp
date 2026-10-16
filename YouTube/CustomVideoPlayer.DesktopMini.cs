using System;
using System.Threading.Tasks;
using Windows.Media.Playback;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace YouTube
{
    public sealed partial class CustomVideoPlayer
    {
        private bool _desktopMiniTransition;
        private bool _desktopMiniPlayerActive;

        internal bool CanRestoreDesktopSurface(MediaPlayer player)
        {
            return !_playbackReleased && !_isDisposed && MediaPlayer != null
                && ReferenceEquals(MediaPlayer.MediaPlayer, player);
        }

        private void UpdateDesktopMiniPlayerButton()
        {
            if (DesktopMiniPlayerButton == null) return;
            DesktopMiniPlayerButton.Visibility = DesktopMiniPlayerSetting.IsAvailable() && !_isMiniMode
                ? Visibility.Visible : Visibility.Collapsed;
            var title = Localization.GetString("DesktopMiniPlayerWindow");
            ToolTipService.SetToolTip(DesktopMiniPlayerButton, title);
            Windows.UI.Xaml.Automation.AutomationProperties.SetName(DesktopMiniPlayerButton, title);
        }

        internal void SetDesktopMiniPlayerMode(bool active)
        {
            var changed = _desktopMiniPlayerActive != active;
            _desktopMiniPlayerActive = active;
            if (DesktopMiniPlayerBlackout != null)
                DesktopMiniPlayerBlackout.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            // Keep the MediaPlayerElement association: all page controls still
            // address the same native player whose image is shown in the other view.
            UpdateDesktopMiniPlayerButton();
            if (changed)
            {
                if (active) _ambientRenderer.ClearVisuals();
                else if (!_playbackReleased && !_isDisposed) RefreshAmbientVideoEffect();
            }
        }

        private double GetDesktopMiniPlayerAspectRatio()
        {
            var session = MediaPlayer == null || MediaPlayer.MediaPlayer == null
                ? null : MediaPlayer.MediaPlayer.PlaybackSession;
            return session != null && session.NaturalVideoHeight > 0 && session.NaturalVideoWidth > 0
                ? (double)session.NaturalVideoWidth / session.NaturalVideoHeight : 16.0 / 9.0;
        }

        private async void DesktopMiniPlayerButton_Click(object sender, RoutedEventArgs e)
        {
            if (_desktopMiniTransition || !DesktopMiniPlayerSetting.IsAvailable()) return;
            _desktopMiniTransition = true;
            FadeInControls();
            ResetAutoHideTimer();
            try
            {
                if (DesktopMiniPlayerWindow.IsPageWindowFor(this))
                {
                    await CloseDesktopMiniPlayerAsync();
                    return;
                }
                if (!_visibleVideoMediaOpened || _playbackReleased || _sourceLoading || _isMiniMode
                    || DesktopMiniPlayerWindow.IsActive || DesktopMiniPlayerWindow.IsOpening) return;

                if (_isFullscreen) ExitFullscreen();
                Action restore = () => SetDesktopMiniPlayerMode(false);
                DesktopMiniPlayerWindow.TryShow(this, GetDesktopMiniPlayerAspectRatio(),
                    restore, restore, restore, true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Manual toggle failed: " + ex.Message);
            }
            finally { _desktopMiniTransition = false; }
        }

        internal async Task CloseDesktopMiniPlayerAsync()
        {
            if (!DesktopMiniPlayerWindow.IsPageWindowFor(this)) return;
            try { await DesktopMiniPlayerWindow.CloseForRestoreAsync(); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[DesktopMiniPlayer] Return to page failed: " + ex.Message);
            }
            finally { SetDesktopMiniPlayerMode(false); }
        }
    }
}
