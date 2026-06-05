using System.IO;
using System.Reflection;

namespace PaintDotNetMcp.Bridge;

// Save the active document as a .pdn file by invoking PaintDotNet.FileType.Save on the
// PdnFileType instance discovered via FileTypesCollection.Instance. The Save signature has
// been stable since Paint.NET 3.5x:
//
//   FileType.Save(Document input, Stream output, SaveConfigToken token,
//                 Surface scratchSurface, ProgressEventHandler callback, bool rememberToken)
//
// Different minor versions add/remove a parameter, so we pick the longest matching
// (Document, Stream, SaveConfigToken, ...) overload and pad with defaults.
internal static class SavePdn
{
    public sealed record SaveResult(bool Ok, string Note, long Bytes = 0, string? Path = null);

    public static SaveResult Save(string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) return new(false, "path required");

        var ws = AppServices.DocumentWorkspaceService();
        if (ws is null) return new(false, "no DocumentWorkspace");
        var doc = AppServices.ActiveDocument();
        if (doc is null) return new(false, "no active document");

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // ---- 1. Find the .pdn FileType -----------------------------------------------------
        object? fileType = AppServices.GetPropertyValue(ws, "FileType");
        if (fileType is null || !LooksLikePdnFileType(fileType))
        {
            fileType = FindPdnFileType(out var ftNote);
            if (fileType is null)
                return new(false, "no .pdn FileType: " + ftNote);
        }

        // ---- 2. Get a save config token ----------------------------------------------------
        object? token = null;
        var tokenField = ws.GetType().GetField("saveConfigToken", BindingFlags.NonPublic | BindingFlags.Instance);
        if (tokenField is not null) token = tokenField.GetValue(ws);
        if (token is null)
        {
            try
            {
                var createM = AppServices.FindMethod(fileType.GetType(), new[] { "CreateDefaultSaveConfigToken" }, 0)
                           ?? AppServices.FindMethod(fileType.GetType(), new[] { "GetDefaultSaveConfigToken" }, 0);
                token = createM?.Invoke(fileType, null);
            }
            catch (Exception ex) { return new(false, "CreateDefaultSaveConfigToken threw: " + AppServices.Unwrap(ex), 0, targetPath); }
        }
        // Some versions accept null token (uses internal default); we proceed regardless.

        // ---- 3. Locate the longest (Document, Stream, ...) Save overload ------------------
        var saveCandidates = fileType.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "Save" || m.Name == "SaveDocument")
            .Where(m =>
            {
                var ps = m.GetParameters();
                return ps.Length >= 2
                    && (ps[0].ParameterType.Name == "Document" || ps[0].ParameterType.Name.EndsWith("Document"))
                    && typeof(Stream).IsAssignableFrom(ps[1].ParameterType);
            })
            .OrderByDescending(m => m.GetParameters().Length)
            .ToList();
        if (saveCandidates.Count == 0)
        {
            // Last resort — log what's actually on the FileType so the next iteration can adapt.
            var inspected = string.Join(", ", fileType.GetType()
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name.Contains("Save", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
            return new(false, "no FileType.Save(Document, Stream, ...) overload found; methods inspected: [" + inspected + "]", 0, targetPath);
        }

        // ---- 4. Invoke (on UI thread) ------------------------------------------------------
        long bytesWritten = 0;
        string note = "";
        bool ok = false;
        AppServices.InvokeOnUiThread(() =>
        {
            foreach (var m in saveCandidates)
            {
                var ps = m.GetParameters();
                try
                {
                    using var fs = File.Create(targetPath);
                    var args = new object?[ps.Length];
                    args[0] = doc;
                    args[1] = fs;
                    if (ps.Length >= 3) args[2] = token; // SaveConfigToken (may be null)
                    for (int i = 3; i < ps.Length; i++)
                    {
                        args[i] = ps[i].ParameterType.IsValueType
                            ? Activator.CreateInstance(ps[i].ParameterType)
                            : null;
                    }
                    m.Invoke(fileType, args);
                    fs.Flush();
                    bytesWritten = fs.Length;
                    ok = true;
                    note = "saved via FileType." + m.Name + "(" + ps.Length + " args)";
                    break;
                }
                catch (Exception ex)
                {
                    note = m.Name + "(" + ps.Length + " args) threw: " + AppServices.Unwrap(ex);
                    // Delete partial file before the next attempt.
                    try { if (File.Exists(targetPath)) File.Delete(targetPath); } catch { }
                }
            }

            // Update DocumentWorkspace state so the title bar / save-status reflects reality.
            if (ok)
            {
                try
                {
                    var t = ws.GetType();
                    t.GetField("filePath", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(ws, targetPath);
                    t.GetField("fileType", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(ws, fileType);
                    t.GetField("lastSaveTime", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(ws, DateTime.Now);
                }
                catch { /* state sync is cosmetic */ }
            }
        }, out var invokeNote);
        if (!ok && !string.IsNullOrEmpty(invokeNote)) note = "UI invoke failed: " + invokeNote + "; " + note;
        return new(ok, note, bytesWritten, targetPath);
    }

    // ----------------------------------------------------------------------------------------

    private static object? FindPdnFileType(out string note)
    {
        note = "";

        // Strategy A: known concrete type names.
        foreach (var typeName in new[] { "PaintDotNet.Data.PdnFileType", "PaintDotNet.PdnFileType", "PaintDotNet.Data.PdnFile.PdnFileType" })
        {
            var t = AppServices.FindType(typeName);
            if (t is null) continue;
            try
            {
                var ctor = t.GetConstructor(Type.EmptyTypes);
                if (ctor is not null)
                {
                    var inst = ctor.Invoke(null);
                    note = "constructed " + typeName;
                    return inst;
                }
            }
            catch (Exception ex) { note = typeName + " ctor failed: " + AppServices.Unwrap(ex); }
        }

        // Strategy B: walk FileTypesCollection.Instance — try many shapes.
        var fcType = AppServices.FindType("PaintDotNet.Data.FileTypesCollection");
        var instance = fcType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (instance is null) { note += "; FileTypesCollection.Instance is null"; return null; }

        // Collect all candidate enumerables to walk.
        var enumerables = new List<System.Collections.IEnumerable>();
        if (instance is System.Collections.IEnumerable rootEnum) enumerables.Add(rootEnum);
        foreach (var pname in new[] { "FileTypes", "Items", "All", "Saving", "Loading", "Savers", "Loaders", "SaveFileTypes", "LoadFileTypes" })
        {
            if (AppServices.GetPropertyValue(instance, pname) is System.Collections.IEnumerable e) enumerables.Add(e);
        }
        var seenTypes = new List<string>();
        foreach (var e in enumerables)
        {
            foreach (var item in e)
            {
                if (item is null) continue;
                seenTypes.Add(item.GetType().FullName ?? item.GetType().Name);
                if (LooksLikePdnFileType(item)) { note = "found via FileTypesCollection enumeration"; return item; }
                // Item may be a factory; try to ask it for an instance.
                foreach (var mname in new[] { "GetInstance", "Create", "CreateInstance", "Instance" })
                {
                    var inner = AppServices.GetPropertyValue(item, mname);
                    if (inner is null)
                    {
                        var m = AppServices.FindMethod(item.GetType(), new[] { mname }, 0);
                        try { inner = m?.Invoke(item, null); } catch { }
                    }
                    if (inner is not null && LooksLikePdnFileType(inner))
                    {
                        note = "found via " + item.GetType().Name + "." + mname;
                        return inner;
                    }
                }
            }
        }

        note += "; FileTypesCollection saw [" + string.Join(", ", seenTypes.Take(20)) + (seenTypes.Count > 20 ? ", ..." : "") + "] (" + seenTypes.Count + " total)";
        return null;
    }

    private static bool LooksLikePdnFileType(object ft)
    {
        try
        {
            var t = ft.GetType();
            if (t.Name.Contains("Pdn", StringComparison.OrdinalIgnoreCase) && t.Name.EndsWith("FileType", StringComparison.OrdinalIgnoreCase))
                return true;
            var exts = AppServices.GetPropertyValue(ft, "Extensions") as System.Collections.IEnumerable;
            if (exts is null) return false;
            foreach (var e in exts)
            {
                var s = e?.ToString();
                if (s is not null && s.Equals(".pdn", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        catch { }
        return false;
    }
}
