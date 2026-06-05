using System.Drawing;
using System.Reflection;

namespace PaintDotNetMcp.Bridge;

// Selection manipulation via reflection on DocumentWorkspace.
//
// Paint.NET 5 exposes selection through a property like "Selection" on the workspace, with
// methods such as SetContinuation / PerformChanging / PerformChanged or PathGeometry assignment.
// We probe candidate APIs. If none match we fall back to a software-side selection mask that
// the bridge respects when drawing — see SoftSelection.
internal static class SelectionOps
{
    public sealed record OpResult(bool Ok, string Note);

    /// <summary>
    /// Software-side selection mask. Used when the bridge can't talk to Paint.NET's native
    /// selection. Drawing ops check this and clip to it. Cleared with ClearSelection.
    /// </summary>
    public static class SoftSelection
    {
        private static readonly object _gate = new();
        private static Rectangle? _rect;
        private static List<Point>? _polygon;
        private static string? _kind; // "rect" | "polygon" | null

        public static bool HasSelection { get { lock (_gate) return _kind is not null; } }
        public static string? Kind { get { lock (_gate) return _kind; } }
        public static Rectangle? Rect { get { lock (_gate) return _rect; } }
        public static List<Point>? Polygon { get { lock (_gate) return _polygon; } }

        public static void SetRect(int x, int y, int w, int h)
        {
            lock (_gate) { _rect = new Rectangle(x, y, w, h); _polygon = null; _kind = "rect"; }
        }
        public static void SetPolygon(List<Point> pts)
        {
            lock (_gate) { _polygon = new List<Point>(pts); _rect = null; _kind = "polygon"; }
        }
        public static void Clear()
        {
            lock (_gate) { _rect = null; _polygon = null; _kind = null; }
        }

        public static bool Contains(int x, int y)
        {
            lock (_gate)
            {
                if (_kind == "rect")
                {
                    var r = _rect!.Value;
                    return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
                }
                if (_kind == "polygon" && _polygon is not null)
                {
                    return PointInPolygon(_polygon, x, y);
                }
                return true; // no selection = whole canvas
            }
        }

        private static bool PointInPolygon(List<Point> poly, int x, int y)
        {
            bool inside = false;
            for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
            {
                if (((poly[i].Y > y) != (poly[j].Y > y)) &&
                    (x < (poly[j].X - poly[i].X) * (y - poly[i].Y) / (double)(poly[j].Y - poly[i].Y) + poly[i].X))
                    inside = !inside;
            }
            return inside;
        }
    }

    public static OpResult SetRectangle(int x, int y, int w, int h)
    {
        // Try native first.
        if (TryNativeSetRect(x, y, w, h, out var note)) return new(true, "native: " + note);
        SoftSelection.SetRect(x, y, w, h);
        return new(true, "software-side selection (drawing ops respect it; Paint.NET UI won't show it)");
    }

    public static OpResult SetPolygon(List<Point> pts)
    {
        // Native polygon selection is far harder via reflection; just use soft.
        SoftSelection.SetPolygon(pts);
        return new(true, "software-side polygon selection");
    }

    public static OpResult Clear()
    {
        TryNativeClear(out _);
        SoftSelection.Clear();
        return new(true, "cleared");
    }

    // -------- Native selection attempt (best-effort) -------------------------

    private static bool TryNativeSetRect(int x, int y, int w, int h, out string note)
    {
        note = "";
        var ws = AppServices.DocumentWorkspaceService();
        if (ws is null) { note = "no DocumentWorkspaceService"; return false; }
        var sel = AppServices.GetPropertyValue(ws, "Selection");
        if (sel is null) { note = "no Selection property"; return false; }

        // Try Selection.PerformChanging() -> SetContinuation(Rect) -> PerformChanged() pattern.
        try
        {
            var t = sel.GetType();
            var changing = AppServices.FindMethod(t, new[] { "PerformChanging" }, 0);
            var changed  = AppServices.FindMethod(t, new[] { "PerformChanged" }, 0);
            var setRect  = AppServices.FindMethod(t, new[] { "SetContinuation", "Set" }, 2);
            if (setRect is null)
            {
                note = "no SetContinuation method";
                return false;
            }
            var rect = new Rectangle(x, y, w, h);
            changing?.Invoke(sel, null);
            // setRect signature varies; try (Rectangle, CombineMode-or-something).
            var ps = setRect.GetParameters();
            object? combine = null;
            if (ps.Length == 2 && ps[1].ParameterType.IsEnum)
            {
                try { combine = Enum.GetValues(ps[1].ParameterType).GetValue(0); } catch { }
            }
            setRect.Invoke(sel, new object?[] { rect, combine });
            changed?.Invoke(sel, null);
            note = "Selection.SetContinuation";
            return true;
        }
        catch (Exception ex) { note = "native set threw: " + ex.Message; return false; }
    }

    private static bool TryNativeClear(out string note)
    {
        note = "";
        var ws = AppServices.DocumentWorkspaceService();
        if (ws is null) return false;
        var sel = AppServices.GetPropertyValue(ws, "Selection");
        if (sel is null) return false;
        try
        {
            var m = AppServices.FindMethod(sel.GetType(), new[] { "Reset", "Clear", "None" }, 0);
            if (m is null) return false;
            m.Invoke(sel, null);
            note = "Selection." + m.Name;
            return true;
        }
        catch (Exception ex) { note = ex.Message; return false; }
    }
}
