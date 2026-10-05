using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;
using PaintDotNet;
using PaintDotNetMcp.Bridge;
using PaintDotNetMcp.Contracts;
using PaintDotNetMcp.Server;

// Exercise production dispatch, Surface rendering, and the MCP tool -> named-pipe path.
// Paint.NET itself need not be running. Its installed runtime assemblies are required.
Environment.SetEnvironmentVariable("PAINTDOTNET_MCP_PIPE_NAME", "PaintDotNetMcp.Regression." + Guid.NewGuid());
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(Environment.GetEnvironmentVariable("PaintDotNetDir") ?? @"C:\Program Files\paint.net", name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
await Run();

[MethodImpl(MethodImplOptions.NoInlining)]
static async Task Run()
{
    await CheckVersionGuard();
    var server = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.BridgeServer")!;
    var dispatch = server.GetMethod("Dispatch", BindingFlags.NonPublic | BindingFlags.Static)!;
    var prepare = server.GetMethod("PrepareRenderPass", BindingFlags.Public | BindingFlags.Static)!;
    var effect = (BridgeEffect)RuntimeHelpers.GetUninitializedObject(typeof(BridgeEffect));
    int id = 0;
    RpcResponse Call(string method, object? parameters = null) =>
        (RpcResponse)dispatch.Invoke(null, [JsonSerializer.Serialize(new { id = ++id, method, @params = parameters })])!;
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    foreach (var invalidSize in new[] { (0, 600), (-1, 600), (16385, 1), (10000, 10000) })
        Check(!Call("new_canvas", new NewCanvasParams { Width = invalidSize.Item1, Height = invalidSize.Item2 }).Ok, "Invalid or excessive canvas dimensions rejected before UI mutation");
    Check(!Call("open_image", new OpenImageParams { Path = "relative.png" }).Ok, "Relative image path rejected");
    Check(!Call("open_image", new OpenImageParams { Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") }).Ok, "Missing image rejected before native loading");
    Console.WriteLine("PASS document input validation prevents invalid native operations");
    object Prepare(Surface source, Rectangle[]? scans = null) =>
        prepare.Invoke(null, [effect, new RenderArgs(source), scans ?? [source.Bounds]])!;
    static void Render(object batch, Surface destination, Rectangle[] rois, int index, int count) =>
        batch.GetType().GetMethod("Render")!.Invoke(batch, [destination, rois, index, count]);
    static void Dispose(object batch) => ((IDisposable)batch).Dispose();
    static bool Pixel(Surface surface, int x, int y, byte r, byte g, byte b) =>
        surface[x, y].R == r && surface[x, y].G == g && surface[x, y].B == b;

    var historyServices = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!;
    var historyCache = (Dictionary<string, object?>)historyServices.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    using (var historySurface = new Surface(12, 10))
    {
        historySurface.Fill(ColorBgra.FromBgra(30, 20, 10, 255));
        var historyWorkspace = new TestHistoryWorkspace(new TestHistoryLayer(historySurface));
        var historyApp = new TestHistoryApp { ActiveDocumentWorkspace = historyWorkspace };
        historyCache["mainForm"] = new TestUiDispatcher();
        historyCache["appws"] = historyApp;
        var initialized = Call("ping");
        Check(initialized.Ok && initialized.Result!.Value.GetProperty("ConnectionStatus").GetString() == "ready", "Ping initializes without an effect render");
        Check(historyWorkspace.History.UndoStack.Count == 0, "Snapshot initialization adds no Undo history");
        historyApp.ActiveDocumentWorkspace = null!;
        Check(Call("ping").Result!.Value.GetProperty("ConnectionStatus").GetString() == "no_document", "No document recovery status");
        Check(!Call("get_canvas_png").Ok, "Closed document cannot return old snapshot");
        historyApp.ActiveDocumentWorkspace = historyWorkspace;
        Check(Call("ping").Result!.Value.GetProperty("SnapshotReady").GetBoolean(), "Opening a document recovers snapshot readiness");
        Check(Call("begin_batch").Ok, "Begin batch");
        Check(!Call("begin_batch").Ok, "Nested batch rejected");
        Check(!Call("new_canvas").Ok && !Call("open_image").Ok, "Document changes rejected during batch");
        Check(!Call("undo").Ok && !Call("redo").Ok, "History changes rejected during batch");
        Check(!Call("commit").Ok && !Call("set_auto_commit", new SetAutoCommitParams { Enabled = true }).Ok, "Batch cannot be split by commit or auto-commit");
        historyApp.ActiveDocumentWorkspace = new TestHistoryWorkspace(new TestHistoryLayer(historySurface));
        Check(!Call("fill", new FillParams { R = 255 }).Ok, "Drawing cannot move to another batch document");
        Check(!Call("end_batch").Ok, "Wrong-document batch completion rejected");
        historyApp.ActiveDocumentWorkspace = historyWorkspace;
        var emptyBatch = Call("end_batch");
        Check(emptyBatch.Ok && emptyBatch.Result!.Value.GetProperty("history_steps").GetInt32() == 0, "Empty batch creates no history step");
        Check(!Call("end_batch").Ok, "End without batch rejected");
        foreach (var method in new[] { "undo", "redo" })
        {
            var unchanged = Call(method);
            Check(unchanged.Ok && !unchanged.Result!.Value.GetProperty("changed").GetBoolean(), "Empty history is a no-op");
            var encoded = Call("get_canvas_png");
            Check(encoded.Ok && encoded.Result!.Value.GetProperty("Width").GetInt32() == 12, "History no-op refreshes active-layer snapshot");
        }
        historyCache.Remove("mainForm");
        historyCache.Remove("appws");
    }
    Console.WriteLine("PASS batch state guards, document binding, empty batch, and empty-history snapshot refresh");

    Check(Call("set_auto_commit", new SetAutoCommitParams { Enabled = false }).Ok, "Disable auto-commit");
    using var source = new Surface(800, 600);
    using var destination = new Surface(800, 600);
    source.Fill(ColorBgra.FromBgra(255, 255, 255, 255));
    var queued = Call("fill", new FillParams { R = 230, G = 40, B = 15 });
    Check(queued.Ok && !queued.Result!.Value.GetProperty("auto_committed").GetBoolean(), "Queue must not claim completion");
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Queued op must not complete");
    Check(!Call("begin_batch").Ok && !Call("undo").Ok && !Call("redo").Ok, "Unapplied drawing blocks history and batch boundaries");
    Check(!Call("new_canvas").Ok, "Unapplied drawing blocks new document creation");
    var rois = Enumerable.Range(0, 30).Select(i => new Rectangle(i % 5 * 160, i / 5 * 100, 160, 100)).ToArray();
    var batch = Prepare(source);
    Render(batch, destination, rois, 0, 1);
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "First tile is not completion");
    var wait = Task.Run(() => Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 2000 }));
    Parallel.For(1, rois.Length, i => Render(batch, destination, rois, i, 1));
    Check((await wait).Ok, "Wait must wake after last tile");
    for (int y = 0; y < 600; y++)
        for (int x = 0; x < 800; x++)
            Check(Pixel(destination, x, y, 230, 40, 15), "Full canvas fill must survive tiled rendering");
    Dispose(batch);
    Console.WriteLine("PASS 800x600 parallel tiled fill and last-tile completion");

    source.CopySurface(destination);
    Check(Call("draw_rect", new DrawRectangleParams { X = 650, Y = 450, Width = 100, Height = 100, R = 10, G = 100, B = 240, Fill = true }).Ok, "Queue rectangle");
    batch = Prepare(source);
    Parallel.For(0, rois.Length, i => Render(batch, destination, rois, i, 1));
    Check(Pixel(destination, 10, 10, 230, 40, 15) && Pixel(destination, 700, 500, 10, 100, 240), "Cumulative drawing on far tile");
    Dispose(batch);
    Console.WriteLine("PASS cumulative drawing across commits");

    // Paint.NET 5.1.12 rents a separate ROI array per tile, always starting at zero.
    // Its capacity can exceed the active length and include zero-area padding.
    Call("fill", new FillParams { R = 230, G = 40, B = 15 });
    batch = Prepare(source);
    Render(batch, destination, [rois[0], Rectangle.Empty, Rectangle.Empty, Rectangle.Empty], 0, 1);
    Render(batch, destination, [rois[0], Rectangle.Empty], 0, 1);
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Duplicate tile cannot complete the canvas");
    Parallel.For(1, rois.Length, i => Render(batch, destination, [rois[i], Rectangle.Empty, Rectangle.Empty, Rectangle.Empty], 0, 1));
    Check(Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Pooled per-tile arrays must complete");
    Dispose(batch);
    Console.WriteLine("PASS real-host pooled ROI arrays, zero-based slices, and duplicate tiles");

    // A cancelled pass must keep the queue, and selection snapshots preserve untouched pixels.
    source.CopySurface(destination);
    Call("fill", new FillParams { R = 0, G = 255, B = 0 });
    batch = Prepare(source);
    Render(batch, destination, rois, 0, 1);
    Dispose(batch);
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 10 }).Ok, "Cancelled pass must time out");
    var selected = new[] { new Rectangle(200, 200, 100, 100) };
    batch = Prepare(source, selected);
    Render(batch, destination, selected, 0, 1);
    Dispose(batch);
    var canvas = Call("get_canvas_png");
    Check(canvas.Ok && !canvas.Result!.Value.GetProperty("MaybeStale").GetBoolean(), "Completed snapshot is fresh");
    var imageIO = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.ImageIO")!;
    object?[] dimensions = [0, 0];
    var bytes = (byte[])imageIO.GetMethod("GetSnapshotCopy")!.Invoke(null, dimensions)!;
    int offset = (10 * 800 + 10) * 4;
    Check(bytes[offset + 2] == 230 && bytes[offset + 1] == 40, "Snapshot preserves pixels outside native ROIs");
    offset = (250 * 800 + 250) * 4;
    Check(bytes[offset + 1] == 255, "Snapshot includes selected pixels");
    Console.WriteLine("PASS cancelled-render retry and selection snapshot");

    // Start the real production pipe, then invoke the same tools exposed through MCP.
    server.GetMethod("EnsureStarted")!.Invoke(null, [effect]);
    await CheckMcpProtocol();
    await using var client = new BridgeClient();
    var tools = new PaintDotNetTools(client);
    var ping = JsonDocument.Parse(await tools.Ping(default));
    Check(ping.RootElement.GetProperty("CompletedRevision").GetInt64() == 4, "Pipe ping completion revision");
    Check(JsonDocument.Parse(await tools.WaitForIdle(0)).RootElement.GetProperty("completed").GetBoolean(), "MCP wait tool is integrated");
    await tools.Fill(50, 60, 70);
    string savedPath = Path.Combine(Path.GetTempPath(), "paintdotnet-mcp-regression-" + Guid.NewGuid() + ".png");
    try
    {
        // The real save request blocks until the render callback has published its snapshot.
        var save = tools.SavePng(savedPath);
        source.CopySurface(destination);
        batch = Prepare(source);
        Parallel.For(0, rois.Length, i => Render(batch, destination, rois, i, 1));
        Dispose(batch);
        await save;
        Check(File.Exists(savedPath), "Save produced an artifact after completion");
        using var bitmap = new System.Drawing.Bitmap(savedPath);
        Check(bitmap.Width == 800 && bitmap.Height == 600 && bitmap.GetPixel(799, 599).R == 50, "Reopen saved artifact and verify far corner");
        Console.WriteLine("PASS MCP tools -> named pipe -> render completion -> saved/reopened PNG");
    }
    finally { if (File.Exists(savedPath)) File.Delete(savedPath); }

    // A queued operation arriving after preparation must survive this batch's completion.
    Call("fill", new FillParams { R = 1, G = 2, B = 3 });
    batch = Prepare(source);
    Call("fill", new FillParams { R = 4, G = 5, B = 6 });
    Parallel.For(0, rois.Length, i => Render(batch, destination, rois, i, 1));
    Dispose(batch);
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Later queue revision must remain pending");
    batch = Prepare(destination);
    Parallel.For(0, rois.Length, i => Render(batch, destination, rois, i, 1));
    Dispose(batch);
    Check(Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Later batch completes");
    Console.WriteLine("PASS operations queued during rendering are preserved");

    Call("fill", new FillParams { R = 9, G = 8, B = 7 });
    savedPath = Path.Combine(Path.GetTempPath(), "paintdotnet-mcp-timeout-" + Guid.NewGuid() + ".png");
    var timedSave = Call("save_png", new SavePngParams { Path = savedPath });
    Check(!timedSave.Ok && !File.Exists(savedPath), "Timed-out save must not create a stale file");
    batch = Prepare(source);
    Parallel.For(0, rois.Length, i => Render(batch, destination, rois, i, 1));
    Dispose(batch);
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = -1 }).Ok, "Invalid timeout rejected");
    Call("paste_image", new PasteImageParams { PngBase64 = "not-base64" });
    try { batch = Prepare(source); throw new Exception("Malformed paste must fail rendering"); }
    catch (TargetInvocationException) { }
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Render failure must not claim completion");
    Check(!Call("get_canvas_png").Ok, "Render failure must block a stale read");
    Console.WriteLine("PASS timeout and render errors block stale reads/saves");

    // Only the UI dispatcher is substituted here. Production scheduling and failure
    // reporting run unchanged; successful host execution requires the live app test.
    var services = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!;
    var cache = (Dictionary<string, object?>)services.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    var ui = new TestUiDispatcher();
    cache["mainForm"] = ui;
    Call("set_auto_commit", new SetAutoCommitParams { Enabled = true });
    Check(Call("commit").Ok && ui.Callbacks.Count == 1, "Commit must post UI work");
    Check(Call("commit").Ok && ui.Callbacks.Count == 1, "Rapid requests must coalesce without discarding work");
    var pendingBefore = Call("ping").Result!.Value.GetProperty("PendingOpCount").GetInt32();
    ui.Callbacks[0].DynamicInvoke();
    var failed = Call("ping").Result!.Value;
    Check(failed.GetProperty("RenderError").GetString()!.StartsWith("automatic effect execution failed:"), "UI callback failure must be reported");
    Check(failed.GetProperty("PendingOpCount").GetInt32() == pendingBefore, "Trigger failure must preserve queued work");
    Check(!Call("wait_for_idle", new WaitForIdleParams { TimeoutMs = 0 }).Ok, "Trigger failure must not claim completion");
    Check(Call("commit").Ok && ui.Callbacks.Count == 2, "Failure must release scheduler for retry");
    ui.Callbacks[1].DynamicInvoke();
    cache.Remove("mainForm");
    Console.WriteLine("PASS UI scheduling, request coalescing, visible trigger errors, and retry");
}

static async Task CheckMcpProtocol()
{
    var root = new DirectoryInfo(AppContext.BaseDirectory);
    while (!File.Exists(Path.Combine(root.FullName, "PaintDotNetMcp.sln")))
        root = root.Parent ?? throw new Exception("Repository root not found");
    var start = new ProcessStartInfo("dotnet")
    {
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };
    start.ArgumentList.Add(Path.Combine(root.FullName, "src", "PaintDotNetMcp.Server", "bin", "Release", "net9.0", "PaintDotNetMcp.Server.dll"));
    using var process = Process.Start(start)!;
    var stderr = process.StandardError.ReadToEndAsync();
    async Task<JsonElement> Request(int id, string method, object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
        await process.StandardInput.FlushAsync();
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (line is null) throw new Exception("MCP server exited: " + await stderr);
            var response = JsonDocument.Parse(line).RootElement;
            if (response.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                if (response.TryGetProperty("error", out var error)) throw new Exception(error.ToString());
                return response.GetProperty("result").Clone();
            }
        }
    }
    try
    {
        await Request(1, "initialize", new { protocolVersion = "2024-11-05", capabilities = new { }, clientInfo = new { name = "regression", version = "1" } });
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
        var list = await Request(2, "tools/list", new { });
        foreach (var required in new[] { "wait_for_idle", "begin_batch", "end_batch", "undo", "redo", "new_canvas", "open_image" })
            if (!list.GetProperty("tools").EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == required))
                throw new Exception(required + " missing from MCP tools/list");
        var called = await Request(3, "tools/call", new { name = "wait_for_idle", arguments = new { timeoutMs = 0 } });
        if (called.TryGetProperty("isError", out var isError) && isError.GetBoolean())
            throw new Exception("MCP wait_for_idle failed: " + called);
        var result = JsonDocument.Parse(called.GetProperty("content")[0].GetProperty("text").GetString()!);
        if (!result.RootElement.GetProperty("completed").GetBoolean()) throw new Exception("MCP completion missing");
        Console.WriteLine("PASS real MCP stdio initialize, tools/list, tools/call wait_for_idle");
    }
    finally
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
    }
}

static async Task CheckVersionGuard()
{
    var original = Environment.GetEnvironmentVariable("PAINTDOTNET_MCP_PIPE_NAME");
    Environment.SetEnvironmentVariable("PAINTDOTNET_MCP_PIPE_NAME", "PaintDotNetMcp.VersionTest." + Guid.NewGuid());
    try
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var pipe = new System.IO.Pipes.NamedPipeServerStream(PipeNames.Current, System.IO.Pipes.PipeDirection.InOut, 1,
            System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var host = Task.Run(async () =>
        {
            await pipe.WaitForConnectionAsync(timeout.Token);
            using var reader = new StreamReader(pipe, leaveOpen: true);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            var request = JsonDocument.Parse((await reader.ReadLineAsync(timeout.Token))!);
            if (request.RootElement.GetProperty("method").GetString() != "ping") throw new Exception("Version check must precede drawing");
            await writer.WriteLineAsync(JsonSerializer.Serialize(new RpcResponse
            {
                Id = request.RootElement.GetProperty("id").GetInt32(), Ok = true,
                Result = JsonSerializer.SerializeToElement(new PingResult { Version = "0.5.17" })
            }));
            if (await reader.ReadLineAsync(timeout.Token) is not null) throw new Exception("Mismatched Bridge must not receive drawing");
        });
        await using var client = new BridgeClient();
        try { await client.CallAsync("fill", new FillParams { R = 255 }, timeout.Token); throw new Exception("Version mismatch was accepted"); }
        catch (ModelContextProtocol.McpException ex) when (ex.Message.Contains("bridge_version_mismatch") && ex.Message.Contains("install.ps1")) { }
        await host;
        Console.WriteLine("PASS version mismatch exposes recovery instructions and blocks mutations");
    }
    finally { Environment.SetEnvironmentVariable("PAINTDOTNET_MCP_PIPE_NAME", original); }
}

sealed class TestUiDispatcher
{
    public bool InvokeRequired => false;
    public List<Delegate> Callbacks { get; } = new();
    public object BeginInvoke(Delegate callback)
    {
        Callbacks.Add(callback);
        return new object();
    }
}

sealed class TestHistoryApp
{
    public TestHistoryWorkspace ActiveDocumentWorkspace { get; set; } = null!;
}

sealed class TestHistoryWorkspace(TestHistoryLayer layer)
{
    public object Document { get; } = new();
    public TestHistoryLayer ActiveLayer { get; } = layer;
    public TestHistoryStacks History { get; } = new();
}

sealed class TestHistoryLayer(Surface surface)
{
    public Surface Surface { get; } = surface;
}

sealed class TestHistoryStacks
{
    public List<object> UndoStack { get; } = new();
    public List<object> RedoStack { get; } = new();
}
