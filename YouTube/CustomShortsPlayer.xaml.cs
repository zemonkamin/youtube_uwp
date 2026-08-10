using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace YouTube
{
    public sealed partial class CustomShortsPlayer : UserControl
    {
        private readonly DispatcherTimer _progressTimer;
        // Raised once per source, when the first frame is genuinely on screen.
        public event EventHandler PlaybackStarted;
        private bool _reportedPlaybackStarted;
        private bool _pendingAutoPlay;
        private bool _isDisposed;
        private bool _isPlaying;
        private bool _isUserSeeking;
        private bool _wasPlayingBeforeSeek;

        public bool IsPlaying
        {
            get { return _isPlaying; }
        }

        public CustomShortsPlayer()
        {
            this.InitializeComponent();

            if (MediaPlayer.MediaPlayer == null)
            {
                MediaPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
            }

            MediaPlayer.MediaPlayer.AutoPlay = false;
            MediaPlayer.MediaPlayer.MediaOpened += Player_MediaOpened;
            MediaPlayer.MediaPlayer.MediaEnded += Player_MediaEnded;
            MediaPlayer.MediaPlayer.MediaFailed += Player_MediaFailed;
            MediaPlayer.MediaPlayer.CurrentStateChanged += Player_CurrentStateChanged;

            _progressTimer = new DispatcherTimer();
            _progressTimer.Interval = TimeSpan.FromMilliseconds(250);
            _progressTimer.Tick += ProgressTimer_Tick;

            this.Unloaded += CustomShortsPlayer_Unloaded;
        }

        // Shared with the demuxer only; the plain-URL path uses MediaSource.CreateFromUri.
        private static readonly System.Net.Http.HttpClient _demuxHttp = new System.Net.Http.HttpClient();

        // Plays a short through the on-the-fly DASH demuxer (adaptive H.264 video-only + AAC),
        // which is the only way past the muxed-progressive ceiling to 1080p on this platform.
        // Returns false when the streams can't be demuxed, so the caller can fall back to the URL.
        public async Task<bool> SetDemuxedSourceAsync(PlayerFormatModel video, PlayerFormatModel audio, bool autoPlay = true)
        {
            if (_isDisposed || video == null || audio == null
                || string.IsNullOrWhiteSpace(video.Url) || string.IsNullOrWhiteSpace(audio.Url))
            {
                return false;
            }

            try
            {
                ShowLoading(true);
                ErrorMessageText.Visibility = Visibility.Collapsed;
                ErrorMessageText.Text = string.Empty;
                ProgressSlider.Value = 0;
                ProgressSlider.Maximum = 100;

                var mss = await DashDemuxer.CreateAsync(_demuxHttp, video, audio);
                if (mss == null || _isDisposed)
                {
                    return false;
                }

                _pendingAutoPlay = autoPlay;
                _isUserSeeking = false;
                _isPlaying = false;
                _reportedPlaybackStarted = false;

                if (MediaPlayer.MediaPlayer == null)
                {
                    MediaPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
                }

                MediaPlayer.MediaPlayer.Pause();
                SetSubtitleCues(null); // captions are per-short; the page re-applies if one is chosen
                MediaPlayer.MediaPlayer.Source = MediaSource.CreateFromMediaStreamSource(mss);
                ApplyDesiredRate();

                await Task.Delay(1);
                System.Diagnostics.Debug.WriteLine(
                    "CustomShortsPlayer: demuxed source set (" + video.Height + "p)");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: demux error: " + ex.Message);
                return false;
            }
        }

        public async Task SetSourceFromUriAsync(Uri uri, bool autoPlay = true)
        {
            if (uri == null || _isDisposed)
            {
                return;
            }

            try
            {
                ShowLoading(true);
                ErrorMessageText.Visibility = Visibility.Collapsed;
                ErrorMessageText.Text = string.Empty;
                ProgressSlider.Value = 0;
                ProgressSlider.Maximum = 100;
                _pendingAutoPlay = autoPlay;
                _isUserSeeking = false;
                _isPlaying = false;
                _reportedPlaybackStarted = false;

                if (MediaPlayer.MediaPlayer == null)
                {
                    MediaPlayer.SetMediaPlayer(new Windows.Media.Playback.MediaPlayer());
                }

                MediaPlayer.MediaPlayer.Pause();
                SetSubtitleCues(null); // captions are per-short; the page re-applies if one is chosen

                // NOTE: the previous MediaSource is deliberately NOT disposed here. Clearing
                // Source does not synchronously hand ownership back — the player still holds it
                // briefly — and disposing it at this point crashed the app after a few swipes.
                // Letting the GC collect it is the safe behaviour and was the original one.
                MediaPlayer.MediaPlayer.Source = MediaSource.CreateFromUri(uri);
                ApplyDesiredRate();

                await Task.Delay(1);
            }
            catch (Exception ex)
            {
                ShowLoading(false);
                ShowError(Localization.GetString("PlaybackFailed"));
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: SetSource error: " + ex.Message);
            }
        }

        // A short is a ~9:16 vertical video. On a phone the surface is portrait too, so filling it
        // edge to edge only crops a sliver. In Continuum / on desktop the surface is wide, and
        // UniformToFill would blow the video up and cut off most of it — so there, switch to
        // Uniform and let the black background show as pillarbox bars.
        private const double ShortAspect = 9.0 / 16.0;

        // How much of the frame may be cropped before we letterbox instead of filling. The Shorts
        // area is never exactly 9:16 — the tab bar takes ~52px off the bottom — so the test cannot
        // be "does the aspect match". It asks how much filling would actually cut off: a few
        // percent on a phone is invisible and is what the official app does, while a wide
        // Continuum/desktop window would lose most of the frame and gets pillarbox bars instead.
        private const double MaxCropFraction = 0.25;

        public static Stretch StretchForContainer(double width, double height)
        {
            if (width <= 0 || height <= 0)
            {
                return Stretch.UniformToFill;
            }

            var containerAspect = width / height;

            // Container is taller (relative) than the video: filling crops nothing horizontally.
            if (containerAspect <= ShortAspect)
            {
                return Stretch.UniformToFill;
            }

            // Fraction of the height lost when filling a wider-than-9:16 container.
            var cropped = 1.0 - (ShortAspect / containerAspect);
            return cropped > MaxCropFraction ? Stretch.Uniform : Stretch.UniformToFill;
        }

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateStretchForSize(e.NewSize.Width, e.NewSize.Height);
        }

        private void UpdateStretchForSize(double width, double height)
        {
            if (MediaPlayer == null)
            {
                return;
            }

            MediaPlayer.Stretch = StretchForContainer(width, height);
        }

        // --- Playback speed -------------------------------------------------------------------

        private double _desiredRate = 1.0;
        public double PlaybackRate { get { return _desiredRate; } }

        public void SetPlaybackRate(double rate)
        {
            _desiredRate = rate;
            ApplyDesiredRate();
        }

        private void ApplyDesiredRate()
        {
            try
            {
                if (MediaPlayer.MediaPlayer != null && Math.Abs(_desiredRate - 1.0) > 0.001)
                {
                    MediaPlayer.MediaPlayer.PlaybackSession.PlaybackRate = _desiredRate;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: ApplyDesiredRate failed - " + ex.Message);
            }
        }

        // --- Subtitles ------------------------------------------------------------------------

        private readonly List<Subtitles.SubtitleCue> _subtitleCues = new List<Subtitles.SubtitleCue>();
        private DispatcherTimer _subtitleTimer;
        private string _shownCueText;
        private static readonly TimeSpan SubtitleLead = TimeSpan.FromMilliseconds(250);

        public void SetSubtitleCues(IEnumerable<Subtitles.SubtitleCue> cues)
        {
            _subtitleCues.Clear();
            _shownCueText = null;

            if (cues != null)
            {
                foreach (var cue in cues)
                {
                    if (cue != null && !string.IsNullOrEmpty(cue.Text))
                    {
                        _subtitleCues.Add(cue);
                    }
                }
            }

            if (SubtitleText != null) SubtitleText.Text = string.Empty;
            if (SubtitleOverlay != null) SubtitleOverlay.Visibility = Visibility.Collapsed;

            if (_subtitleCues.Count == 0)
            {
                if (_subtitleTimer != null) _subtitleTimer.Stop();
                return;
            }

            if (_subtitleTimer == null)
            {
                _subtitleTimer = new DispatcherTimer();
                _subtitleTimer.Interval = TimeSpan.FromMilliseconds(100);
                _subtitleTimer.Tick += SubtitleTimer_Tick;
            }
            _subtitleTimer.Start();
        }

        private void SubtitleTimer_Tick(object sender, object e)
        {
            try
            {
                if (SubtitleOverlay == null || SubtitleText == null || MediaPlayer.MediaPlayer == null)
                {
                    return;
                }

                var position = MediaPlayer.MediaPlayer.PlaybackSession.Position + SubtitleLead;
                string text = null;
                foreach (var cue in _subtitleCues)
                {
                    if (cue.Start > position) break;
                    if (position < cue.End) { text = cue.Text; break; }
                }

                if (text == _shownCueText) return;
                _shownCueText = text;
                SubtitleText.Text = text ?? string.Empty;
                SubtitleOverlay.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: subtitle tick failed - " + ex.Message);
            }
        }

        public void Play()
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                MediaPlayer.MediaPlayer.Play();
                _isPlaying = true;
                _progressTimer.Start();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: Play error: " + ex.Message);
            }
        }

        public void Pause()
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                MediaPlayer.MediaPlayer.Pause();
                _isPlaying = false;
                _progressTimer.Stop();
                UpdateProgress();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: Pause error: " + ex.Message);
            }
        }

        public void TogglePlayPause()
        {
            if (_isPlaying)
            {
                Pause();
            }
            else
            {
                Play();
            }
        }

        public void Stop()
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                _pendingAutoPlay = false;
                _progressTimer.Stop();
                _isUserSeeking = false;
                _isPlaying = false;
                MediaPlayer.MediaPlayer.Pause();
                MediaPlayer.MediaPlayer.Source = null;
                ProgressSlider.Value = 0;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: Stop error: " + ex.Message);
            }
        }

        private async void Player_MediaOpened(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_isDisposed)
                {
                    return;
                }

                ShowLoading(false);
                UpdateProgressBounds();

                if (_pendingAutoPlay)
                {
                    _pendingAutoPlay = false;
                    Play();
                }
            });
        }

        private async void Player_MediaEnded(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (_isDisposed || MediaPlayer.MediaPlayer == null)
                {
                    return;
                }

                try
                {
                    MediaPlayer.MediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
                    MediaPlayer.MediaPlayer.Play();
                    _isPlaying = true;
                    _progressTimer.Start();
                }
                catch
                {
                }
            });
        }

        private async void Player_MediaFailed(Windows.Media.Playback.MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                ShowLoading(false);
                ShowError(Localization.GetString("UnablePlayShort"));
                _isPlaying = false;
                _progressTimer.Stop();
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: MediaFailed: " + args.ErrorMessage);
            });
        }

        private async void Player_CurrentStateChanged(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Low, () =>
            {
                if (_isDisposed || MediaPlayer.MediaPlayer == null)
                {
                    return;
                }

                var state = MediaPlayer.MediaPlayer.CurrentState;
                _isPlaying = state == MediaPlayerState.Playing;

                // "Playing" is the first moment a frame is actually on screen — MediaOpened fires
                // earlier, while the surface is still blank. Whoever is covering the player with a
                // still image needs this exact moment to uncover it.
                if (state == MediaPlayerState.Playing && !_reportedPlaybackStarted)
                {
                    _reportedPlaybackStarted = true;
                    var handler = PlaybackStarted;
                    if (handler != null)
                    {
                        handler(this, EventArgs.Empty);
                    }
                }

                if (state == MediaPlayerState.Buffering || state == MediaPlayerState.Opening)
                {
                    ShowLoading(true);
                }
                else if (state == MediaPlayerState.Playing || state == MediaPlayerState.Paused)
                {
                    ShowLoading(false);
                }

                if (_isPlaying)
                {
                    _progressTimer.Start();
                }
                else
                {
                    _progressTimer.Stop();
                    UpdateProgress();
                }
            });
        }

        private void ProgressTimer_Tick(object sender, object e)
        {
            UpdateProgress();
        }

        private void UpdateProgressBounds()
        {
            try
            {
                if (MediaPlayer.MediaPlayer == null)
                {
                    return;
                }

                var duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;
                if (duration.TotalMilliseconds > 0)
                {
                    ProgressSlider.Maximum = duration.TotalSeconds;
                }
            }
            catch
            {
            }
        }

        private void UpdateProgress()
        {
            try
            {
                if (MediaPlayer.MediaPlayer == null || _isUserSeeking)
                {
                    return;
                }

                UpdateProgressBounds();
                var position = MediaPlayer.MediaPlayer.PlaybackSession.Position;
                if (ProgressSlider.Maximum > 0)
                {
                    ProgressSlider.Value = Math.Max(0, Math.Min(position.TotalSeconds, ProgressSlider.Maximum));
                }
            }
            catch
            {
            }
        }

        private void ProgressSlider_ValueChanged(object sender, Windows.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            // Keep the same behavior as CustomVideoPlayer: while dragging we only update
            // the visual value. The real seek is applied when manipulation/pointer ends.
        }

        private void ProgressSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            _isUserSeeking = true;
            _wasPlayingBeforeSeek = _isPlaying;
            _progressTimer.Stop();
        }

        private void ProgressSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            if (MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                SeekToSliderValue();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: manipulation seek error: " + ex.Message);
            }
            finally
            {
                _isUserSeeking = false;
                if (_wasPlayingBeforeSeek)
                {
                    Play();
                }
                else
                {
                    UpdateProgress();
                }
            }
        }

        private void ProgressSliderTrack_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null || ProgressSlider == null)
            {
                return;
            }

            try
            {
                UpdateProgressBounds();
                if (ProgressSlider.ActualWidth <= 0 || ProgressSlider.Maximum <= ProgressSlider.Minimum)
                {
                    return;
                }

                var point = e.GetPosition(ProgressSlider);
                var ratio = point.X / ProgressSlider.ActualWidth;
                if (ratio < 0)
                {
                    ratio = 0;
                }
                else if (ratio > 1)
                {
                    ratio = 1;
                }

                ProgressSlider.Value = ProgressSlider.Minimum + ((ProgressSlider.Maximum - ProgressSlider.Minimum) * ratio);
                SeekToSliderValue();
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: track tap seek error: " + ex.Message);
            }
        }

        private void ProgressSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                _isUserSeeking = true;
                _wasPlayingBeforeSeek = _isPlaying;
                _progressTimer.Stop();

                var element = sender as UIElement;
                if (element != null)
                {
                    element.CapturePointer(e.Pointer);
                }

                SetSliderValueFromPointer(e);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: seek pointer pressed error: " + ex.Message);
            }
        }

        private void ProgressSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isUserSeeking || _isDisposed || MediaPlayer.MediaPlayer == null)
            {
                return;
            }

            try
            {
                SetSliderValueFromPointer(e);
                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: seek pointer moved error: " + ex.Message);
            }
        }

        private void ProgressSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            FinishSliderSeek(sender as UIElement, e);
        }

        private void ProgressSlider_PointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            FinishSliderSeek(sender as UIElement, e);
        }

        private void ProgressSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            FinishSliderSeek(sender as UIElement, e);
        }

        private void SetSliderValueFromPointer(PointerRoutedEventArgs e)
        {
            if (ProgressSlider == null)
            {
                return;
            }

            UpdateProgressBounds();

            var width = ProgressSlider.ActualWidth;
            if (width <= 0 || ProgressSlider.Maximum <= ProgressSlider.Minimum)
            {
                return;
            }

            var point = e.GetCurrentPoint(ProgressSlider).Position;
            var ratio = point.X / width;
            if (ratio < 0)
            {
                ratio = 0;
            }
            else if (ratio > 1)
            {
                ratio = 1;
            }

            ProgressSlider.Value = ProgressSlider.Minimum + ((ProgressSlider.Maximum - ProgressSlider.Minimum) * ratio);
        }

        private void FinishSliderSeek(UIElement element, PointerRoutedEventArgs e)
        {
            if (!_isUserSeeking)
            {
                return;
            }

            try
            {
                if (e != null)
                {
                    SetSliderValueFromPointer(e);
                }

                SeekToSliderValue();

                if (element != null && e != null)
                {
                    element.ReleasePointerCapture(e.Pointer);
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("CustomShortsPlayer: finish seek error: " + ex.Message);
            }
            finally
            {
                _isUserSeeking = false;
                if (_wasPlayingBeforeSeek)
                {
                    Play();
                }
                else
                {
                    UpdateProgress();
                }
            }
        }

        private void SeekToSliderValue()
        {
            if (MediaPlayer.MediaPlayer == null || ProgressSlider == null)
            {
                return;
            }

            UpdateProgressBounds();
            var value = Math.Max(ProgressSlider.Minimum, Math.Min(ProgressSlider.Value, ProgressSlider.Maximum));
            var targetPosition = TimeSpan.FromSeconds(value);
            var duration = MediaPlayer.MediaPlayer.PlaybackSession.NaturalDuration;

            if (duration.TotalMilliseconds > 0 && targetPosition > duration)
            {
                targetPosition = duration;
            }

            MediaPlayer.MediaPlayer.PlaybackSession.Position = targetPosition;
            ProgressSlider.Value = targetPosition.TotalSeconds;
        }

        private void ShowLoading(bool show)
        {
            LoadingRing.IsActive = show;
            LoadingRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ShowError(string message)
        {
            ErrorMessageText.Text = message;
            ErrorMessageText.Visibility = Visibility.Visible;
        }

        private void CustomShortsPlayer_Unloaded(object sender, RoutedEventArgs e)
        {
            DisposePlayer();
        }

        public void DisposePlayer()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _isUserSeeking = false;
            _progressTimer.Stop();

            if (MediaPlayer.MediaPlayer != null)
            {
                MediaPlayer.MediaPlayer.MediaOpened -= Player_MediaOpened;
                MediaPlayer.MediaPlayer.MediaEnded -= Player_MediaEnded;
                MediaPlayer.MediaPlayer.MediaFailed -= Player_MediaFailed;
                MediaPlayer.MediaPlayer.CurrentStateChanged -= Player_CurrentStateChanged;
                MediaPlayer.MediaPlayer.Pause();
                MediaPlayer.MediaPlayer.Source = null;
            }
        }
    }
}
