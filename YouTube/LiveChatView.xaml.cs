using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using YouTube.Innertube;

namespace YouTube
{
    public sealed partial class LiveChatView : UserControl
    {
        private readonly ObservableCollection<LiveChatItem> _items = new ObservableCollection<LiveChatItem>();
        private CancellationTokenSource _cancel;
        private string _videoId, _seed;
        private int _generation;
        private bool _following = true;
        private ScrollViewer _messagesScroll;

        public LiveChatView()
        {
            InitializeComponent();
            ChatTitle.Text = Localization.GetString("LiveChatTitle");
            MessagesList.ItemsSource = _items;
            Unloaded += (s, e) => Stop();
        }

        internal void Open(string videoId, string seed)
        {
            Stop();
            _videoId = videoId; _seed = seed;
            _items.Clear(); PinnedMessage.Visibility = Visibility.Collapsed;
            _following = true; ChatNotice.Text = Localization.GetString("LiveChatEmpty"); ChatNotice.Visibility = Visibility.Visible;
            Start();
        }

        private void Start()
        {
            if (_cancel != null) _cancel.Cancel();
            var generation = ++_generation;
            _cancel = new CancellationTokenSource();
            var ignored = PollAsync(generation, _cancel.Token);
        }

        private async Task PollAsync(int generation, CancellationToken cancellation)
        {
            var owner = PlaybackResolver.AccountKey;
            var token = string.Empty;
            var started = false;
            try
            {
                while (!cancellation.IsCancellationRequested && generation == _generation)
                {
                    var delay = 10.0;
                    try
                    {
                        if (!started)
                        {
                            var initial = await LiveChatService.OpenAsync(_videoId, _seed, cancellation);
                            if (cancellation.IsCancellationRequested || generation != _generation || owner != PlaybackResolver.AccountKey) return;
                            token = !string.IsNullOrEmpty(initial.AllToken) ? initial.AllToken : initial.Token;
                            if (string.IsNullOrEmpty(token)) throw new InvalidOperationException("No live chat token");
                            started = true;
                        }
                        var page = await LiveChatService.FetchAsync(token, cancellation);
                        if (cancellation.IsCancellationRequested || generation != _generation || owner != PlaybackResolver.AccountKey) return;
                        if (!string.IsNullOrEmpty(page.Token)) token = page.Token;
                        if (page.ClearBanner) PinnedMessage.Visibility = Visibility.Collapsed;
                        if (!string.IsNullOrEmpty(page.Banner)) { PinnedMessage.Text = page.Banner; PinnedMessage.Visibility = Visibility.Visible; }
                        for (var i = _items.Count - 1; i >= 0; i--)
                            if (page.RemovedIds.Contains(_items[i].Id) || page.RemovedAuthors.Contains(_items[i].ChannelId)) _items.RemoveAt(i);
                        foreach (var item in page.Items)
                        {
                            var previous = !string.IsNullOrEmpty(item.Id) ? _items.FirstOrDefault(x => x.Id == item.Id) : null;
                            if (previous != null) _items[_items.IndexOf(previous)] = item;
                            else _items.Add(item);
                        }
                        // Troubadour retains the last 50 messages; never grow a live list forever.
                        while (_items.Count > 50) _items.RemoveAt(0);
                        ChatNotice.Text = _items.Count == 0 ? Localization.GetString("LiveChatEmpty") : string.Empty;
                        ChatNotice.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                        if (_following && _items.Count > 0) MessagesList.ScrollIntoView(_items.Last());
                        delay = page.WaitSeconds;
                    }
                    catch (OperationCanceledException) { if (cancellation.IsCancellationRequested) return; }
                    catch (Exception ex)
                    {
                        if (generation != _generation || owner != PlaybackResolver.AccountKey) return;
                        ChatNotice.Text = Localization.GetString("LiveChatRetry"); ChatNotice.Visibility = Visibility.Visible;
                        System.Diagnostics.Debug.WriteLine("[LiveChat] Retry: " + ex.GetType().Name);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(2, delay)), cancellation);
                }
            }
            catch (OperationCanceledException) { }
        }

        private void Messages_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            var scroll = sender as ScrollViewer ?? FindScroll(MessagesList);
            if (scroll != null && !e.IsIntermediate) _following = scroll.ScrollableHeight - scroll.VerticalOffset < 60;
        }

        private void Messages_Loaded(object sender, RoutedEventArgs e)
        {
            if (_messagesScroll != null) _messagesScroll.ViewChanged -= Messages_ViewChanged;
            _messagesScroll = FindScroll(MessagesList);
            if (_messagesScroll != null) _messagesScroll.ViewChanged += Messages_ViewChanged;
        }

        private static ScrollViewer FindScroll(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            { var child = VisualTreeHelper.GetChild(parent, i); var scroll = child as ScrollViewer ?? FindScroll(child); if (scroll != null) return scroll; }
            return null;
        }

        internal void Stop()
        {
            _generation++;
            if (_cancel != null) { _cancel.Cancel(); _cancel = null; }
        }
    }
}
