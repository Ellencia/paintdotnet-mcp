using System.Security.Cryptography;
using System.Text.Json;
using PaintDotNet;
using PaintDotNetMcp.Contracts;
using static PaintDotNetMcp.Bridge.NativeEditing;
using Rectangle = System.Drawing.Rectangle;

namespace PaintDotNetMcp.Bridge;

// Native bitmap layers with persistent text definitions. Detect edits before regeneration.
internal static class TextLayers
{
    private const string MetadataKey = "PaintDotNetMcp.Text.v1";
    private sealed class Definition
    {
        public int SchemaVersion { get; set; } = 1;
        public string Id { get; set; } = "";
        public DrawTextParams Parameters { get; set; } = new();
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelHash { get; set; } = "";
    }

    public static object Create(CreateTextLayerParams p)
    {
        ValidateName(p.Name);
        var draw = new DrawTextOp(p.Text ?? throw new ArgumentException("Text parameters required."));
        return Run(workspace =>
        {
            var document = (Document)Property(workspace, "Document");
            using var owner = new LayerOwner(BuildLayer(document, p.Name, p.Text, draw, Guid.NewGuid().ToString("N")));
            int index = (int)Property(workspace, "ActiveLayerIndex") + 1;
            var history = Memento("NewLayerHistoryMemento", "MCP create text layer", workspace, index);
            document.Layers.Insert(index, owner.Layer);
            owner.Accept();
            workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, index);
            Push(workspace, history);
            return Describe(owner.Layer, index, 1);
        });
    }

    public static object Update(UpdateTextLayerParams p)
    {
        if (p.LayerIndex < -1) throw new ArgumentException("Layer index must be -1 (active) or nonnegative.");
        if (p.Name is not null) ValidateName(p.Name);
        return Run(workspace =>
        {
            var document = (Document)Property(workspace, "Document");
            int index = ResolveIndex(workspace, document, p.LayerIndex);
            var original = document.Layers[index] as BitmapLayer ?? throw new InvalidOperationException("Target is not a bitmap layer.");
            var definition = Read(original) ?? throw new InvalidOperationException("This layer has no MCP text definition. Use create_text_layer first.");
            bool modified = Modified(original, definition);
            if (modified && !p.ReplaceModifiedPixels)
                throw new InvalidOperationException("Text layer pixels or canvas dimensions changed after rendering. Undo those edits first, or set replaceModifiedPixels=true to regenerate the entire layer from its saved text definition.");
            var old = definition.Parameters;
            var next = new DrawTextParams
            {
                Text = p.Text ?? old.Text, X = p.X ?? old.X, Y = p.Y ?? old.Y,
                FontFamily = p.FontFamily ?? old.FontFamily, FontSize = p.FontSize ?? old.FontSize,
                Bold = p.Bold ?? old.Bold, Italic = p.Italic ?? old.Italic, AntiAlias = p.AntiAlias ?? old.AntiAlias,
                R = p.R ?? old.R, G = p.G ?? old.G, B = p.B ?? old.B, A = p.A ?? old.A
            };
            string name = p.Name ?? original.Name;
            if (!modified && name == original.Name && JsonSerializer.Serialize(next) == JsonSerializer.Serialize(old))
                return Describe(original, index, 0);
            var draw = new DrawTextOp(next);
            using var owner = new LayerOwner(BuildLayer(document, name, next, draw, definition.Id, original));
            var removed = Memento("DeleteLayerHistoryMemento", null, workspace, original);
            var added = Memento("NewLayerHistoryMemento", null, workspace, index);
            var history = Compound("MCP update text layer", removed, added);
            document.Layers[index] = owner.Layer;
            owner.Accept();
            workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, index);
            Push(workspace, history);
            return Describe(owner.Layer, index, 1);
        });
    }

    // Moves whole layers by whole pixels to align/distribute their visible bounds, as one Undo step. Lives here because
    // unmodified text layers are regenerated at the shifted X/Y (staying editable); other bitmap layers shift pixels.
    public static object Arrange(ArrangeLayersParams p)
    {
        var indices = p.LayerIndices ?? [];
        if (indices.Length == 0 || indices.Distinct().Count() != indices.Length) throw new ArgumentException("layerIndices must list distinct layer indexes.");
        if (p.Horizontal is not (null or "left" or "center" or "right")) throw new ArgumentException("Horizontal must be left, center or right.");
        if (p.Vertical is not (null or "top" or "middle" or "bottom")) throw new ArgumentException("Vertical must be top, middle or bottom.");
        if (p.Distribute is not (null or "horizontal" or "vertical")) throw new ArgumentException("Distribute must be horizontal or vertical.");
        if (p.RelativeTo is not ("canvas" or "layers")) throw new ArgumentException("RelativeTo must be canvas or layers.");
        if (p.Margin < 0) throw new ArgumentException("Margin must be nonnegative.");
        if (p.Horizontal is null && p.Vertical is null && p.Distribute is null) throw new ArgumentException("Specify horizontal, vertical or distribute.");
        if ((p.Distribute == "horizontal" && p.Horizontal is not null) || (p.Distribute == "vertical" && p.Vertical is not null))
            throw new ArgumentException("Distribute already positions that axis; align the other one.");
        if (p.Distribute is not null && indices.Length < 2) throw new ArgumentException("Distribute needs at least two layers.");
        return Run(workspace =>
        {
            var document = (Document)Property(workspace, "Document");
            int n = indices.Length;
            var layers = indices.Select(i => i < 0 || i >= document.Layers.Count ? throw new ArgumentException("Layer index " + i + " out of range.")
                : document.Layers[i] as BitmapLayer ?? throw new InvalidOperationException("Layer " + i + " is not a bitmap layer.")).ToArray();
            var bounds = layers.Select((l, k) => Bounds(l.Surface) ?? throw new InvalidOperationException("Layer " + indices[k] + " has no visible pixels.")).ToArray();
            var box = p.RelativeTo == "layers" ? bounds.Aggregate(Rectangle.Union)
                : Rectangle.FromLTRB(p.Margin, p.Margin, document.Width - p.Margin, document.Height - p.Margin);
            if (box.Width <= 0 || box.Height <= 0) throw new ArgumentException("Margin leaves no room on the canvas.");
            static int Place(string edge, int start, int size, int content) => edge switch
            {
                "left" or "top" => start,
                "center" or "middle" => start + (size - content) / 2,
                _ => start + size - content
            };
            var dx = new int[n]; var dy = new int[n];
            for (int k = 0; k < n; k++)
            {
                if (p.Horizontal is { } h) dx[k] = Place(h, box.X, box.Width, bounds[k].Width) - bounds[k].X;
                if (p.Vertical is { } v) dy[k] = Place(v, box.Y, box.Height, bounds[k].Height) - bounds[k].Y;
            }
            if (p.Distribute is { } axis)
            {
                // Equal gaps between neighbours in their current order; the outer ones touch the box edges.
                bool horizontal = axis == "horizontal";
                int Start(Rectangle r) => horizontal ? r.X : r.Y;
                int Size(Rectangle r) => horizontal ? r.Width : r.Height;
                var order = Enumerable.Range(0, n).OrderBy(k => Start(bounds[k])).ToArray();
                double gap = (double)(Size(box) - order.Sum(k => Size(bounds[k]))) / (n - 1), position = Start(box);
                foreach (int k in order)
                {
                    int d = (int)Math.Round(position) - Start(bounds[k]);
                    if (horizontal) dx[k] = d; else dy[k] = d;
                    position += Size(bounds[k]) + gap;
                }
            }

            var moved = new List<(int Index, BitmapLayer Original, BitmapLayer Replacement)>();
            try
            {
                for (int k = 0; k < n; k++)
                {
                    if (dx[k] == 0 && dy[k] == 0) continue;
                    var original = layers[k];
                    var definition = Read(original);
                    BitmapLayer replacement;
                    if (definition is not null && !Modified(original, definition))
                    {
                        var next = JsonSerializer.Deserialize<DrawTextParams>(JsonSerializer.Serialize(definition.Parameters))!;
                        next.X += dx[k]; next.Y += dy[k];
                        replacement = BuildLayer(document, original.Name, next, new DrawTextOp(next), definition.Id, original);
                    }
                    else
                    {
                        replacement = (BitmapLayer)original.Clone();
                        for (int y = 0; y < original.Height; y++)
                            for (int x = 0; x < original.Width; x++)
                            {
                                int sx = x - dx[k], sy = y - dy[k];
                                replacement.Surface[x, y] = sx >= 0 && sy >= 0 && sx < original.Width && sy < original.Height
                                    ? original.Surface[sx, sy] : ColorBgra.Zero;
                            }
                    }
                    moved.Add((indices[k], original, replacement));
                }
                if (moved.Count > 0)
                {
                    int active = (int)Property(workspace, "ActiveLayerIndex");
                    var history = moved.SelectMany(m => new[]
                        { Memento("DeleteLayerHistoryMemento", null, workspace, m.Original), Memento("NewLayerHistoryMemento", null, workspace, m.Index) }).ToArray();
                    foreach (var m in moved) document.Layers[m.Index] = m.Replacement;
                    moved.Clear();
                    workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, active);
                    Push(workspace, Compound("MCP arrange layers", history));
                }
            }
            finally { foreach (var m in moved) m.Replacement.Dispose(); }
            return new
            {
                Ok = true,
                Layers = Enumerable.Range(0, n).Select(k => (BitmapLayer)document.Layers[indices[k]] is var layer && Bounds(layer.Surface) is { } r
                    ? new { LayerIndex = indices[k], r.X, r.Y, r.Width, r.Height, Dx = dx[k], Dy = dy[k],
                        EditableText = Read(layer) is { } d && !Modified(layer, d) }
                    : null).ToList(),
                HistorySteps = dx.Any(d => d != 0) || dy.Any(d => d != 0) ? 1 : 0
            };
        });
    }

    private static Rectangle? Bounds(Surface surface)
    {
        int left = surface.Width, top = surface.Height, right = -1, bottom = -1;
        for (int y = 0; y < surface.Height; y++)
            for (int x = 0; x < surface.Width; x++)
                if (surface[x, y].A > 0)
                {
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                }
        return right < 0 ? null : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    public static object Get(TextLayerIndexParams p) => Query(workspace =>
    {
        var document = (Document)Property(workspace, "Document");
        int index = ResolveIndex(workspace, document, p.LayerIndex);
        var layer = document.Layers[index] as BitmapLayer ?? throw new InvalidOperationException("Target is not a bitmap layer.");
        return Describe(layer, index, 0);
    });

    public static object List() => Query(workspace =>
    {
        var document = (Document)Property(workspace, "Document");
        var list = new List<TextLayerResult>();
        for (int i = 0; i < document.Layers.Count; i++)
            if (document.Layers[i] is BitmapLayer layer && Read(layer) is not null) list.Add(Describe(layer, i, 0));
        return new { Layers = list };
    });

    private static BitmapLayer BuildLayer(Document document, string name, DrawTextParams p, DrawTextOp draw, string id, BitmapLayer? original = null)
    {
        var layer = new BitmapLayer(document.Width, document.Height);
        try
        {
            if (original is not null) layer.LoadProperties(original.SaveProperties());
            layer.Name = name;
            layer.Surface.Fill(ColorBgra.Zero);
            draw.Apply(layer.Surface);
            layer.Metadata.SetUserValue(MetadataKey, JsonSerializer.Serialize(new Definition
            {
                Id = id, Parameters = p, Width = document.Width, Height = document.Height,
                PixelHash = Hash(layer.Surface)
            }));
            return layer;
        }
        catch { layer.Dispose(); throw; }
    }

    // A cloned text layer carries its source's Id; give the copy its own.
    public static void RenewId(BitmapLayer layer)
    {
        if (Read(layer) is not { } definition) return;
        definition.Id = Guid.NewGuid().ToString("N");
        layer.Metadata.SetUserValue(MetadataKey, JsonSerializer.Serialize(definition));
    }

    private static Definition? Read(BitmapLayer layer)
    {
        var json = layer.Metadata.GetUserValue(MetadataKey);
        if (json is null) return null;
        var definition = JsonSerializer.Deserialize<Definition>(json);
        if (definition is null || definition.SchemaVersion != 1 || definition.Parameters is null || string.IsNullOrEmpty(definition.Id))
            throw new InvalidOperationException("Unsupported or invalid MCP text metadata.");
        return definition;
    }

    private static TextLayerResult Describe(BitmapLayer layer, int index, int steps)
    {
        var definition = Read(layer) ?? throw new InvalidOperationException("This layer has no MCP text definition.");
        return new TextLayerResult { LayerIndex = index, Name = layer.Name, Id = definition.Id,
            Text = definition.Parameters, PixelsModified = Modified(layer, definition), HistorySteps = steps };
    }

    private static bool Modified(BitmapLayer layer, Definition definition) => layer.Width != definition.Width ||
        layer.Height != definition.Height || Hash(layer.Surface) != definition.PixelHash;
    private static string Hash(Surface surface) => Convert.ToHexString(SHA256.HashData(ImageIO.ReadSurface(surface)));

    private static int ResolveIndex(object workspace, Document document, int requested)
    {
        int index = requested == -1 ? (int)Property(workspace, "ActiveLayerIndex") : requested;
        if (index < 0 || index >= document.Layers.Count) throw new ArgumentException("Layer index out of range.");
        return index;
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256) throw new ArgumentException("Layer name must contain 1..256 characters.");
    }

    private static object Query(Func<object, object> read)
    {
        if (HistoryOps.BatchActive) throw new InvalidOperationException("Finish the active batch before reading text layers.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount > 0) throw new InvalidOperationException("Apply pending drawing before reading text layers.");
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() => result = read(AppServices.DocumentWorkspaceService()
            ?? throw new InvalidOperationException("Open a canvas first.")), out var note)) throw new InvalidOperationException(note);
        return result!;
    }

    private sealed class LayerOwner(BitmapLayer layer) : IDisposable
    {
        public BitmapLayer Layer { get; } = layer;
        private bool _accepted;
        public void Accept() => _accepted = true;
        public void Dispose() { if (!_accepted) Layer.Dispose(); }
    }
}
