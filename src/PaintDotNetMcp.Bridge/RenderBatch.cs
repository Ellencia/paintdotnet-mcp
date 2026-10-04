using System.Drawing;
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
    private readonly HashSet<int> _rendered = new();
    private readonly Action<Surface> _complete;
    private bool _published;

    public RenderBatch(Surface source, IEnumerable<PendingOp> operations, Action<Surface> complete)
    {
        _backing = new Surface(source.Width, source.Height);
        _snapshot = new Surface(source.Width, source.Height);
        _complete = complete;
        try
        {
            _backing.CopySurface(source);
            _snapshot.CopySurface(source);
            foreach (var operation in operations) operation.Apply(_backing);
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
                if (_rendered.Add(i))
                    _snapshot.CopySurface(_backing, rois[i].Location, rois[i]);
            }
            if (!_published && _rendered.Count == rois.Length)
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
