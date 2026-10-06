using System.Reflection;
using PaintDotNet;

namespace PaintDotNetMcp.Bridge;

internal static class HistoryOps
{
    private static volatile object? _batchWorkspace;
    private static object? _batchLayer;
    private static bool _previousAutoCommit;
    private static volatile bool _committing;
    public static bool BatchActive => _batchWorkspace is not null;

    private static object Workspace() => AppServices.GetPropertyValue(
        AppServices.AppWorkspaceService() ?? throw new InvalidOperationException("No AppWorkspace"),
        "ActiveDocumentWorkspace") ?? throw new InvalidOperationException("No active document");

    internal static List<object> Stack(object workspace, string name)
    {
        var history = AppServices.GetPropertyValue(workspace, "History")
            ?? throw new InvalidOperationException("History not found");
        return (AppServices.GetPropertyValue(history, name) as System.Collections.IEnumerable)?.Cast<object>().ToList()
            ?? throw new InvalidOperationException(name + " not found");
    }

    private static void OnUi(Action action)
    {
        if (!AppServices.InvokeOnUiThread(action, out var note))
            throw new InvalidOperationException(note);
    }

    public static object BeginBatch()
    {
        if (BatchActive) throw new InvalidOperationException("A batch is already active; call end_batch first.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount != 0)
            throw new InvalidOperationException("Apply pending drawing before begin_batch.");
        OnUi(() =>
        {
            var workspace = Workspace();
            var layer = AppServices.GetPropertyValue(workspace, "ActiveLayer")
                ?? throw new InvalidOperationException("No active layer");
            // Resolve history now, before changing the automatic execution setting.
            Stack(workspace, "UndoStack");
            _previousAutoCommit = AutoCommit.Enabled;
            AutoCommit.Enabled = false;
            _batchLayer = layer;
            _batchWorkspace = workspace;
        });
        return new { batch_active = true, pending = 0 };
    }

    public static void ValidateBatchTarget()
    {
        if (!BatchActive) return;
        var workspace = Workspace();
        if (!ReferenceEquals(workspace, _batchWorkspace) ||
            !ReferenceEquals(AppServices.GetPropertyValue(workspace, "ActiveLayer"), _batchLayer))
            throw new InvalidOperationException("Batch document or layer changed. Return to its original tab and layer, then retry end_batch.");
    }

    public static void ValidateRender()
    {
        if (BatchActive && !_committing)
            throw new InvalidOperationException("A batch is active; use end_batch to render it as one history step.");
    }

    public static object EndBatch()
    {
        if (!BatchActive) throw new InvalidOperationException("No active batch; call begin_batch first.");
        int historySteps = 0;
        int operationCount = BridgeServer.PendingCount;
        OnUi(() =>
        {
            ValidateBatchTarget();
            var workspace = Workspace();
            int before = Stack(workspace, "UndoStack").Count;
            _committing = true;
            try
            {
                if (operationCount > 0) AutoCommit.RunBridgeEffect();
                historySteps = Stack(workspace, "UndoStack").Count - before;
                if (operationCount > 0)
                {
                    if (historySteps != 1)
                    {
                        ImageIO.InvalidateSnapshot();
                        throw new InvalidOperationException("Paint.NET did not accept the batch as one Undo step. The batch remains active; retry end_batch.");
                    }
                    BridgeServer.AcceptBatchRender();
                }
                _batchWorkspace = null;
                _batchLayer = null;
                AutoCommit.Enabled = _previousAutoCommit;
            }
            finally { _committing = false; }
        });
        return new { batch_active = false, applied_operations = operationCount, history_steps = historySteps };
    }

    public static object Step(bool redo)
    {
        if (BatchActive) throw new InvalidOperationException("Finish the active batch with end_batch before Undo/Redo.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount > 0)
            throw new InvalidOperationException("Apply pending drawing before Undo/Redo.");
        bool changed = false;
        int undoCount = 0, redoCount = 0;
        OnUi(() =>
        {
            var workspace = Workspace();
            var stack = Stack(workspace, redo ? "RedoStack" : "UndoStack");
            if (stack.Count > 0 && stack[redo ? 0 : stack.Count - 1].GetType().FullName != "PaintDotNet.HistoryMementos.NullHistoryMemento")
            {
                var actionType = AppServices.FindType(redo ? "PaintDotNet.Actions.HistoryRedoAction" : "PaintDotNet.Actions.HistoryUndoAction")
                    ?? throw new InvalidOperationException("History action not found");
                var action = Activator.CreateInstance(actionType)!;
                var perform = workspace.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Single(m => m.Name == "PerformAction" && m.GetParameters().Length == 1 &&
                        m.GetParameters()[0].ParameterType.IsInstanceOfType(action));
                ImageIO.InvalidateSnapshot();
                perform.Invoke(workspace, [action]);
                changed = true;
            }
            // Do not run an effect to refresh: it would create history and clear Redo.
            var layer = AppServices.GetPropertyValue(workspace, "ActiveLayer")
                ?? throw new InvalidOperationException("No active layer after Undo/Redo");
            var surface = AppServices.GetPropertyValue(layer, "Surface") as Surface
                ?? throw new InvalidOperationException("Active layer surface unavailable");
            ImageIO.CaptureSnapshot(surface);
            undoCount = Stack(workspace, "UndoStack").Count;
            redoCount = Stack(workspace, "RedoStack").Count;
        });
        return new { changed, undo_count = undoCount, redo_count = redoCount, snapshot_updated = true };
    }
}
