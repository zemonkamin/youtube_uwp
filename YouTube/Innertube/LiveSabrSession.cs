using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Data.Json;

namespace YouTube.Innertube
{
    // Protocol and live request state ported from Troubadour YTSabr/YTUmp.
    // UWP consumes the same fMP4 pieces directly through MediaStreamSource.
    internal sealed class LiveSabrSession
    {
        internal sealed class Piece
        {
            internal int Itag;
            internal long Sequence, StartMs, DurationMs;
            internal bool Video, Init;
            internal byte[] Bytes;
        }

        private sealed class Header
        {
            internal Piece Piece = new Piece();
            internal int Id;
            internal MemoryStream Body = new MemoryStream();
        }

        private sealed class Range
        {
            internal int Itag;
            internal long First, Last, Start, End;
        }

        private sealed class RememberedHead
        {
            internal double Seconds;
            internal DateTime At;
        }

        private const double LiveCushion = 120;
        private static readonly object HeadGate = new object();
        private static readonly Dictionary<string, RememberedHead> Heads = new Dictionary<string, RememberedHead>();

        private readonly Dictionary<int, bool> _formats = new Dictionary<int, bool>();
        private readonly Dictionary<int, Header> _headers = new Dictionary<int, Header>();
        private readonly Dictionary<int, Range> _ranges = new Dictionary<int, Range>();
        private readonly List<int> _video = new List<int>();
        private readonly List<int> _audio = new List<int>();
        private readonly string _videoId, _cpn, _owner;
        private readonly int _height;
        private readonly DateTime _watchedFrom;
        private DateTime _sessionFrom = DateTime.UtcNow;
        private string _url, _version, _agent, _poToken;
        private JsonObject _clientInfo;
        private byte[] _config, _cookie;
        private int _number, _clientNumber;
        private bool _toldFormats, _ackResume, _askLiveNow;
        private bool _portrait;
        internal bool NeedsReload;
        internal string ReloadToken;
        internal long BackoffMs;
        internal double HeadSeconds, LimitSeconds, NextSeconds;
        internal long HeadSequence;
        internal DateTime LastMediaUtc = DateTime.UtcNow;
        internal Action<Piece> Received;
        internal int LastHttpStatus;

        internal LiveSabrSession(string videoId, JsonObject root, int height, string poToken, DateTime watchedFrom)
        {
            _videoId = videoId;
            _owner = PlaybackResolver.AccountKey;
            // YTApi.playbackNonceForVideo supplies the same cpn to /player and
            // every SABR request, including source renewal and quality changes.
            var nonce = LiveJson.String(root, "_uwpPlaybackNonce");
            _cpn = string.IsNullOrEmpty(nonce) ? Guid.NewGuid().ToString("N").Substring(0, 16) : nonce;
            _height = Math.Max(144, height);
            _watchedFrom = watchedFrom;
            Adopt(root, poToken);
            // Troubadour remembers the head for ten minutes and advances it by
            // elapsed wall time. Reopening a broadcast must not request time zero.
            lock (HeadGate)
            {
                RememberedHead remembered;
                if (Heads.TryGetValue(_owner + ":" + _videoId, out remembered))
                {
                    var age = (DateTime.UtcNow - remembered.At).TotalSeconds;
                    if (age >= 0 && age <= 600) HeadSeconds = remembered.Seconds + age;
                }
            }
            Restart(HeadSeconds > 0 ? Math.Max(0, HeadSeconds - LiveCushion) : 0);
        }

        internal static bool HasConfig(JsonObject root)
        {
            return !string.IsNullOrEmpty(LiveJson.String(LiveJson.Object(root, "streamingData"), "serverAbrStreamingUrl"))
                && !string.IsNullOrEmpty(ConfigString(root));
        }

        private static string ConfigString(JsonObject root)
        {
            return LiveJson.String(LiveJson.Object(LiveJson.Object(LiveJson.Object(root, "playerConfig"),
                "mediaCommonConfig"), "mediaUstreamerRequestConfig"), "videoPlaybackUstreamerConfig");
        }

        internal void Adopt(JsonObject root, string poToken)
        {
            _url = LiveJson.String(LiveJson.Object(root, "streamingData"), "serverAbrStreamingUrl");
            _config = Decode(ConfigString(root));
            _poToken = poToken;
            _version = LiveJson.String(root, "_uwpClientVersion");
            _clientNumber = (int)LiveJson.Number(root, "_uwpClientNumber");
            _agent = PlaybackResolver.MediaUserAgent(root);
            _clientInfo = LiveJson.Object(root, "_uwpClientInfo");
            if (_clientNumber == 0) _clientNumber = 85;
            if (string.IsNullOrEmpty(_version)) _version = "7.20250209.19.00";
            _video.Clear(); _audio.Clear(); _formats.Clear();
            _portrait = false;
            var formats = LiveJson.Array(LiveJson.Object(root, "streamingData"), "adaptiveFormats");
            foreach (var value in formats)
            {
                if (value.ValueType != JsonValueType.Object) continue;
                var f = value.GetObject();
                var mime = LiveJson.String(f, "mimeType");
                var itag = (int)LiveJson.Number(f, "itag");
                var video = mime.IndexOf("avc1", StringComparison.OrdinalIgnoreCase) >= 0;
                var audio = mime.IndexOf("mp4a", StringComparison.OrdinalIgnoreCase) >= 0 && mime.StartsWith("audio/");
                if (itag <= 0 || (!video && !audio)) continue;
                _formats[itag] = video;
                // List every compatible track in the first request, including every AAC track.
                // Live servers can move between rungs; declaring only one can return no media.
                if (video)
                {
                    _video.Add(itag);
                    _portrait = LiveJson.Number(f, "width") < LiveJson.Number(f, "height");
                }
                else if (audio) _audio.Add(itag);
            }
            if (_video.Count == 0 || _audio.Count == 0 || _config.Length == 0 || string.IsNullOrEmpty(_url))
                throw new InvalidOperationException("SABR has no compatible H.264/AAC tracks");
            // A renewed delivery keeps the same cpn. Report already delivered
            // ranges in its first request, as YTSabr.adoptLiveProgressFrom does.
            foreach (var itag in _ranges.Keys.Where(itag => !_formats.ContainsKey(itag)).ToList()) _ranges.Remove(itag);
            RestartDelivery();
            NeedsReload = false; ReloadToken = string.Empty;
        }

        internal void Restart(double seconds, bool preserveRanges = false)
        {
            _cookie = null; _number = 0; _toldFormats = false; _ackResume = false;
            if (!preserveRanges) _ranges.Clear();
            foreach (var h in _headers.Values) h.Body.Dispose();
            _headers.Clear(); _sessionFrom = DateTime.UtcNow;
            NextSeconds = Math.Max(0, seconds);
            _askLiveNow = NextSeconds <= 0 && HeadSeconds <= 0;
        }

        internal void RestartDelivery()
        {
            var target = NextSeconds;
            var limit = LimitSeconds > 0 ? LimitSeconds : HeadSeconds > 0 ? HeadSeconds - 10 : 0;
            if (limit > 0 && (target <= 0 || limit - target > 20)) target = limit;
            if (target <= 0 && HeadSeconds > 0) target = Math.Max(0, HeadSeconds - LiveCushion);
            Restart(target, true);
        }

        internal async Task FetchAsync(CancellationToken cancellation, int timeoutMs = 30000)
        {
            cancellation.ThrowIfCancellationRequested();
            if (_owner != PlaybackResolver.AccountKey) throw new OperationCanceledException();
            var body = RequestBody();
            // The sentinel belongs only to the first request with an unknown head.
            // Numeric media/watch clocks never contain the live-now sentinel.
            _askLiveNow = false;
            var address = _url + (_url.IndexOf('?') >= 0 ? "&" : "?") + "alr=yes&cpn=" + _cpn
                + "&cver=" + Uri.EscapeDataString(_version) + "&rn=" + _number++;
            using (var request = new HttpRequestMessage(HttpMethod.Post, address))
            {
                request.Content = new ByteArrayContent(body);
                request.Headers.TryAddWithoutValidation("User-Agent", _agent);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");
                request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
                request.Headers.TryAddWithoutValidation("Origin", "https://www.youtube.com");
                request.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
                request.Headers.TryAddWithoutValidation("X-Browser-Channel", "stable");
                request.Headers.TryAddWithoutValidation("X-Browser-Year", "2026");
                request.Headers.TryAddWithoutValidation("X-Browser-Copyright", "Copyright 2026 Google LLC. All Rights Reserved.");
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(250, timeoutMs)));
                    using (var response = await YouTubeHttpClient.Shared.RawClient.SendAsync(request,
                        HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        LastHttpStatus = (int)response.StatusCode;
                        if (!response.IsSuccessStatusCode)
                            System.Diagnostics.Debug.WriteLine("[Live] SABR HTTP " + LastHttpStatus + ", client=" + _clientNumber
                                + ", proof=" + !string.IsNullOrEmpty(_poToken) + ", configBytes=" + _config.Length);
                        response.EnsureSuccessStatusCode();
                        using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            // Parse incrementally: a live response may be held open until the next piece.
                            // Never wait for the entire HTTP response before handing media to the player.
                            var pending = new List<byte>();
                            var buffer = new byte[32768];
                            int count;
                            while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) > 0)
                            {
                                if (_owner != PlaybackResolver.AccountKey) throw new OperationCanceledException();
                                for (var i = 0; i < count; i++) pending.Add(buffer[i]);
                                var offset = 0;
                                while (offset < pending.Count)
                                {
                                    var begin = offset;
                                    long type, length;
                                    if (!UmpInteger(pending, ref offset, out type) || !UmpInteger(pending, ref offset, out length))
                                    { offset = begin; break; }
                                    if (length < 0 || length > 16 * 1024 * 1024) throw new InvalidDataException("UMP part too large");
                                    if (pending.Count - offset < length) { offset = begin; break; }
                                    var bytes = pending.GetRange(offset, (int)length).ToArray();
                                    offset += (int)length;
                                    Handle((int)type, bytes);
                                }
                                if (offset > 0) pending.RemoveRange(0, offset);
                                if (pending.Count > 16 * 1024 * 1024) throw new InvalidDataException("UMP buffer too large");
                            }
                            if (pending.Count != 0) throw new InvalidDataException("Truncated UMP response");
                        }
                    }
                }
            }
        }

        private byte[] RequestBody()
        {
            var requestedMs = (long)(NextSeconds * 1000);
            var videoRanges = _ranges.Values.Where(r => _formats.ContainsKey(r.Itag) && _formats[r.Itag]).ToList();
            if (videoRanges.Count > 0)
            {
                var start = videoRanges.Min(r => r.Start);
                var end = videoRanges.Max(r => r.End);
                var limit = (long)((LimitSeconds > 0 ? LimitSeconds : HeadSeconds - 10) * 1000);
                requestedMs = limit > 0 ? Math.Min(Math.Max(Math.Max(limit - 30000, end), start), limit - 2000) : Math.Max(start, end - 12000);
            }
            var state = new SabrWriter();
            state.Varint(18, (ulong)(_height * 16 / 9)); state.Varint(19, (ulong)_height);
            state.Varint(16, (ulong)_height); state.Varint(21, (ulong)_height);
            var currentMs = Math.Max(0, requestedMs);
            if (currentMs > 15000) currentMs -= 15000;
            state.Varint(28, _askLiveNow ? 9007199254740991UL : (ulong)currentMs);
            var watched = (ulong)Math.Max(0, (DateTime.UtcNow - _watchedFrom).TotalMilliseconds);
            state.Varint(29, watched); state.Varint(36, watched);
            state.Varint(13, (ulong)Math.Max(0, (DateTime.UtcNow - _sessionFrom).TotalMilliseconds));
            state.Varint(14, 0); state.Varint(34, 0);
            var vc = new SabrWriter(); vc.Varint(1, 2); vc.Varint(2, 1);
            vc.Varint(3, (ulong)(_portrait ? _height * 16 / 9 : _height));
            vc.Varint(4, (ulong)(_portrait ? _height : _height * 16 / 9)); vc.Varint(11, 60);
            vc.Varint(12, (ulong)(2684048.0 / (1280 * 720 * 30) * _height * (_height * 16 / 9) * 60)); vc.Varint(15, 0);
            var ac = new SabrWriter(); ac.Varint(1, 1); ac.Varint(2, 2); ac.Varint(6, 0);
            var caps = new SabrWriter(); caps.Message(1, vc); caps.Message(2, ac);
            caps.Varint(4, 249); caps.Varint(4, 350); caps.Varint(4, 278); caps.Varint(5, 3);
            state.Message(38, caps); state.Varint(40, 3); state.Varint(58, 0); state.Varint(59, (ulong)_height);
            state.Varint(71, 1); state.Varint(73, 2); state.Varint(76, 0); state.Varint(85, 1);
            var quality = new SabrWriter(); for (var i = 1; i <= 6; i++) quality.Varint(i, i == 2 ? (ulong)_height : 0);
            state.Message(72, quality);
            state.Bytes(79, new byte[] { 10, 4, 8, 1, 16, 0, 10, 4, 8, 2, 16, 0, 10, 4, 8, 2, 16, 1 });
            if (_number == 0) state.Varint(80, 1);
            var request = new SabrWriter(); request.Message(1, state); request.Bytes(5, _config);
            foreach (var r in _ranges.Values)
            {
                var range = new SabrWriter(); range.Message(1, Format(r.Itag));
                range.Varint(2, (ulong)Math.Max(0, r.Start)); range.Varint(3, (ulong)Math.Max(0, r.End - r.Start));
                range.Varint(4, (ulong)r.First); range.Varint(5, (ulong)r.Last); request.Message(3, range);
            }
            if (!_toldFormats)
            {
                foreach (var itag in _audio) request.Message(16, Format(itag));
                foreach (var itag in _video) request.Message(17, Format(itag));
                _toldFormats = true;
            }
            var info = new SabrWriter(); info.String(1, global::Config.Hl + "_" + global::Config.Gl);
            info.Varint(16, (ulong)_clientNumber); info.String(17, _version);
            foreach (var field in new[] { "deviceMake", "deviceModel", "osName", "osVersion" })
            {
                var value = LiveJson.String(_clientInfo, field);
                if (string.IsNullOrEmpty(value) && _clientNumber == 85)
                    value = field == "deviceMake" ? "Samsung" : field == "deviceModel" ? "SmartTV" : field == "osName" ? "Tizen" : "5.0";
                if (!string.IsNullOrEmpty(value)) info.String(field == "deviceMake" ? 12 : field == "deviceModel" ? 13 : field == "osName" ? 18 : 19, value);
            }
            var context = new SabrWriter(); context.Message(1, info);
            if (!string.IsNullOrEmpty(_poToken)) context.Bytes(2, Decode(_poToken));
            if (_cookie != null) context.Bytes(3, _cookie);
            var inner = new SabrWriter(); inner.Varint(1, 5);
            var four = new SabrWriter(); four.Message(3, inner); context.Message(4, four);
            request.Message(19, context);
            if (_ackResume) { var ack = new SabrWriter(); ack.Varint(8, 7); request.Message(24, ack); _ackResume = false; }
            return request.ToArray();
        }

        private static SabrWriter Format(int itag)
        { var f = new SabrWriter(); f.Varint(1, (ulong)itag); f.Varint(2, 0); f.Bytes(3, new byte[0]); return f; }

        private void Handle(int type, byte[] bytes)
        {
            if (type != 20 && type != 21 && type != 22 && type != 31 && type != 35
                && type != 43 && type != 44 && type != 46 && type != 58 && type != 69) return;
            var p = type == 21 || type == 22 ? null : SabrReader.Read(bytes);
            if (type == 20)
            {
                if (_headers.Count >= 32) throw new InvalidDataException("Too many open UMP media headers");
                var h = new Header { Id = (int)p.Number(1) };
                var piece = h.Piece;
                piece.Itag = (int)p.Number(3); piece.Init = p.Number(8) != 0; piece.Sequence = (long)p.Number(9);
                piece.StartMs = (long)p.Number(11); piece.DurationMs = (long)p.Number(12);
                bool video; piece.Video = _formats.TryGetValue(piece.Itag, out video) && video;
                if (piece.DurationMs <= 0)
                {
                    var range = SabrReader.Read(p.Bytes(15)); var scale = (long)range.Number(3);
                    if (scale > 0) { piece.StartMs = (long)range.Number(1) * 1000 / scale; piece.DurationMs = (long)range.Number(2) * 1000 / scale; }
                }
                Header previous; if (_headers.TryGetValue(h.Id, out previous)) previous.Body.Dispose();
                _headers[h.Id] = h;
            }
            else if ((type == 21 || type == 22) && bytes.Length > 0)
            {
                Header h;
                if (!_headers.TryGetValue(bytes[0], out h)) return;
                if (type == 21)
                {
                    if (h.Body.Length + bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("SABR media piece too large");
                    h.Body.Write(bytes, 1, bytes.Length - 1); return;
                }
                _headers.Remove(h.Id); h.Piece.Bytes = h.Body.ToArray(); h.Body.Dispose();
                var piece = h.Piece;
                if (!_formats.ContainsKey(piece.Itag) || piece.Bytes.Length == 0) return;
                if (Received != null) Received(piece);
                if (piece.Init) return;
                Range r;
                if (!_ranges.TryGetValue(piece.Itag, out r) || piece.Sequence < r.First || piece.Sequence > r.Last + 1)
                    _ranges[piece.Itag] = r = new Range { Itag = piece.Itag, First = piece.Sequence, Last = piece.Sequence, Start = piece.StartMs };
                r.Last = Math.Max(r.Last, piece.Sequence); r.End = Math.Max(r.End, piece.StartMs + piece.DurationMs);
                if (piece.Video) NextSeconds = Math.Max(NextSeconds, r.End / 1000.0);
                LastMediaUtc = DateTime.UtcNow;
            }
            else if (type == 31)
            {
                var sequence = (long)p.Number(3);
                if (sequence > 0) HeadSequence = sequence;
                var head = p.Number(4);
                // 12/13 describe the DVR window START, not the broadcast head.
                // A partial update must not replace the remembered head with it.
                if (head > 0)
                {
                    HeadSeconds = head / 1000.0;
                    lock (HeadGate)
                    {
                        Heads[_owner + ":" + _videoId] = new RememberedHead { Seconds = HeadSeconds, At = DateTime.UtcNow };
                        if (Heads.Count > 16) Heads.Remove(Heads.OrderBy(pair => pair.Value.At).First().Key);
                    }
                }
                var scale = p.Number(15); var limit = p.Number(14);
                if (scale > 0 && limit > 0) LimitSeconds = limit / (double)scale;
            }
            else if (type == 35) { _cookie = p.Bytes(7) ?? _cookie; BackoffMs = (long)p.Number(4); }
            else if (type == 43)
            {
                var next = p.String(1); Uri uri;
                if (Uri.TryCreate(next, UriKind.Absolute, out uri) && uri.Scheme == "https"
                    && (uri.Host.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "googlevideo.com"))
                    _url = next;
            }
            else if (type == 46) { NeedsReload = true; ReloadToken = SabrReader.Read(p.Bytes(1)).String(1); }
            else if (type == 44) throw new InvalidDataException("SABR server rejected request");
            else if (type == 58 && p.Number(1) != 1) throw new InvalidDataException("SABR proof required");
            else if (type == 69)
            {
                _ackResume = true;
                var seq = (long)SabrReader.Read(SabrReader.Read(p.Bytes(1)).Bytes(1)).Number(9);
                if (seq > 0 && HeadSequence > seq && HeadSeconds > 0)
                    NextSeconds = Math.Max(0, HeadSeconds - (HeadSequence - seq) * 5.0);
                else if (HeadSeconds > 0) NextSeconds = Math.Max(0, HeadSeconds - 30);
            }
        }

        internal static byte[] Decode(string value)
        {
            if (string.IsNullOrEmpty(value)) return new byte[0];
            try { var s = value.Replace('-', '+').Replace('_', '/'); return Convert.FromBase64String(s.PadRight((s.Length + 3) / 4 * 4, '=')); }
            catch (FormatException) { return new byte[0]; }
        }

        internal static bool UmpInteger(IList<byte> bytes, ref int offset, out long value)
        {
            value = 0; if (offset >= bytes.Count) return false;
            var b = bytes[offset]; int size;
            if (b < 128) { size = 1; value = b; }
            else if (b < 192) { size = 2; value = b & 63; }
            else if (b < 224) { size = 3; value = b & 31; }
            else if (b < 240) { size = 4; value = b & 15; }
            else if (b == 240) size = 5;
            else throw new InvalidDataException("Invalid UMP integer");
            if (offset + size > bytes.Count) return false;
            var shift = size == 5 ? 0 : 8 - size;
            for (var i = 1; i < size; i++, shift += 8) value |= (long)bytes[offset + i] << shift;
            offset += size; return true;
        }
    }

    internal sealed class SabrWriter
    {
        private readonly MemoryStream _stream = new MemoryStream();
        private void Integer(ulong value)
        { while (value > 127) { _stream.WriteByte((byte)((value & 127) | 128)); value >>= 7; } _stream.WriteByte((byte)value); }
        internal void Varint(int field, ulong value) { Integer((ulong)field << 3); Integer(value); }
        internal void Bytes(int field, byte[] bytes)
        { if (bytes == null) return; Integer(((ulong)field << 3) | 2); Integer((ulong)bytes.Length); _stream.Write(bytes, 0, bytes.Length); }
        internal void String(int field, string value) { Bytes(field, Encoding.UTF8.GetBytes(value ?? string.Empty)); }
        internal void Message(int field, SabrWriter writer) { Bytes(field, writer.ToArray()); }
        internal byte[] ToArray() { return _stream.ToArray(); }
    }

    internal sealed class SabrReader
    {
        private readonly Dictionary<int, ulong> _numbers = new Dictionary<int, ulong>();
        private readonly Dictionary<int, byte[]> _bytes = new Dictionary<int, byte[]>();
        internal ulong Number(int field) { ulong n; return _numbers.TryGetValue(field, out n) ? n : 0; }
        internal byte[] Bytes(int field) { byte[] b; return _bytes.TryGetValue(field, out b) ? b : null; }
        internal string String(int field) { var b = Bytes(field); return b == null ? string.Empty : Encoding.UTF8.GetString(b, 0, b.Length); }
        private static ulong Integer(byte[] b, ref int offset)
        {
            ulong n = 0;
            for (var shift = 0; shift < 70 && offset < b.Length; shift += 7)
            { var v = b[offset++]; n |= (ulong)(v & 127) << shift; if (v < 128) return n; }
            throw new InvalidDataException("Truncated protobuf integer");
        }
        internal static SabrReader Read(byte[] b)
        {
            var p = new SabrReader(); if (b == null) return p;
            var offset = 0;
            while (offset < b.Length)
            {
                var tag = Integer(b, ref offset); var field = (int)(tag >> 3); var wire = (int)(tag & 7);
                if (field <= 0) throw new InvalidDataException("Invalid protobuf field");
                if (wire == 0) p._numbers[field] = Integer(b, ref offset);
                else if (wire == 2)
                {
                    var length = Integer(b, ref offset);
                    if (length > (ulong)(b.Length - offset)) throw new InvalidDataException("Truncated protobuf field");
                    var value = new byte[(int)length]; Buffer.BlockCopy(b, offset, value, 0, value.Length);
                    offset += value.Length; p._bytes[field] = value;
                }
                else if (wire == 1 || wire == 5)
                { offset += wire == 1 ? 8 : 4; if (offset > b.Length) throw new InvalidDataException("Truncated protobuf field"); }
                else throw new InvalidDataException("Unsupported protobuf wire type");
            }
            return p;
        }
    }

    internal static class LiveJson
    {
        internal static string String(JsonObject o, string key)
        { IJsonValue v; return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.String ? v.GetString() : string.Empty; }
        internal static double Number(JsonObject o, string key)
        {
            IJsonValue v; if (o == null || !o.TryGetValue(key, out v)) return 0;
            if (v.ValueType == JsonValueType.Number) return v.GetNumber();
            double n; return v.ValueType == JsonValueType.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out n) ? n : 0;
        }
        internal static bool Bool(JsonObject o, string key)
        { IJsonValue v; return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Boolean && v.GetBoolean(); }
        internal static JsonObject Object(JsonObject o, string key)
        { IJsonValue v; return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Object ? v.GetObject() : null; }
        internal static JsonArray Array(JsonObject o, string key)
        { IJsonValue v; return o != null && o.TryGetValue(key, out v) && v.ValueType == JsonValueType.Array ? v.GetArray() : new JsonArray(); }
        internal static JsonObject Find(IJsonValue root, string key)
        {
            if (root == null) return null;
            var queue = new Queue<IJsonValue>(); queue.Enqueue(root); var seen = 0;
            while (queue.Count > 0 && seen++ < 200000)
            {
                var value = queue.Dequeue();
                if (value.ValueType == JsonValueType.Object)
                {
                    var o = value.GetObject(); var found = Object(o, key); if (found != null) return found;
                    foreach (var pair in o) if (pair.Value.ValueType == JsonValueType.Object || pair.Value.ValueType == JsonValueType.Array) queue.Enqueue(pair.Value);
                }
                else if (value.ValueType == JsonValueType.Array) foreach (var v in value.GetArray()) queue.Enqueue(v);
            }
            return null;
        }
    }
}
