using System;
using System.Collections.Generic;

namespace YouTube
{
    // One quality level of a video's scrubbing storyboard: a grid of small frames packed into
    // sprite sheets. Parsed from playerStoryboardSpecRenderer.spec.
    internal sealed class StoryboardLevel
    {
        public int Index;
        public int ThumbWidth;
        public int ThumbHeight;
        public int TotalCount;
        public int Columns;
        public int Rows;
        public int IntervalMs;   // playback time each frame covers
        public string NameTemplate = string.Empty; // e.g. "M$M" or "default"
        public string Sigh = string.Empty;

        public int PerSheet { get { return Math.Max(1, Columns * Rows); } }
    }

    // Turns a time position into a concrete sprite-sheet URL plus the tile rectangle inside it.
    // The spec format was verified against a live player response:
    //   baseUrl|w#h#count#cols#rows#intervalMs#name#sigh|<next level>|...
    // and the URL is baseUrl with $L -> level, $N -> name (with $M -> sheet index), &sigh=... .
    internal sealed class StoryboardSpec
    {
        private readonly string _baseUrl;
        private readonly List<StoryboardLevel> _levels;

        private StoryboardSpec(string baseUrl, List<StoryboardLevel> levels)
        {
            _baseUrl = baseUrl;
            _levels = levels;
        }

        public bool HasFrames { get { return _levels.Count > 0; } }

        public static StoryboardSpec Parse(string spec)
        {
            if (string.IsNullOrWhiteSpace(spec))
            {
                return null;
            }

            try
            {
                var parts = spec.Split('|');
                if (parts.Length < 2)
                {
                    return null;
                }

                var baseUrl = parts[0];
                var levels = new List<StoryboardLevel>();

                for (int i = 1; i < parts.Length; i++)
                {
                    var f = parts[i].Split('#');
                    if (f.Length < 8)
                    {
                        continue;
                    }

                    var level = new StoryboardLevel
                    {
                        Index = i - 1,
                        ThumbWidth = ParseInt(f[0]),
                        ThumbHeight = ParseInt(f[1]),
                        TotalCount = ParseInt(f[2]),
                        Columns = ParseInt(f[3]),
                        Rows = ParseInt(f[4]),
                        IntervalMs = ParseInt(f[5]),
                        NameTemplate = f[6],
                        Sigh = f[7]
                    };

                    if (level.ThumbWidth > 0 && level.ThumbHeight > 0 && level.TotalCount > 0
                        && level.Columns > 0 && level.Rows > 0 && level.IntervalMs > 0)
                    {
                        levels.Add(level);
                    }
                }

                if (levels.Count == 0)
                {
                    return null;
                }

                return new StoryboardSpec(baseUrl, levels);
            }
            catch
            {
                return null;
            }
        }

        // The level to preview with. Chosen to MINIMISE the number of sprite sheets — the one
        // that packs the most frames per sheet needs the fewest downloads (e.g. an 80x45 level
        // holds 100 frames/sheet against a 160x90 level's 25, so ~3x fewer sheets to fetch). The
        // frames are a bit smaller, but they load far faster. Ties break toward the larger tile.
        public StoryboardLevel PreviewLevel()
        {
            StoryboardLevel best = null;
            for (int i = 0; i < _levels.Count; i++)
            {
                var lvl = _levels[i];
                if (best == null
                    || lvl.PerSheet > best.PerSheet
                    || (lvl.PerSheet == best.PerSheet && lvl.ThumbWidth > best.ThumbWidth))
                {
                    best = lvl;
                }
            }
            return best;
        }

        // Every sprite-sheet URL for the best level, so they can be pre-warmed. Capped so a very
        // long video does not queue hundreds of downloads.
        public List<string> AllSheetUrls(int maxSheets)
        {
            var urls = new List<string>();
            var level = PreviewLevel();
            if (level == null)
            {
                return urls;
            }

            var sheetCount = (level.TotalCount + level.PerSheet - 1) / level.PerSheet;
            if (maxSheets > 0 && sheetCount > maxSheets)
            {
                sheetCount = maxSheets;
            }

            for (int s = 0; s < sheetCount; s++)
            {
                var name = level.NameTemplate.Replace("$M", s.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var url = _baseUrl
                    .Replace("$L", level.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Replace("$N", name);
                url += (url.IndexOf('?') >= 0 ? "&" : "?") + "sigh=" + level.Sigh;
                urls.Add(url);
            }

            return urls;
        }

        public StoryboardFrame FrameAt(double positionSeconds)
        {
            var level = PreviewLevel();
            if (level == null)
            {
                return null;
            }

            var frameIndex = (int)Math.Floor(positionSeconds * 1000.0 / level.IntervalMs);
            if (frameIndex < 0) frameIndex = 0;
            if (frameIndex > level.TotalCount - 1) frameIndex = level.TotalCount - 1;

            var sheetIndex = frameIndex / level.PerSheet;
            var posInSheet = frameIndex % level.PerSheet;
            var col = posInSheet % level.Columns;
            var row = posInSheet / level.Columns;

            var name = level.NameTemplate.Replace("$M", sheetIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            var url = _baseUrl
                .Replace("$L", level.Index.ToString(System.Globalization.CultureInfo.InvariantCulture))
                .Replace("$N", name);
            url += (url.IndexOf('?') >= 0 ? "&" : "?") + "sigh=" + level.Sigh;

            return new StoryboardFrame
            {
                SheetUrl = url,
                Column = col,
                Row = row,
                Columns = level.Columns,
                Rows = level.Rows,
                ThumbWidth = level.ThumbWidth,
                ThumbHeight = level.ThumbHeight
            };
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }
    }

    internal sealed class StoryboardFrame
    {
        public string SheetUrl;
        public int Column;
        public int Row;
        public int Columns;
        public int Rows;
        public int ThumbWidth;
        public int ThumbHeight;
    }
}
