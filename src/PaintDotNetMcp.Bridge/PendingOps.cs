using System.Drawing;
using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

// Operations queued by the MCP server, applied during the next Effect render pass.
internal abstract class PendingOp
{
    public abstract void Apply(Surface s);
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
        if (p.Fill) Drawing.FillRect(s, rect, c);
        else Drawing.StrokeRect(s, rect, p.Thickness, c);
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

internal sealed class DrawTextOp(DrawTextParams p) : PendingOp
{
    public override void Apply(Surface s)
    {
        var buf = ImageIO.RenderText(
            p.Text, p.FontFamily, p.FontSize, p.Bold, p.Italic,
            p.R, p.G, p.B, p.A, p.AntiAlias,
            out int w, out int h);
        ImageIO.BlitOnto(s, buf, w, h, p.X, p.Y, replaceAlpha: false);
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
