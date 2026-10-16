using System;
using System.Collections.Generic;
using System.Linq;

namespace YouTube.Innertube
{
    // YTHlsProxy.livePlaylistFor/liveStartFor, without the HTTP proxy: MSS
    // receives the same ordered pieces and the same append-only local clock.
    internal sealed class LiveTimeline
    {
        internal sealed class Piece
        {
            internal long Sequence, SourceStartMs, DurationMs;
        }

        private sealed class Entry
        {
            internal long SourceStartMs, SourceEndMs, MediaStartMs;
        }

        internal enum Mapping { Ready, Wait, Discard }
        private readonly List<Entry> _entries = new List<Entry>();
        private long _nextSequence, _clockMs = 10000;
        private DateTime _gapAt = DateTime.MinValue;
        internal bool Started { get; private set; }
        internal long NextSequence { get { return _nextSequence; } }

        internal static Piece ChooseStart(IEnumerable<Piece> pieces, double waitedSeconds)
        {
            var ordered = pieces.Where(p => p.DurationMs > 0).OrderBy(p => p.Sequence)
                .GroupBy(p => p.Sequence).Select(g => g.First()).ToList();
            if (ordered.Count == 0) return null;
            Piece best = null, start = null;
            var length = 0; var bestLength = 0; long previous = -1;
            foreach (var piece in ordered)
            {
                if (length > 0 && piece.Sequence == previous + 1) length++;
                else { start = piece; length = 1; }
                // Equal runs choose the newer row, as in Troubadour.
                if (length >= bestLength) { best = start; bestLength = length; }
                previous = piece.Sequence;
            }
            if (bestLength >= 3) return best;
            if (waitedSeconds < 6) return null;
            var newest = ordered.Last().Sequence;
            return ordered.First(p => newest - p.Sequence <= 24);
        }

        internal void Begin(long sequence)
        {
            if (Started) return;
            _nextSequence = sequence;
            Started = true;
        }

        internal bool TryAppend(Piece piece, DateTime now, out long mediaStartMs)
        {
            mediaStartMs = 0;
            if (!Started || piece.Sequence < _nextSequence || piece.DurationMs <= 0) return false;
            if (piece.Sequence > _nextSequence)
            {
                if (_gapAt == DateTime.MinValue) _gapAt = now;
                if ((now - _gapAt).TotalSeconds < 8) return false;
            }
            _gapAt = DateTime.MinValue;
            mediaStartMs = _clockMs;
            _entries.Add(new Entry { SourceStartMs = piece.SourceStartMs,
                SourceEndMs = piece.SourceStartMs + piece.DurationMs, MediaStartMs = _clockMs });
            _clockMs += piece.DurationMs;
            _nextSequence = piece.Sequence + 1;
            // Only the retained DVR media needs a mapping, not the whole broadcast.
            if (_entries.Count > 128) _entries.RemoveAt(0);
            return true;
        }

        internal Mapping MapSample(long sourceTicks, out long shiftTicks)
        {
            long pieceStart;
            return MapSample(sourceTicks, out shiftTicks, out pieceStart);
        }

        internal Mapping MapSample(long sourceTicks, out long shiftTicks, out long mediaPieceStartMs)
        {
            shiftTicks = 0;
            mediaPieceStartMs = 0;
            // Audio sequences do not necessarily match video sequences. Select
            // audio frames by the broadcast clock, then shift with their video piece.
            for (var i = _entries.Count - 1; i >= 0; i--)
            {
                var entry = _entries[i];
                if (sourceTicks >= entry.SourceStartMs * 10000 && sourceTicks < entry.SourceEndMs * 10000)
                {
                    shiftTicks = (entry.MediaStartMs - entry.SourceStartMs) * 10000;
                    mediaPieceStartMs = entry.MediaStartMs;
                    return Mapping.Ready;
                }
            }
            if (_entries.Count == 0) return Mapping.Wait;
            // Before the chosen start or inside a confirmed hole: omit this
            // audio frame. Beyond delivered video: keep it until that piece arrives.
            return sourceTicks < _entries.Last().SourceEndMs * 10000 ? Mapping.Discard : Mapping.Wait;
        }
    }
}
