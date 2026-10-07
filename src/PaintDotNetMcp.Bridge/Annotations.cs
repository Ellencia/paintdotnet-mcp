using System.Drawing;
using System.Text.Json;
using System.Text.Json.Nodes;
using PaintDotNet;
using PaintDotNetMcp.Contracts;
using static PaintDotNetMcp.Bridge.NativeEditing;

namespace PaintDotNetMcp.Bridge;

// Editable annotations: one bitmap layer keeps an ordered list of callouts, arrows and markers in its metadata and is
// redrawn whole from that list, the same way TextLayers keeps text editable. Arrow ends (from/to) and callout leaders
// (target) may name a marker or callout on the same layer; those points are recomputed on every redraw, so moving a
// box drags its arrows along.
internal static class Annotations
{
    private const string MetadataKey = "PaintDotNetMcp.Annotations.v1";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private sealed class Item
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public JsonObject Properties { get; set; } = new();
        public string? From { get; set; }
        public string? To { get; set; }
        public string? Target { get; set; }
    }

    private sealed class Definition
    {
        public int SchemaVersion { get; set; } = 1;
        public List<Item> Items { get; set; } = [];
        public int Width { get; set; }
        public int Height { get; set; }
        public string PixelHash { get; set; } = "";
    }

    private static Type ParamsType(string type) => type switch
    {
        "callout" => typeof(DrawCalloutParams),
        "arrow" => typeof(DrawArrowParams),
        "marker" => typeof(DrawMarkerParams),
        _ => throw new ArgumentException("Type must be callout, arrow or marker.")
    };

    // Adds to the active layer when it is an annotation layer, otherwise to a new "Annotations" layer above it.
    public static object Add(AnnotationParams p)
    {
        var item = new Item { Type = p.Type ?? "" };
        Merge(item, p);
        return Run(workspace =>
        {
            var document = (Document)Property(workspace, "Document");
            int active = (int)Property(workspace, "ActiveLayerIndex");
            var layer = document.Layers[active] as BitmapLayer;
            var definition = layer is null ? null : Read(layer);
            var all = AllItems(document).Select(h => h.Item.Id).ToHashSet();
            int n = 1;
            while (all.Contains(item.Type + n)) n++;
            item.Id = item.Type + n;
            if (definition is not null)
            {
                definition.Items.Add(item);
                return Replace(workspace, document, active, layer!, definition, p.ReplaceModifiedPixels, item.Id);
            }
            definition = new Definition { Items = [item] };
            var created = Build(document, definition, null);
            object history;
            try
            {
                history = Memento("NewLayerHistoryMemento", "MCP add annotation", workspace, active + 1);
                document.Layers.Insert(active + 1, created);
            }
            catch { created.Dispose(); throw; }
            workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, active + 1);
            Push(workspace, history);
            return Describe(active + 1, created, definition, item.Id, 1);
        });
    }

    public static object Update(AnnotationParams p) => Run(workspace =>
    {
        var (document, index, layer, definition, item) = Find(workspace, p.Id);
        Merge(item, p);
        return Replace(workspace, document, index, layer, definition, p.ReplaceModifiedPixels, item.Id);
    });

    public static object Delete(AnnotationParams p) => Run(workspace =>
    {
        var (document, index, layer, definition, item) = Find(workspace, p.Id);
        var users = definition.Items.Where(i => i.From == item.Id || i.To == item.Id || i.Target == item.Id).Select(i => i.Id).ToList();
        if (users.Count > 0) throw new InvalidOperationException(item.Id + " is linked from " + string.Join(", ", users) + "; delete or relink those first.");
        definition.Items.Remove(item);
        return Replace(workspace, document, index, layer, definition, p.ReplaceModifiedPixels, item.Id);
    });

    public static object List() => TextLayers.Query(workspace =>
    {
        var document = (Document)Property(workspace, "Document");
        var layers = new List<object>();
        for (int i = 0; i < document.Layers.Count; i++)
            if (document.Layers[i] is BitmapLayer layer && Read(layer) is { } definition)
                layers.Add(Describe(i, layer, definition, null, 0));
        return new { Layers = layers };
    });

    private static void Merge(Item item, AnnotationParams p)
    {
        var type = ParamsType(item.Type);
        foreach (var (name, value) in p.Properties ?? [])
        {
            var property = type.GetProperties().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException("unknown " + item.Type + " property '" + name + "'; known: " + string.Join(", ", type.GetProperties().Select(x => x.Name)));
            item.Properties[property.Name] = JsonNode.Parse(value.GetRawText());
        }
        if ((p.From ?? p.To) is not null && item.Type != "arrow") throw new ArgumentException("from/to link arrows only.");
        if (p.Target is not null && item.Type != "callout") throw new ArgumentException("target links callouts only.");
        // "" removes a link; the drawing then falls back to the stored coordinates.
        if (p.From is not null) item.From = p.From == "" ? null : p.From;
        if (p.To is not null) item.To = p.To == "" ? null : p.To;
        if (p.Target is not null) item.Target = p.Target == "" ? null : p.Target;
    }

    // Draws every item onto surface (null = only measure) and returns each item's bounds.
    private static Dictionary<string, Rectangle> Render(Definition d, Surface? surface)
    {
        object Params(Item i)
        {
            try { return i.Properties.Deserialize(ParamsType(i.Type), Json)!; }
            catch (JsonException ex) { throw new ArgumentException(i.Id + ": " + ex.Message); }
        }
        // Link targets: marker circles and callout boxes. Neither depends on links, so there are no cycles.
        var shapes = new Dictionary<string, (Rectangle Box, bool Round)>();
        foreach (var i in d.Items)
            if (i.Type == "marker" && Params(i) is DrawMarkerParams m) shapes[i.Id] = (new Rectangle(m.X - m.Radius, m.Y - m.Radius, 2 * m.Radius, 2 * m.Radius), true);
            else if (i.Type == "callout") shapes[i.Id] = (new DrawCalloutOp((DrawCalloutParams)Params(i)).Box, false);
        (Rectangle Box, bool Round) Shape(string from, string id) => shapes.TryGetValue(id, out var s) && id != from ? s
            : throw new ArgumentException(from + " links to '" + id + "', which is not another marker or callout on this layer.");
        Point Center(Rectangle r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
        // Where a line toward `toward` meets the shape: the circle edge, or the box point nearest to it.
        Point Anchor(string from, string id, Point toward)
        {
            var (box, round) = Shape(from, id);
            var c = Center(box);
            if (!round) return new(Math.Clamp(toward.X, box.Left, box.Right - 1), Math.Clamp(toward.Y, box.Top, box.Bottom - 1));
            double dx = toward.X - c.X, dy = toward.Y - c.Y, length = Math.Sqrt(dx * dx + dy * dy);
            return length == 0 ? c : new((int)Math.Round(c.X + dx * box.Width / 2 / length), (int)Math.Round(c.Y + dy * box.Width / 2 / length));
        }

        var bounds = new Dictionary<string, Rectangle>();
        var ops = new List<PendingOp>();
        foreach (var i in d.Items)
        {
            switch (Params(i))
            {
                case DrawMarkerParams m:
                    ops.Add(new DrawMarkerOp(m));
                    bounds[i.Id] = shapes[i.Id].Box;
                    break;
                case DrawCalloutParams c:
                    if (i.Target is { } target)
                    {
                        var point = Anchor(i.Id, target, Center(shapes[i.Id].Box));
                        (c.TargetX, c.TargetY) = (point.X, point.Y);
                    }
                    ops.Add(new DrawCalloutOp(c));
                    var box = shapes[i.Id].Box;
                    bounds[i.Id] = c.TargetX is int tx && c.TargetY is int ty ? Rectangle.Union(box, new Rectangle(tx, ty, 1, 1)) : box;
                    break;
                case DrawArrowParams a:
                    if (a.Thickness < 1 || a.Thickness > 256 || a.HeadSize < 0 || a.HeadSize > 1024)
                        throw new ArgumentException(i.Id + ": thickness 1..256, headSize 0..1024.");
                    Point p1 = new(a.X1, a.Y1), p2 = new(a.X2, a.Y2);
                    Point o1 = i.From is { } f ? Center(Shape(i.Id, f).Box) : p1, o2 = i.To is { } t ? Center(Shape(i.Id, t).Box) : p2;
                    if (i.From is not null) p1 = Anchor(i.Id, i.From, o2);
                    if (i.To is not null) p2 = Anchor(i.Id, i.To, o1);
                    (a.X1, a.Y1, a.X2, a.Y2) = (p1.X, p1.Y, p2.X, p2.Y);
                    ops.Add(new DrawArrowOp(a));
                    bounds[i.Id] = Rectangle.FromLTRB(Math.Min(p1.X, p2.X), Math.Min(p1.Y, p2.Y), Math.Max(p1.X, p2.X) + 1, Math.Max(p1.Y, p2.Y) + 1);
                    break;
            }
        }
        if (surface is not null)
        {
            surface.Fill(ColorBgra.Zero);
            // Arrows first, so their roots sit under boxes and markers like a callout's own leader.
            foreach (var op in ops.OrderBy(op => op is DrawArrowOp ? 0 : 1)) op.Apply(surface);
        }
        return bounds;
    }

    private static BitmapLayer Build(Document document, Definition d, BitmapLayer? original)
    {
        var layer = new BitmapLayer(document.Width, document.Height);
        try
        {
            if (original is not null) layer.LoadProperties(original.SaveProperties());
            else layer.Name = "Annotations";
            Render(d, layer.Surface);
            (d.Width, d.Height, d.PixelHash) = (document.Width, document.Height, TextLayers.Hash(layer.Surface));
            layer.Metadata.SetUserValue(MetadataKey, JsonSerializer.Serialize(d));
            return layer;
        }
        catch { layer.Dispose(); throw; }
    }

    private static object Replace(object workspace, Document document, int index, BitmapLayer original, Definition d, bool replaceModified, string id)
    {
        if (Modified(original, d) && !replaceModified)
            throw new InvalidOperationException("Annotation layer pixels or canvas size changed after rendering. Undo those edits first, or set replaceModifiedPixels=true to redraw the layer from its annotations.");
        int active = (int)Property(workspace, "ActiveLayerIndex");
        var layer = Build(document, d, original);
        try
        {
            var history = Compound("MCP edit annotation",
                Memento("DeleteLayerHistoryMemento", null, workspace, original), Memento("NewLayerHistoryMemento", null, workspace, index));
            document.Layers[index] = layer;
            workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, active);
            Push(workspace, history);
        }
        catch { if (!ReferenceEquals(document.Layers[index], layer)) layer.Dispose(); throw; }
        return Describe(index, layer, d, id, 1);
    }

    private static List<(int Index, BitmapLayer Layer, Definition Definition, Item Item)> AllItems(Document document)
    {
        var hits = new List<(int, BitmapLayer, Definition, Item)>();
        for (int i = 0; i < document.Layers.Count; i++)
            if (document.Layers[i] is BitmapLayer layer && Read(layer) is { } d)
                hits.AddRange(d.Items.Select(item => (i, layer, d, item)));
        return hits;
    }

    private static (Document, int, BitmapLayer, Definition, Item) Find(object workspace, string? id)
    {
        var document = (Document)Property(workspace, "Document");
        var hits = AllItems(document).Where(h => h.Item.Id == id).ToList();
        if (hits.Count == 0) throw new ArgumentException("No annotation '" + id + "'; see list_annotations.");
        if (hits.Count > 1) throw new InvalidOperationException("'" + id + "' exists on layers " + string.Join(", ", hits.Select(h => h.Index)) + " (a duplicated layer); delete one copy first.");
        var (index, layer, definition, item) = hits[0];
        return (document, index, layer, definition, item);
    }

    private static Definition? Read(BitmapLayer layer)
    {
        var json = layer.Metadata.GetUserValue(MetadataKey);
        if (json is null) return null;
        var d = JsonSerializer.Deserialize<Definition>(json);
        if (d is null || d.SchemaVersion != 1 || d.Items is null) throw new InvalidOperationException("Unsupported or invalid MCP annotation metadata.");
        return d;
    }

    private static bool Modified(BitmapLayer layer, Definition d) =>
        layer.Width != d.Width || layer.Height != d.Height || TextLayers.Hash(layer.Surface) != d.PixelHash;

    private static object Describe(int index, BitmapLayer layer, Definition d, string? id, int steps)
    {
        var bounds = Render(d, null);
        return new
        {
            LayerIndex = index, layer.Name, Id = id, PixelsModified = Modified(layer, d), HistorySteps = steps,
            Items = d.Items.Select(i => new
            {
                i.Id, i.Type, i.Properties, i.From, i.To, i.Target,
                Bounds = new { bounds[i.Id].X, bounds[i.Id].Y, bounds[i.Id].Width, bounds[i.Id].Height }
            }).ToList()
        };
    }
}
