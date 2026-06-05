using System.Reflection;
using PaintDotNet;

namespace PaintDotNetMcp.Bridge;

// Layer enumeration / add / delete / select via reflection on the live Document.
//
// All mutations run on the WinForms UI thread via AppServices.InvokeOnUiThread because
// Paint.NET's Document/Layers collections assert single-threaded access. Inner exceptions
// are unwrapped from TargetInvocationException so callers see the real error.
internal static class LayerOps
{
    public sealed record LayerInfo(int Index, string Name, int Width, int Height, bool IsActive, bool IsVisible, double Opacity);
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
                list.Add(new LayerInfo(i, name, w, h, isActive, vis, opacityRaw));
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
}
