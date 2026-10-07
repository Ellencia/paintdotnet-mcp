using System.Drawing;
using System.Reflection;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

internal static class NativeSelection
{
    private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    public static SelectionResult Rectangle(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)x + width > int.MaxValue || (long)y + height > int.MaxValue)
            throw new ArgumentException("Selection width and height must be positive, with edges within integer range.");
        return Run(selection =>
        {
            var set = selection.GetType().GetMethods(Flags).SingleOrDefault(m => m.Name == "SetContinuation" &&
                m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name == "RectInt32")
                ?? throw new InvalidOperationException("Native rectangular selection API not found.");
            var rectangle = Activator.CreateInstance(set.GetParameters()[0].ParameterType, x, y, width, height)!;
            set.Invoke(selection, [rectangle, Enum.Parse(set.GetParameters()[1].ParameterType, "Replace")]);
            selection.GetType().GetMethod("CommitContinuation", Type.EmptyTypes)!.Invoke(selection, null);
        }, "MCP rectangle selection");
    }

    public static SelectionResult Polygon(List<Point> points)
    {
        if (points.Count < 3 || points.Distinct().Count() < 3)
            throw new ArgumentException("Polygon selection requires at least three distinct points.");
        return Run(selection =>
        {
            var set = selection.GetType().GetMethods(Flags).SingleOrDefault(m => m.Name == "SetContinuation" &&
                m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.IsGenericType &&
                m.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
                ?? throw new InvalidOperationException("Native polygon selection API not found.");
            var pointType = set.GetParameters()[0].ParameterType.GetGenericArguments()[0];
            var polygon = Array.CreateInstance(pointType, points.Count);
            for (int i = 0; i < points.Count; i++)
                polygon.SetValue(Activator.CreateInstance(pointType, (double)points[i].X, (double)points[i].Y), i);
            set.Invoke(selection, [polygon, Enum.Parse(set.GetParameters()[1].ParameterType, "Replace")]);
            selection.GetType().GetMethod("CommitContinuation", Type.EmptyTypes)!.Invoke(selection, null);
        }, "MCP polygon selection");
    }

    // Same path as the Magic Wand: stencil -> GeometryList.FromStencil -> SetContinuation. Pixel-exact, keeps holes.
    public static SelectionResult Mask(Func<int, int, bool> inside, int width, int height, string mode, string name)
    {
        return Run(selection =>
        {
            var stencilType = AppServices.FindType("PaintDotNet.Rendering.BitSurface")
                ?? throw new InvalidOperationException("Paint.NET BitSurface not found.");
            var fill = stencilType.GetMethods(Flags).Single(m => m.Name == "Fill" && m.GetParameters().Length == 2);
            var rectType = fill.GetParameters()[0].ParameterType;
            var fromStencil = AppServices.FindType("PaintDotNet.Rendering.GeometryList")!.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .Single(m => m.Name == "FromStencil" && m.GetParameters().Length == 1).MakeGenericMethod(stencilType);
            var set = selection.GetType().GetMethods(Flags).Single(m => m.Name == "SetContinuation" &&
                m.GetParameters().Length == 2 && m.GetParameters()[0].ParameterType.Name == "GeometryList");
            var stencil = Activator.CreateInstance(stencilType, width, height)!;
            try
            {
                // One Fill per horizontal run keeps reflection calls to the number of runs, not pixels.
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        if (!inside(x, y)) continue;
                        int start = x;
                        while (x < width && inside(x, y)) x++;
                        fill.Invoke(stencil, [Activator.CreateInstance(rectType, start, y, x - start, 1)!, true]);
                    }
                var geometry = fromStencil.Invoke(null, [stencil])!;
                set.Invoke(selection, [geometry, Enum.Parse(set.GetParameters()[1].ParameterType, mode, ignoreCase: true)]);
                selection.GetType().GetMethod("CommitContinuation", Type.EmptyTypes)!.Invoke(selection, null);
            }
            finally { (stencil as IDisposable)?.Dispose(); }
        }, name);
    }

    public static SelectionResult Clear() => Run(selection =>
        selection.GetType().GetMethod("Reset", Type.EmptyTypes)!.Invoke(selection, null), "MCP clear selection", clear: true);

    public static SelectionResult Get() => Run(null, "Current native selection");

    private static SelectionResult Run(Action<object>? mutate, string name, bool clear = false)
    {
        if (mutate is not null && HistoryOps.BatchActive)
            throw new InvalidOperationException("Finish the active batch with end_batch before changing the selection.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount > 0)
            throw new InvalidOperationException("Apply pending drawing with commit and wait_for_idle before querying or changing selection.");
        SelectionResult? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var workspace = AppServices.DocumentWorkspaceService()
                ?? throw new InvalidOperationException("Open a canvas before using native selection.");
            var document = AppServices.GetPropertyValue(workspace, "Document")
                ?? throw new InvalidOperationException("Open a canvas before using native selection.");
            var selection = AppServices.GetPropertyValue(workspace, "Selection")
                ?? throw new InvalidOperationException("Paint.NET native Selection not found.");
            int historySteps = 0;
            if (mutate is not null && !(clear && AppServices.GetPropertyValue(selection, "IsEmpty") is true))
            {
                var saved = selection.GetType().GetMethod("Save", Type.EmptyTypes)!.Invoke(selection, null)!;
                var history = AppServices.GetPropertyValue(workspace, "History")!;
                var mementoType = AppServices.FindType("PaintDotNet.HistoryMementos.SelectionHistoryMemento")
                    ?? throw new InvalidOperationException("Selection Undo history API not found.");
                var constructor = mementoType.GetConstructors().Single(c => c.GetParameters().Length == 3);
                var memento = constructor.Invoke([name, null, workspace]);
                var push = history.GetType().GetMethods(Flags).Single(m => m.Name == "PushNewMemento" && m.GetParameters().Length == 1);
                try
                {
                    using (var scope = (IDisposable)selection.GetType().GetMethod("UseChangeScope", Type.EmptyTypes)!.Invoke(selection, null)!)
                    {
                        mutate(selection);
                        if (!clear)
                        {
                            var bounds = selection.GetType().GetMethod("GetBounds", Type.EmptyTypes)!.Invoke(selection, null)!;
                            var clipped = ClipBounds(bounds, document);
                            if (clipped.Width <= 0 || clipped.Height <= 0)
                                throw new ArgumentException("Selection must intersect the canvas with nonzero area.");
                        }
                    }
                    push.Invoke(history, [memento]);
                    historySteps = 1;
                }
                catch
                {
                    selection.GetType().GetMethods(Flags).Single(m => m.Name == "Restore" && m.GetParameters().Length == 1).Invoke(selection, [saved]);
                    (memento as IDisposable)?.Dispose();
                    throw;
                }
                finally { (saved as IDisposable)?.Dispose(); }
            }
            if (mutate is not null) SelectionOps.SoftSelection.Clear();
            var rect = selection.GetType().GetMethod("GetBounds", Type.EmptyTypes)!.Invoke(selection, null)!;
            bool empty = AppServices.GetPropertyValue(selection, "IsEmpty") is true;
            var visibleBounds = empty ? System.Drawing.Rectangle.Empty : ClipBounds(rect, document);
            result = new SelectionResult
            {
                Ok = true, Note = name, NativeVisible = !empty && visibleBounds.Width > 0 && visibleBounds.Height > 0,
                IsEmpty = empty, HistorySteps = historySteps,
                X = visibleBounds.X, Y = visibleBounds.Y, Width = visibleBounds.Width, Height = visibleBounds.Height
            };
        }, out var note)) throw new InvalidOperationException(note);
        return result!;
    }

    private static System.Drawing.Rectangle ClipBounds(object bounds, object document)
    {
        int x = (int)AppServices.GetPropertyValue(bounds, "X")!;
        int y = (int)AppServices.GetPropertyValue(bounds, "Y")!;
        int width = (int)AppServices.GetPropertyValue(bounds, "Width")!;
        int height = (int)AppServices.GetPropertyValue(bounds, "Height")!;
        int canvasWidth = (int)AppServices.GetPropertyValue(document, "Width")!;
        int canvasHeight = (int)AppServices.GetPropertyValue(document, "Height")!;
        int left = Math.Clamp(x, 0, canvasWidth), top = Math.Clamp(y, 0, canvasHeight);
        int right = (int)Math.Clamp((long)x + width, 0, canvasWidth);
        int bottom = (int)Math.Clamp((long)y + height, 0, canvasHeight);
        return new(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
