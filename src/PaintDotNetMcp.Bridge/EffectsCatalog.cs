using System.Reflection;
using PaintDotNet;
using PaintDotNet.Effects;

namespace PaintDotNetMcp.Bridge;

// Built-in effect discovery & invocation via reflection.
//
// Strategy:
//   1. Enumerate types in PaintDotNet.* assemblies that derive from the Effect base classes.
//   2. list_effects exposes name + category + asm so users can discover what's available.
//   3. apply_effect(name) tries — in order — to invoke the effect through Paint.NET's own
//      runner ("RunEffect" / "PerformEffect"-style methods on AppWorkspace / DocumentWorkspace).
//      No fallback to a raw OnRender call is attempted; that path doesn't compose with the
//      host's history/undo/threading and would produce confusing results.
//
// Limitations:
//   - v0.5 doesn't pass effect property values (e.g. blur radius). Effects with parameter-less
//     defaults run; others may fail or produce no visible change.
//   - The reflection path is best-effort. If Paint.NET 5 moves the runner method, the call
//     returns a `note` listing the methods it inspected.
internal static class EffectsCatalog
{
    public sealed record EffectEntry(string Name, string FullName, string Category, string Assembly);
    public sealed record InvokeResult(bool Ok, string Note);

    private static List<Type>? _cachedTypes;

    private static List<Type> DiscoverEffectTypes()
    {
        if (_cachedTypes is not null) return _cachedTypes;
        var found = new List<Type>();
        var effectBase = AppServices.FindType("PaintDotNet.Effects.Effect");
        var pbe = AppServices.FindType("PaintDotNet.Effects.PropertyBasedEffect");
        var bmpFx = AppServices.FindType("PaintDotNet.Effects.BitmapEffect");
        var bases = new[] { effectBase, pbe, bmpFx }.Where(t => t is not null).ToArray();
        if (bases.Length == 0) { _cachedTypes = found; return found; }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!(asm.FullName?.Contains("PaintDotNet", StringComparison.OrdinalIgnoreCase) ?? false)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray(); }
            catch { continue; }
            foreach (var t in types)
            {
                if (t.IsAbstract || t.IsInterface) continue;
                if (!bases.Any(b => b is not null && b.IsAssignableFrom(t))) continue;
                if (t == effectBase || t == pbe || t == bmpFx) continue;
                if (t.Namespace == "PaintDotNetMcp.Bridge") continue; // skip our own bridge effect
                found.Add(t);
            }
        }
        _cachedTypes = found;
        return found;
    }

    public static List<EffectEntry> List()
    {
        var types = DiscoverEffectTypes();
        var result = new List<EffectEntry>(types.Count);
        foreach (var t in types)
        {
            string cat = "Unknown";
            try
            {
                var attr = t.GetCustomAttribute<EffectCategoryAttribute>();
                if (attr is not null) cat = attr.Category.ToString();
            }
            catch { }
            result.Add(new EffectEntry(
                Name: t.Name,
                FullName: t.FullName ?? t.Name,
                Category: cat,
                Assembly: t.Assembly.GetName().Name ?? ""));
        }
        return result.OrderBy(e => e.Category).ThenBy(e => e.Name).ToList();
    }

    public static InvokeResult Apply(string effectName)
    {
        var types = DiscoverEffectTypes();
        var match = types.FirstOrDefault(t =>
            string.Equals(t.Name, effectName, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(t.FullName, effectName, StringComparison.OrdinalIgnoreCase));
        if (match is null) return new(false, "effect '" + effectName + "' not found (see list_effects)");

        // Find AppWorkspace.RunEffect (or similar). The signature takes an EffectInfo wrapper,
        // not the Effect instance itself — EffectInfo carries metadata (icon, category, ...) the
        // host needs to wire up its menu/history.
        var host = AppServices.AppWorkspaceService() ?? AppServices.DocumentWorkspaceService();
        if (host is null) return new(false, "no AppWorkspace/DocumentWorkspace");

        // Resolve the EffectInfo by walking EffectsCollection.Instance for the matching EffectType.
        var (effectInfo, infoNote) = FindEffectInfoFor(match);
        if (effectInfo is null)
        {
            // Fallback: EffectsCollection only registers modern (BitmapEffect / GpuEffect) effects.
            // For Legacy effects (PaintDotNet.Effects.Legacy.dll, 40+ items) we construct an
            // EffectInfo manually from the Type.
            var built = BuildEffectInfoFor(match, out var buildNote);
            if (built is null) return new(false, "no EffectInfo for " + match.Name + ": " + infoNote + " | build fallback: " + buildNote);
            effectInfo = built;
            infoNote = "constructed EffectInfo manually (" + buildNote + ")";
        }

        var runM = host.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "RunEffect" || m.Name == "PerformEffect" || m.Name == "ExecuteEffect")
            .Where(m => m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType.IsAssignableFrom(effectInfo.GetType()))
            .OrderBy(m => m.GetParameters().Length)
            .FirstOrDefault();
        if (runM is null)
        {
            var inspected = string.Join(", ", host.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(m => m.Name.Contains("Effect", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name)) + ")"));
            return new(false, "no compatible RunEffect(EffectInfo, ...) on " + host.GetType().Name + "; inspected: [" + inspected + "]");
        }

        // Fire-and-forget — RunEffect typically opens a modal "Effect Properties" dialog. If we
        // synchronously waited on UI invoke, our pipe response would deadlock until the user
        // dismisses the dialog (which may never happen in a remote/headless scenario).
        bool posted = AppServices.PostOnUiThread(() =>
        {
            try
            {
                var ps = runM.GetParameters();
                var args = new object?[ps.Length];
                args[0] = effectInfo;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[i].ParameterType)
                        : null;
                }
                runM.Invoke(host, args);
            }
            catch { /* swallow on the UI thread; can't propagate */ }
        }, out var invokeNote);
        if (!posted) return new(false, "UI post failed: " + invokeNote);
        return new(true, "posted to UI thread via " + host.GetType().Name + "." + runM.Name + " (effect may open a config dialog; user must confirm/cancel)");
    }

    /// <summary>
    /// Look up the EffectInfo for a given Effect type by walking EffectsCollection.Instance.
    /// Each registered effect carries an EffectType (or similar) property pointing back at its
    /// concrete Effect class. We find the matching entry — that's what RunEffect wants.
    /// </summary>
    private static (object? info, string note) FindEffectInfoFor(Type effectType)
    {
        var collType = AppServices.FindType("PaintDotNet.Effects.EffectsCollection");
        var collection = collType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null);
        if (collection is null) return (null, "EffectsCollection.Instance not available");

        // Try common enumerables and properties.
        var enums = new List<System.Collections.IEnumerable>();
        if (collection is System.Collections.IEnumerable e0) enums.Add(e0);
        foreach (var n in new[] { "Items", "All", "Effects", "EffectInfos", "AvailableEffects" })
        {
            if (AppServices.GetPropertyValue(collection, n) is System.Collections.IEnumerable ee) enums.Add(ee);
        }

        var seenInfoTypes = new HashSet<string>();
        object? firstItem = null;
        string targetFullName = effectType.FullName ?? effectType.Name;
        // Compare Types by FullName, not reference equality. Paint.NET loads Effects.Legacy.dll
        // into its own AssemblyLoadContext, so the same logical class shows up as two distinct
        // Type objects depending on which ALC is asking.
        bool TypeMatches(Type? t) => t is not null && (t.FullName ?? t.Name) == targetFullName;
        foreach (var en in enums)
        {
            foreach (var item in en)
            {
                if (item is null) continue;
                seenInfoTypes.Add(item.GetType().Name);
                firstItem ??= item;
                foreach (var prop in item.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var v = prop.GetValue(item);
                        if (TypeMatches(v as Type)) return (item, "matched on " + prop.Name);
                    }
                    catch { }
                }
                foreach (var fld in item.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    try
                    {
                        var v = fld.GetValue(item);
                        if (TypeMatches(v as Type)) return (item, "matched on field " + fld.Name);
                    }
                    catch { }
                }
                if (TypeMatches(item as Type)) return (item, "matched by item itself");
            }
        }

        // No match — dump the first EffectInfo's members so the next iteration knows what to look at.
        string dump = "";
        if (firstItem is not null)
        {
            var members = new List<string>();
            foreach (var prop in firstItem.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                try
                {
                    var v = prop.GetValue(firstItem);
                    members.Add(prop.Name + ":" + (prop.PropertyType.Name) + "=" + (v?.GetType().Name ?? "null"));
                }
                catch { members.Add(prop.Name + ":throw"); }
            }
            dump = "; first EffectInfo (" + firstItem.GetType().FullName + ") members: [" + string.Join(", ", members) + "]";
        }
        return (null, "no matching EffectInfo; seen wrapper types: [" + string.Join(", ", seenInfoTypes) + "]" + dump);
    }

    /// <summary>
    /// Build an EffectInfo wrapper for an effect Type that EffectsCollection didn't pre-register
    /// (i.e. Legacy effects). Tries static factory methods first, then constructors.
    /// </summary>
    private static object? BuildEffectInfoFor(Type effectType, out string note)
    {
        note = "";
        var infoType = AppServices.FindType("PaintDotNet.Effects.EffectInfo");
        if (infoType is null) { note = "EffectInfo type not found"; return null; }

        // Static factories: EffectInfo.Create(Type), EffectInfo.FromType(Type), EffectInfo.For(Type) ...
        foreach (var name in new[] { "Create", "FromType", "For", "FromEffectType", "Make" })
        {
            var m = infoType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(mi => mi.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                                       && mi.GetParameters().Length == 1
                                       && mi.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Type)));
            if (m is null) continue;
            try
            {
                var inst = m.Invoke(null, new object[] { effectType });
                if (inst is not null) { note = "via static EffectInfo." + m.Name; return inst; }
            }
            catch (Exception ex) { note = "static " + m.Name + " threw: " + AppServices.Unwrap(ex); }
        }

        // Constructors: ordered by parameter count, take the simplest that accepts a Type as arg 0.
        var ctors = infoType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters().Length >= 1 && c.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Type)))
            .OrderBy(c => c.GetParameters().Length)
            .ToList();
        var inspectedCtors = new List<string>();
        foreach (var c in ctors)
        {
            var ps = c.GetParameters();
            inspectedCtors.Add("ctor(" + string.Join(",", ps.Select(p => p.ParameterType.Name)) + ")");
            try
            {
                var args = new object?[ps.Length];
                args[0] = effectType;
                for (int i = 1; i < ps.Length; i++)
                {
                    args[i] = ps[i].ParameterType.IsValueType
                        ? Activator.CreateInstance(ps[i].ParameterType)
                        : null;
                }
                var inst = c.Invoke(args);
                note = "via " + ps.Length + "-arg ctor";
                return inst;
            }
            catch (Exception ex) { note = ps.Length + "-arg ctor threw: " + AppServices.Unwrap(ex); }
        }

        if (note == "") note = "no EffectInfo factory or ctor found; ctors inspected: [" + string.Join(", ", inspectedCtors) + "]";
        return null;
    }
}
