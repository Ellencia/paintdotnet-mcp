using System.Reflection;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

internal static class DocumentOps
{
    public static DocumentOpResult Open(OpenImageParams parameters)
    {
        if (string.IsNullOrWhiteSpace(parameters.Path) || !Path.IsPathFullyQualified(parameters.Path))
            throw new ArgumentException("open_image requires an absolute file path.");
        var path = Path.GetFullPath(parameters.Path);
        if (!File.Exists(path)) throw new FileNotFoundException("Image file not found: " + path);
        return Run(workspace =>
        {
            var method = workspace.GetType().GetMethod("OpenFileInNewWorkspace", [typeof(string)])
                ?? throw new InvalidOperationException("Paint.NET OpenFileInNewWorkspace(string) not found.");
            if (method.Invoke(workspace, [path]) is not true)
                throw new InvalidOperationException("Paint.NET did not open the image. Check any loading dialog or format error.");
        }, path);
    }

    public static DocumentOpResult Create(NewCanvasParams parameters)
    {
        if (parameters.Width <= 0 || parameters.Height <= 0 ||
            parameters.Width > 16384 || parameters.Height > 16384 ||
            (long)parameters.Width * parameters.Height > 64_000_000)
            throw new ArgumentOutOfRangeException(nameof(parameters), "Canvas dimensions must be 1..16384 pixels with at most 64 million pixels total.");
        return Run(workspace =>
        {
            var method = workspace.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .SingleOrDefault(m => m.Name == "CreateBlankDocumentInNewWorkspace" && m.GetParameters().Length == 4)
                ?? throw new InvalidOperationException("Paint.NET CreateBlankDocumentInNewWorkspace not found.");
            var types = method.GetParameters();
            var size = Activator.CreateInstance(types[0].ParameterType, parameters.Width, parameters.Height)
                ?? throw new InvalidOperationException("Paint.NET size construction failed.");
            var unit = Enum.Parse(types[1].ParameterType, "Inch");
            if (method.Invoke(workspace, [size, unit, 96d, false]) is not true)
                throw new InvalidOperationException("Paint.NET did not create the canvas. Check available memory or any error dialog.");
        }, null);
    }

    private static DocumentOpResult Run(Action<object> action, string? path)
    {
        if (HistoryOps.BatchActive) throw new InvalidOperationException("Finish the active batch with end_batch before opening or creating a document.");
        AutoCommit.WaitForExecutionIdle(5000);
        if (BridgeServer.PendingCount > 0)
            throw new InvalidOperationException("Apply pending drawing with commit and wait_for_idle before opening or creating a document.");
        DocumentOpResult? result = null;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            var workspace = AppServices.AppWorkspaceService()
                ?? throw new InvalidOperationException("Paint.NET workspace is not ready. Wait for startup and retry.");
            action(workspace);
            ImageIO.InvalidateSnapshot();
            var state = new PingResult();
            ConnectionState.Update(state);
            if (state.ConnectionStatus != "ready" || !state.SnapshotReady)
                throw new InvalidOperationException("Document opened but snapshot initialization failed. Retry ping. " + state.RecoveryAction);
            result = new DocumentOpResult { Path = path, Width = state.Width!.Value, Height = state.Height!.Value, SnapshotReady = true };
        }, out var note)) throw new InvalidOperationException(note);
        return result!;
    }
}
