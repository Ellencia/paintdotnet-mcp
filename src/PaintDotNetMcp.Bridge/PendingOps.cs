using System.Drawing;
using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

// Operations queued by the MCP server, applied during the next Effect render pass.
internal abstract class PendingOp
{
    public abstract void Apply(Surface s);
    /// <summary>Extra data returned to the caller when queued (e.g. computed bounds).</summary>
    public virtual object? Info => null;
}

internal sealed class FillOp(FillParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        int x = p.X ?? 0;
        int y = p.Y ?? 0;
        int w = p.Width ?? s.Width;
        int h = p.Height ?? s.Height;
        Drawing.FillRect(s, new Rectangle(x, y, w, h), c);
    }
}

internal sealed class DrawRectOp(DrawRectangleParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        var rect = new Rectangle(p.X, p.Y, p.Width, p.Height);
        if (p.CornerRadius > 0) Drawing.RoundedRect(s, rect, p.CornerRadius, p.Thickness, c, p.Fill);
        else if (p.Fill) Drawing.FillRect(s, rect, c);
        else Drawing.StrokeRect(s, rect, p.Thickness, c);
    }
}

internal sealed class DrawArrowOp(DrawArrowParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        Drawing.Arrow(s, p.X1, p.Y1, p.X2, p.Y2, p.Thickness, p.HeadSize, p.BothEnds, c);
    }
}

internal sealed class DrawMarkerOp : PendingOp
{
    private readonly DrawMarkerParams _p;
    private readonly byte[] _pixels;
    private readonly int _width, _height;
    private readonly Rectangle _ink;

    public DrawMarkerOp(DrawMarkerParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Label) || p.Label.Length > 8) throw new ArgumentException("Label must contain 1..8 characters.");
        if (p.Radius < 4 || p.Radius > 256) throw new ArgumentException("Radius must be 4..256.");
        _p = p;
        // Fit the label inside ~75% of the diameter: render once, shrink proportionally if too wide.
        float size = p.Radius * 1.1f;
        _pixels = Render(size, out _width, out _height, out _ink);
        double fit = p.Radius * 1.5;
        if (_ink.Width > fit)
            _pixels = Render(size * (float)(fit / _ink.Width), out _width, out _height, out _ink);
    }

    private byte[] Render(float size, out int w, out int h, out Rectangle ink)
    {
        var buf = ImageIO.RenderText(_p.Label, _p.FontFamily, size, true, false, _p.TextR, _p.TextG, _p.TextB, 255, true, out w, out h);
        ink = Drawing.InkBounds(buf, w, h);
        return buf;
    }

    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(_p.B, _p.G, _p.R, _p.A);
        int d = _p.Radius * 2;
        Drawing.Ellipse(s, new Rectangle(_p.X - _p.Radius, _p.Y - _p.Radius, d, d), 1, c, fill: true);
        // Center by glyph ink, not by the font line box, so digits sit visually centered.
        int ox = _p.X - (_ink.X + _ink.Width / 2), oy = _p.Y - (_ink.Y + _ink.Height / 2);
        ImageIO.BlitOnto(s, _pixels, _width, _height, ox, oy, replaceAlpha: false);
    }
}

internal sealed class DrawCalloutOp : PendingOp
{
    private readonly DrawCalloutParams _p;
    private readonly byte[] _pixels;
    private readonly int _width, _height;
    private readonly Rectangle _ink, _box;

    public DrawCalloutOp(DrawCalloutParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Text) || p.Text.Length > 4096) throw new ArgumentException("Text must contain 1..4096 characters.");
        if (!float.IsFinite(p.FontSize) || p.FontSize <= 0 || p.FontSize > 512) throw new ArgumentException("Font size must be greater than zero, up to 512 pixels.");
        if (p.Padding < 0 || p.Padding > 256 || p.BorderThickness < 0 || p.BorderThickness > 64 || p.CornerRadius < 0)
            throw new ArgumentException("padding 0..256, borderThickness 0..64, cornerRadius >= 0.");
        if (p.TargetX.HasValue != p.TargetY.HasValue) throw new ArgumentException("Give both targetX and targetY, or neither.");
        _p = p;
        _pixels = ImageIO.RenderText(p.Text, p.FontFamily, p.FontSize, p.Bold, false, p.R, p.G, p.B, 255, true, out _width, out _height);
        _ink = Drawing.InkBounds(_pixels, _width, _height);
        _box = new Rectangle(p.X, p.Y, _ink.Width + 2 * p.Padding, _ink.Height + 2 * p.Padding);
    }

    public override object? Info => new { box = new { x = _box.X, y = _box.Y, width = _box.Width, height = _box.Height } };

    public override void Apply(Surface s)
    {
        var fg = ColorBgra.FromBgra(_p.B, _p.G, _p.R, 255);
        if (_p.TargetX is int tx && _p.TargetY is int ty && !_box.Contains(tx, ty))
        {
            // Leader starts at the box point nearest the target; the box is drawn over its root.
            int sx = Math.Clamp(tx, _box.Left, _box.Right - 1), sy = Math.Clamp(ty, _box.Top, _box.Bottom - 1);
            Drawing.Arrow(s, sx, sy, tx, ty, Math.Max(1, _p.BorderThickness), 0, false, fg);
        }
        Drawing.RoundedRect(s, _box, _p.CornerRadius, 1, ColorBgra.FromBgra(_p.BgB, _p.BgG, _p.BgR, _p.BgA), fill: true);
        if (_p.BorderThickness > 0) Drawing.RoundedRect(s, _box, _p.CornerRadius, _p.BorderThickness, fg, fill: false);
        ImageIO.BlitOnto(s, _pixels, _width, _height, _box.X + _p.Padding - _ink.X, _box.Y + _p.Padding - _ink.Y, replaceAlpha: false);
    }
}

internal sealed class DrawLineOp(DrawLineParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        Drawing.Line(s, p.X1, p.Y1, p.X2, p.Y2, p.Thickness, c);
    }
}

internal sealed class DrawEllipseOp(DrawEllipseParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        Drawing.Ellipse(s, new Rectangle(p.X, p.Y, p.Width, p.Height), p.Thickness, c, p.Fill);
    }
}

internal sealed class DrawPolygonOp(DrawPolygonParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        var pts = new Point[p.Points.Count];
        for (int i = 0; i < pts.Length; i++) pts[i] = new Point(p.Points[i].X, p.Points[i].Y);
        Drawing.Polygon(s, pts, p.Thickness, c, p.Fill, p.Closed);
    }
}

internal sealed class DrawTextOp : PendingOp
{
    private readonly DrawTextParams _parameters;
    private readonly byte[] _pixels;
    private readonly int _width, _height;

    public DrawTextOp(DrawTextParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Text) || p.Text.Length > 4096)
            throw new ArgumentException("Text must contain 1..4096 characters.");
        if (!float.IsFinite(p.FontSize) || p.FontSize <= 0 || p.FontSize > 512)
            throw new ArgumentException("Font size must be finite and greater than zero, up to 512 pixels.");
        if (string.IsNullOrWhiteSpace(p.FontFamily)) throw new ArgumentException("Specify an installed font family.");
        _parameters = p;
        // Resolve fonts and render before enqueueing, so invalid input cannot poison pending drawing.
        _pixels = ImageIO.RenderText(p.Text, p.FontFamily, p.FontSize, p.Bold, p.Italic,
            p.R, p.G, p.B, p.A, p.AntiAlias, out _width, out _height);
    }

    public override void Apply(Surface s)
    {
        ImageIO.BlitOnto(s, _pixels, _width, _height, _parameters.X, _parameters.Y, replaceAlpha: false);
    }
}

internal sealed class FloodFillOp(FloodFillParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c = ColorBgra.FromBgra(p.B, p.G, p.R, p.A);
        Drawing.FloodFill(s, p.X, p.Y, c, p.Tolerance);
    }
}

internal sealed class GradientFillOp(GradientFillParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var c1 = ColorBgra.FromBgra(p.B1, p.G1, p.R1, p.A1);
        var c2 = ColorBgra.FromBgra(p.B2, p.G2, p.R2, p.A2);
        var rect = new Rectangle(
            p.X ?? 0, p.Y ?? 0,
            p.Width ?? s.Width, p.Height ?? s.Height);
        if (string.Equals(p.Mode, "radial", StringComparison.OrdinalIgnoreCase))
        {
            int cx = (p.X1 + p.X2) / 2;
            int cy = (p.Y1 + p.Y2) / 2;
            double dx = p.X2 - p.X1, dy = p.Y2 - p.Y1;
            double r = Math.Sqrt(dx * dx + dy * dy) / 2;
            Drawing.GradientRadial(s, rect, cx, cy, r, c1, c2);
        }
        else
        {
            Drawing.GradientLinear(s, rect, p.X1, p.Y1, p.X2, p.Y2, c1, c2);
        }
    }
}

internal sealed class PasteImageOp(PasteImageParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var pngBytes = Convert.FromBase64String(p.PngBase64);
        var buf = ImageIO.DecodePng(pngBytes, out int w, out int h);
        bool replace = string.Equals(p.BlendMode, "replace", StringComparison.OrdinalIgnoreCase);
        ImageIO.BlitOnto(s, buf, w, h, p.X, p.Y, replaceAlpha: replace);
    }
}
