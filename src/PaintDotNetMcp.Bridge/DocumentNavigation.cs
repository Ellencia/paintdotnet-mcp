using System.Collections;
using System.Reflection;

namespace PaintDotNetMcp.Bridge;

// All document and tab access runs on Paint.NET's UI thread. Switching tabs is
// intentional: existing painting operations always target the active document.
internal static class DocumentNavigation
{
    private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static object? Member(object? target, params string[] names)
    {
        if (target is null) return null;
        foreach (var name in names)
        {
            try
            {
                var property = target.GetType().GetProperty(name, Members);
                if (property is not null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(target);
                var field = target.GetType().GetField(name, Members);
                if (field is not null) return field.GetValue(target);
            }
            catch { /* Internal host members may differ between Paint.NET versions. */ }
        }
        return null;
    }

    internal static string? FilePath(object? workspace)
    {
        var value = Member(workspace, "FilePath", "filePath", "FileName", "fileName");
        return value is string path && !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
            ? path : null;
    }

    internal static string? FileName(object? workspace)
    {
        if (workspace is null) return null;
        var path = FilePath(workspace);
        if (path is not null) return Path.GetFileName(path);
        var name = Member(workspace, "DocumentName", "DisplayName", "FileName", "Name", "Text");
        return name is string title && !string.IsNullOrWhiteSpace(title) ? title : null;
    }

    private static List<object> Workspaces(object app, object? active)
    {
        var discovered = new List<object>();
        var collection = Member(app, "DocumentWorkspaces", "documentWorkspaces", "Workspaces");
        if (collection is IEnumerable entries && collection is not string)
        {
            foreach (var item in entries)
            {
                if (item is null || Member(item, "Document") is null) continue;
                if (!discovered.Any(ws => ReferenceEquals(ws, item))) discovered.Add(item);
            }
        }
        // Some host versions do not expose the collection. Still report the
        // active document; never claim other tabs have been enumerated.
        if (active is not null && Member(active, "Document") is not null &&
            !discovered.Any(ws => ReferenceEquals(ws, active)))
            discovered.Add(active);
        return discovered;
    }

    private static object RequireWorkspace()
        => AppServices.AppWorkspaceService() ?? throw new InvalidOperationException("Paint.NET workspace is not available.");

    internal static object List()
    {
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var app = RequireWorkspace();
            var active = AppServices.DocumentWorkspaceService();
            var list = Workspaces(app, active);
            result = new
            {
                Documents = list.Select((ws, index) => new
                {
                    Index = index,
                    Name = FileName(ws),
                    Path = FilePath(ws),
                    IsActive = ReferenceEquals(ws, active)
                }).ToArray(),
                Count = list.Count
            };
        }, out var reason)) throw new InvalidOperationException(reason);
        return result!;
    }

    internal static object Activate(int index)
    {
        if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
        if (BridgeServer.PendingCount > 0 || HistoryOps.BatchActive || AutoCommit.IsExecuting)
            throw new InvalidOperationException("Finish pending drawing operations or batches before switching documents.");
        object? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var app = RequireWorkspace();
            var active = AppServices.DocumentWorkspaceService();
            var items = Workspaces(app, active);
            if (index >= items.Count) throw new ArgumentOutOfRangeException(nameof(index), "Document index is no longer valid. Call list_open_documents again.");
            var target = items[index];
            if (!ReferenceEquals(active, target))
            {
                bool invoked = false;
                foreach (var name in new[] { "SetActiveDocumentWorkspace", "ActivateDocumentWorkspace", "SelectDocumentWorkspace" })
                {
                    var method = app.GetType().GetMethods(Members).FirstOrDefault(m =>
                        m.Name == name && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsInstanceOfType(target));
                    if (method is null) continue;
                    method.Invoke(app, [target]);
                    invoked = true;
                    break;
                }
                if (!invoked)
                {
                    var prop = app.GetType().GetProperty("ActiveDocumentWorkspace", Members);
                    if (prop?.CanWrite == true && prop.PropertyType.IsInstanceOfType(target))
                    {
                        prop.SetValue(app, target);
                        invoked = true;
                    }
                }
                // Do not proceed with editing if the host did not actually switch.
                if (!invoked || !ReferenceEquals(AppServices.DocumentWorkspaceService(), target))
                    throw new NotSupportedException("This Paint.NET version does not expose a compatible tab selection API.");
                ImageIO.InvalidateSnapshot();
            }
            result = new { Index = index, Name = FileName(target), Path = FilePath(target), IsActive = true };
        }, out var reason)) throw new InvalidOperationException(reason);
        return result!;
    }
}
