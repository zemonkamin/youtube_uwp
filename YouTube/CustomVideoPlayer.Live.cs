using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Streaming.Adaptive;
using Windows.Storage.Streams;
using Windows.UI.Xaml;

namespace YouTube
{
    public sealed partial class CustomVideoPlayer
    {
        private bool _isLivePlayback, _liveEndConfirmed, _liveStopped;
        private LiveMediaSource _liveStream;
        private AdaptiveMediaSource _liveAdaptive;
        private DateTime _liveStalledSince;
        private TimeSpan _livePreviousPosition;
        private bool _liveAwaitingSource;
        public bool IsLivePlayback { get { return _isLivePlayback; } }

        internal void SetLivePlayback(bool live, LiveMediaSource stream = null)
        {
            if (_liveStream != null && _liveStream != stream) _liveStream.Dispose();
            _liveStream = stream; _liveAdaptive = null;
            _isLivePlayback = live; _liveEndConfirmed = false; _liveStopped = false;
            _liveAwaitingSource = live;
            _liveStalledSince = DateTime.MinValue; _livePreviousPosition = TimeSpan.Zero;
            if (live) _parsedDuration = TimeSpan.Zero;
            if (LiveEdgeButton != null)
            {
                LiveEdgeButton.Content = Localization.GetString("CategoryLive");
                LiveEdgeButton.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
                Windows.UI.Xaml.Controls.ToolTipService.SetToolTip(LiveEdgeButton, Localization.GetString("LiveGoToEdge"));
            }
            if (ProgressSlider != null) ProgressSlider.Minimum = 0;
            UpdateLiveProgressAppearance();
            if (TimeDisplayText != null)
            {
                TimeDisplayText.Visibility = live ? Visibility.Collapsed : Visibility.Visible;
                if (live) TimeDisplayText.Text = string.Empty;
            }
            if (!live) UpdateTimeDisplay();
        }

        private void UpdateLiveProgressAppearance()
        {
            // Streams use the player's plain track and thumb. Chapter gaps and
            // SponsorBlock overlays belong to regular videos, not the rolling live window.
            if (ProgressSlider != null)
            {
                ProgressSlider.Opacity = 1;
                ProgressSlider.Background = _isLivePlayback ? _timelineBackgroundBrush
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
                ProgressSlider.Foreground = _isLivePlayback ? _timelineForegroundBrush
                    : new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.Transparent);
            }
            if (ProgressSliderHitArea != null) ProgressSliderHitArea.IsHitTestVisible = true;
            if (SegmentedProgressBar != null)
                SegmentedProgressBar.Visibility = _isLivePlayback ? Visibility.Collapsed : Visibility.Visible;
            if (SponsorOverlay != null)
                SponsorOverlay.Visibility = _isLivePlayback ? Visibility.Collapsed : Visibility.Visible;
            if (_isLivePlayback) HideScrubPreview();
        }

        internal async Task<bool> WaitForLiveSourceOpenedAsync(CancellationToken cancellation)
        {
            var generation = _visibleSourceGeneration;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                try
                {
                    var opened = await WaitForVisibleVideoOpenedOrUsableAsync(timeout.Token, generation);
                    return opened && generation == _visibleSourceGeneration && !_playbackReleased;
                }
                catch (OperationCanceledException) { return false; }
            }
        }

        internal async Task<bool> TrySetHlsSourceAsync(Uri uri, bool autoPlay, string quality, CancellationToken cancellation)
        {
            var generation = _visibleSourceGeneration;
            var agent = MediaRequestUserAgent;
            var result = await CreateHlsAdaptiveSourceAsync(uri, quality, agent);
            if (result.Status != AdaptiveMediaSourceCreationStatus.Success || result.MediaSource == null)
            {
                System.Diagnostics.Debug.WriteLine("[Player] HLS manifest rejected: " + result.Status);
                return false;
            }
            if (cancellation.IsCancellationRequested || generation != _visibleSourceGeneration || _playbackReleased)
            { MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource).Dispose(); return false; }
            ResetSeparateAudio();
            _usingSeparateAudio = false;
            _suppressAutoPlayUntilSeparateAudioReady = false;
            _pendingAutoPlayAfterSeparateAudioReady = false;
            _playImmediatelyAfterSeparateAudioOpened = false;
            RememberLiveAdaptiveSource(result.MediaSource);
            System.Diagnostics.Debug.WriteLine("[Player] HLS uses one native audio/video source");
            SetSource(MediaSource.CreateFromAdaptiveMediaSource(result.MediaSource), autoPlay);
            return true;
        }

        private void RememberLiveAdaptiveSource(AdaptiveMediaSource adaptive)
        {
            if (!_isLivePlayback || adaptive == null) return;
            _liveAdaptive = adaptive;
            try { if (adaptive.IsLive) adaptive.DesiredLiveOffset = TimeSpan.FromSeconds(5); } catch { }
        }

        private TimeSpan GetLiveEdge()
        {
            if (_liveStream != null) return TimeSpan.FromSeconds(_liveStream.LiveEdge);
            try
            {
                // GetSeekableRanges is a 15063 API; this app targets the 14393 SDK.
                // HLS keeps its native timeline and chooses its initial live offset itself.
                var duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                return duration > TimeSpan.Zero && duration < TimeSpan.FromDays(3650) ? duration : TimeSpan.Zero;
            }
            catch { return TimeSpan.Zero; }
        }

        private TimeSpan GetLiveStart()
        {
            if (_liveStream != null) return TimeSpan.FromSeconds(_liveStream.BufferedStart);
            // On 14393 the native HLS source owns and clamps its seekable window;
            // the public window/range queries were introduced in the 15063 SDK.
            return TimeSpan.Zero;
        }

        private TimeSpan GetLivePlaybackTarget()
        {
            if (_liveStream != null) return TimeSpan.FromSeconds(_liveStream.LivePosition);
            var edge = GetLiveEdge();
            return edge > TimeSpan.Zero
                ? TimeSpan.FromSeconds(Math.Max(GetLiveStart().TotalSeconds, edge.TotalSeconds - 5)) : TimeSpan.Zero;
        }

        private void SeekToLiveEdge()
        {
            if (!_isLivePlayback || _liveStopped || _liveEndConfirmed || _liveAwaitingSource || _sourceLoading
                || !_visibleVideoMediaOpened || MediaPlayer == null || MediaPlayer.MediaPlayer == null) return;
            if (_liveStream != null && _liveStream.Ended) return;
            var target = GetLivePlaybackTarget();
            if (target <= TimeSpan.Zero) return;
            var session = MediaPlayer.MediaPlayer.PlaybackSession;
            // Only the viewer's explicit Live button invokes this seek. Playback
            // starts at the edge in MSS.Starting/HLS and then consumes samples in order.
            if (Math.Abs((target - session.Position).TotalSeconds) <= .25) return;
            ApplySeekToPosition(target);
            _liveStalledSince = DateTime.MinValue;
        }

        private void LiveEdgeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLivePlayback || _liveEndConfirmed || MediaPlayer.MediaPlayer == null) return;
            SeekToLiveEdge();
            Play();
        }

        private void UpdateLivePlayback()
        {
            if (_liveStopped || MediaPlayer == null || MediaPlayer.MediaPlayer == null) return;
            var session = MediaPlayer.MediaPlayer.PlaybackSession;
            var edge = GetLiveEdge();
            if (_liveStream != null && _liveStream.Ended && !_liveEndConfirmed)
            { _liveEndConfirmed = true; UpdateLiveProgressAppearance(); }
            var position = session.Position;
            if (!_isUserDragging && _pendingSeekTarget == null && edge > TimeSpan.Zero && ProgressSlider != null)
            {
                var start = GetLiveStart().TotalSeconds;
                // Maximum first: a rolling window may advance beyond the old maximum.
                ProgressSlider.Maximum = Math.Max(start + 1, edge.TotalSeconds);
                ProgressSlider.Minimum = start;
                ProgressSlider.Value = Math.Max(start, Math.Min(edge.TotalSeconds, position.TotalSeconds));
            }
            UpdateLiveTime(position);
            if (_liveEndConfirmed) return;
            if (_videoLoaded && _isPlaying && Math.Abs((position - _livePreviousPosition).TotalSeconds) < .05)
            {
                if (_liveStalledSince == DateTime.MinValue) _liveStalledSince = DateTime.UtcNow;
                // SABR handles empty responses internally. Recover only a long native decoder
                // stall; HLS has no feed pump and needs fresh signed URLs sooner.
                if ((DateTime.UtcNow - _liveStalledSince).TotalSeconds >= (_liveStream != null ? 45 : 20))
                { _liveStalledSince = DateTime.UtcNow; RequestLiveRecovery(); }
            }
            else _liveStalledSince = DateTime.MinValue;
            _livePreviousPosition = position;
        }

        private void UpdateLiveTime(TimeSpan position)
        {
            if (TimeDisplayText == null) return;
            if (_liveEndConfirmed)
            {
                if (LiveEdgeButton != null) LiveEdgeButton.Visibility = Visibility.Collapsed;
                TimeDisplayText.Text = Localization.GetString("LiveEnded");
                TimeDisplayText.Visibility = Visibility.Visible;
                return;
            }
            var behind = Math.Max(0, (GetLiveEdge() - position).TotalSeconds);
            // The adjacent live-edge button already names the broadcast. Show only
            // a DVR offset here when the viewer is behind, not a second Live label.
            TimeDisplayText.Text = behind > 20 ? "−" + FormatTimeSpan(TimeSpan.FromSeconds(behind)) : string.Empty;
            TimeDisplayText.Visibility = behind > 20 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RequestLiveRecovery()
        { var handler = PlaybackRecoveryRequested; if (handler != null) handler(this, null); }

        internal void ConfirmLiveEnded()
        { _liveEndConfirmed = true; UpdateLiveProgressAppearance(); UpdateLiveTime(CurrentPlaybackPosition); HandleVideoEnd(); }

        private bool GuardLiveEnd()
        {
            if (!_isLivePlayback) return false;
            if (_liveStopped || _liveAwaitingSource || _sourceLoading) return true;
            if (_liveStream != null && _liveStream.Ended)
            { _liveEndConfirmed = true; UpdateLiveProgressAppearance(); }
            if (_liveEndConfirmed) return false;
            RequestLiveRecovery(); return true;
        }

        private void StopLivePlayback()
        {
            _liveStopped = true;
            if (_liveStream != null) { _liveStream.Dispose(); _liveStream = null; }
            _liveAdaptive = null;
        }

    }
}
