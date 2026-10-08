using System.Collections;
using System.Drawing;
using System.Reflection;
using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

internal static class NativeEditing
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Dictionary<string, string> Anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top_left"] = "TopLeft", ["top"] = "Top", ["top_right"] = "TopRight",
        ["left"] = "Left", ["center"] = "Middle", ["right"] = "Right",
        ["bottom_left"] = "BottomLeft", ["bottom"] = "Bottom", ["bottom_right"] = "BottomRight"
    };

    public static object CopySelection(CopySelectionToLayerParams p)
    {
        if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 256)
            throw new ArgumentException("Layer name must contain 1..256 characters.");
        return Run(workspace =>
        {
            var selection = Property(workspace, "Selection");
            if (Property(selection, "IsEmpty") is true)
                throw new InvalidOperationException("Select a region before copying it to a new layer.");
            var source = Property(Property(workspace, "ActiveLayer"), "Surface") as Surface
                ?? throw new InvalidOperationException("Active layer is not a bitmap layer.");
            var scans = (IEnumerable)selection.GetType().GetMethod("GetCachedClippingMaskScans", Type.EmptyTypes)!.Invoke(selection, null)!;
            int index = InsertLayer(workspace, p.Name, "MCP copy selection to layer", destination =>
            {
                bool hasArea = false;
                foreach (var scan in scans)
                {
                    var bounds = Rectangle.Intersect(source.Bounds, new Rectangle(
                        (int)Property(scan, "X"), (int)Property(scan, "Y"),
                        (int)Property(scan, "Width"), (int)Property(scan, "Height")));
                    for (int y = bounds.Top; y < bounds.Bottom; y++)
                        for (int x = bounds.Left; x < bounds.Right; x++) destination[x, y] = source[x, y];
                    hasArea |= bounds.Width > 0 && bounds.Height > 0;
                }
                if (!hasArea) throw new InvalidOperationException("Selection does not intersect the canvas.");
            });
            return new { Ok = true, LayerIndex = index, Name = p.Name, Width = source.Width, Height = source.Height, HistorySteps = 1 };
        });
    }

    // Inserts a canvas-sized transparent layer above the active one, drawn by fill, and selects it. One Undo step;
    // if fill throws, nothing is inserted.
    internal static int InsertLayer(object workspace, string name, string historyName, Action<Surface> fill)
    {
        var document = Property(workspace, "Document");
        var layerType = AppServices.FindType("PaintDotNet.BitmapLayer")!;
        var layer = layerType.GetConstructor([typeof(int), typeof(int)])!.Invoke([(int)Property(document, "Width"), (int)Property(document, "Height")]);
        bool inserted = false;
        try
        {
            var destination = (Surface)Property(layer, "Surface");
            destination.Fill(ColorBgra.Zero);
            fill(destination);
            layerType.GetProperty("Name")!.SetValue(layer, name);
            int index = (int)Property(workspace, "ActiveLayerIndex") + 1;
            var history = Memento("NewLayerHistoryMemento", historyName, workspace, index);
            var layers = Property(document, "Layers");
            layers.GetType().GetMethod("Insert", [typeof(int), layerType.BaseType!])!.Invoke(layers, [index, layer]);
            inserted = true;
            workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, index);
            Push(workspace, history);
            return index;
        }
        finally { if (!inserted) (layer as IDisposable)?.Dispose(); }
    }

    public static object Resize(ResizeCanvasParams p)
    {
        if (p.Width <= 0 || p.Height <= 0 || p.Width > 16384 || p.Height > 16384 || (long)p.Width * p.Height > 64_000_000)
            throw new ArgumentException("Canvas dimensions must be 1..16384 pixels with at most 64 million pixels total.");
        if (p.Anchor is null || !Anchors.TryGetValue(p.Anchor, out string? anchor))
            throw new ArgumentException("Anchor must be top_left, top, top_right, left, center, right, bottom_left, bottom, or bottom_right.");
        return Run(workspace =>
        {
            var document = Property(workspace, "Document");
            if ((int)Property(document, "Width") == p.Width && (int)Property(document, "Height") == p.Height)
                return new { Ok = true, Width = p.Width, Height = p.Height, HistorySteps = 0 };
            var resize = AppServices.FindType("PaintDotNet.Actions.CanvasSizeAction")!.GetMethod("ResizeDocument", BindingFlags.Public | BindingFlags.Static)!;
            var replacement = resize.Invoke(null, [document, new Size(p.Width, p.Height), Enum.Parse(resize.GetParameters()[2].ParameterType, anchor), ColorBgra.FromBgra(p.B, p.G, p.R, p.A)])!;
            bool installed = false;
            try
            {
                var selection = Memento("SelectionHistoryMemento", null, workspace);
                var replace = Memento("ReplaceDocumentHistoryMemento", null, workspace);
                var historyType = AppServices.FindType("PaintDotNet.HistoryMementos.HistoryMemento")!;
                var children = Array.CreateInstance(historyType, 2);
                children.SetValue(selection, 0); children.SetValue(replace, 1);
                var compound = AppServices.FindType("PaintDotNet.HistoryMementos.CompoundHistoryMemento")!.GetConstructors()
                    .Single(c => c.GetParameters().Length == 3 && c.GetParameters()[2].ParameterType.IsArray)
                    .Invoke(["MCP resize canvas", null, children]);
                workspace.GetType().GetProperty("Document")!.SetValue(workspace, replacement);
                installed = true;
                Push(workspace, compound);
                return new { Ok = true, Width = p.Width, Height = p.Height, HistorySteps = 1 };
            }
            finally { if (!installed) (replacement as IDisposable)?.Dispose(); }
        });
    }

    public static object Crop() => Run(workspace =>
    {
        if (Property(Property(workspace, "Selection"), "IsEmpty") is true)
            throw new InvalidOperationException("Select a region before cropping the canvas.");
        var oldDocument = Property(workspace, "Document");
        AppServices.FindType("PaintDotNet.Actions.CropToSelectionAction")!.GetMethod("PerformAction", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [workspace]);
        var document = Property(workspace, "Document");
        if (ReferenceEquals(oldDocument, document)) throw new InvalidOperationException("Paint.NET did not crop the document; select a region with nonzero area inside the canvas.");
        return new { Ok = true, Width = (int)Property(document, "Width"), Height = (int)Property(document, "Height"), HistorySteps = 1 };
    });

    internal static object Run(Func<object, object> edit)
    {
        if (HistoryOps.BatchActive) throw new InvalidOperationException("Finish the active batch with end_batch before editing layers or canvas size.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount > 0) throw new InvalidOperationException("Apply pending drawing with commit and wait_for_idle before editing layers or canvas size.");
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var workspace = AppServices.DocumentWorkspaceService() ?? throw new InvalidOperationException("Open a canvas first.");
            Property(workspace, "Document");
            workspace.GetType().GetMethod("PushNullTool", Type.EmptyTypes)!.Invoke(workspace, null);
            try { result = edit(workspace); }
            finally { workspace.GetType().GetMethod("PopNullTool", Type.EmptyTypes)!.Invoke(workspace, null); }
            ImageIO.InvalidateSnapshot();
            var layer = Property(workspace, "ActiveLayer");
            ImageIO.CaptureSnapshot((Surface)Property(layer, "Surface"));
        }, out var note)) throw new InvalidOperationException(note);
        return result!;
    }

    internal static object Property(object instance, string name) => AppServices.GetPropertyValue(instance, name)
        ?? throw new InvalidOperationException("Native " + name + " unavailable.");

    internal static object Memento(string type, string? name, object workspace, params object[] extra)
    {
        var args = new object?[] { name, null, workspace }.Concat(extra).ToArray();
        return AppServices.FindType("PaintDotNet.HistoryMementos." + type)!.GetConstructors()
            .Single(c => c.GetParameters().Length == args.Length).Invoke(args);
    }

    internal static void Push(object workspace, object history)
    {
        var stack = Property(workspace, "History");
        stack.GetType().GetMethods(Instance).Single(m => m.Name == "PushNewMemento" && m.GetParameters().Length == 1).Invoke(stack, [history]);
    }

    internal static object Compound(string name, params object[] mementos)
    {
        var type = AppServices.FindType("PaintDotNet.HistoryMementos.HistoryMemento")!;
        var children = Array.CreateInstance(type, mementos.Length);
        for (int i = 0; i < mementos.Length; i++) children.SetValue(mementos[i], i);
        return AppServices.FindType("PaintDotNet.HistoryMementos.CompoundHistoryMemento")!.GetConstructors()
            .Single(c => c.GetParameters().Length == 3 && c.GetParameters()[2].ParameterType.IsArray)
            .Invoke([name, null, children]);
    }
}
