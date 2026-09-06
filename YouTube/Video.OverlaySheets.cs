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
        private void ShowSubscriptionMenuBottomSheet()
        {
            if (!_hasSignedInAccount) return;

            if (_currentSubscriptionState != ChannelSubscriptionState.Subscribed)
            {
                return;
            }

            UpdateSubscriptionMenuVisualState();

            if (OverlayGrid != null)
            {
                OverlayGrid.Visibility = Visibility.Visible;
            }

            if (SubscriptionMenuBottomSheetPanel != null)
            {
                SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Visible;
            }

            AnimateSubscriptionMenuBottomSheet(true);
        }

        private void AnimateSubscriptionMenuBottomSheet(bool show)
        {
            if (SubscriptionMenuBottomSheetTransform == null)
            {
                return;
            }

            var animation = new DoubleAnimation();
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(300));
            animation.EasingFunction = new CircleEase();
            animation.To = show ? 0 : SubscriptionMenuBottomSheetPanel.DismissDistance;

            Storyboard.SetTarget(animation, SubscriptionMenuBottomSheetTransform);
            Storyboard.SetTargetProperty(animation, "Y");

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);

            if (!show)
            {
                storyboard.Completed += (s, e) =>
                {
                    if (SubscriptionMenuBottomSheetPanel != null)
                    {
                        SubscriptionMenuBottomSheetPanel.Visibility = Visibility.Collapsed;
                    }
                    if (OverlayGrid != null)
                    {
                        OverlayGrid.Visibility = Visibility.Collapsed;
                    }
                };
            }

            storyboard.Begin();
        }

        private async void NotificationAllOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            if (_currentNotificationState == ChannelNotificationState.All)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.All);
        }

        private async void NotificationPersonalizedOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            if (_currentNotificationState == ChannelNotificationState.Default || _currentNotificationState == ChannelNotificationState.Unknown)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.Default);
        }

        private async void NotificationNoneOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            if (_currentNotificationState == ChannelNotificationState.None)
            {
                AnimateSubscriptionMenuBottomSheet(false);
                return;
            }

            await ModifyChannelNotificationPreferenceAsync(ChannelNotificationState.None);
        }

        private async void NotificationUnsubscribeOptionButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_hasSignedInAccount) return;
            await SetChannelSubscriptionStateAsync(false);
        }

        private void SubscriptionMenuDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSubscriptionMenuBottomSheet(false);
        }

        private void SubscriptionMenuDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _subscriptionMenuInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _subscriptionMenuInitialTransformY = SubscriptionMenuBottomSheetTransform != null ? SubscriptionMenuBottomSheetTransform.Y : 0;
                _subscriptionMenuIsDragging = true;
                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_subscriptionMenuIsDragging && SubscriptionMenuBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _subscriptionMenuInitialY;
                double newY = _subscriptionMenuInitialTransformY + dragOffset;

                if (newY >= 0 && newY <= SubscriptionMenuBottomSheetPanel.DismissDistance)
                {
                    SubscriptionMenuBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void SubscriptionMenuDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_subscriptionMenuIsDragging)
            {
                _subscriptionMenuIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (SubscriptionMenuBottomSheetTransform != null && SubscriptionMenuBottomSheetTransform.Y > SubscriptionMenuBottomSheetPanel.DragDismissThreshold)
                {
                    AnimateSubscriptionMenuBottomSheet(false);
                }
                else
                {
                    AnimateSubscriptionMenuBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void OverlayGrid_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            AnimateSaveBottomSheet(false);
            AnimateCommentsBottomSheet(false);
            AnimateShareBottomSheet(false);
            AnimateSettingsBottomSheet(false);
            AnimateSubscriptionMenuBottomSheet(false);
            OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void ShareDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // Close share bottom sheet when tapping drag area
            AnimateShareBottomSheet(false);
            if (OverlayGrid != null)
                OverlayGrid.Visibility = Visibility.Collapsed;
        }

        private void SaveDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            AnimateSaveBottomSheet(false);
            e.Handled = true;
        }

        private void SaveDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _saveInitialY = e.GetCurrentPoint(element).Position.Y;
                _saveInitialTransformY = SaveBottomSheetTransform != null ? SaveBottomSheetTransform.Y : 0;
                _saveIsDragging = true;
                e.Handled = true;
            }
        }

        private void SaveDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (!_saveIsDragging || element == null || SaveBottomSheetTransform == null)
            {
                return;
            }

            var dragOffset = e.GetCurrentPoint(element).Position.Y - _saveInitialY;
            var newY = _saveInitialTransformY + dragOffset;
            var dismissDistance = GetSaveSheetDismissDistance();
            if (newY >= 0 && newY <= dismissDistance)
            {
                SaveBottomSheetTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SaveDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_saveIsDragging)
            {
                return;
            }

            _saveIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SaveBottomSheetTransform != null
                && SaveBottomSheetTransform.Y > GetSaveSheetDismissDistance() * 0.5)
            {
                AnimateSaveBottomSheet(false);
            }
            else
            {
                AnimateSaveBottomSheet(true);
            }
            e.Handled = true;
        }

        private void ShareDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pointer = e.Pointer;
            if ((sender as UIElement).CapturePointer(pointer))
            {
                _shareInitialY = e.GetCurrentPoint(sender as UIElement).Position.Y;
                _shareInitialTransformY = ShareBottomSheetTransform?.Y ?? 0;
                _shareIsDragging = true;
                e.Handled = true;
            }
        }

        private void ShareDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_shareIsDragging && ShareBottomSheetTransform != null)
            {
                var currentPoint = e.GetCurrentPoint(sender as UIElement);
                double dragOffset = currentPoint.Position.Y - _shareInitialY;
                double newY = _shareInitialTransformY + dragOffset;

                double dismissDistance = GetShareSheetDismissDistance();
                if (newY >= 0 && newY <= dismissDistance)
                {
                    ShareBottomSheetTransform.Y = newY;
                }

                e.Handled = true;
            }
        }

        private void ShareDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_shareIsDragging)
            {
                _shareIsDragging = false;
                (sender as UIElement).ReleasePointerCapture(e.Pointer);

                if (ShareBottomSheetTransform.Y > GetShareSheetDismissDistance() * 0.5)
                {
                    // Close the sheet
                    AnimateShareBottomSheet(false);
                    if (OverlayGrid != null)
                        OverlayGrid.Visibility = Visibility.Collapsed;
                }
                else
                {
                    // Snap back to open position
                    AnimateShareBottomSheet(true);
                }

                e.Handled = true;
            }
        }

        private void CopyLinkButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentVideoId))
            {
                string shareUrl = BuildShareUrl();
                var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
                dataPackage.SetText(shareUrl);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

                AnimateShareBottomSheet(false);
                ShowSharePopup(Localization.GetString("LinkCopied"), Localization.GetString("VideoLinkCopied"));
            }
        }

        private void ShareViaSystemButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(currentVideoId))
            {
                string shareUrl = BuildShareUrl();
                string title = VideoTitleText?.Text ?? "YouTube Video";

                // Close the share bottom sheet first
                AnimateShareBottomSheet(false);

                // Use Windows Share UI
                var dataTransferManager =
                    Windows.ApplicationModel.DataTransfer.DataTransferManager.GetForCurrentView();
                dataTransferManager.DataRequested += (shareSender, args) =>
                {
                    var request = args.Request;
                    request.Data.Properties.Title = title;
                    request.Data.Properties.Description = Localization.GetString("ShareVideoDescription");
                    request.Data.SetWebLink(new Uri(shareUrl));
                };

                // Show share UI
                Windows.ApplicationModel.DataTransfer.DataTransferManager.ShowShareUI();
            }
        }

        private void SharePopupDragArea_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            e.Handled = true;
        }

        private void SharePopupDragArea_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var element = sender as UIElement;
            if (element != null && element.CapturePointer(e.Pointer))
            {
                _sharePopupInitialY = e.GetCurrentPoint(element).Position.Y;
                _sharePopupInitialTransformY = SharePopupTransform != null ? SharePopupTransform.Y : 0;
                _sharePopupIsDragging = true;
                e.Handled = true;
            }
        }

        private void SharePopupDragArea_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_sharePopupIsDragging || SharePopupTransform == null)
            {
                return;
            }

            var element = sender as UIElement;
            if (element == null)
            {
                return;
            }

            var dragOffset = e.GetCurrentPoint(element).Position.Y - _sharePopupInitialY;
            var newY = _sharePopupInitialTransformY + dragOffset;
            var dismissDistance = GetSharePopupDismissDistance();
            if (newY >= 0 && newY <= dismissDistance)
            {
                SharePopupTransform.Y = newY;
            }
            e.Handled = true;
        }

        private void SharePopupDragArea_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_sharePopupIsDragging)
            {
                return;
            }

            _sharePopupIsDragging = false;
            var element = sender as UIElement;
            if (element != null)
            {
                element.ReleasePointerCapture(e.Pointer);
            }

            if (SharePopupTransform != null && SharePopupTransform.Y > GetSharePopupDismissDistance() * 0.35)
            {
                HideSharePopup();
            }
            else
            {
                AnimateSharePopup(true);
            }
            e.Handled = true;
        }

        private void SharePopupOverlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            HideSharePopup();
            e.Handled = true;
        }

        private void SharePopupContainer_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
        }

    }
}
