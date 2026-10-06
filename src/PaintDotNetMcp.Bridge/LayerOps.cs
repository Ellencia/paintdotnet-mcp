using System.Reflection;
using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

// Layer enumeration / add / delete / select via reflection on the live Document.
//
// All mutations run on the WinForms UI thread via AppServices.InvokeOnUiThread because
// Paint.NET's Document/Layers collections assert single-threaded access. Inner exceptions
// are unwrapped from TargetInvocationException so callers see the real error.
internal static class LayerOps
{
    public sealed record LayerInfo(int Index, string Name, int Width, int Height, bool IsActive, bool IsVisible, double Opacity, string BlendMode);
    public sealed record OpResult(bool Ok, string Note, object? Data = null);

    public static OpResult List()
    {
        var doc = AppServices.ActiveDocument();
        if (doc is null) return new(false, "no active document (open a file then run Effects > Tools > MCP Bridge once)");
        var layers = AppServices.GetPropertyValue(doc, "Layers") as System.Collections.IEnumerable;
        if (layers is null) return new(false, "Document.Layers not found via reflection");

        var active = AppServices.ActiveLayer();
        var list = new List<LayerInfo>();
        int i = 0;
        foreach (var layer in layers)
        {
            try
            {
                string name = (AppServices.GetPropertyValue(layer, "Name") as string) ?? ("Layer " + i);
                int w = (AppServices.GetPropertyValue(layer, "Width") as int?) ?? 0;
                int h = (AppServices.GetPropertyValue(layer, "Height") as int?) ?? 0;
                bool vis = (AppServices.GetPropertyValue(layer, "Visible") as bool?) ?? true;
                double opacityRaw = 1.0;
                var op = AppServices.GetPropertyValue(layer, "Opacity");
                if (op is byte b) opacityRaw = b / 255.0;
                else if (op is double d) opacityRaw = d;
                else if (op is float f) opacityRaw = f;
                bool isActive = ReferenceEquals(layer, active);
                string blend = AppServices.GetPropertyValue(layer, "BlendMode")?.ToString() ?? "Normal";
                list.Add(new LayerInfo(i, name, w, h, isActive, vis, opacityRaw, blend));
            }
            catch { }
            i++;
        }
        return new(true, "ok", list);
    }

    public static OpResult Add(string name)
    {
        var doc = AppServices.ActiveDocument();
        if (doc is null) return new(false, "no active document");

        int w = (AppServices.GetPropertyValue(doc, "Width") as int?) ?? 0;
        int h = (AppServices.GetPropertyValue(doc, "Height") as int?) ?? 0;
        if (w <= 0 || h <= 0) return new(false, "document dimensions unknown");

        var blType = AppServices.FindType("PaintDotNet.BitmapLayer");
        if (blType is null) return new(false, "PaintDotNet.BitmapLayer type not found");

        string note = "";
        bool ok = false;
        AppServices.InvokeOnUiThread(() =>
        {
            try
            {
                var ctor = blType.GetConstructor(new[] { typeof(int), typeof(int) });
                if (ctor is null) { note = "BitmapLayer(int,int) ctor not found"; return; }
                var newLayer = ctor.Invoke(new object[] { w, h });
                try { blType.GetProperty("Name")?.SetValue(newLayer, name); } catch { }

                var layers = AppServices.GetPropertyValue(doc, "Layers");
                if (layers is null) { note = "Document.Layers not found"; return; }
                var addM = AppServices.FindMethod(layers.GetType(), new[] { "Add" }, 1);
                if (addM is null) { note = "Layers.Add method not found"; return; }
                addM.Invoke(layers, new[] { newLayer });
                ok = true;
                note = "added layer";
            }
            catch (Exception ex) { note = "add threw: " + AppServices.Unwrap(ex); }
        }, out var invokeNote);
        if (!ok && !string.IsNullOrEmpty(invokeNote)) note = "UI invoke failed: " + invokeNote + "; " + note;
        return new(ok, note, ok ? new { Name = name, Width = w, Height = h } : null);
    }

    public static OpResult Delete(int index)
    {
        var doc = AppServices.ActiveDocument();
        if (doc is null) return new(false, "no active document");
        var layers = AppServices.GetPropertyValue(doc, "Layers");
        if (layers is null) return new(false, "Document.Layers not found");
        int count = (AppServices.GetPropertyValue(layers, "Count") as int?) ?? 0;
        if (count <= 1) return new(false, "cannot remove last remaining layer");
        if (index < 0 || index >= count) return new(false, "index out of range (0.." + (count - 1) + ")");

        string note = "";
        bool ok = false;
        AppServices.InvokeOnUiThread(() =>
        {
            try
            {
                var rm = AppServices.FindMethod(layers.GetType(), new[] { "RemoveAt" }, 1);
                if (rm is null) { note = "Layers.RemoveAt method not found"; return; }
                rm.Invoke(layers, new object[] { index });
                ok = true;
                note = "removed layer " + index;
            }
            catch (Exception ex) { note = "remove threw: " + AppServices.Unwrap(ex); }
        }, out var invokeNote);
        if (!ok && !string.IsNullOrEmpty(invokeNote)) note = "UI invoke failed: " + invokeNote + "; " + note;
        return new(ok, note);
    }

    public static OpResult Select(int index)
    {
        var ws = AppServices.DocumentWorkspaceService();
        if (ws is null) return new(false, "no DocumentWorkspaceService");
        var doc = AppServices.ActiveDocument();
        if (doc is null) return new(false, "no active document");
        var layers = AppServices.GetPropertyValue(doc, "Layers") as System.Collections.IList;
        if (layers is null) return new(false, "Document.Layers not indexable");
        if (index < 0 || index >= layers.Count) return new(false, "index out of range");

        var target = layers[index];
        string note = "";
        bool ok = false;
        AppServices.InvokeOnUiThread(() =>
        {
            try
            {
                var p = ws.GetType().GetProperty("ActiveLayer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (p is not null && p.CanWrite) { p.SetValue(ws, target); ok = true; note = "selected via ActiveLayer setter"; return; }

                var pIdx = ws.GetType().GetProperty("ActiveLayerIndex", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (pIdx is not null && pIdx.CanWrite) { pIdx.SetValue(ws, index); ok = true; note = "selected via ActiveLayerIndex setter"; return; }

                var setM = AppServices.FindMethod(ws.GetType(), new[] { "SetActiveLayer", "SelectLayer" }, 1);
                if (setM is not null) { setM.Invoke(ws, new[] { target }); ok = true; note = "selected via " + setM.Name; return; }

                note = "no writable ActiveLayer property or SetActiveLayer method found";
            }
            catch (Exception ex) { note = "select threw: " + AppServices.Unwrap(ex); }
        }, out var invokeNote);
        if (!ok && !string.IsNullOrEmpty(invokeNote)) note = "UI invoke failed: " + invokeNote + "; " + note;
        return new(ok, note);
    }

    // Same native memento as Paint.NET's Layer Properties dialog: captured before the change, one Undo step.
    public static object SetProperties(SetLayerPropertiesParams p)
    {
        if (p.LayerIndex < -1) throw new ArgumentException("Layer index must be -1 (active) or nonnegative.");
        if (p.Name is not null && (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 256))
            throw new ArgumentException("Layer name must contain 1..256 characters.");
        if (p.Opacity is double o && !(o >= 0 && o <= 1)) throw new ArgumentException("Opacity must be 0..1.");
        LayerBlendMode? blend = null;
        if (p.BlendMode is not null)
        {
            if (!Enum.TryParse(p.BlendMode, true, out LayerBlendMode parsed) || !Enum.IsDefined(parsed) || char.IsDigit(p.BlendMode.TrimStart()[0]))
                throw new ArgumentException("BlendMode must be one of: " + string.Join(", ", Enum.GetNames<LayerBlendMode>()) + ".");
            blend = parsed;
        }
        byte? opacity = p.Opacity is double v ? (byte)Math.Round(v * 255) : null;
        return NativeEditing.Run(workspace =>
        {
            var document = (Document)NativeEditing.Property(workspace, "Document");
            int index = p.LayerIndex == -1 ? (int)NativeEditing.Property(workspace, "ActiveLayerIndex") : p.LayerIndex;
            if (index >= document.Layers.Count) throw new ArgumentException("Layer index out of range (0.." + (document.Layers.Count - 1) + ").");
            var layer = document.Layers[index];
            bool changed = (p.Name is not null && p.Name != layer.Name) || (p.Visible is bool vis && vis != layer.Visible)
                || (opacity is byte op && op != layer.Opacity) || (blend is LayerBlendMode bm && bm != layer.BlendMode);
            if (changed)
            {
                var history = NativeEditing.Memento("LayerPropertyHistoryMemento", "MCP layer properties", workspace, index);
                if (p.Name is not null) layer.Name = p.Name;
                if (p.Visible is bool visible) layer.Visible = visible;
                if (opacity is byte opa) layer.Opacity = opa;
                if (blend is LayerBlendMode mode) layer.BlendMode = mode;
                NativeEditing.Push(workspace, history);
            }
            return new { Ok = true, LayerIndex = index, layer.Name, layer.Visible, Opacity = layer.Opacity / 255.0,
                BlendMode = layer.BlendMode.ToString(), HistorySteps = changed ? 1 : 0 };
        });
    }

    // Paint.NET's own Layers-menu HistoryFunctions, applied the way the menu does: one native Undo step each.
    public static object ApplyFunction(LayerFunctionParams p)
    {
        if (p.Function is not ("duplicate" or "move" or "merge_down" or "flatten"))
            throw new ArgumentException("Function must be duplicate, move, merge_down, or flatten.");
        if (p.LayerIndex < -1) throw new ArgumentException("Layer index must be -1 (active) or nonnegative.");
        if (p.Function == "move" && p.ToIndex < 0) throw new ArgumentException("move requires a nonnegative toIndex.");
        return NativeEditing.Run(workspace =>
        {
            var document = (Document)NativeEditing.Property(workspace, "Document");
            int count = document.Layers.Count;
            int index = p.LayerIndex == -1 ? (int)NativeEditing.Property(workspace, "ActiveLayerIndex") : p.LayerIndex;
            if (index >= count || (p.Function == "move" && p.ToIndex >= count))
                throw new ArgumentException("Layer index out of range (0.." + (count - 1) + ").");
            if (p.Function == "merge_down" && index == 0) throw new ArgumentException("The bottom layer has no layer below to merge into.");
            bool noop = (p.Function == "move" && p.ToIndex == index) || (p.Function == "flatten" && count == 1);
            if (!noop)
            {
                object[] args = p.Function switch
                {
                    "move" => [index, p.ToIndex],
                    "flatten" => [],
                    _ => [index]
                };
                string typeName = p.Function switch
                {
                    "duplicate" => "DuplicateLayerFunction", "move" => "MoveLayerFunction",
                    "merge_down" => "MergeLayerDownFunction", _ => "FlattenFunction"
                };
                // DuplicateLayerFunction clones ActiveLayer but inserts above layerIndex; the menu always passes the active index.
                if (p.Function == "duplicate")
                    workspace.GetType().GetProperty("ActiveLayerIndex")!.SetValue(workspace, index);
                var function = Activator.CreateInstance(AppServices.FindType("PaintDotNet.HistoryFunctions." + typeName)!,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, args, null)!;
                var result = AppServices.FindType("PaintDotNet.Controls.DocumentWorkspaceExtensions")!
                    .GetMethod("ApplyFunction", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [workspace, function])!.ToString();
                if (result != "Success") throw new InvalidOperationException("Paint.NET " + typeName + " returned " + result + ".");
                if (p.Function == "duplicate" && document.Layers[index + 1] is BitmapLayer copy) TextLayers.RenewId(copy);
            }
            return new { Ok = true, Function = p.Function, LayerIndex = index,
                LayerCount = ((Document)NativeEditing.Property(workspace, "Document")).Layers.Count,
                ActiveLayerIndex = (int)NativeEditing.Property(workspace, "ActiveLayerIndex"), HistorySteps = noop ? 0 : 1 };
        });
    }
}
