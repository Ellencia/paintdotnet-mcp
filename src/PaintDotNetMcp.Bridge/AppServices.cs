using System.Reflection;
using PaintDotNet.Effects;

namespace PaintDotNetMcp.Bridge;

// Reflection-based bridge to Paint.NET's internal services (DocumentWorkspace, AppWorkspace,
// EffectsRegistry, etc.). The public PaintDotNet.* SDK only exposes the Effect render path;
// to add/delete layers, save .pdn files, or invoke built-in effects we have to walk the
// IServiceProvider that lives on every Effect instance.
//
// Strategy:
//   - Cache the first non-null Services we see.
//   - Probe common type names; the first match wins. Names that move across Paint.NET versions
//     are listed in the candidate arrays — adding a name there is a one-line fix.
//   - Every public helper returns (success, payload, note). The note explains why a lookup
//     failed so users can paste it into a bug report.
//
// IMPORTANT: This file is best-effort. Paint.NET 5 minor releases may rename internal types
// without warning. When something stops working, run `ping` — the response now includes a
// `Probe` block listing which services resolved and which didn't.
internal static class AppServices
{
    private static IServiceProvider? _services;
    private static readonly object _gate = new();

    // Cached resolved instances keyed by short logical name.
    private static readonly Dictionary<string, object?> _cache = new();

    public static IServiceProvider? Services => _services;

    /// <summary>Cache the Effect.Services if we haven't already.</summary>
    public static void Capture(object? effect)
    {
        if (effect is null) return;
        if (_services is not null) return;
        try
        {
            var prop = effect.GetType().GetProperty("Services", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var sp = prop?.GetValue(effect) as IServiceProvider;
            if (sp is null) return;
            lock (_gate) { _services ??= sp; }
        }
        catch { }
    }

    // -------- Generic lookups ------------------------------------------------

    /// <summary>Try to resolve a service by walking a list of candidate type names.</summary>
    public static object? Resolve(string cacheKey, params string[] candidateTypeNames)
    {
        if (_services is null) return null;
        lock (_gate)
        {
            if (_cache.TryGetValue(cacheKey, out var hit)) return hit;
            foreach (var name in candidateTypeNames)
            {
                var t = FindType(name);
                if (t is null) continue;
                try
                {
                    var svc = _services.GetService(t);
                    if (svc is not null) { _cache[cacheKey] = svc; return svc; }
                }
                catch { }
            }
            _cache[cacheKey] = null;
            return null;
        }
    }

    /// <summary>
    /// Dump every public+nonpublic instance property and field of a target object, recording
    /// member name, declared type, and runtime value type (or "null"). Skips trivially noisy
    /// members (compiler-generated, indexers) and value types we don't care about.
    /// </summary>
    private static void DumpMembers(object target, List<string> sink, string prefix)
    {
        var t = target.GetType();
        sink.Add(prefix + "<self> : " + (t.FullName ?? t.Name));
        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        // Properties.
        foreach (var p in t.GetProperties(flags))
        {
            if (p.GetIndexParameters().Length > 0) continue; // indexer
            string? runtime = null;
            try
            {
                var v = p.GetValue(target);
                runtime = v is null ? "null" : (v.GetType().FullName ?? v.GetType().Name);
            }
            catch (Exception ex) { runtime = "<throw: " + ex.GetType().Name + ">"; }
            sink.Add(prefix + p.Name + " {prop} : " + (p.PropertyType.FullName ?? p.PropertyType.Name) + " => " + runtime);
        }
        // Fields.
        foreach (var f in t.GetFields(flags))
        {
            if (f.Name.StartsWith("<")) continue; // compiler-generated backing fields
            string? runtime = null;
            try
            {
                var v = f.GetValue(target);
                runtime = v is null ? "null" : (v.GetType().FullName ?? v.GetType().Name);
            }
            catch (Exception ex) { runtime = "<throw: " + ex.GetType().Name + ">"; }
            sink.Add(prefix + f.Name + " {field} : " + (f.FieldType.FullName ?? f.FieldType.Name) + " => " + runtime);
        }
    }

    public static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName); if (t is not null) return t; }
            catch { }
        }
        return null;
    }

    // -------- Well-known objects --------------------------------------------
    //
    // Discovery path (confirmed via diagnose_services dump in PdN 5.x):
    //
    //   PaintDotNet.Program.Instance              (static)
    //       .MainForm                              (PaintDotNet.Dialogs.MainForm)
    //           .appWorkspace                      (PaintDotNet.Controls.AppWorkspace, field)
    //               .ActiveDocumentWorkspace?      (PaintDotNet.Controls.DocumentWorkspace)
    //                   .Document                  (PaintDotNet.Document)
    //                       .Layers
    //
    // MainForm.InnerMostActiveContainerControl is also typed DocumentWorkspace and is a
    // shortcut to the same instance — used as a fallback.

    public static object? GetMainForm()
    {
        if (_cache.TryGetValue("mainForm", out var hit)) return hit;
        object? mf = null;
        var programType = FindType("PaintDotNet.Program");
        if (programType is not null)
        {
            var program =
                programType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? programType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            if (program is not null)
            {
                mf = GetPropertyValue(program, "MainForm");
                if (mf is null)
                {
                    var f = program.GetType().GetField("mainForm", BindingFlags.NonPublic | BindingFlags.Instance);
                    mf = f?.GetValue(program);
                }
            }
        }
        lock (_gate) _cache["mainForm"] = mf;
        return mf;
    }

    /// <summary>The PaintDotNet.Controls.AppWorkspace — owner of tabs/document workspaces.</summary>
    public static object? AppWorkspaceService()
    {
        if (_cache.TryGetValue("appws", out var hit)) return hit;
        object? aw = null;
        var mf = GetMainForm();
        if (mf is not null)
        {
            var f = mf.GetType().GetField("appWorkspace", BindingFlags.NonPublic | BindingFlags.Instance);
            aw = f?.GetValue(mf);
            if (aw is null) aw = GetPropertyValue(mf, "AppWorkspace");
        }
        lock (_gate) _cache["appws"] = aw;
        return aw;
    }

    /// <summary>The currently active DocumentWorkspace (the tab the user is editing).</summary>
    public static object? DocumentWorkspaceService()
    {
        if (_cache.TryGetValue("docws", out var hit)) return hit;
        object? dw = null;
        var aw = AppWorkspaceService();
        if (aw is not null)
        {
            dw = GetPropertyValue(aw, "ActiveDocumentWorkspace", "DocumentWorkspace", "ActiveWorkspace");
        }
        if (dw is null)
        {
            // Fallback: MainForm.InnerMostActiveContainerControl points straight at DocumentWorkspace.
            var mf = GetMainForm();
            if (mf is not null)
            {
                dw = GetPropertyValue(mf, "InnerMostActiveContainerControl");
                if (dw is not null && dw.GetType().Name.Contains("DocumentWorkspace") == false)
                {
                    dw = null; // wrong type — bail
                }
            }
        }
        lock (_gate) _cache["docws"] = dw;
        return dw;
    }

    public static object? EffectsService() => Resolve(
        "fx",
        "PaintDotNet.Effects.IEffectsService",
        "PaintDotNet.Effects.IEffectsService2",
        "PaintDotNet.Effects.EffectsCollection");

    /// <summary>The live document on the active tab.</summary>
    public static object? ActiveDocument()
    {
        var ws = DocumentWorkspaceService();
        if (ws is null) return null;
        return GetPropertyValue(ws, "Document", "ActiveDocument", "CurrentDocument");
    }

    /// <summary>The currently selected layer.</summary>
    public static object? ActiveLayer()
    {
        var ws = DocumentWorkspaceService();
        if (ws is null) return null;
        var v = GetPropertyValue(ws, "ActiveLayer", "CurrentLayer", "SelectedLayer");
        if (v is not null) return v;
        var doc = ActiveDocument();
        if (doc is null) return null;
        return GetPropertyValue(doc, "ActiveLayer", "CurrentLayer", "SelectedLayer");
    }

    // -------- Property/method helpers ---------------------------------------

    public static object? GetPropertyValue(object target, params string[] names) => GetPropertyValue(target, (IEnumerable<string>)names);
    public static object? GetPropertyValue(object target, IEnumerable<string> names)
    {
        var t = target.GetType();
        foreach (var n in names)
        {
            var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p is null) continue;
            try { return p.GetValue(target); }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// Surface the inner exception when a reflection call wraps the real error in
    /// TargetInvocationException. Returns the shortest meaningful message.
    /// </summary>
    public static string Unwrap(Exception ex)
    {
        var e = ex;
        while (e is TargetInvocationException tie && tie.InnerException is not null)
            e = tie.InnerException;
        return e.GetType().Name + ": " + (e.Message ?? "");
    }

    /// <summary>
    /// Run an action on the UI thread of the MainForm. WinForms forbids cross-thread mutation
    /// of Controls and most Paint.NET document/layer operations transitively touch UI state.
    /// Returns true if the invoke succeeded; false if no MainForm or invocation failed.
    /// </summary>
    public static bool InvokeOnUiThread(Action action, out string note)
    {
        note = "";
        var mf = GetMainForm();
        if (mf is null) { note = "no MainForm"; return false; }
        try
        {
            var invokeRequired = mf.GetType().GetProperty("InvokeRequired", BindingFlags.Public | BindingFlags.Instance)?.GetValue(mf) as bool?;
            if (invokeRequired == false)
            {
                action();
                return true;
            }
            var invokeM = mf.GetType().GetMethod("Invoke", new[] { typeof(Delegate) });
            if (invokeM is null) { note = "MainForm.Invoke(Delegate) not found"; return false; }
            invokeM.Invoke(mf, new object[] { action });
            return true;
        }
        catch (Exception ex) { note = Unwrap(ex); return false; }
    }

    /// <summary>
    /// Fire-and-forget variant: post the action to the UI thread via BeginInvoke and return
    /// immediately. Use for operations that may open modal dialogs (effects, dialogs in general)
    /// where blocking the pipe thread would deadlock the RPC.
    /// </summary>
    public static bool PostOnUiThread(Action action, out string note)
    {
        note = "";
        var mf = GetMainForm();
        if (mf is null) { note = "no MainForm"; return false; }
        try
        {
            var beginInvokeM = mf.GetType().GetMethod("BeginInvoke", new[] { typeof(Delegate) });
            if (beginInvokeM is null) { note = "MainForm.BeginInvoke(Delegate) not found"; return false; }
            beginInvokeM.Invoke(mf, new object[] { action });
            return true;
        }
        catch (Exception ex) { note = Unwrap(ex); return false; }
    }

    public static MethodInfo? FindMethod(Type type, IEnumerable<string> namesContaining, int? expectedParams = null)
    {
        foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (expectedParams.HasValue && m.GetParameters().Length != expectedParams.Value) continue;
            foreach (var n in namesContaining)
            {
                if (m.Name.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return m;
            }
        }
        return null;
    }

    public static object? InvokeMethod(object target, string nameContains, params object?[] args)
    {
        var m = FindMethod(target.GetType(), new[] { nameContains }, args.Length);
        return m?.Invoke(target, args);
    }

    /// <summary>Diagnostic report — for the ping response.</summary>
    public static Dictionary<string, bool> Probe()
    {
        var r = new Dictionary<string, bool>();
        r["servicesCaptured"]     = _services is not null;
        r["docWorkspaceFound"]    = DocumentWorkspaceService() is not null;
        r["appWorkspaceFound"]    = AppWorkspaceService() is not null;
        r["effectsServiceFound"]  = EffectsService() is not null;
        r["activeDocumentFound"]  = ActiveDocument() is not null;
        r["activeLayerFound"]     = ActiveLayer() is not null;
        return r;
    }

    public sealed class DiagnoseResult
    {
        public string ServiceContainerType { get; set; } = "";
        /// <summary>Interface types in loaded PaintDotNet.* assemblies that look like host services.</summary>
        public List<string> CandidateInterfaces { get; set; } = new();
        /// <summary>The subset of candidates that actually resolved through services.GetService(t).</summary>
        public List<string> RegisteredServices { get; set; } = new();
        /// <summary>For each registered service, properties that look layer/document related.</summary>
        public List<string> InterestingProperties { get; set; } = new();
        /// <summary>Static properties on PaintDotNet types whose runtime value is non-null. Each entry: "Type.Property : RuntimeType".</summary>
        public List<string> StaticEntryPoints { get; set; } = new();
        /// <summary>If a WPF Application.Current is running, its MainWindow type and DataContext type.</summary>
        public string WpfApplicationCurrent { get; set; } = "";
        public string WpfMainWindowType { get; set; } = "";
        public string WpfMainWindowDataContext { get; set; } = "";
        /// <summary>Properties on the main window (or its DataContext) that look workspace/document-related.</summary>
        public List<string> WpfMainWindowProperties { get; set; } = new();
        /// <summary>Instance members of PaintDotNet.Program.Instance — host root for WinForms.</summary>
        public List<string> ProgramInstanceMembers { get; set; } = new();
        /// <summary>Type of each entry in System.Windows.Forms.Application.OpenForms.</summary>
        public List<string> OpenForms { get; set; } = new();
        /// <summary>Members of OpenForms[0] (typically the MainForm).</summary>
        public List<string> MainFormMembers { get; set; } = new();
        /// <summary>Members of MainForm.appWorkspace (PaintDotNet.Controls.AppWorkspace).</summary>
        public List<string> AppWorkspaceMembers { get; set; } = new();
        /// <summary>Members of the active DocumentWorkspace.</summary>
        public List<string> DocumentWorkspaceMembers { get; set; } = new();
        /// <summary>Members of the active Document (if reachable).</summary>
        public List<string> DocumentMembers { get; set; } = new();
    }

    /// <summary>
    /// Deep diagnostic: walks every loaded PaintDotNet assembly, lists candidate service interfaces
    /// (anything with Document/Workspace/Layer/Effect/App in the name), then asks the IServiceProvider
    /// for each candidate. Returns the ones that actually resolve. Used to fill in the candidate-name
    /// arrays in DocumentWorkspaceService() etc. without guessing.
    /// </summary>
    public static DiagnoseResult Diagnose()
    {
        var result = new DiagnoseResult();
        if (_services is null) return result;
        result.ServiceContainerType = _services.GetType().FullName ?? "";

        var keywords = new[] { "Document", "Workspace", "Layer", "Effect", "App", "Imaging", "Selection", "Canvas" };
        var seen = new HashSet<string>();
        var candidates = new List<Type>();

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name ?? "";
            if (!asmName.StartsWith("PaintDotNet", StringComparison.OrdinalIgnoreCase)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                if (!t.IsInterface) continue;
                if (!t.IsPublic && !t.IsNestedPublic && !t.IsNotPublic) continue;
                var name = t.Name;
                if (!keywords.Any(k => name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                if (!seen.Add(t.FullName ?? t.Name)) continue;
                candidates.Add(t);
            }
        }
        candidates.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
        foreach (var t in candidates) result.CandidateInterfaces.Add(t.FullName ?? t.Name);

        // Ask the service provider for each candidate.
        foreach (var t in candidates)
        {
            try
            {
                var svc = _services.GetService(t);
                if (svc is null) continue;
                result.RegisteredServices.Add(t.FullName ?? t.Name);
                foreach (var p in svc.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    var pn = p.Name;
                    if (pn.IndexOf("Document", StringComparison.OrdinalIgnoreCase) < 0 &&
                        pn.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) < 0 &&
                        pn.IndexOf("Active", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    result.InterestingProperties.Add(
                        (t.Name) + "." + pn + " : " + (p.PropertyType.FullName ?? p.PropertyType.Name));
                }
            }
            catch { }
        }

        // --- Static entry points -----------------------------------------------------------
        // The effects-scoped service provider hides the live document. The real workspace is
        // typically reachable through a static accessor somewhere (e.g. `AppWorkspace.Current`,
        // `App.Instance`). Walk every PaintDotNet type and look for non-null static getters
        // whose name signals a host root.
        var staticKeywords = new[] { "Current", "Instance", "Application", "App", "Workspace", "MainForm", "MainWindow", "Active" };
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var asmName = asm.GetName().Name ?? "";
            if (!asmName.StartsWith("PaintDotNet", StringComparison.OrdinalIgnoreCase)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!staticKeywords.Any(k => p.Name.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                                                  p.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) == 0))
                        continue;
                    try
                    {
                        var val = p.GetValue(null);
                        if (val is null) continue;
                        result.StaticEntryPoints.Add(
                            (t.FullName ?? t.Name) + "." + p.Name + " : " + (val.GetType().FullName ?? val.GetType().Name));
                    }
                    catch { }
                }
                foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (!staticKeywords.Any(k => f.Name.Equals(k, StringComparison.OrdinalIgnoreCase) ||
                                                  f.Name.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0))
                        continue;
                    try
                    {
                        var val = f.GetValue(null);
                        if (val is null) continue;
                        result.StaticEntryPoints.Add(
                            (t.FullName ?? t.Name) + "." + f.Name + " (field) : " + (val.GetType().FullName ?? val.GetType().Name));
                    }
                    catch { }
                }
            }
        }

        // --- PaintDotNet.Program.Instance deep dive -----------------------------------------
        // This is the only static accessor pointing at a live host object. From here we expect
        // a chain like Program → MainForm → AppWorkspace → ActiveDocumentWorkspace → Document.
        try
        {
            var programType = FindType("PaintDotNet.Program");
            object? program = null;
            if (programType is not null)
            {
                program = programType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                       ?? programType.GetField("instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            }
            if (program is not null)
            {
                DumpMembers(program, result.ProgramInstanceMembers, prefix: "Program.");
            }
        }
        catch { }

        // --- System.Windows.Forms.Application.OpenForms ------------------------------------
        // Try WinForms first (PdN 4 lineage), then fall back to anything that looks like a host
        // window enumeration. If Paint.NET 5 has moved to WinUI 3 or a custom shell, the WinForms
        // probe will simply return an empty list and we'll need yet another probe.
        try
        {
            var appType = FindType("System.Windows.Forms.Application");
            var openForms = appType?.GetProperty("OpenForms", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as System.Collections.IEnumerable;
            if (openForms is not null)
            {
                int idx = 0;
                object? mainForm = null;
                foreach (var f in openForms)
                {
                    if (f is null) continue;
                    var ft = f.GetType();
                    result.OpenForms.Add("[" + idx + "] " + (ft.FullName ?? ft.Name));
                    if (mainForm is null) mainForm = f;
                    idx++;
                }
                if (mainForm is not null)
                {
                    DumpMembers(mainForm, result.MainFormMembers, prefix: "MainForm.");
                }
            }
        }
        catch { }

        // --- AppWorkspace / DocumentWorkspace / Document deep dive --------------------------
        try
        {
            var aw = AppWorkspaceService();
            if (aw is not null) DumpMembers(aw, result.AppWorkspaceMembers, prefix: "AppWorkspace.");
        }
        catch { }
        try
        {
            var dw = DocumentWorkspaceService();
            if (dw is not null) DumpMembers(dw, result.DocumentWorkspaceMembers, prefix: "DocumentWorkspace.");
        }
        catch { }
        try
        {
            var doc = ActiveDocument();
            if (doc is not null) DumpMembers(doc, result.DocumentMembers, prefix: "Document.");
        }
        catch { }

        // --- WPF Application.Current (kept for completeness; usually empty in PdN 5) -------
        // Paint.NET 5 is WPF. Application.Current.MainWindow is the host window; its DataContext
        // (or a property on it) often exposes the workspace root.
        try
        {
            var appType = FindType("System.Windows.Application");
            var current = appType?.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            if (current is not null)
            {
                result.WpfApplicationCurrent = current.GetType().FullName ?? current.GetType().Name;
                var mainWin = current.GetType().GetProperty("MainWindow")?.GetValue(current);
                if (mainWin is not null)
                {
                    result.WpfMainWindowType = mainWin.GetType().FullName ?? mainWin.GetType().Name;
                    var dc = mainWin.GetType().GetProperty("DataContext")?.GetValue(mainWin);
                    if (dc is not null) result.WpfMainWindowDataContext = dc.GetType().FullName ?? dc.GetType().Name;

                    foreach (var target in new[] { mainWin, dc }.Where(o => o is not null).Select(o => o!))
                    {
                        var tt = target.GetType();
                        foreach (var p in tt.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        {
                            var pn = p.Name;
                            if (pn.IndexOf("Workspace", StringComparison.OrdinalIgnoreCase) < 0 &&
                                pn.IndexOf("Document", StringComparison.OrdinalIgnoreCase) < 0 &&
                                pn.IndexOf("Active", StringComparison.OrdinalIgnoreCase) < 0 &&
                                pn.IndexOf("Layer", StringComparison.OrdinalIgnoreCase) < 0)
                                continue;
                            try
                            {
                                var v = p.GetValue(target);
                                var label = (tt.Name) + "." + pn + " : " + (p.PropertyType.FullName ?? p.PropertyType.Name);
                                if (v is not null) label += " => " + (v.GetType().FullName ?? v.GetType().Name);
                                result.WpfMainWindowProperties.Add(label);
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        return result;
    }
}
