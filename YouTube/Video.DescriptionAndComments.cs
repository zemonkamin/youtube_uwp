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
        private void ShowDescriptionButton_Click(object sender, RoutedEventArgs e)
        {
            ShowDescriptionBottomSheet();
        }

        private void CommentsContainerButton_Click(object sender, RoutedEventArgs e)
        {
            ShowCommentsBottomSheet();
        }

        private async void CommentRepliesButton_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            var comment = button != null ? button.DataContext as CommentItem : null;
            if (comment == null || string.IsNullOrWhiteSpace(currentVideoId)
                || !comment.TryBeginLoadingReplies())
            {
                return;
            }

            try
            {
                var replies = await Config.GetCommentRepliesAsync(
                    currentVideoId,
                    comment.ReplyContinuationToken,
                    false);

                // As in youtube-ios, the replies row is replaced by the returned branch.
                var loaded = replies != null && replies.Count > 0;
                comment.FinishLoadingReplies(replies, loaded);
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] Replies opened: " + (replies != null ? replies.Count : 0)
                    + ", tokenContext=WEB");
            }
            catch (Exception ex)
            {
                comment.FinishLoadingReplies(null, false);
                System.Diagnostics.Debug.WriteLine("[Comments] Replies load failed: " + ex.Message);
            }
        }

        private void ShowDescriptionBottomSheet()
        {
            UpdateDescriptionSheetHeight();

            // Set the description text with link support
            if (!string.IsNullOrEmpty(_currentVideoDescription))
            {
                SetDescriptionWithLinks(_currentVideoDescription);
            }
            else
            {
                if (DescriptionTextBlock != null)
                {
                    DescriptionTextBlock.Blocks.Clear();
                    var paragraph = new Paragraph();
                    var run = new Run();
                    run.Text = Localization.GetString("DescriptionNotAvailable");
                    paragraph.Inlines.Add(run);
                    DescriptionTextBlock.Blocks.Add(paragraph);
                }
            }

            // Show the overlay and bottom sheet
            if (DescriptionOverlayGrid != null)
                DescriptionOverlayGrid.Visibility = Visibility.Visible;
            if (DescriptionBottomSheetPanel != null)
                DescriptionBottomSheetPanel.Visibility = Visibility.Visible;

            // Animate the bottom sheet up
            AnimateDescriptionBottomSheet(true);
        }

        private void SetDescriptionWithLinks(string description)
        {
            SetDescriptionWithLinks(DescriptionTextBlock, description);
        }

        private void UpdateLandscapeDescriptionText()
        {
            if (LandscapeDescriptionTextBlock == null)
                return;

            _landscapeDescriptionExpanded = false;
            _landscapeDescriptionNeedsToggle = false;
            UpdateLandscapeDescriptionExpansionState();

            var text = string.IsNullOrWhiteSpace(_currentVideoDescription)
                ? Localization.GetString("DescriptionNotAvailable")
                : _currentVideoDescription;
            SetDescriptionWithLinks(LandscapeDescriptionTextBlock, text);

            var ignored = Dispatcher.RunAsync(
                CoreDispatcherPriority.Low,
                RefreshLandscapeDescriptionToggleVisibility);
        }

        private void LandscapeDescriptionTextBlock_LayoutUpdated(object sender, object e)
        {
            if (!_landscapeDescriptionExpanded)
                RefreshLandscapeDescriptionToggleVisibility();
        }

        private void RefreshLandscapeDescriptionToggleVisibility()
        {
            if (LandscapeDescriptionTextBlock == null || LandscapeDescriptionToggleButton == null)
                return;

            if (!_landscapeDescriptionExpanded)
                _landscapeDescriptionNeedsToggle = LandscapeDescriptionTextBlock.HasOverflowContent;

            LandscapeDescriptionToggleButton.Visibility = _landscapeDescriptionNeedsToggle
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void LandscapeDescriptionToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _landscapeDescriptionExpanded = !_landscapeDescriptionExpanded;
            UpdateLandscapeDescriptionExpansionState();
        }

        private void UpdateLandscapeDescriptionExpansionState()
        {
            if (LandscapeDescriptionTextBlock != null)
                LandscapeDescriptionTextBlock.MaxHeight = _landscapeDescriptionExpanded
                    ? double.MaxValue
                    : 58.0;

            if (LandscapeDescriptionToggleText != null)
            {
                LandscapeDescriptionToggleText.Text = _landscapeDescriptionExpanded
                    ? Localization.GetString("ShowLess")
                    : "…" + Localization.GetString("ShowMore");
            }

            if (LandscapeDescriptionToggleButton != null)
                LandscapeDescriptionToggleButton.Visibility = _landscapeDescriptionNeedsToggle
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SetDescriptionWithLinks(RichTextBlock target, string description)
        {
            if (target == null)
                return;

            target.Blocks.Clear();
            var paragraph = new Paragraph();
            var regex = new Regex(
                "(?<url>" + DescriptionUrlPattern + ")|(?<time>" + DescriptionTimecodePattern + ")",
                RegexOptions.IgnoreCase
            );

            var lastPos = 0;
            foreach (Match match in regex.Matches(description))
            {
                if (match.Index > lastPos)
                {
                    AddDescriptionTextRun(paragraph, description.Substring(lastPos, match.Index - lastPos));
                }

                if (match.Groups["url"].Success)
                {
                    var hyperlink = new Hyperlink();
                    var linkRun = new Run();
                    linkRun.Text = match.Value;
                    hyperlink.Inlines.Add(linkRun);
                    hyperlink.Click += Hyperlink_Click;
                    paragraph.Inlines.Add(hyperlink);
                }
                else if (match.Groups["time"].Success)
                {
                    TimeSpan timestamp;
                    if (TryParseDescriptionTimecode(match.Value, out timestamp))
                    {
                        var hyperlink = new Hyperlink();
                        var linkRun = new Run();
                        linkRun.Text = match.Value;
                        hyperlink.Inlines.Add(linkRun);
                        TimeSpan targetTimestamp = timestamp;
                        hyperlink.Click += (sender, args) => SeekToDescriptionTimestamp(targetTimestamp);
                        paragraph.Inlines.Add(hyperlink);
                    }
                    else
                    {
                        AddDescriptionTextRun(paragraph, match.Value);
                    }
                }
                else
                {
                    AddDescriptionTextRun(paragraph, match.Value);
                }

                lastPos = match.Index + match.Length;
            }

            if (lastPos < description.Length)
            {
                AddDescriptionTextRun(paragraph, description.Substring(lastPos));
            }

            if (paragraph.Inlines.Count == 0)
            {
                AddDescriptionTextRun(paragraph, description);
            }

            target.Blocks.Add(paragraph);
        }

        private void AddDescriptionTextRun(Paragraph paragraph, string text)
        {
            if (paragraph == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            var run = new Run();
            run.Text = text;
            paragraph.Inlines.Add(run);
        }

        private void CommentTextRichTextBlock_Loaded(object sender, RoutedEventArgs e)
        {
            var richTextBlock = sender as RichTextBlock;
            if (richTextBlock == null)
            {
                return;
            }

            var text = richTextBlock.Tag as string;
            SetCommentTextWithTimecodeLinks(richTextBlock, text ?? string.Empty);
        }

        private void SetCommentTextWithTimecodeLinks(RichTextBlock richTextBlock, string text)
        {
            if (richTextBlock == null)
            {
                return;
            }

            richTextBlock.Blocks.Clear();

            var paragraph = new Paragraph();
            if (string.IsNullOrEmpty(text))
            {
                richTextBlock.Blocks.Add(paragraph);
                return;
            }

            var regex = new Regex(
                "(?<url>" + DescriptionUrlPattern + ")|(?<time>" + DescriptionTimecodePattern + ")",
                RegexOptions.IgnoreCase
            );

            var lastPos = 0;
            foreach (Match match in regex.Matches(text))
            {
                if (match.Index > lastPos)
                {
                    AddDescriptionTextRun(paragraph, text.Substring(lastPos, match.Index - lastPos));
                }

                if (match.Groups["time"].Success)
                {
                    TimeSpan timestamp;
                    if (TryParseDescriptionTimecode(match.Value, out timestamp))
                    {
                        var hyperlink = new Hyperlink();
                        var linkRun = new Run();
                        linkRun.Text = match.Value;
                        hyperlink.Inlines.Add(linkRun);
                        TimeSpan targetTimestamp = timestamp;
                        hyperlink.Click += (hyperlinkSender, args) => SeekToCommentTimestamp(targetTimestamp);
                        paragraph.Inlines.Add(hyperlink);
                    }
                    else
                    {
                        AddDescriptionTextRun(paragraph, match.Value);
                    }
                }
                else if (match.Groups["url"].Success)
                {
                    var hyperlink = new Hyperlink();
                    var linkRun = new Run();
                    linkRun.Text = match.Value;
                    hyperlink.Inlines.Add(linkRun);
                    hyperlink.Click += Hyperlink_Click;
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                {
                    AddDescriptionTextRun(paragraph, match.Value);
                }

                lastPos = match.Index + match.Length;
            }

            if (lastPos < text.Length)
            {
                AddDescriptionTextRun(paragraph, text.Substring(lastPos));
            }

            if (paragraph.Inlines.Count == 0)
            {
                AddDescriptionTextRun(paragraph, text);
            }

            richTextBlock.Blocks.Add(paragraph);
        }

        // Fires and forgets: SponsorBlock is a third-party service, so playback must never wait on
        // it. The answer arrives a moment after the video starts, which is fine — sponsor segments
        // at the very top of a video are rare, and the check runs continuously afterwards.
        private async void LoadSponsorBlockSegments(string videoId)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SetSkipSegments(null);
                }

                var segments = await SponsorBlock.GetSegmentsAsync(videoId);

                // The user may have moved on to another video while the request was in flight.
                if (CustomVideoPlayer == null
                    || !string.Equals(currentVideoId, videoId, StringComparison.Ordinal))
                {
                    return;
                }

                CustomVideoPlayer.SetSkipSegments(segments);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] SponsorBlock load failed: " + ex.Message);
            }
        }

        private void UpdateDescriptionChaptersFromDescription()
        {
            _descriptionChapterMarkers.Clear();

            if (!string.IsNullOrWhiteSpace(_currentVideoDescription))
            {
                var seenSeconds = new HashSet<int>();
                var timecodeRegex = new Regex(DescriptionTimecodePattern, RegexOptions.IgnoreCase);
                var lines = _currentVideoDescription.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    foreach (Match match in timecodeRegex.Matches(line))
                    {
                        TimeSpan position;
                        if (!TryParseDescriptionTimecode(match.Value, out position))
                        {
                            continue;
                        }

                        int totalSeconds = (int)Math.Round(position.TotalSeconds);
                        if (seenSeconds.Contains(totalSeconds))
                        {
                            continue;
                        }

                        seenSeconds.Add(totalSeconds);
                        _descriptionChapterMarkers.Add(new YouTube.CustomVideoPlayer.VideoChapterMarker
                        {
                            Position = position,
                            Title = ExtractDescriptionChapterTitle(line, match.Value)
                        });
                    }
                }
            }

            if (CustomVideoPlayer != null)
            {
                CustomVideoPlayer.SetChapters(_descriptionChapterMarkers);
            }
        }

        private string ExtractDescriptionChapterTitle(string line, string timecodeText)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return string.Empty;
            }

            string title = line.Replace(timecodeText, string.Empty).Trim();
            title = title.Trim(' ', '-', '–', '—', ':', '|', '.', ')', '(');
            return title;
        }

        private bool TryParseDescriptionTimecode(string text, out TimeSpan position)
        {
            position = TimeSpan.Zero;

            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var parts = text.Split(':');
            if (parts.Length < 2 || parts.Length > 3)
            {
                return false;
            }

            int hours = 0;
            int minutes;
            int seconds;

            if (parts.Length == 2)
            {
                if (!int.TryParse(parts[0], out minutes) || !int.TryParse(parts[1], out seconds))
                {
                    return false;
                }
            }
            else
            {
                if (!int.TryParse(parts[0], out hours)
                    || !int.TryParse(parts[1], out minutes)
                    || !int.TryParse(parts[2], out seconds))
                {
                    return false;
                }

                if (minutes < 0 || minutes > 59)
                {
                    return false;
                }
            }

            if (hours < 0 || minutes < 0 || seconds < 0 || seconds > 59)
            {
                return false;
            }

            position = new TimeSpan(hours, minutes, seconds);
            return true;
        }

        private void SeekToDescriptionTimestamp(TimeSpan timestamp)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SeekTo(timestamp);
                }

                AnimateDescriptionBottomSheet(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to seek to description timestamp: " + ex.Message);
            }
        }

        private void SeekToCommentTimestamp(TimeSpan timestamp)
        {
            try
            {
                if (CustomVideoPlayer != null)
                {
                    CustomVideoPlayer.SeekTo(timestamp);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[Video] Failed to seek to comment timestamp: " + ex.Message);
            }
        }

        private async void Hyperlink_Click(Hyperlink sender, HyperlinkClickEventArgs args)
        {
            try
            {
                var url = "";
                foreach (var inline in sender.Inlines)
                {
                    var run = inline as Run;
                    if (run != null)
                    {
                        url += run.Text;
                    }
                }

                if (!string.IsNullOrEmpty(url))
                {
                    Uri uri;
                    if (Uri.TryCreate(url, UriKind.Absolute, out uri))
                    {
                        await Windows.System.Launcher.LaunchUriAsync(uri);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error launching URL: {ex.Message}");
            }
        }

        private void AnimateDescriptionBottomSheet(bool show)
        {
            if (DescriptionBottomSheetTransform == null)
                return;

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0;
                if (DescriptionOverlayGrid != null)
                    DescriptionOverlayGrid.Visibility = Visibility.Visible;
            }
            else
            {
                animation.To = DescriptionBottomSheetPanel.DismissDistance;
            }

            Storyboard.SetTarget(animation, DescriptionBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (DescriptionBottomSheetPanel != null)
                        DescriptionBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (DescriptionOverlayGrid != null)
                        DescriptionOverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            storyboard.Begin();
        }

        private void UpdateDescriptionSheetHeight()
        {
            try
            {
                double playerHeight = VideoPlayerContainer?.ActualHeight ?? 0;
                if (playerHeight <= 0)
                {
                    playerHeight = CustomVideoPlayer?.ActualHeight ?? 0;
                }

                if (playerHeight > 0)
                {
                    _lastKnownPlayerHeight = playerHeight;
                }
                else if (_lastKnownPlayerHeight > 0)
                {
                    playerHeight = _lastKnownPlayerHeight;
                }
                else
                {
                    return;
                }

                double contentRowHeight = MainScrollViewer?.ActualHeight ?? 0;
                if (contentRowHeight <= 0)
                {
                    contentRowHeight = Window.Current.Bounds.Height;
                }

                double availableHeight = Math.Max(0, contentRowHeight - playerHeight);
                double desiredHeight = availableHeight;

                if (DescriptionBottomSheetPanel != null)
                    DescriptionBottomSheetPanel.Height = desiredHeight;
                DescriptionBottomSheetPanel.UpdateLayout();
                _descriptionSheetHiddenOffset = DescriptionBottomSheetPanel.DismissDistance;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"Failed to update description sheet height: {ex.Message}"
                );
            }
        }

        private void DescriptionCloseButton_Click(object sender, RoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionOverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateDescriptionBottomSheet(false);
        }

        private void DescriptionDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _descriptionInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _descriptionInitialTransformY = DescriptionBottomSheetTransform?.Y ?? 0;
                _descriptionIsDragging = true;
                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_descriptionIsDragging && DescriptionBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _descriptionInitialY;
                double newY = _descriptionInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= DescriptionBottomSheetPanel.DismissDistance)
                {
                    DescriptionBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void DescriptionDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_descriptionIsDragging)
            {
                _descriptionIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (DescriptionBottomSheetTransform.Y > DescriptionBottomSheetPanel.DragDismissThreshold)
                {
                    AnimateDescriptionBottomSheet(false);
                }
                else
                {
                    AnimateDescriptionBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void ShowCommentsBottomSheet()
        {
            System.Diagnostics.Debug.WriteLine("[Comments] ShowCommentsBottomSheet called");

            // Materialize the modal list only while it is open. Keeping a third comment visual
            // tree alive behind the page caused avoidable layout work on Windows 10 Mobile.
            if (_currentComments != null && _currentComments.Count > 0)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[Comments] Setting CommentsItemsControl.ItemsSource with items"
                );

                if (CommentsItemsControl != null)
                    CommentsItemsControl.ItemsSource = _currentComments;
                else
                    System.Diagnostics.Debug.WriteLine(
                        "[Comments] ERROR: CommentsItemsControl is null!"
                    );
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[Comments] No comments to show");
            }

            // Show the overlay and bottom sheet
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Visible;
            if (CommentsBottomSheetPanel != null)
            {
                CommentsBottomSheetPanel.Visibility = Visibility.Visible;
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] CommentsBottomSheetPanel set to Visible"
                );
            }
            else
            {
                System.Diagnostics.Debug.WriteLine(
                    "[Comments] ERROR: CommentsBottomSheetPanel is null!"
                );
            }

            // Animate the bottom sheet up
            AnimateCommentsBottomSheet(true);
        }

        private void AnimateCommentsBottomSheet(bool show)
        {
            if (CommentsBottomSheetTransform == null)
                return;

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();

            if (show)
            {
                animation.To = 0;
            }
            else
            {
                animation.To = CommentsBottomSheetPanel.DismissDistance;
            }

            Storyboard.SetTarget(animation, CommentsBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (CommentsBottomSheetPanel != null)
                        CommentsBottomSheetPanel.Visibility = Visibility.Collapsed;
                    if (CommentsItemsControl != null)
                        CommentsItemsControl.ItemsSource = null;
                    if (OverlayGrid != null)
                        OverlayGrid.Visibility = Visibility.Collapsed;
                };
            }

            storyboard.Begin();
        }

        private void CommentsDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateCommentsBottomSheet(false);
        }

        private void CommentsDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _commentsInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _commentsInitialTransformY = CommentsBottomSheetTransform?.Y ?? 0;
                _commentsIsDragging = true;
                e.Handled = true;
            }
        }

        private void CommentsDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_commentsIsDragging && CommentsBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _commentsInitialY;
                double newY = _commentsInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= CommentsBottomSheetPanel.DismissDistance)
                {
                    CommentsBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void CommentsDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_commentsIsDragging)
            {
                _commentsIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (CommentsBottomSheetTransform.Y > CommentsBottomSheetPanel.DragDismissThreshold)
                {
                    AnimateCommentsBottomSheet(false);
                }
                else
                {
                    AnimateCommentsBottomSheet(true);
                }

                e.Handled = true;
            }
        }

    }
}
