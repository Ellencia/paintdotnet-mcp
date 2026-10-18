using System.Collections;

namespace PaintDotNetMcp.Bridge;

// Tabs = AppWorkspace.DocumentWorkspaces. Every editing tool targets the active tab,
// so activate_document switches the visible tab rather than editing in the background.
// Based on PR #2 by 1SiDPlayer, reduced to the members Paint.NET 5.1 actually has.
internal static class DocumentNavigation
{
    internal static string? FilePath(object workspace) => AppServices.GetPropertyValue(workspace, "FilePath") as string;

    // Saved file name, or Paint.NET's localized "Untitled".
    internal static string? DisplayName(object workspace) => AppServices.InvokeMethod(workspace, "GetFileDisplayName") as string;

    private static List<object> Tabs(object app) =>
        (AppServices.GetPropertyValue(app, "DocumentWorkspaces") as IEnumerable ?? throw new NotSupportedException("AppWorkspace.DocumentWorkspaces not found."))
            .Cast<object>().ToList();

    private static object App() => AppServices.AppWorkspaceService() ?? throw new InvalidOperationException("Paint.NET workspace is not available.");

    private static object Describe(object ws, int index, object? active) =>
        new { Index = index, Name = DisplayName(ws), Path = FilePath(ws), IsActive = ReferenceEquals(ws, active) };

    internal static object List()
    {
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var tabs = Tabs(App());
            var active = AppServices.DocumentWorkspaceService();
            result = new { Documents = tabs.Select((ws, i) => Describe(ws, i, active)).ToArray(), Count = tabs.Count };
        }, out var reason)) throw new InvalidOperationException(reason);
        return result!;
    }

    internal static object Activate(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index), "index must be >= 0.");
        if (BridgeServer.PendingCount > 0 || HistoryOps.BatchActive)
            throw new InvalidOperationException("Finish pending drawing operations (wait_for_idle) or end_batch before switching documents.");
        // The host effect can still be closing right after wait_for_idle; wait on the pipe thread like ping does.
        AutoCommit.WaitForExecutionIdle(5000);
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var app = App();
            var tabs = Tabs(app);
            if (index >= tabs.Count)
                throw new ArgumentOutOfRangeException(nameof(index), $"Only {tabs.Count} documents are open. Call list_open_documents again.");
            var target = tabs[index];
            if (!ReferenceEquals(AppServices.DocumentWorkspaceService(), target))
            {
                if (AppServices.InvokeMethod(app, "TrySetActiveDocumentWorkspace", target) is not true ||
                    !ReferenceEquals(AppServices.DocumentWorkspaceService(), target))
                    throw new InvalidOperationException("Paint.NET refused to switch documents. Retry after the current operation finishes.");
                ImageIO.InvalidateSnapshot();
            }
            result = Describe(target, index, target);
        }, out var reason)) throw new InvalidOperationException(reason);
        return result!;
    }
}
