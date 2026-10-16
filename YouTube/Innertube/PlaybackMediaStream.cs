using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace YouTube.Innertube
{
    // UWP's CreateFromUri does not accept HTTP headers for a progressive MP4.
    // Troubadour relays these requests with the issuing client's User-Agent. A seekable
    // range stream provides the same transport to CreateFromStream without a local server.
    internal sealed class PlaybackMediaStream : Stream
    {
        private const int ChunkBytes = 256 * 1024;
        private readonly Uri _uri;
        private readonly string _userAgent;
        private readonly SemaphoreSlim _readGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();
        private long _length;
        private long _position;
        private long _chunkStart;
        private byte[] _chunk;
        private bool _disposed;

        private PlaybackMediaStream(Uri uri, string userAgent)
        {
            _uri = uri;
            _userAgent = userAgent;
        }

        internal static async Task<PlaybackMediaStream> OpenAsync(Uri uri, string userAgent)
        {
            var stream = new PlaybackMediaStream(uri, userAgent);
            try
            {
                await stream.FetchAsync(0, ChunkBytes, CancellationToken.None).ConfigureAwait(false);
                return stream;
            }
            catch { stream.Dispose(); throw; }
        }

        public override bool CanRead { get { return !_disposed; } }
        public override bool CanSeek { get { return !_disposed; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return _length; } }
        public override long Position
        {
            get { return _position; }
            set { Seek(value, SeekOrigin.Begin); }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (_disposed) throw new ObjectDisposedException("PlaybackMediaStream");
            var position = origin == SeekOrigin.Begin ? offset
                : origin == SeekOrigin.Current ? _position + offset : _length + offset;
            if (position < 0) throw new ArgumentOutOfRangeException("offset");
            _position = position;
            return position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentOutOfRangeException("count");
            await _readGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed) throw new ObjectDisposedException("PlaybackMediaStream");
                cancellationToken.ThrowIfCancellationRequested();
                if (count == 0 || _position >= _length) return 0;
                if (_chunk == null || _position < _chunkStart || _position >= _chunkStart + _chunk.Length)
                    await FetchAsync(_position, Math.Min(1024 * 1024, Math.Max(ChunkBytes, count)), cancellationToken).ConfigureAwait(false);
                var chunkOffset = (int)(_position - _chunkStart);
                var bytes = Math.Min(count, _chunk.Length - chunkOffset);
                Buffer.BlockCopy(_chunk, chunkOffset, buffer, offset, bytes);
                _position += bytes;
                return bytes;
            }
            finally { _readGate.Release(); }
        }

        private async Task FetchAsync(long start, int count, CancellationToken cancellationToken)
        {
            var end = _length > 0 ? Math.Min(_length - 1, start + count - 1) : start + count - 1;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token))
            using (var request = new HttpRequestMessage(HttpMethod.Get, _uri))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                request.Headers.Range = new RangeHeaderValue(start, end);
                request.Headers.TryAddWithoutValidation("User-Agent", _userAgent);
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                using (var response = await YouTubeHttpClient.Shared.RawClient.SendAsync(
                    request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        if (range == null || range.From != start || !range.Length.HasValue)
                            throw new IOException("Invalid media byte range");
                        _length = range.Length.Value;
                    }
                    else
                    {
                        if (start != 0 || !response.Content.Headers.ContentLength.HasValue)
                            throw new IOException("Media server does not support seeking");
                        _length = response.Content.Headers.ContentLength.Value;
                    }
                    var size = (int)Math.Min(count, _length - start);
                    if (size <= 0) throw new IOException("Empty media response");
                    var chunk = new byte[size];
                    var received = 0;
                    using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        while (received < size)
                        {
                            var read = await body.ReadAsync(chunk, received, size - received, timeout.Token).ConfigureAwait(false);
                            if (read == 0) throw new IOException("Incomplete media byte range");
                            received += read;
                        }
                    _chunkStart = start;
                    _chunk = chunk;
                }
            }
        }

        public override void Flush() { }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _lifetime.Cancel();
            }
            base.Dispose(disposing);
        }
    }
}
