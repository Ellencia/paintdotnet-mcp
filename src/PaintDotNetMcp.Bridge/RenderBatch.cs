using System.Drawing;
using System.Collections;
using PaintDotNet;

namespace PaintDotNetMcp.Bridge;

// Owned by one Effect instance. Queued operations run once before tiled rendering.
// Completion means all requested ROIs were copied and the snapshot was published;
// it does not claim that Paint.NET has accepted the effect into its undo history.
internal sealed class RenderBatch : IDisposable
{
    private readonly object _gate = new();
    private readonly Surface _backing;
    private readonly Surface _snapshot;
    private readonly BitArray _remainingPixels;
    private long _remainingCount;
    private readonly Action<Surface> _complete;
    private bool _published;

    public RenderBatch(Surface source, IEnumerable<PendingOp> operations, Action<Surface> complete,
        IReadOnlyList<Rectangle> selectionScans)
    {
        _backing = new Surface(source.Width, source.Height);
        _snapshot = new Surface(source.Width, source.Height);
        _complete = complete;
        _remainingPixels = new BitArray(checked(source.Width * source.Height));
        try
        {
            _backing.CopySurface(source);
            _snapshot.CopySurface(source);
            foreach (var operation in operations) operation.Apply(_backing);
            foreach (var scan in selectionScans)
            {
                var clipped = Rectangle.Intersect(scan, source.Bounds);
                for (int y = clipped.Top; y < clipped.Bottom; y++)
                    for (int x = clipped.Left; x < clipped.Right; x++)
                    {
                        int pixel = y * source.Width + x;
                        if (!_remainingPixels[pixel])
                        {
                            _remainingPixels[pixel] = true;
                            _remainingCount++;
                        }
                    }
            }
            if (_remainingCount == 0)
            {
                _complete(_snapshot);
                _published = true;
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Render(Surface destination, Rectangle[] rois, int startIndex, int length)
    {
        for (int i = startIndex; i < startIndex + length; i++)
            destination.CopySurface(_backing, rois[i].Location, rois[i]);

        lock (_gate)
        {
            for (int i = startIndex; i < startIndex + length; i++)
            {
                var roi = Rectangle.Intersect(rois[i], _backing.Bounds);
                _snapshot.CopySurface(_backing, roi.Location, roi);
                for (int y = roi.Top; y < roi.Bottom; y++)
                    for (int x = roi.Left; x < roi.Right; x++)
                    {
                        int pixel = y * _backing.Width + x;
                        if (_remainingPixels[pixel])
                        {
                            _remainingPixels[pixel] = false;
                            _remainingCount--;
                        }
                    }
            }
            if (!_published && _remainingCount == 0)
            {
                _complete(_snapshot);
                _published = true;
            }
        }
    }

    public void Dispose()
    {
        _backing.Dispose();
        _snapshot.Dispose();
    }
}
