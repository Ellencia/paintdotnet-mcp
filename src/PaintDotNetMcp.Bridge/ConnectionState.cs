using PaintDotNet;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

internal static class ConnectionState
{
    // Snapshot initialization reads the active layer on the UI thread; no effect or history entry.
    public static void Update(PingResult result)
    {
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var workspace = AppServices.AppWorkspaceService();
            if (workspace is null) throw new InvalidOperationException("AppWorkspace not found");
            var documentWorkspace = AppServices.GetPropertyValue(workspace, "ActiveDocumentWorkspace");
            var document = documentWorkspace is null ? null : AppServices.GetPropertyValue(documentWorkspace, "Document");
            if (document is null)
            {
                ImageIO.InvalidateSnapshot();
                result.ConnectionStatus = "no_document";
                result.RecoveryAction = "Open an image or create a canvas in Paint.NET, then retry.";
                return;
            }
            result.DocumentOpen = true;
            var layer = AppServices.GetPropertyValue(documentWorkspace!, "ActiveLayer");
            if (layer is null || AppServices.GetPropertyValue(layer, "Surface") is not Surface surface)
            {
                ImageIO.InvalidateSnapshot();
                result.ConnectionStatus = "no_active_layer";
                result.RecoveryAction = "Select a bitmap layer in Paint.NET, then retry.";
                return;
            }
            result.Width = surface.Width;
            result.Height = surface.Height;
            // Preserve the render-completion snapshot while operations are pending or grouped.
            if (BridgeServer.PendingCount == 0 && !HistoryOps.BatchActive)
            {
                AutoCommit.WaitForExecutionIdle(0);
                ImageIO.CaptureSnapshot(surface);
            }
            result.SnapshotReady = ImageIO.HasSnapshot;
            result.ConnectionStatus = BridgeServer.PendingCount > 0 ? "render_pending" : "ready";
            if (result.ConnectionStatus == "render_pending")
                result.RecoveryAction = HistoryOps.BatchActive ? "Call end_batch, then retry." : "Call wait_for_idle; if automatic execution failed, retry commit.";
        }, out var note))
        {
            result.ConnectionStatus = "host_not_ready";
            result.RecoveryAction = "Wait for Paint.NET startup or the current effect to finish, then retry. If this persists, run Effects > Tools > MCP Bridge and check plugin errors. " + note;
        }
    }
}
