using System.Reflection;

namespace PaintDotNetMcp.Bridge;

// Execute the registered bridge through the same host path as its Effects menu item.
internal static class AutoCommit
{
    private static readonly object Gate = new();
    private static bool _scheduled;
    private static bool _requested;

    public static bool Enabled = true;
    public static bool Available => AppServices.GetMainForm() is not null;

    public static bool TryTrigger(object? effect, out string note)
    {
        if (!Enabled)
        {
            note = "auto-commit disabled";
            return false;
        }
        lock (Gate)
        {
            _requested = true;
            if (_scheduled)
            {
                note = "MCP Bridge execution already scheduled; request retained";
                return true;
            }
            _scheduled = true;
            if (!AppServices.PostOnUiThread(RunRequested, out note))
            {
                _scheduled = false;
                return false;
            }
        }
        note = "posted registered MCP Bridge effect execution to UI thread";
        return true;
    }

    private static void RunRequested()
    {
        try
        {
            while (true)
            {
                lock (Gate) _requested = false;
                if (BridgeServer.PendingCount > 0) RunBridgeEffect();
                lock (Gate)
                {
                    if (_requested && BridgeServer.PendingCount > 0) continue;
                    _scheduled = false;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            BridgeServer.RecordTriggerError(AppServices.Unwrap(ex));
            lock (Gate) _scheduled = false;
        }
    }

    private static void RunBridgeEffect()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var collectionType = AppServices.FindType("PaintDotNet.Effects.EffectsCollection")
            ?? throw new InvalidOperationException("EffectsCollection not found");
        var collection = collectionType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new InvalidOperationException("EffectsCollection.Instance not found");
        var effectInfo = collectionType.GetMethod("TryGetEffectInfo", [typeof(Type)])?.Invoke(collection, [typeof(BridgeEffect)])
            ?? throw new InvalidOperationException("Registered MCP Bridge EffectInfo not found");
        var workspace = AppServices.AppWorkspaceService()
            ?? throw new InvalidOperationException("AppWorkspace not found");
        var documentWorkspace = AppServices.GetPropertyValue(workspace, "ActiveDocumentWorkspace")
            ?? throw new InvalidOperationException("No active document workspace");
        if (AppServices.GetPropertyValue(documentWorkspace, "ActiveLayer") is null)
            throw new InvalidOperationException("No active layer");
        var toolbar = AppServices.GetPropertyValue(workspace, "ToolBar")
            ?? throw new InvalidOperationException("ToolBar not found");
        var mainMenu = AppServices.GetPropertyValue(toolbar, "MainMenu")
            ?? throw new InvalidOperationException("MainMenu not found");
        var effectsMenu = mainMenu.GetType().GetField("effectsMenu", flags)?.GetValue(mainMenu)
            ?? throw new InvalidOperationException("EffectsMenu not found");
        var run = effectsMenu.GetType().GetMethod("TryRunEffect", flags, null, [effectInfo.GetType()], null)
            ?? throw new InvalidOperationException("EffectsMenu.TryRunEffect not found");
        if (run.Invoke(effectsMenu, [effectInfo]) is not true)
            throw new InvalidOperationException("Paint.NET declined MCP Bridge execution");
    }
}
