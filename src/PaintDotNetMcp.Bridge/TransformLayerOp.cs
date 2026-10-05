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
