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
        private void LoadSubtitleTracks(JsonObject playerRoot)
        {
            _showingSubtitleTranslations = false;

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetSubtitleTracks(Subtitles.ParseTracks(playerRoot));
            }
        }

        // Fills the track list lazily; if it turns out there really are ≥2 languages, reveals the
        // menu entry (it may have been hidden when the sheet first opened).
        private async void UpdateAudioTrackButtonVisibilityAsync()
        {
            await EnsureAudioTracksLoadedAsync();
            if (AudioTrackButton != null)
            {
                AudioTrackButton.Visibility = (_availableAudioTracks != null && _availableAudioTracks.Count > 1)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
                UpdateSettingsBottomSheetHeight();
            }
            UpdateSettingsRowValues();
        }

        private void AudioTrackButton_Click(object sender, RoutedEventArgs e)
        {
            PopulateAudioTrackOptions();

            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Collapsed;
            if (AudioTrackSettingsPanel != null)
                AudioTrackSettingsPanel.Visibility = Visibility.Visible;
            UpdateSettingsBottomSheetHeight();
        }

        private void PopulateAudioTrackOptions()
        {
            if (AudioTrackOptionsPanel == null)
            {
                return;
            }

            AudioTrackOptionsPanel.Children.Clear();
            var currentTrack = GetCurrentAudioTrackInfo();
            var currentTrackId = currentTrack == null ? null : currentTrack.Id;

            foreach (var track in _availableAudioTracks)
            {
                var chosen = track;
                var isCurrent = string.IsNullOrEmpty(_selectedAudioTrackId)
                    ? string.Equals(currentTrackId, chosen.Id, StringComparison.Ordinal)
                    : string.Equals(_selectedAudioTrackId, chosen.Id, StringComparison.Ordinal);

                var button = MakeCheckableOptionButton(chosen.Name, isCurrent, () => ApplyAudioTrack(chosen.Id));
                AudioTrackOptionsPanel.Children.Add(button);
            }
        }

        private Config.AudioTrackInfo GetCurrentAudioTrackInfo()
        {
            if (_availableAudioTracks == null || _availableAudioTracks.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(_selectedAudioTrackId))
            {
                var explicitTrack = _availableAudioTracks.FirstOrDefault(t =>
                    string.Equals(t.Id, _selectedAudioTrackId, StringComparison.Ordinal));
                if (explicitTrack != null)
                {
                    return explicitTrack;
                }
            }

            Config.AudioTrackInfo systemTrack = null;
            var bestSystemScore = 0;
            foreach (var track in _availableAudioTracks)
            {
                var score = Config.SystemAudioTrackMatchScore(track.Id);
                if (score > bestSystemScore)
                {
                    bestSystemScore = score;
                    systemTrack = track;
                }
            }

            if (systemTrack != null)
            {
                return systemTrack;
            }

            return _availableAudioTracks.FirstOrDefault(t => t.IsDefault)
                ?? _availableAudioTracks[0];
        }

        private async void ApplyAudioTrack(string trackId)
        {
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;

            if (string.Equals(_selectedAudioTrackId, trackId, StringComparison.Ordinal))
            {
                return;
            }

            _selectedAudioTrackId = trackId;

            // youtube-ios rebuilds the same selected height (including Auto) with the new
            // preferred audio track and keeps the current playback position.
            var effective = GetEffectiveVideoQualityTag();
            await ChangeQualityAsync(string.IsNullOrWhiteSpace(effective) ? "Auto" : effective + "p");
        }

        private void SubtitlesButton_Click(object sender, RoutedEventArgs e)
        {
            _showingSubtitleTranslations = false;
            PopulateSubtitleOptions();

            if (MainSettingsPanel != null)
                MainSettingsPanel.Visibility = Visibility.Collapsed;
            if (QualitySettingsPanel != null)
                QualitySettingsPanel.Visibility = Visibility.Collapsed;
            if (SpeedSettingsPanel != null)
                SpeedSettingsPanel.Visibility = Visibility.Collapsed;
            if (SubtitlesSettingsPanel != null)
                SubtitlesSettingsPanel.Visibility = Visibility.Visible;
            UpdateSettingsBottomSheetHeight();
        }

        // Two levels in the same panel: the author's tracks, and — behind one entry — the full
        // machine-translation language list, which runs to well over a hundred entries and would
        // bury the real tracks if shown inline.
        private void PopulateSubtitleOptions()
        {
            if (SubtitlesOptionsPanel == null)
            {
                return;
            }

            SubtitlesOptionsPanel.Children.Clear();

            if (_showingSubtitleTranslations)
            {
                var source = _subtitleTracks.TranslationSource;

                AddSubtitleOption(Localization.GetString("Back"), false, () =>
                {
                    _showingSubtitleTranslations = false;
                    PopulateSubtitleOptions();
                });

                foreach (var language in _subtitleTracks.TranslationLanguages)
                {
                    var target = language;
                    var isActive = _activeSubtitleTrack != null
                        && string.Equals(_activeSubtitleTrack.TranslationLanguageCode, target.LanguageCode, StringComparison.Ordinal);

                    AddSubtitleOption(target.Name, isActive,
                        () => ApplySubtitleTrack(CustomVideoPlayer.MakeTranslatedTrack(source, target)));
                }

                UpdateSettingsBottomSheetHeight();
                return;
            }

            // Sync adjustment, only meaningful while a track is on.
            if (_activeSubtitleTrack != null && CustomVideoPlayer != null)
            {
                AddSubtitleOption(Localization.Format("SyncFormat", CustomVideoPlayer.SubtitleOffsetDisplayText), false, null);
                AddSubtitleOption(Localization.GetString("SubtitleEarlier"), false, () =>
                {
                    CustomVideoPlayer.AdjustSubtitleOffset(250);
                    PopulateSubtitleOptions();
                });
                AddSubtitleOption(Localization.GetString("SubtitleLater"), false, () =>
                {
                    CustomVideoPlayer.AdjustSubtitleOffset(-250);
                    PopulateSubtitleOptions();
                });
                var resetTitle = Localization.GetString("SubtitleReset");
                if (string.Equals(resetTitle, "SubtitleReset", StringComparison.Ordinal))
                {
                    resetTitle = "Reset sync (0.00s)";
                }
                AddSubtitleOption(resetTitle, false, () =>
                {
                    CustomVideoPlayer.SetSubtitleOffset(0);
                    PopulateSubtitleOptions();
                });
            }

            AddSubtitleOption(Localization.GetString("Off"), _activeSubtitleTrack == null, () => ApplySubtitleTrack(null));

            foreach (var track in _subtitleTracks.Tracks)
            {
                var selected = track;
                var isActive = _activeSubtitleTrack != null
                    && string.IsNullOrEmpty(_activeSubtitleTrack.TranslationLanguageCode)
                    && string.Equals(_activeSubtitleTrack.BaseUrl, selected.BaseUrl, StringComparison.Ordinal);

                AddSubtitleOption(selected.DisplayName, isActive, () => ApplySubtitleTrack(selected));
            }

            if (_subtitleTracks.CanTranslate)
            {
                AddSubtitleOption(Localization.GetString("AutoTranslate"), false, () =>
                {
                    _showingSubtitleTranslations = true;
                    PopulateSubtitleOptions();
                });
            }
            UpdateSettingsBottomSheetHeight();
        }

        private void AddSubtitleOption(string label, bool isActive, Action onClick)
        {
            var button = new Button
            {
                Content = label,
                Background = new SolidColorBrush(Windows.UI.Colors.Transparent),
                Foreground = new SolidColorBrush(isActive
                    ? Windows.UI.Color.FromArgb(255, 255, 0, 51)
                    : App.GetThemeColor("AppPrimaryTextBrush", Windows.UI.Colors.White)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(16, 12, 16, 12),
                Margin = new Thickness(0, 0, 0, 4),
                FontSize = 14
            };

            if (onClick == null)
            {
                // A plain caption row, not a choice.
                button.IsHitTestVisible = false;
                button.Opacity = 0.7;
            }
            else
            {
                button.Click += (s, args) => onClick();
            }

            SubtitlesOptionsPanel.Children.Add(button);
        }

        private void ApplySubtitleTrack(Subtitles.SubtitleTrack track)
        {
            // Closed first: the download happens in the player and the sheet has nothing to wait for.
            AnimateSettingsBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SelectSubtitleTrack(track);
            }
        }

        private async void ChangeQuality(string quality)
        {
            // This is intentionally async void because it is used directly by UI handlers.
            // Never let an exception escape it: on UWP that becomes an unhandled dispatcher
            // exception and terminates the app.
            try
            {
                // MediaPlaybackSession / MediaStreamSource callbacks are not guaranteed to run on
                // the XAML UI thread. Quality changes touch CustomVideoPlayer and replace its
                // MediaPlayer source, so marshal the whole operation to the owning dispatcher.
                if (Dispatcher != null && !Dispatcher.HasThreadAccess)
                {
                    await Dispatcher.RunAsync(
                        Windows.UI.Core.CoreDispatcherPriority.Normal,
                        () => ChangeQuality(quality));
                    return;
                }

                // A manual quality pick takes precedence over the speed-driven Auto switch, so the
                // next drop to 1x must not undo it.
                _speedForcedAuto = false;
                await ChangeQualityAsync(quality);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Video] ChangeQuality failed safely for '" + quality + "': " + ex.Message);
                try
                {
                    if (CustomVideoPlayer != null)
                    {
                        CustomVideoPlayer.EndSourceLoading();
                    }
                }
                catch { }
            }
        }

        private async Task ChangeQualityAsync(string quality)
        {
            // Ignore a new quality switch while one is still building its source. Overlapping
            // source swaps (each an async void SetSource) race on the MediaPlayer and can crash,
            // especially combined with a rotation happening in the same window.
            if (_qualityChangeInProgress)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Quality change already in progress; ignoring '" + quality + "'");
                return;
            }

            _qualityChangeInProgress = true;
            try
            {
                currentQualityTag = NormalizeQualityTag(quality);
                // A per-video pick — including "Auto" — now overrides the Settings default until
                // the next video opens.
                _qualityExplicitlyChosen = true;
                ResetVideoOnlyDecodeFallbackState();

                // Capture the current position BEFORE the reload — ReloadPlayerOnlyAsync -> Stop()
                // resets it to zero. Arming the resume makes the new-quality source seek back to
                // this spot and keep playing (no manual tap on the play button).
                var resumePosition = TimeSpan.Zero;
                if (CustomVideoPlayer != null)
                {
                    resumePosition = CustomVideoPlayer.CurrentPlaybackPosition;
                    CustomVideoPlayer.CurrentQuality = string.IsNullOrWhiteSpace(currentQualityTag) ? null : currentQualityTag;
                    CustomVideoPlayer.PrepareResumeAfterSourceReload(resumePosition, true);
                    // Spinner + no play/seek until the new stream is actually ready.
                    CustomVideoPlayer.BeginSourceLoading();
                }

                System.Diagnostics.Debug.WriteLine(
                    "[Video] Changing quality to: "
                    + (string.IsNullOrWhiteSpace(currentQualityTag) ? "Auto" : currentQualityTag + "p")
                    + " @ " + resumePosition
                );

                await ReloadPlayerOnlyAsync(true);
            }
            finally
            {
                _qualityChangeInProgress = false;
            }
        }

        // Set when a >1x speed forced the progressive/Auto path; remembers the demuxer quality to
        // restore once the speed drops back to 1x or below.
        private bool _speedForcedAuto;
        private string _qualityBeforeSpeedForce;

        private async void ChangeSpeed(double speed)
        {
            System.Diagnostics.Debug.WriteLine($"[Video] Changing speed to: {speed}x");

            currentSpeed = speed;

            if (CustomVideoPlayer == null)
            {
                return;
            }

            // Arm the rate up front so it is applied whether or not the source is swapped below.
            CustomVideoPlayer.SetPlaybackRate(speed);

            if (_qualityChangeInProgress)
            {
                return;
            }

            if (speed > 1.0)
            {
                // The demuxer can't exceed 1x. Move to the progressive/Auto path and remember the
                // demuxer quality so 1x-and-below can bring it back.
                if (CustomVideoPlayer.IsUsingDemuxer)
                {
                    _qualityBeforeSpeedForce = GetEffectiveVideoQualityTag();
                    _speedForcedAuto = true;
                    System.Diagnostics.Debug.WriteLine(
                        "[Video] Speed >1x: switching to Auto (progressive); will restore "
                        + (string.IsNullOrWhiteSpace(_qualityBeforeSpeedForce) ? "Auto" : _qualityBeforeSpeedForce + "p"));
                    await ChangeQualityAsync("Auto");
                    // Re-arm after the reload so the new progressive source opens at the right rate.
                    CustomVideoPlayer.SetPlaybackRate(speed);
                }
            }
            else
            {
                // 1x or slower belongs on the demuxer again if a faster speed had forced Auto.
                if (_speedForcedAuto)
                {
                    _speedForcedAuto = false;
                    var restore = string.IsNullOrWhiteSpace(_qualityBeforeSpeedForce)
                        ? "Auto"
                        : _qualityBeforeSpeedForce + "p";
                    System.Diagnostics.Debug.WriteLine("[Video] Speed <=1x: restoring " + restore + " (demuxer)");
                    await ChangeQualityAsync(restore);
                    CustomVideoPlayer.SetPlaybackRate(speed);
                }
            }
        }

        // Fills the right-edge current-value labels on the main settings rows.
        private void UpdateSettingsRowValues()
        {
            if (QualityValueText != null)
            {
                var effective = GetEffectiveVideoQualityTag();
                var picked = string.IsNullOrWhiteSpace(effective)
                    ? Localization.GetString("Auto")
                    : effective + "p";
                var pickedHeight = ParseInt(effective);
                QualityValueText.Text = _readyHeight > 0 && _readyHeight != pickedHeight
                    ? picked + " · " + _readyHeight + "p"
                    : picked;
            }

            if (SpeedValueText != null)
            {
                SpeedValueText.Text = currentSpeed.ToString("0.##",
                    System.Globalization.CultureInfo.InvariantCulture) + "x";
            }

            if (AudioTrackValueText != null)
            {
                var audioTrack = GetCurrentAudioTrackInfo();
                AudioTrackValueText.Text = audioTrack == null
                    ? string.Empty
                    : (string.IsNullOrWhiteSpace(audioTrack.Name) ? audioTrack.Id : audioTrack.Name);
            }
        }

    }
}
