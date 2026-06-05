using System.Drawing;

namespace PaintDotNetMcp.Bridge;

// Connected-components object detection on a BGRA buffer.
//
// Pipeline:
//   1. Auto-detect background color from the four corners of the search region (or use a key).
//   2. Build a binary foreground mask (color-distance > tolerance).
//   3. Two-pass labeling with union-find (4-connectivity).
//   4. Compute bbox for each label.
//   5. Filter by min/max size and aspect ratio (drop noise + over-wide text rows).
//   6. Optionally merge bboxes whose inflated rects overlap (groupGap) — useful for compound icons.
//   7. Apply padding, clip to region, sort row-major (top→bottom, left→right).
internal static class ObjectDetection
{
    public sealed record DetectedRect(int X, int Y, int Width, int Height, int Area)
    {
        public int Right => X + Width;
        public int Bottom => Y + Height;
    }

    public sealed class Options
    {
        public int RegionX = 0;
        public int RegionY = 0;
        public int RegionW = -1;     // -1 = whole canvas
        public int RegionH = -1;
        public byte? BgR = null;
        public byte? BgG = null;
        public byte? BgB = null;
        public int Tolerance = 32;   // color-distance threshold (0-441)
        public int MinSize = 16;     // min bbox dimension (W AND H)
        public int MaxSize = int.MaxValue;
        public int Padding = 0;      // expand bbox by N px before clipping
        public int GroupGap = 0;     // merge bboxes whose inflated rects overlap (legacy: both axes same)
        public int? GroupGapX = null; // overrides GroupGap on the X axis. null = use GroupGap.
        public int? GroupGapY = null; // overrides GroupGap on the Y axis. null = use GroupGap.
        public double MaxAspectRatio = 6.0; // drop bboxes wider/taller than this ratio
        public int MinArea = 0;      // min foreground pixel count
        // Reading-order row threshold: bboxes considered same row if their Y ranges overlap by this fraction.
        public double RowOverlap = 0.4;
        // Pixels with alpha below this threshold (0-255) are treated as background regardless of RGB.
        // Default 8 catches cleanly-transparent pixels (e.g. residue from a prior remove_background
        // applyToLayer pass) without throwing away anti-aliased edges of real content.
        public byte MinAlpha = 8;
    }

    public static List<DetectedRect> Detect(byte[] bgra, int canvasW, int canvasH, Options opt)
    {
        // Resolve region.
        int rx = Math.Max(0, opt.RegionX);
        int ry = Math.Max(0, opt.RegionY);
        int rw = opt.RegionW < 0 ? canvasW - rx : Math.Min(opt.RegionW, canvasW - rx);
        int rh = opt.RegionH < 0 ? canvasH - ry : Math.Min(opt.RegionH, canvasH - ry);
        if (rw <= 0 || rh <= 0) return new();

        // 1. Background.
        byte bgR, bgG, bgB;
        if (opt.BgR.HasValue && opt.BgG.HasValue && opt.BgB.HasValue)
        {
            bgR = opt.BgR.Value; bgG = opt.BgG.Value; bgB = opt.BgB.Value;
        }
        else
        {
            (bgR, bgG, bgB) = SampleCorners(bgra, canvasW, rx, ry, rw, rh);
        }

        // 2. Binary mask. Stored row-major in region coords. Pixels with alpha < MinAlpha are
        //    skipped (they're invisible — typical leftovers from a previous remove_background pass
        //    where alpha was zeroed but RGB left untouched). Without this guard the CCL would
        //    happily union those ghost pixels and bridge unrelated icons together.
        var fg = new bool[rw * rh];
        int tol = Math.Max(1, opt.Tolerance);
        int tol2 = tol * tol;
        byte minA = opt.MinAlpha;
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int i = ((ry + y) * canvasW + (rx + x)) * 4;
                if (bgra[i + 3] < minA) continue; // effectively transparent
                int dr = bgra[i + 2] - bgR;
                int dg = bgra[i + 1] - bgG;
                int db = bgra[i + 0] - bgB;
                if (dr * dr + dg * dg + db * db > tol2)
                    fg[y * rw + x] = true;
            }
        }

        // 3. Two-pass union-find labeling (4-connectivity).
        var labels = new int[rw * rh];
        var parent = new List<int> { 0 }; // index 0 unused
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                if (!fg[y * rw + x]) continue;
                int left = x > 0 ? labels[y * rw + (x - 1)] : 0;
                int up = y > 0 ? labels[(y - 1) * rw + x] : 0;
                int label;
                if (left == 0 && up == 0)
                {
                    label = parent.Count;
                    parent.Add(label);
                }
                else if (left != 0 && up == 0) label = left;
                else if (left == 0 && up != 0) label = up;
                else
                {
                    label = Math.Min(left, up);
                    Union(parent, left, up);
                }
                labels[y * rw + x] = label;
            }
        }

        // 4. Compute per-label bboxes + area.
        var bboxes = new Dictionary<int, (int X1, int Y1, int X2, int Y2, int Area)>();
        for (int y = 0; y < rh; y++)
        {
            for (int x = 0; x < rw; x++)
            {
                int l = labels[y * rw + x];
                if (l == 0) continue;
                int root = Find(parent, l);
                if (bboxes.TryGetValue(root, out var b))
                {
                    bboxes[root] = (
                        Math.Min(b.X1, x), Math.Min(b.Y1, y),
                        Math.Max(b.X2, x + 1), Math.Max(b.Y2, y + 1),
                        b.Area + 1);
                }
                else
                {
                    bboxes[root] = (x, y, x + 1, y + 1, 1);
                }
            }
        }

        // 5a. Pre-merge: only drop trivial single-pixel noise. Keeping small fragments here is
        //     intentional — disconnected stroke fragments (e.g. the three bars in a chart icon,
        //     toe-pad dots in a paw print, steam wisps above a coffee cup) need to survive long
        //     enough to be merged in step 6. Filtering by MinSize here would strand them.
        var preMerge = new List<DetectedRect>();
        foreach (var b in bboxes.Values)
        {
            int w = b.X2 - b.X1, h = b.Y2 - b.Y1;
            // Always-on noise floor: anything smaller than 2 pixels in either dim or with <2 fg pixels
            // is almost certainly a stray artifact and won't help even after merging.
            if (w < 2 || h < 2 || b.Area < 2) continue;
            // Hard upper bound — over-large blobs (e.g. an entire-canvas component if the user passed
            // bg incorrectly) shouldn't pollute the merge graph.
            if (w > opt.MaxSize || h > opt.MaxSize) continue;
            preMerge.Add(new DetectedRect(b.X1, b.Y1, w, h, b.Area));
        }

        // 6. Merge nearby bboxes (groupGap > 0). Done before the main size/aspect filter so
        //    multi-fragment icons collapse into one bbox first. Per-axis gaps allow tight vertical
        //    coupling (so an icon doesn't bleed into its caption below) while still merging
        //    horizontally separated fragments (chart bars, side-by-side compound icons).
        int gapX = opt.GroupGapX ?? opt.GroupGap;
        int gapY = opt.GroupGapY ?? opt.GroupGap;
        var rects = ((gapX > 0 || gapY > 0) && preMerge.Count > 1)
            ? MergeNearby(preMerge, gapX, gapY)
            : preMerge;

        // 5b. Post-merge filter: now that fragments are unified, apply the user-facing limits.
        rects = rects.Where(r =>
        {
            if (r.Width < opt.MinSize || r.Height < opt.MinSize) return false;
            if (r.Width > opt.MaxSize || r.Height > opt.MaxSize) return false;
            if (r.Area < opt.MinArea) return false;
            double aspect = (double)Math.Max(r.Width, r.Height) / Math.Max(1, Math.Min(r.Width, r.Height));
            if (aspect > opt.MaxAspectRatio) return false;
            return true;
        }).ToList();

        // 7. Pad + clip + translate to canvas coords.
        var padded = new List<DetectedRect>(rects.Count);
        foreach (var r in rects)
        {
            int x0 = Math.Max(0, r.X - opt.Padding);
            int y0 = Math.Max(0, r.Y - opt.Padding);
            int x1 = Math.Min(rw, r.Right + opt.Padding);
            int y1 = Math.Min(rh, r.Bottom + opt.Padding);
            padded.Add(new DetectedRect(rx + x0, ry + y0, x1 - x0, y1 - y0, r.Area));
        }

        // Sort row-major.
        return SortReadingOrder(padded, opt.RowOverlap);
    }

    private static (byte r, byte g, byte b) SampleCorners(byte[] bgra, int canvasW, int rx, int ry, int rw, int rh)
    {
        // Sample 4 corners, then a small neighborhood inside each as a fallback. Skip transparent
        // pixels — a corner that's been alpha-zeroed by a prior matting pass returns a misleading
        // RGB which would corrupt the bg key.
        long sumR = 0, sumG = 0, sumB = 0; int n = 0;
        void Sample(int x, int y)
        {
            if ((uint)x >= (uint)canvasW) return;
            int i = (y * canvasW + x) * 4;
            if (bgra[i + 3] < 128) return; // mostly-transparent → skip
            sumB += bgra[i]; sumG += bgra[i + 1]; sumR += bgra[i + 2]; n++;
        }
        // 4 corners.
        Sample(rx, ry);
        Sample(rx + rw - 1, ry);
        Sample(rx, ry + rh - 1);
        Sample(rx + rw - 1, ry + rh - 1);
        // Step inward a bit if all corners were transparent.
        if (n == 0)
        {
            Sample(rx + 4, ry + 4);
            Sample(rx + rw - 5, ry + 4);
            Sample(rx + 4, ry + rh - 5);
            Sample(rx + rw - 5, ry + rh - 5);
        }
        return n == 0 ? ((byte)255, (byte)255, (byte)255)
                      : ((byte)(sumR / n), (byte)(sumG / n), (byte)(sumB / n));
    }

    private static int Find(List<int> parent, int x)
    {
        while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; }
        return x;
    }

    private static void Union(List<int> parent, int a, int b)
    {
        int ra = Find(parent, a), rb = Find(parent, b);
        if (ra == rb) return;
        if (ra < rb) parent[rb] = ra; else parent[ra] = rb;
    }

    private static List<DetectedRect> MergeNearby(List<DetectedRect> rects, int gapX, int gapY)
    {
        // Union-find over rects: edge if their inflated rects overlap.
        int n = rects.Count;
        var par = new int[n];
        for (int i = 0; i < n; i++) par[i] = i;
        int Find(int x) { while (par[x] != x) { par[x] = par[par[x]]; x = par[x]; } return x; }
        void Union(int a, int b) { int ra = Find(a), rb = Find(b); if (ra != rb) par[ra] = rb; }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Near(rects[i], rects[j], gapX, gapY)) Union(i, j);

        var groups = new Dictionary<int, (int X1, int Y1, int X2, int Y2, int Area)>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            var r = rects[i];
            if (groups.TryGetValue(root, out var g))
            {
                groups[root] = (
                    Math.Min(g.X1, r.X), Math.Min(g.Y1, r.Y),
                    Math.Max(g.X2, r.Right), Math.Max(g.Y2, r.Bottom),
                    g.Area + r.Area);
            }
            else groups[root] = (r.X, r.Y, r.Right, r.Bottom, r.Area);
        }
        return groups.Values.Select(g => new DetectedRect(g.X1, g.Y1, g.X2 - g.X1, g.Y2 - g.Y1, g.Area)).ToList();
    }

    private static bool Near(DetectedRect a, DetectedRect b, int gapX, int gapY)
    {
        if (a.X > b.Right + gapX) return false;
        if (b.X > a.Right + gapX) return false;
        if (a.Y > b.Bottom + gapY) return false;
        if (b.Y > a.Bottom + gapY) return false;
        return true;
    }

    /// <summary>
    /// Group bboxes into rows by Y-overlap then sort each row by X. This produces a stable reading
    /// order even when icons in a row aren't perfectly aligned.
    /// </summary>
    private static List<DetectedRect> SortReadingOrder(List<DetectedRect> rects, double rowOverlapFrac)
    {
        if (rects.Count <= 1) return rects;
        var sorted = rects.OrderBy(r => r.Y).ToList();
        var rows = new List<List<DetectedRect>>();
        foreach (var r in sorted)
        {
            bool placed = false;
            foreach (var row in rows)
            {
                // Y-overlap with row's average bbox
                int avgY = row.Sum(x => x.Y) / row.Count;
                int avgH = row.Sum(x => x.Height) / row.Count;
                int rowTop = avgY, rowBot = avgY + avgH;
                int oTop = Math.Max(rowTop, r.Y);
                int oBot = Math.Min(rowBot, r.Bottom);
                int overlap = Math.Max(0, oBot - oTop);
                int minH = Math.Min(avgH, r.Height);
                if (minH > 0 && (double)overlap / minH >= rowOverlapFrac)
                {
                    row.Add(r);
                    placed = true;
                    break;
                }
            }
            if (!placed) rows.Add(new List<DetectedRect> { r });
        }
        var result = new List<DetectedRect>(rects.Count);
        foreach (var row in rows)
        {
            row.Sort((a, b) => a.X - b.X);
            result.AddRange(row);
        }
        return result;
    }
}
