using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using PaintDotNet;
using SkiaSharp;

namespace PaintDotNetMcp.Bridge;

// Image encode/decode (PNG + WebP via SkiaSharp), snapshot management, and matting helpers.
//
// Snapshot model:
//   - During every render pass we copy the destination surface into a managed BGRA byte[] buffer.
//   - Read-only methods (get_canvas_png / extract_region / remove_background) operate on this buffer
//     so they don't depend on a live render context.
//   - First read before any render returns "no snapshot" — caller should invoke the bridge effect once.
//
// Format support:
//   - PNG: lossless, ignores quality. Always available.
//   - WebP: lossy by default; quality 0-100 (default 85). Requires libSkiaSharp.dll deployed
//           alongside the plugin.
//   - "auto" format: detected from file extension when a path is provided; defaults to PNG otherwise.
internal static class ImageIO
{
    private static readonly object _snapGate = new();
    private static byte[]? _snapBgra;
    private static int _snapW, _snapH;
    // Monotonic timestamp of the last snapshot capture (Environment.TickCount64).
    private static long _snapTick;

    public static bool HasSnapshot => _snapBgra is not null;
    public static int SnapshotWidth { get { lock (_snapGate) return _snapW; } }
    public static int SnapshotHeight { get { lock (_snapGate) return _snapH; } }
    public static long SnapshotTick { get { lock (_snapGate) return _snapTick; } }

    /// <summary>Snapshot the entire surface into the managed buffer (called from OnRender).</summary>
    public static void CaptureSnapshot(Surface s)
    {
        int w = s.Width, h = s.Height;
        var buf = new byte[w * h * 4];
        int i = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                var c = s[x, y];
                buf[i++] = c.B;
                buf[i++] = c.G;
                buf[i++] = c.R;
                buf[i++] = c.A;
            }
        }
        lock (_snapGate)
        {
            _snapBgra = buf;
            _snapW = w; _snapH = h;
            _snapTick = Environment.TickCount64;
        }
    }

    public static byte[]? GetSnapshotCopy(out int w, out int h)
    {
        lock (_snapGate)
        {
            w = _snapW; h = _snapH;
            if (_snapBgra is null) return null;
            return (byte[])_snapBgra.Clone();
        }
    }

    /// <summary>
    /// Resolve a format string + optional path into a concrete SkiaSharp encode format.
    /// Falls back to PNG if format is unrecognized or "auto" with no path.
    /// </summary>
    public static SKEncodedImageFormat ResolveFormat(string? format, string? path)
    {
        var f = (format ?? "auto").Trim().ToLowerInvariant();
        if (f == "auto")
        {
            if (!string.IsNullOrEmpty(path))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".webp") return SKEncodedImageFormat.Webp;
                if (ext == ".jpg" || ext == ".jpeg") return SKEncodedImageFormat.Jpeg;
            }
            return SKEncodedImageFormat.Png;
        }
        return f switch
        {
            "webp" => SKEncodedImageFormat.Webp,
            "jpg" or "jpeg" => SKEncodedImageFormat.Jpeg,
            _ => SKEncodedImageFormat.Png,
        };
    }

    /// <summary>The MIME content-type for a Skia format.</summary>
    public static string MimeFor(SKEncodedImageFormat fmt) => fmt switch
    {
        SKEncodedImageFormat.Webp => "image/webp",
        SKEncodedImageFormat.Jpeg => "image/jpeg",
        _ => "image/png",
    };

    /// <summary>
    /// Encode a BGRA buffer (subregion or full) using SkiaSharp. Quality applies to lossy formats
    /// (webp, jpeg) and is ignored for PNG.
    /// </summary>
    public static byte[] EncodeImage(byte[] bgra, int w, int h, int x, int y, int rw, int rh,
        SKEncodedImageFormat fmt, int quality)
    {
        // Clip region.
        if (x < 0) { rw += x; x = 0; }
        if (y < 0) { rh += y; y = 0; }
        if (x + rw > w) rw = w - x;
        if (y + rh > h) rh = h - y;
        if (rw <= 0 || rh <= 0) throw new InvalidOperationException("region out of bounds");

        // Materialize the region as a tightly-packed BGRA buffer for Skia.
        byte[] regionBuf;
        if (x == 0 && y == 0 && rw == w && rh == h)
        {
            regionBuf = bgra;
        }
        else
        {
            regionBuf = new byte[rw * rh * 4];
            for (int yy = 0; yy < rh; yy++)
            {
                int srcRow = ((y + yy) * w + x) * 4;
                int dstRow = yy * rw * 4;
                Buffer.BlockCopy(bgra, srcRow, regionBuf, dstRow, rw * 4);
            }
        }

        // SKImageInfo: BGRA8888 matches our buffer order. Unpremul keeps user's alpha untouched.
        var info = new SKImageInfo(rw, rh, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var image = SKImage.FromPixelCopy(info, regionBuf);
        int q = Math.Clamp(quality, 1, 100);
        using var data = image.Encode(fmt, q);
        if (data is null) throw new InvalidOperationException("Skia encode returned null for format " + fmt);
        return data.ToArray();
    }

    /// <summary>Backward-compatible PNG-only shortcut. Defers to EncodeImage.</summary>
    public static byte[] EncodePng(byte[] bgra, int w, int h, int x, int y, int rw, int rh)
        => EncodeImage(bgra, w, h, x, y, rw, rh, SKEncodedImageFormat.Png, 100);

    /// <summary>Decode any format SkiaSharp understands (PNG, WebP, JPEG, ...) into a BGRA buffer.</summary>
    public static byte[] DecodeImage(byte[] bytes, out int w, out int h)
    {
        using var data = SKData.CreateCopy(bytes);
        using var bitmap = SKBitmap.Decode(data)
            ?? throw new InvalidOperationException("Skia could not decode the supplied image bytes");

        // Convert to BGRA8888 / Unpremul if needed. We always force Unpremul here so downstream
        // pixel math (alpha-over blending in Drawing.cs) operates on straight alpha rather than
        // premultiplied — matching the assumption everywhere else in this code base.
        if (bitmap.ColorType != SKColorType.Bgra8888 || bitmap.AlphaType != SKAlphaType.Unpremul)
        {
            var target = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var converted = new SKBitmap(target);
            try
            {
                if (!bitmap.CopyTo(converted, target.ColorType))
                    throw new InvalidOperationException("Skia color conversion to BGRA8888 failed");
                w = converted.Width; h = converted.Height;
                return SnapshotPixelBytes(converted);
            }
            finally { converted.Dispose(); }
        }
        w = bitmap.Width; h = bitmap.Height;
        return SnapshotPixelBytes(bitmap);
    }

    /// <summary>
    /// Materialize an SKBitmap's pixel data as a tightly-packed byte[] (BGRA, w*h*4).
    /// Avoids using SKBitmap.Bytes whose presence varies across SkiaSharp versions.
    /// </summary>
    private static byte[] SnapshotPixelBytes(SKBitmap bitmap)
    {
        int w = bitmap.Width, h = bitmap.Height;
        int bytesPerRow = w * 4;
        var span = bitmap.GetPixelSpan();
        // If the row stride matches our packed layout, single copy. Otherwise row-by-row.
        if (bitmap.RowBytes == bytesPerRow)
        {
            return span.ToArray();
        }
        var buf = new byte[bytesPerRow * h];
        for (int y = 0; y < h; y++)
        {
            var srcRow = span.Slice(y * bitmap.RowBytes, bytesPerRow);
            srcRow.CopyTo(buf.AsSpan(y * bytesPerRow, bytesPerRow));
        }
        return buf;
    }

    /// <summary>Backward-compatible PNG-only decoder. Defers to DecodeImage.</summary>
    public static byte[] DecodePng(byte[] png, out int w, out int h) => DecodeImage(png, out w, out h);

    /// <summary>Render text to a 32bpp BGRA buffer via System.Drawing. Returns buf + dims.</summary>
    public static byte[] RenderText(
        string text, string fontFamily, float fontSize, bool bold, bool italic,
        byte r, byte g, byte b, byte a, bool antiAlias,
        out int w, out int h)
    {
        var style = FontStyle.Regular;
        if (bold) style |= FontStyle.Bold;
        if (italic) style |= FontStyle.Italic;
        Font font;
        try { font = new Font(fontFamily, fontSize, style, GraphicsUnit.Pixel); }
        catch { font = new Font(FontFamily.GenericSansSerif, fontSize, style, GraphicsUnit.Pixel); }

        SizeF sz;
        using (var probe = new Bitmap(1, 1))
        using (var pg = Graphics.FromImage(probe))
        {
            sz = pg.MeasureString(text, font);
        }
        w = Math.Max(1, (int)Math.Ceiling(sz.Width) + 2);
        h = Math.Max(1, (int)Math.Ceiling(sz.Height) + 2);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var gx = Graphics.FromImage(bmp))
        {
            gx.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            gx.TextRenderingHint = antiAlias
                ? System.Drawing.Text.TextRenderingHint.AntiAliasGridFit
                : System.Drawing.Text.TextRenderingHint.SingleBitPerPixelGridFit;
            using var brush = new SolidBrush(Color.FromArgb(a, r, g, b));
            gx.DrawString(text, font, brush, 0, 0);
        }
        font.Dispose();

        var buf = new byte[w * h * 4];
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* src = (byte*)data.Scan0;
                int stride = data.Stride;
                for (int y = 0; y < h; y++)
                {
                    byte* row = src + y * stride;
                    int dstRow = y * w * 4;
                    for (int x = 0; x < w; x++)
                    {
                        buf[dstRow + x * 4 + 0] = row[x * 4 + 0];
                        buf[dstRow + x * 4 + 1] = row[x * 4 + 1];
                        buf[dstRow + x * 4 + 2] = row[x * 4 + 2];
                        buf[dstRow + x * 4 + 3] = row[x * 4 + 3];
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return buf;
    }

    /// <summary>Blit a BGRA buffer onto a Paint.NET Surface at (x,y) with source-over blending.</summary>
    public static void BlitOnto(Surface s, byte[] bgra, int srcW, int srcH, int dstX, int dstY, bool replaceAlpha)
    {
        for (int y = 0; y < srcH; y++)
        {
            int sy = y;
            int ty = dstY + y;
            if ((uint)ty >= (uint)s.Height) continue;
            for (int x = 0; x < srcW; x++)
            {
                int tx = dstX + x;
                if ((uint)tx >= (uint)s.Width) continue;
                int i = (sy * srcW + x) * 4;
                byte sb = bgra[i + 0], sg = bgra[i + 1], sr = bgra[i + 2], sa = bgra[i + 3];
                if (sa == 0) continue;
                if (replaceAlpha || sa == 255)
                {
                    s[tx, ty] = ColorBgra.FromBgra(sb, sg, sr, sa);
                }
                else
                {
                    s[tx, ty] = Drawing.AlphaOver(ColorBgra.FromBgra(sb, sg, sr, sa), s[tx, ty]);
                }
            }
        }
    }

    /// <summary>
    /// Background matting on a BGRA buffer. Modifies alpha in-place: pixels close to the key color
    /// become transparent; far pixels stay. With Feather=true, alpha grades smoothly with distance.
    /// Returns the key color actually used.
    /// </summary>
    public static (byte r, byte g, byte b) RemoveBackground(
        byte[] bgra, int w, int h, int x, int y, int rw, int rh,
        string method, byte? keyR, byte? keyG, byte? keyB, int tolerance, bool feather)
    {
        if (x < 0) { rw += x; x = 0; }
        if (y < 0) { rh += y; y = 0; }
        if (x + rw > w) rw = w - x;
        if (y + rh > h) rh = h - y;

        byte kr, kg, kb;
        if (method == "auto_corners")
        {
            // Average four corner pixels of the region.
            long sumR = 0, sumG = 0, sumB = 0; int n = 0;
            void Sample(int sx, int sy)
            {
                if ((uint)sx >= (uint)w || (uint)sy >= (uint)h) return;
                int i = (sy * w + sx) * 4;
                sumB += bgra[i]; sumG += bgra[i + 1]; sumR += bgra[i + 2]; n++;
            }
            Sample(x, y); Sample(x + rw - 1, y); Sample(x, y + rh - 1); Sample(x + rw - 1, y + rh - 1);
            if (n == 0) { kr = kg = kb = 0; }
            else { kr = (byte)(sumR / n); kg = (byte)(sumG / n); kb = (byte)(sumB / n); }
        }
        else
        {
            kr = keyR ?? 255; kg = keyG ?? 255; kb = keyB ?? 255;
        }

        int tol = Math.Max(1, tolerance);
        // Distances: 0..~441 (sqrt(3*255^2)). We map distance to alpha.
        for (int yy = 0; yy < rh; yy++)
        {
            for (int xx = 0; xx < rw; xx++)
            {
                int i = ((y + yy) * w + (x + xx)) * 4;
                int dr = bgra[i + 2] - kr;
                int dg = bgra[i + 1] - kg;
                int db = bgra[i + 0] - kb;
                double dist = Math.Sqrt(dr * dr + dg * dg + db * db);
                if (dist <= tol)
                {
                    bgra[i + 3] = 0;
                }
                else if (feather && dist < tol * 2)
                {
                    double frac = (dist - tol) / tol; // 0..1
                    bgra[i + 3] = (byte)(bgra[i + 3] * frac);
                }
                // else: leave alpha untouched.
            }
        }
        return (kr, kg, kb);
    }

    /// <summary>Crop a BGRA buffer to a region, returning a new BGRA buffer.</summary>
    public static byte[] CropBuffer(byte[] bgra, int w, int h, int x, int y, int rw, int rh)
    {
        if (x < 0) { rw += x; x = 0; }
        if (y < 0) { rh += y; y = 0; }
        if (x + rw > w) rw = w - x;
        if (y + rh > h) rh = h - y;
        if (rw <= 0 || rh <= 0) throw new InvalidOperationException("region out of bounds");

        var outBuf = new byte[rw * rh * 4];
        for (int yy = 0; yy < rh; yy++)
        {
            int srcRow = ((y + yy) * w + x) * 4;
            int dstRow = yy * rw * 4;
            Buffer.BlockCopy(bgra, srcRow, outBuf, dstRow, rw * 4);
        }
        return outBuf;
    }
}
