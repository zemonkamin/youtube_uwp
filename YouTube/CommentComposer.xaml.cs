using System;
using Windows.ApplicationModel;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;
using YouTube.Innertube;

namespace YouTube
{
    // The Video page's sheet and landscape row share one draft and one sending lock.
    public sealed class CommentDraft
    {
        public CommentDraft(string videoId) { VideoId = videoId ?? string.Empty; }
        public string VideoId { get; private set; }
        internal string Text = string.Empty;
        internal string Error = string.Empty;
        internal bool Sending;
        internal bool NeedsBrowserSignIn;
        internal event EventHandler Changed;
        internal void NotifyChanged()
        {
            var handler = Changed;
            if (handler != null) handler(this, EventArgs.Empty);
        }
    }

    public sealed class CommentPostedEventArgs : EventArgs
    {
        public string VideoId { get; internal set; }
        public CommentItem Comment { get; internal set; }
    }

    public sealed partial class CommentComposer : UserControl
    {
        private CommentDraft _draft;
        private bool _updatingInput;
        private bool _attached;
        public bool CanCompose
        {
            get
            {
                return _draft != null && !string.IsNullOrWhiteSpace(_draft.VideoId)
                    && (WebAccountSession.IsSignedIn || !string.IsNullOrWhiteSpace(Config.UserToken));
            }
        }
        public event EventHandler StateChanged;
        public event EventHandler<CommentPostedEventArgs> CommentPosted;

        public CommentComposer()
        {
            InitializeComponent();
            if (DesignMode.DesignModeEnabled) return;
            CommentInput.PlaceholderText = Localization.GetString("CommentPlaceholder");
            BrowserSignInButton.Content = Localization.GetString("WebAccountSignIn");
            // Receive Enter even when the native text editor marks it handled.
            CommentInput.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler(CommentInput_KeyDown), true);
            Loaded += Composer_Loaded;
            Unloaded += Composer_Unloaded;
        }

        public void BindDraft(CommentDraft draft)
        {
            if (ReferenceEquals(_draft, draft)) { Refresh(); return; }
            DetachDraft();
            _draft = draft;
            AttachDraft();
            Refresh();
        }

        private void AttachDraft()
        {
            if (_draft == null || _attached) return;
            _draft.Changed += Draft_Changed;
            _attached = true;
        }

        private void DetachDraft()
        {
            if (_draft != null && _attached) _draft.Changed -= Draft_Changed;
            _attached = false;
        }

        private void Composer_Loaded(object sender, RoutedEventArgs e) { AttachDraft(); Refresh(); }
        private void Composer_Unloaded(object sender, RoutedEventArgs e) { DetachDraft(); }
        private void Draft_Changed(object sender, EventArgs e) { UpdateInputState(); }

        public async void Refresh()
        {
            if (DesignMode.DesignModeEnabled) return;
            try
            {
                await WebAccountSession.InitializeAsync();
                Config.LoadUserToken();
                if (_draft != null && _draft.NeedsBrowserSignIn && WebAccountSession.IsSignedIn)
                {
                    _draft.NeedsBrowserSignIn = false;
                    _draft.Error = string.Empty;
                    _draft.NotifyChanged();
                }
                UpdateInputState();
                var avatar = Config.GetCachedAccountAvatarUri(Config.UserToken);
                if (!string.IsNullOrWhiteSpace(avatar))
                {
                    // Use the same persistent image and cache policy as Tabbar.
                    // The file URI stays constant when the cached profile photo changes.
                    var bitmap = new BitmapImage
                    {
                        CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                        DecodePixelWidth = ChannelIconController.DecodeSize,
                        DecodePixelHeight = ChannelIconController.DecodeSize,
                        UriSource = new Uri(avatar)
                    };
                    ComposerAvatar.Fill = new ImageBrush { ImageSource = bitmap, Stretch = Stretch.UniformToFill };
                    ComposerAvatar.Visibility = Visibility.Visible;
                    AvatarPlaceholder.Visibility = Visibility.Collapsed;
                }
                else
                {
                    ComposerAvatar.Fill = new ImageBrush { Stretch = Stretch.UniformToFill };
                    ComposerAvatar.Visibility = Visibility.Collapsed;
                    AvatarPlaceholder.Visibility = Visibility.Visible;
                }
                var handler = StateChanged;
                if (handler != null) handler(this, EventArgs.Empty);
            }
            catch (Exception)
            {
                System.Diagnostics.Debug.WriteLine("[Comments] Composer account state unavailable.");
            }
        }

        private void UpdateInputState()
        {
            if (CommentInput == null) return;
            ComposerRoot.Visibility = CanCompose ? Visibility.Visible : Visibility.Collapsed;
            var text = _draft == null ? string.Empty : _draft.Text;
            _updatingInput = true;
            if (CommentInput.Text != text) CommentInput.Text = text;
            _updatingInput = false;
            var sending = _draft != null && _draft.Sending;
            CommentInput.IsReadOnly = sending;
            PlaceholderTextVisibility(text);
            var error = _draft == null ? string.Empty : _draft.Error;
            ErrorText.Text = error;
            ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
            BrowserSignInButton.Visibility = _draft != null && _draft.NeedsBrowserSignIn ? Visibility.Visible : Visibility.Collapsed;
        }

        private void CommentInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingInput || _draft == null) return;
            _draft.Text = CommentInput.Text;
            _draft.Error = string.Empty;
            _draft.NeedsBrowserSignIn = false;
            _draft.NotifyChanged();
        }

        private void PlaceholderTextVisibility(string text)
        {
            var placeholder = FindPlaceholder(CommentInput);
            if (placeholder != null) placeholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }

        private static TextBlock FindPlaceholder(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                var text = child as TextBlock;
                if (text != null && text.Name == "PlaceholderTextContentPresenter") return text;
                var nested = FindPlaceholder(child);
                if (nested != null) return nested;
            }
            return null;
        }

        private void CommentInput_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != VirtualKey.Enter) return;
            e.Handled = true;
            if (_draft == null || _draft.Sending) return;
            if ((Window.Current.CoreWindow.GetKeyState(VirtualKey.Shift) & CoreVirtualKeyStates.Down) != 0)
            {
                // AcceptsReturn=False gives the touch keyboard an action key. Keep
                // multiline comments available through Shift+Enter on hardware keyboards.
                var position = CommentInput.SelectionStart;
                CommentInput.SelectedText = "\r\n";
                CommentInput.Select(position + 2, 0);
                return;
            }
            SendCommentAsync();
        }

        private async void SendCommentAsync()
        {
            var draft = _draft;
            if (!CanCompose || draft == null || draft.Sending || string.IsNullOrWhiteSpace(draft.Text)) return;
            var text = draft.Text;
            draft.Sending = true;
            draft.Error = string.Empty;
            draft.NeedsBrowserSignIn = false;
            draft.NotifyChanged();
            try
            {
                var result = await Config.PostCommentAsync(draft.VideoId, text);
                if (result.Succeeded)
                {
                    draft.Text = string.Empty;
                    var handler = CommentPosted;
                    if (handler != null)
                    {
                        try { handler(this, new CommentPostedEventArgs { VideoId = draft.VideoId, Comment = result.Comment }); }
                        catch (Exception) { System.Diagnostics.Debug.WriteLine("[Comments] Posted comment could not be displayed."); }
                    }
                }
                else
                {
                    draft.Error = result.ErrorMessage;
                    draft.NeedsBrowserSignIn = result.BrowserSignInRequired;
                }
            }
            catch (Exception)
            {
                draft.Error = Localization.GetString("CommentSendFailed");
            }
            finally
            {
                draft.Sending = false;
                draft.NotifyChanged();
                Refresh();
            }
        }

        private void BrowserSignInButton_Click(object sender, RoutedEventArgs e)
        {
            var frame = Window.Current.Content as Frame;
            if (frame != null) frame.Navigate(typeof(GoogleSignIn));
        }
    }
}
