using System.Drawing;
using PaintDotNet;

namespace PaintDotNetMcp.Bridge;

// Low-level pixel routines that operate directly on a Paint.NET Surface.
// All primitives clip to the surface bounds. Alpha-blending is "source-over".
internal static class Drawing
{
    public static void SetPixel(Surface s, int x, int y, ColorBgra c)
    {
        if ((uint)x >= (uint)s.Width || (uint)y >= (uint)s.Height) return;
        // Respect any software-side selection so drawing primitives can be clipped to a region.
        if (SelectionOps.SoftSelection.HasSelection && !SelectionOps.SoftSelection.Contains(x, y)) return;
        if (c.A == 255) { s[x, y] = c; return; }
        if (c.A == 0) return;
        var dst = s[x, y];
        s[x, y] = AlphaOver(c, dst);
    }

    public static ColorBgra AlphaOver(ColorBgra src, ColorBgra dst)
    {
        // src over dst, premul-free integer math.
        int sa = src.A;
        int da = dst.A;
        int outA = sa + (da * (255 - sa)) / 255;
        if (outA == 0) return ColorBgra.FromBgra(0, 0, 0, 0);
        int outB = (src.B * sa + dst.B * da * (255 - sa) / 255) / outA;
        int outG = (src.G * sa + dst.G * da * (255 - sa) / 255) / outA;
        int outR = (src.R * sa + dst.R * da * (255 - sa) / 255) / outA;
        return ColorBgra.FromBgra((byte)outB, (byte)outG, (byte)outR, (byte)outA);
    }

    public static void FillRect(Surface s, Rectangle r, ColorBgra c)
    {
        var rect = Rectangle.Intersect(r, s.Bounds);
        // When a soft selection is active, every pixel must go through SetPixel for clipping.
        bool needsClip = SelectionOps.SoftSelection.HasSelection;
        if (c.A == 255 && !needsClip)
        {
            for (int yy = rect.Top; yy < rect.Bottom; yy++)
                for (int xx = rect.Left; xx < rect.Right; xx++)
                    s[xx, yy] = c;
        }
        else
        {
            for (int yy = rect.Top; yy < rect.Bottom; yy++)
                for (int xx = rect.Left; xx < rect.Right; xx++)
                    SetPixel(s, xx, yy, c);
        }
    }

    public static void StrokeRect(Surface s, Rectangle r, int thickness, ColorBgra c)
    {
        var rect = Rectangle.Intersect(r, s.Bounds);
        int t = Math.Max(1, thickness);
        for (int yy = rect.Top; yy < Math.Min(rect.Top + t, rect.Bottom); yy++)
            for (int xx = rect.Left; xx < rect.Right; xx++) SetPixel(s, xx, yy, c);
        for (int yy = Math.Max(rect.Bottom - t, rect.Top); yy < rect.Bottom; yy++)
            for (int xx = rect.Left; xx < rect.Right; xx++) SetPixel(s, xx, yy, c);
        for (int xx = rect.Left; xx < Math.Min(rect.Left + t, rect.Right); xx++)
            for (int yy = rect.Top; yy < rect.Bottom; yy++) SetPixel(s, xx, yy, c);
        for (int xx = Math.Max(rect.Right - t, rect.Left); xx < rect.Right; xx++)
            for (int yy = rect.Top; yy < rect.Bottom; yy++) SetPixel(s, xx, yy, c);
    }

    public static void RoundedRect(Surface s, Rectangle r, int radius, int thickness, ColorBgra c, bool fill)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        int rad = Math.Clamp(radius, 0, Math.Min(r.Width, r.Height) / 2);
        int t = Math.Max(1, thickness);
        var inner = Rectangle.Inflate(r, -t, -t);
        int innerRad = Math.Max(0, rad - t);
        var area = Rectangle.Intersect(r, s.Bounds);
        for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                if (!InsideRounded(r, rad, x, y)) continue;
                if (!fill && inner.Width > 0 && inner.Height > 0 && InsideRounded(inner, innerRad, x, y)) continue;
                SetPixel(s, x, y, c);
            }
    }

    // Pixel-center test against a rectangle whose corners are quarter circles of radius rad.
    private static bool InsideRounded(Rectangle r, int rad, int x, int y)
    {
        double px = x + 0.5, py = y + 0.5;
        if (px < r.Left || px > r.Right || py < r.Top || py > r.Bottom) return false;
        double cx = Math.Clamp(px, r.Left + rad, r.Right - rad);
        double cy = Math.Clamp(py, r.Top + rad, r.Bottom - rad);
        double dx = px - cx, dy = py - cy;
        return dx * dx + dy * dy <= (double)rad * rad;
    }

    /// <summary>Line with a filled triangular head at (x2,y2), optionally also at (x1,y1).</summary>
    public static void Arrow(Surface s, int x1, int y1, int x2, int y2, int thickness, int headSize, bool bothEnds, ColorBgra c)
    {
        double dx = x2 - x1, dy = y2 - y1, len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1) return;
        double ux = dx / len, uy = dy / len;
        double head = headSize > 0 ? headSize : Math.Max(10, thickness * 4);
        head = Math.Min(head, bothEnds ? len / 2 : len);
        var start = bothEnds ? Head(s, x1, y1, -ux, -uy, head, c) : new Point(x1, y1);
        var end = Head(s, x2, y2, ux, uy, head, c);
        Line(s, start.X, start.Y, end.X, end.Y, thickness, c);
    }

    // Draws a head pointing along (ux,uy) with its tip at (tx,ty); returns the base center.
    private static Point Head(Surface s, int tx, int ty, double ux, double uy, double head, ColorBgra c)
    {
        double bx = tx - ux * head, by = ty - uy * head, w = head * 0.5;
        Polygon(s,
        [
            new Point(tx, ty),
            new Point((int)Math.Round(bx - uy * w), (int)Math.Round(by + ux * w)),
            new Point((int)Math.Round(bx + uy * w), (int)Math.Round(by - ux * w)),
        ], 1, c, fill: true, closed: true);
        return new Point((int)Math.Round(bx), (int)Math.Round(by));
    }

    /// <summary>Bounding box of non-transparent pixels in a BGRA buffer (Empty if none).</summary>
    public static Rectangle InkBounds(byte[] bgra, int w, int h)
    {
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (bgra[(y * w + x) * 4 + 3] != 0)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
        return maxX < 0 ? Rectangle.Empty : Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    public static void Line(Surface s, int x1, int y1, int x2, int y2, int thickness, ColorBgra c)
    {
        // Bresenham + thickness via small disc per step.
        int t = Math.Max(1, thickness);
        int half = t / 2;
        int dx = Math.Abs(x2 - x1), sx = x1 < x2 ? 1 : -1;
        int dy = -Math.Abs(y2 - y1), sy = y1 < y2 ? 1 : -1;
        int err = dx + dy;
        int x = x1, y = y1;
        while (true)
        {
            for (int oy = -half; oy <= half; oy++)
                for (int ox = -half; ox <= half; ox++)
                    SetPixel(s, x + ox, y + oy, c);
            if (x == x2 && y == y2) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x += sx; }
            if (e2 <= dx) { err += dx; y += sy; }
        }
    }

    public static void Ellipse(Surface s, Rectangle bounds, int thickness, ColorBgra c, bool fill)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double cx = bounds.X + bounds.Width / 2.0;
        double cy = bounds.Y + bounds.Height / 2.0;
        double rx = bounds.Width / 2.0;
        double ry = bounds.Height / 2.0;
        if (rx <= 0 || ry <= 0) return;

        if (fill)
        {
            int yMin = (int)Math.Floor(cy - ry);
            int yMax = (int)Math.Ceiling(cy + ry);
            for (int y = yMin; y <= yMax; y++)
            {
                double dy = (y + 0.5 - cy) / ry;
                if (dy * dy > 1) continue;
                double dx = Math.Sqrt(1 - dy * dy) * rx;
                int xL = (int)Math.Floor(cx - dx);
                int xR = (int)Math.Ceiling(cx + dx);
                for (int x = xL; x <= xR; x++) SetPixel(s, x, y, c);
            }
            return;
        }

        // Stroke: draw outer ellipse, subtract inner ellipse band.
        int t = Math.Max(1, thickness);
        double rxo = rx, ryo = ry;
        double rxi = Math.Max(0, rx - t);
        double ryi = Math.Max(0, ry - t);
        int yMin2 = (int)Math.Floor(cy - ryo);
        int yMax2 = (int)Math.Ceiling(cy + ryo);
        for (int y = yMin2; y <= yMax2; y++)
        {
            double dyo = (y + 0.5 - cy) / ryo;
            if (dyo * dyo > 1) continue;
            double dxo = Math.Sqrt(1 - dyo * dyo) * rxo;
            int xLo = (int)Math.Floor(cx - dxo);
            int xRo = (int)Math.Ceiling(cx + dxo);

            bool insideHasGap = rxi > 0 && ryi > 0;
            int xLi = 0, xRi = 0;
            if (insideHasGap)
            {
                double dyi = (y + 0.5 - cy) / ryi;
                if (dyi * dyi <= 1)
                {
                    double dxi = Math.Sqrt(1 - dyi * dyi) * rxi;
                    xLi = (int)Math.Ceiling(cx - dxi);
                    xRi = (int)Math.Floor(cx + dxi);
                }
                else insideHasGap = false;
            }
            for (int x = xLo; x <= xRo; x++)
            {
                if (insideHasGap && x > xLi && x < xRi) continue;
                SetPixel(s, x, y, c);
            }
        }
    }

    public static void Polygon(Surface s, Point[] pts, int thickness, ColorBgra c, bool fill, bool closed)
    {
        if (pts.Length < 2) return;
        if (fill && pts.Length >= 3)
        {
            // Scanline polygon fill (even-odd rule).
            int yMin = pts[0].Y, yMax = pts[0].Y;
            for (int i = 1; i < pts.Length; i++)
            {
                if (pts[i].Y < yMin) yMin = pts[i].Y;
                if (pts[i].Y > yMax) yMax = pts[i].Y;
            }
            yMin = Math.Max(yMin, 0);
            yMax = Math.Min(yMax, s.Height - 1);
            for (int y = yMin; y <= yMax; y++)
            {
                var xs = new List<double>();
                for (int i = 0; i < pts.Length; i++)
                {
                    var a = pts[i];
                    var b = pts[(i + 1) % pts.Length];
                    if ((a.Y <= y && b.Y > y) || (b.Y <= y && a.Y > y))
                    {
                        double x = a.X + (double)(y - a.Y) / (b.Y - a.Y) * (b.X - a.X);
                        xs.Add(x);
                    }
                }
                xs.Sort();
                for (int i = 0; i + 1 < xs.Count; i += 2)
                {
                    int xL = (int)Math.Ceiling(xs[i]);
                    int xR = (int)Math.Floor(xs[i + 1]);
                    for (int x = xL; x <= xR; x++) SetPixel(s, x, y, c);
                }
            }
        }
        // Stroke (always, even after fill, to ensure crisp outline).
        int last = closed ? pts.Length : pts.Length - 1;
        for (int i = 0; i < last; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Length];
            Line(s, a.X, a.Y, b.X, b.Y, thickness, c);
        }
    }

    public static void FloodFill(Surface s, int sx, int sy, ColorBgra fill, int tolerance)
    {
        if ((uint)sx >= (uint)s.Width || (uint)sy >= (uint)s.Height) return;
        var target = s[sx, sy];
        if (ColorEquals(target, fill, 0)) return;

        int W = s.Width, H = s.Height;
        var visited = new bool[W * H];
        var stack = new Stack<(int, int)>();
        stack.Push((sx, sy));
        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            if ((uint)x >= (uint)W || (uint)y >= (uint)H) continue;
            int idx = y * W + x;
            if (visited[idx]) continue;
            if (!ColorEquals(s[x, y], target, tolerance)) continue;
            visited[idx] = true;
            s[x, y] = fill;
            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }
    }

    private static bool ColorEquals(ColorBgra a, ColorBgra b, int tol)
    {
        if (tol <= 0) return a.B == b.B && a.G == b.G && a.R == b.R && a.A == b.A;
        return Math.Abs(a.B - b.B) <= tol &&
               Math.Abs(a.G - b.G) <= tol &&
               Math.Abs(a.R - b.R) <= tol &&
               Math.Abs(a.A - b.A) <= tol;
    }

    public static void GradientLinear(Surface s, Rectangle bounds, int x1, int y1, int x2, int y2, ColorBgra c1, ColorBgra c2)
    {
        var rect = Rectangle.Intersect(bounds, s.Bounds);
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        if (len2 == 0) { FillRect(s, rect, c1); return; }
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                double t = ((x - x1) * dx + (y - y1) * dy) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                SetPixel(s, x, y, Lerp(c1, c2, t));
            }
    }

    public static void GradientRadial(Surface s, Rectangle bounds, int cx, int cy, double radius, ColorBgra c1, ColorBgra c2)
    {
        var rect = Rectangle.Intersect(bounds, s.Bounds);
        if (radius <= 0) { FillRect(s, rect, c1); return; }
        for (int y = rect.Top; y < rect.Bottom; y++)
            for (int x = rect.Left; x < rect.Right; x++)
            {
                double dx = x - cx, dy = y - cy;
                double t = Math.Sqrt(dx * dx + dy * dy) / radius;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                SetPixel(s, x, y, Lerp(c1, c2, t));
            }
    }

    private static ColorBgra Lerp(ColorBgra a, ColorBgra b, double t)
    {
        byte L(int x, int y) => (byte)(x + (y - x) * t);
        return ColorBgra.FromBgra(L(a.B, b.B), L(a.G, b.G), L(a.R, b.R), L(a.A, b.A));
    }
}
