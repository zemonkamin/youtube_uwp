using System;
using System.Threading.Tasks;
using Windows.UI.Xaml;

namespace YouTube.Innertube
{
    internal struct WatchHistorySample
    {
        internal bool Active;
        internal bool Playing;
        internal double Position;
        internal double Length;
        internal double Rate;
    }

    // One playback session, one CPN and one set of authenticated tracking URLs.
    // Sample locally each second; network reports follow Troubadour's 10s/40s cadence.
    internal sealed class WatchHistoryReporter
    {
        private readonly string _videoId;
        private readonly string _owner;
        private readonly string _refreshToken;
        private readonly DispatcherTimer _timer;
        private Func<WatchHistorySample> _sample;
        private Config.WatchHistorySession _session;
        private Task _sendTail = Task.FromResult(false);
        private WatchHistorySample _previous;
        private DateTime _sampleTime;
        private double _segmentStart;
        private double _segmentEnd;
        private double _elapsed;
        private int _reports;
        private bool _opened;
        private bool _closed;

        internal WatchHistoryReporter(string videoId, Func<WatchHistorySample> sample)
        {
            _videoId = videoId;
            _sample = sample;
            Config.LoadUserToken();
            _refreshToken = Config.UserToken;
            _owner = PlaybackResolver.AccountKey;
            _sampleTime = DateTime.UtcNow;
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        private void Timer_Tick(object sender, object e)
        {
            try { Observe(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine("[History] Sample failed: " + ex.Message); }
        }

        private void Observe()
        {
            if (_closed) return;
            if (_owner != PlaybackResolver.AccountKey) { Close(); return; }
            var current = _sample();
            if (!current.Active) { Stop(); return; }
            var now = DateTime.UtcNow;
            var wall = Math.Max(0, (now - _sampleTime).TotalSeconds);
            var delta = current.Position - _previous.Position;
            var rate = Math.Max(0.25, _previous.Rate);

            if (!_opened && current.Playing && current.Position > 0.1)
            {
                _opened = true;
                _segmentStart = _segmentEnd = current.Position;
                QueueReport(current.Position, current.Length, -1, 0, false);
            }
            else if (_opened)
            {
                if (_previous.Playing && delta >= -0.05 && delta <= Math.Max(3, wall * rate * 1.5 + 1))
                {
                    _segmentEnd = Math.Max(_segmentStart, current.Position);
                    _elapsed += Math.Min(wall, Math.Max(0, delta) / rate);
                }
                else if (Math.Abs(delta) > 0.5)
                {
                    // A seek/source switch splits the segment; never report the skipped range.
                    SendSegment(false);
                    _segmentStart = _segmentEnd = current.Position;
                }

                if (_elapsed >= (_reports < 3 ? 10 : 40) || (_previous.Playing && !current.Playing))
                    SendSegment(false);
            }
            _previous = current;
            _sampleTime = now;
        }

        internal void Stop()
        {
            if (_closed) return;
            // Use the last observed endpoint when the source is already gone. The caller
            // normally stops this reporter before resetting or replacing the media source.
            if (_owner == PlaybackResolver.AccountKey && _opened) SendSegment(true);
            Close();
        }

        private void Close()
        {
            _closed = true;
            _timer.Stop();
            _timer.Tick -= Timer_Tick;
            _sample = null;
        }

        private void SendSegment(bool final)
        {
            if (!_opened || (!final && _elapsed <= 0)) return;
            QueueReport(_segmentEnd, _previous.Length, _segmentStart, _elapsed, final);
            _segmentStart = _segmentEnd;
            _elapsed = 0;
            _reports++;
        }

        private void QueueReport(double position, double length, double from, double elapsed, bool final)
        {
            // Chain sends explicitly so a slow initial /player cannot reorder watch segments.
            _sendTail = SendAfterAsync(_sendTail, position, length, from, elapsed, final);
        }

        private async Task SendAfterAsync(Task previous, double position, double length,
            double from, double elapsed, bool final)
        {
            try
            {
                await previous.ConfigureAwait(false);
                if (_owner != PlaybackResolver.AccountKey) return;
                if (_session == null)
                    _session = await Config.CreateWatchHistorySessionAsync(_videoId, _refreshToken).ConfigureAwait(false);
                if (_session == null || _session.Owner != _owner) return;
                await Config.ReportWatchHistorySessionAsync(_session, position, length, from, elapsed, final).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[History] Watch report failed: " + ex.Message);
            }
        }
    }
}
