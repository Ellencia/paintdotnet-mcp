using System.Security.Cryptography;
using System.Text.Json;
using PaintDotNet;
using PaintDotNetMcp.Contracts;
using static PaintDotNetMcp.Bridge.NativeEditing;

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
