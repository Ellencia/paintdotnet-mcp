using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

internal sealed class TransformLayerOp : PendingOp
{
    private readonly TransformLayerParams _parameters;

    public TransformLayerOp(TransformLayerParams parameters)
    {
        if (!double.IsFinite(parameters.OffsetX) || !double.IsFinite(parameters.OffsetY) ||
            !double.IsFinite(parameters.ScaleX) || !double.IsFinite(parameters.ScaleY) ||
            parameters.ScaleX <= 0 || parameters.ScaleY <= 0 || !double.IsFinite(parameters.AngleDegrees) ||
            (parameters.PivotX.HasValue && !double.IsFinite(parameters.PivotX.Value)) ||
            (parameters.PivotY.HasValue && !double.IsFinite(parameters.PivotY.Value)))
            throw new ArgumentException("Transform values must be finite and scales must be positive.");
        if (parameters.Interpolation is not ("nearest" or "bilinear"))
            throw new ArgumentException("Interpolation must be nearest or bilinear.");
        _parameters = parameters;
    }

    public override void Apply(Surface surface)
    {
        using var source = new Surface(surface.Width, surface.Height);
        source.CopySurface(surface);
        var p = _parameters;
        double pivotX = p.PivotX ?? surface.Width / 2d;
        double pivotY = p.PivotY ?? surface.Height / 2d;
        double radians = (p.AngleDegrees % 360) * Math.PI / 180;
        double cos = Math.Cos(radians), sin = Math.Sin(radians);
        // Exact quarter-turns avoid tiny trig errors at pixel boundaries.
        if (Math.Abs(cos) < 1e-12) cos = 0;
        if (Math.Abs(sin) < 1e-12) sin = 0;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
            {
                double dx = x + 0.5 - pivotX - p.OffsetX;
                double dy = y + 0.5 - pivotY - p.OffsetY;
                double sx = (cos * dx + sin * dy) / p.ScaleX + pivotX - 0.5;
                double sy = (-sin * dx + cos * dy) / p.ScaleY + pivotY - 0.5;
                surface[x, y] = Sample(source, sx, sy, p.Interpolation == "nearest");
            }
    }

    private static ColorBgra Sample(Surface source, double x, double y, bool nearest)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < -1 || y < -1 || x > source.Width || y > source.Height)
            return ColorBgra.FromBgra(0, 0, 0, 0);
        if (nearest) return Pixel(source, (int)Math.Floor(x + 0.5), (int)Math.Floor(y + 0.5));
        int left = (int)Math.Floor(x), top = (int)Math.Floor(y);
        double fx = x - left, fy = y - top;
        double alpha = 0, blue = 0, green = 0, red = 0;
        // Interpolate premultiplied color to prevent transparent RGB from creating halos.
        for (int row = 0; row < 2; row++)
            for (int col = 0; col < 2; col++)
            {
                var pixel = Pixel(source, left + col, top + row);
                double weight = (col == 0 ? 1 - fx : fx) * (row == 0 ? 1 - fy : fy);
                double a = pixel.A * weight;
                alpha += a;
                blue += pixel.B * a; green += pixel.G * a; red += pixel.R * a;
            }
        if (alpha <= 0) return ColorBgra.FromBgra(0, 0, 0, 0);
        static byte Byte(double value) => (byte)Math.Clamp((int)Math.Round(value), 0, 255);
        return ColorBgra.FromBgra(Byte(blue / alpha), Byte(green / alpha), Byte(red / alpha), Byte(alpha));
    }

    private static ColorBgra Pixel(Surface source, int x, int y)
        => x >= 0 && y >= 0 && x < source.Width && y < source.Height
            ? source[x, y] : ColorBgra.FromBgra(0, 0, 0, 0);
}

// Moves/scales the layer's visible content (alpha > 0) into a target box. Bounds are measured at
// render time, so earlier queued drawing in the same pass is included.
internal sealed class AlignLayerOp : PendingOp
{
    private readonly AlignLayerParams _parameters;

    public AlignLayerOp(AlignLayerParams p)
    {
        if (p.Horizontal is not (null or "left" or "center" or "right"))
            throw new ArgumentException("Horizontal must be left, center or right.");
        if (p.Vertical is not (null or "top" or "middle" or "bottom"))
            throw new ArgumentException("Vertical must be top, middle or bottom.");
        if (p.Fit is not ("none" or "contain" or "cover")) throw new ArgumentException("Fit must be none, contain or cover.");
        if (p.Fit == "none" && p.Horizontal is null && p.Vertical is null)
            throw new ArgumentException("Specify horizontal, vertical or fit.");
        if (p.Margin < 0) throw new ArgumentException("Margin must be nonnegative.");
        bool anyTarget = p.TargetX.HasValue || p.TargetY.HasValue || p.TargetWidth.HasValue || p.TargetHeight.HasValue;
        bool fullTarget = p.TargetX.HasValue && p.TargetY.HasValue && p.TargetWidth.HasValue && p.TargetHeight.HasValue;
        if (anyTarget && !fullTarget) throw new ArgumentException("Target box needs targetX, targetY, targetWidth and targetHeight together.");
        if (fullTarget && (p.TargetWidth <= 2 * p.Margin || p.TargetHeight <= 2 * p.Margin))
            throw new ArgumentException("Target box must be larger than twice the margin.");
        if (p.Interpolation is not ("nearest" or "bilinear")) throw new ArgumentException("Interpolation must be nearest or bilinear.");
        _parameters = p;
    }

    public override void Apply(Surface surface)
    {
        var p = _parameters;
        int left = surface.Width, top = surface.Height, right = -1, bottom = -1;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
                if (surface[x, y].A > 0)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        if (right < 0) return;
        double width = right - left + 1, height = bottom - top + 1;
        // ponytail: canvas margin too large for the canvas collapses to a 1px box instead of failing mid-render.
        double tx = (p.TargetX ?? 0) + p.Margin, ty = (p.TargetY ?? 0) + p.Margin;
        double tw = Math.Max(1, (p.TargetWidth ?? surface.Width) - 2 * p.Margin);
        double th = Math.Max(1, (p.TargetHeight ?? surface.Height) - 2 * p.Margin);
        double scale = p.Fit switch
        {
            "contain" => Math.Min(tw / width, th / height),
            "cover" => Math.Max(tw / width, th / height),
            _ => 1
        };
        double Place(string? edge, double start, double size, double content, double current) => edge switch
        {
            "left" or "top" => start,
            "center" or "middle" => start + (size - content) / 2,
            "right" or "bottom" => start + size - content,
            _ => p.Fit == "none" ? current : start + (size - content) / 2
        };
        double newLeft = Place(p.Horizontal, tx, tw, width * scale, left);
        double newTop = Place(p.Vertical, ty, th, height * scale, top);
        // Whole-pixel moves stay lossless under bilinear sampling.
        if (scale == 1) { newLeft = Math.Floor(newLeft); newTop = Math.Floor(newTop); }
        new TransformLayerOp(new TransformLayerParams
        {
            OffsetX = newLeft - left, OffsetY = newTop - top, ScaleX = scale, ScaleY = scale,
            PivotX = left, PivotY = top, Interpolation = p.Interpolation
        }).Apply(surface);
    }
}
