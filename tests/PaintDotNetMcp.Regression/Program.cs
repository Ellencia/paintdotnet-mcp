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
    CheckLayerTransforms();
    CheckComposite();
    CheckAnnotations();
    CheckTextIdRenewal();
    TextEditorChecks.Run();
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
    Check(!Call("set_selection_rect", new SetSelectionRectParams { Width = 0, Height = 10 }).Ok, "Zero-size selection rejected");
    Check(!Call("set_selection_rect", new SetSelectionRectParams { X = int.MaxValue, Width = 10, Height = 10 }).Ok, "Overflow selection rejected");
    Check(!Call("set_selection_polygon", new SetSelectionPolygonParams { Points = [new() { X = 1, Y = 1 }, new() { X = 1, Y = 1 }, new() { X = 2, Y = 2 }] }).Ok, "Polygon must have three distinct points");
    Console.WriteLine("PASS native selection input validation");
    Check(!Call("resize_canvas", new ResizeCanvasParams { Width = 0, Height = 20 }).Ok, "Invalid resize rejected before native mutation");
    Check(!Call("resize_canvas", new ResizeCanvasParams { Width = 10000, Height = 10000 }).Ok, "Excessive resize rejected");
    Check(!Call("resize_canvas", new ResizeCanvasParams { Width = 20, Height = 20, Anchor = "invalid" }).Ok, "Invalid anchor rejected");
    Check(!Call("copy_selection_to_layer", new CopySelectionToLayerParams { Name = " " }).Ok, "Empty layer name rejected");
    foreach (var size in new[] { 0f, -1f, 513f })
        Check(!Call("draw_text", new DrawTextParams { Text = "Test", FontSize = size }).Ok, "Invalid text size rejected before queueing");
    Check(!Call("draw_text", new DrawTextParams { Text = " " }).Ok, "Empty text rejected");
    Check(!Call("draw_text", new DrawTextParams { Text = "Test", FontFamily = "MCP Nonexistent Font 5723" }).Ok, "Missing font rejected instead of silently substituting");
    Check(!Call("draw_marker", new DrawMarkerParams { Label = " " }).Ok && !Call("draw_marker", new DrawMarkerParams { Radius = 2 }).Ok, "Invalid marker rejected");
    Check(!Call("draw_callout", new DrawCalloutParams { Text = "A", TargetX = 5 }).Ok && !Call("draw_callout", new DrawCalloutParams { Text = " " }).Ok, "Invalid callout rejected");
    Check((int)server.GetProperty("PendingCount")!.GetValue(null)! == 0, "Invalid editing inputs leave no pending operations");
    Console.WriteLine("PASS canvas editing and text validation reject invalid input without pending mutations");
    Check(!Call("create_text_layer", new CreateTextLayerParams { Text = new() { Text = " " } }).Ok, "Invalid managed text rejected");
    Check(!Call("create_text_layer", new CreateTextLayerParams { Name = " ", Text = new() { Text = "Test" } }).Ok, "Invalid text layer name rejected");
    Check(!Call("update_text_layer", new UpdateTextLayerParams { LayerIndex = -2 }).Ok, "Invalid text layer index rejected");
    Console.WriteLine("PASS editable text input validation");
    bool Rejects(SetLayerPropertiesParams p, string field) => Call("set_layer_properties", p) is { Ok: false } r && r.Error!.Contains(field);
    Check(Rejects(new() { LayerIndex = -2 }, "index"), "Invalid layer index rejected");
    Check(Rejects(new() { Name = " " }, "name"), "Empty layer name rejected");
    Check(Rejects(new() { Opacity = 1.5 }, "Opacity") && Rejects(new() { Opacity = -0.1 }, "Opacity"), "Out-of-range opacity rejected");
    Check(Rejects(new() { BlendMode = "Soft" }, "Multiply") && Rejects(new() { BlendMode = "3" }, "Multiply") && Rejects(new() { BlendMode = "" }, "Multiply"), "Unknown or numeric blend mode rejected");
    Console.WriteLine("PASS layer property input validation");
    bool RejectsFn(LayerFunctionParams p, string field) => Call("layer_function", p) is { Ok: false } r && r.Error!.Contains(field);
    Check(RejectsFn(new() { Function = "rotate" }, "merge_down"), "Unknown layer function rejected");
    Check(RejectsFn(new() { Function = "duplicate", LayerIndex = -2 }, "index"), "Invalid layer function index rejected");
    Check(RejectsFn(new() { Function = "move" }, "toIndex"), "Move without destination rejected");
    bool RejectsAnn(AnnotationParams p, string text) => Call("add_annotation", p) is { Ok: false } r && r.Error!.Contains(text);
    Check(RejectsAnn(new() { Type = "circle" }, "callout, arrow or marker")
        && RejectsAnn(new() { Type = "marker", Properties = new() { ["Lable"] = JsonDocument.Parse("\"1\"").RootElement } }, "known: X")
        && RejectsAnn(new() { Type = "marker", From = "callout1" }, "arrows only") && RejectsAnn(new() { Type = "arrow", Target = "marker1" }, "callouts only"),
        "Invalid annotations rejected");
    bool RejectsArr(ArrangeLayersParams p, string text) => Call("arrange_layers", p) is { Ok: false } r && r.Error!.Contains(text);
    Check(RejectsArr(new(), "distinct") && RejectsArr(new() { LayerIndices = [1, 1], Horizontal = "left" }, "distinct")
        && RejectsArr(new() { LayerIndices = [0, 1], Distribute = "horizontal", Horizontal = "left" }, "other one")
        && RejectsArr(new() { LayerIndices = [0] }, "Specify") && RejectsArr(new() { LayerIndices = [0], Distribute = "vertical" }, "two layers"), "Invalid arrange_layers rejected");
    Console.WriteLine("PASS layer function input validation");
    // Paint.NET loads Effects.Legacy at startup; this host must touch it (a bare typeof is not enough).
    Check(typeof(PaintDotNet.Effects.GaussianBlurEffect).Assembly.GetName().Name == "PaintDotNet.Effects.Legacy", "Legacy effects loaded");
    // The Effects menu's own effects are GPU ones (internal, PaintDotNet.Effects.Gpu); load before the catalog caches.
    System.Reflection.Assembly.Load("PaintDotNet.Effects.Gpu");
    var effects = Call("list_effects", null);
    Check(effects.Ok && effects.Result!.Value.GetProperty("Effects").EnumerateArray().Any(e => e.GetProperty("Name").GetString() == "GaussianBlurGpuEffect"), "GPU effects listed: " + effects.Error);
    var listed = effects.Result!.Value.GetProperty("Effects").EnumerateArray().ToDictionary(e => e.GetProperty("Name").GetString()!, e => e.GetProperty("Category").GetString());
    Check(listed["GaussianBlurGpuEffect"] == "Effect" && listed.Values.Contains("Adjustment") && !listed.Values.Contains("Unknown"), "Effect categories come from EffectInfo");
    Check(!listed.ContainsKey("GaussianBlurEffect") && !listed.ContainsKey("RotateZoomGpuEffect"), "DoNotDisplay effects hidden from list_effects");
    Check(!listed.ContainsKey("LevelsEffect") && !listed.ContainsKey("InkSketchEffect") && listed.ContainsKey("LevelsGpuEffect"), "Legacy effects without DoNotDisplay hidden too");
    // This host has no Paint.NET settings service, which GPU defaults read; the real app supplies it.
    var gpuBlur = Call("get_effect_properties", new { Name = "GaussianBlurGpuEffect" });
    Check(!gpuBlur.Ok && gpuBlur.Error!.Contains("ISettingsService"), "GPU effect defaults reach Paint.NET services: " + gpuBlur.Error);
    var blurProps = Call("get_effect_properties", new { Name = "GaussianBlurEffect" });
    Check(blurProps.Ok && blurProps.Result!.Value.GetProperty("Properties")[0].GetProperty("Max").GetInt32() == 200, "Effect properties expose name and range: " + blurProps.Error);
    bool RejectsFx(object props, string text) => Call("apply_effect", new { Name = "GaussianBlurEffect", Properties = props }) is { Ok: false } r && r.Error!.Contains(text);
    Check(RejectsFx(new { Radius = 999 }, "out of range 0..200"), "Out-of-range effect value rejected instead of clamped");
    Check(RejectsFx(new { Radios = 3 }, "known: Radius"), "Unknown effect property rejected");
    Check(RejectsFx(new { Radius = "big" }, "Radius"), "Wrong effect value type rejected");
    var bulge = Call("apply_effect", new { Name = "BulgeEffect", Properties = new { Offset = new[] { 5.0, 0 } } });
    Check(!bulge.Ok && bulge.Error!.Contains("[-1, -1]..[1, 1]"), "Out-of-range vector rejected (Paint.NET does not clamp vectors): " + bulge.Error);
    Console.WriteLine("PASS effect property discovery and value validation");
    var cropMethod = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.ImageIO")!.GetMethod("ImageCrop")!;
    var clippedCrop = (Rectangle)cropMethod.Invoke(null, [640, 320, -10, -20, 50, 60])!;
    Check(clippedCrop == new Rectangle(0, 0, 40, 40), "Negative crop coordinates report actual clipped dimensions");
    var overflowCrop = (Rectangle)cropMethod.Invoke(null, [640, 320, 600, 300, int.MaxValue, int.MaxValue])!;
    Check(overflowCrop == new Rectangle(600, 300, 40, 20), "Crop intersection does not overflow");
    Console.WriteLine("PASS preview/export crop clipping and overflow-safe dimensions");
    Check(!Call("transform_layer", new TransformLayerParams { ScaleX = 0 }).Ok, "Invalid transform is not queued");
    Check(!Call("transform_layer", new TransformLayerParams { Interpolation = "invalid" }).Ok, "Invalid interpolation is not queued");
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
        Check(!Call("set_selection_rect", new SetSelectionRectParams { Width = 5, Height = 5 }).Ok && !Call("clear_selection").Ok, "Selection changes rejected during batch");
        Check(!Call("copy_selection_to_layer").Ok && !Call("crop_to_selection").Ok && !Call("resize_canvas", new ResizeCanvasParams { Width = 20, Height = 20 }).Ok, "Layer and canvas edits rejected during batch");
        Check(!Call("create_text_layer", new CreateTextLayerParams { Text = new() { Text = "Test" } }).Ok && !Call("update_text_layer").Ok, "Text layer edits rejected during batch");
        Check(!Call("set_layer_properties", new SetLayerPropertiesParams { Visible = false }).Ok, "Layer property edits rejected during batch");
        Check(!Call("layer_function", new LayerFunctionParams { Function = "duplicate" }).Ok && !Call("arrange_layers", new ArrangeLayersParams { LayerIndices = [0], Horizontal = "left" }).Ok
            && !Call("add_annotation", new AnnotationParams { Type = "marker" }).Ok, "Layer functions rejected during batch");
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
        foreach (var required in new[] { "wait_for_idle", "begin_batch", "end_batch", "undo", "redo", "new_canvas", "open_image", "transform_layer", "get_selection", "copy_selection_to_layer", "resize_canvas", "crop_to_selection", "draw_text", "create_text_layer", "update_text_layer", "get_text_layer", "list_text_layers", "open_text_editor", "get_document_image", "export_document", "set_layer_properties", "duplicate_layer", "move_layer", "merge_layer_down", "flatten_image", "align_layer" })
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

static void CheckComposite()
{
    var imageIO = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.ImageIO")!;
    var services = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!;
    var cache = (Dictionary<string, object?>)services.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
    using var document = new Document(8, 6);
    var bottom = new BitmapLayer(8, 6);
    bottom.Surface.Fill(ColorBgra.FromBgra(0, 0, 255, 255));
    var top = new BitmapLayer(8, 6);
    top.Surface.Fill(ColorBgra.FromBgra(255, 0, 0, 128));
    top.Opacity = 128;
    document.Layers.Add(bottom); document.Layers.Add(top);
    cache["mainForm"] = new TestUiDispatcher();
    cache["appws"] = new TestCompositeApp { ActiveDocumentWorkspace = new TestCompositeWorkspace(document, top) };
    byte[] Capture()
    {
        object?[] args = ["composite", 0, 0];
        var pixels = (byte[])imageIO.GetMethod("ReadImageSource")!.Invoke(null, args)!;
        if ((int)args[1]! != 8 || (int)args[2]! != 6) throw new Exception("Composite dimensions");
        return pixels;
    }
    try
    {
        var pixels = Capture();
        if (Math.Abs(pixels[0] - 64) > 1 || pixels[1] != 0 || Math.Abs(pixels[2] - 191) > 1 || pixels[3] != 255)
            throw new Exception("Native composition must combine source alpha with layer opacity");
        top.Visible = false; pixels = Capture();
        if (pixels[0] != 0 || pixels[2] != 255 || pixels[3] != 255) throw new Exception("Hidden layer must be excluded");
        top.Visible = true; top.BlendMode = LayerBlendMode.Multiply; pixels = Capture();
        if (pixels[0] != 0 || Math.Abs(pixels[2] - 191) > 1) throw new Exception("Native Multiply blend mode must be used");
        bottom.Visible = false; top.Visible = false; pixels = Capture();
        if (pixels.Where((_, i) => i % 4 == 3).Any(a => a != 0)) throw new Exception("Hidden document must export transparency");
        if (document.Layers.Count != 2 || document.Width != 8 || document.Height != 6) throw new Exception("Preview must preserve document layers and size");
        Console.WriteLine("PASS native composition respects alpha, opacity, visibility, Multiply blend mode and source layers");
    }
    finally { cache.Clear(); }
}

static void CheckTextIdRenewal()
{
    var textLayers = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.TextLayers")!;
    void Renew(BitmapLayer layer) => textLayers.GetMethod("RenewId")!.Invoke(null, [layer]);
    using var text = new BitmapLayer(4, 4);
    var definition = new { SchemaVersion = 1, Id = "source", Parameters = new DrawTextParams { Text = "A" }, Width = 4, Height = 4, PixelHash = "h" };
    text.Metadata.SetUserValue("PaintDotNetMcp.Text.v1", JsonSerializer.Serialize(definition));
    Renew(text);
    using var stored = JsonDocument.Parse(text.Metadata.GetUserValue("PaintDotNetMcp.Text.v1")!);
    var root = stored.RootElement;
    if (root.GetProperty("Id").GetString() is not { Length: 32 } id || id == "source") throw new Exception("Duplicated text layer must get a new Id");
    if (root.GetProperty("Parameters").GetProperty("Text").GetString() != "A" || root.GetProperty("PixelHash").GetString() != "h")
        throw new Exception("Id renewal must keep the text definition");
    using var plain = new BitmapLayer(4, 4);
    Renew(plain);
    if (plain.Metadata.GetUserValue("PaintDotNetMcp.Text.v1") is not null) throw new Exception("Plain layers must stay without text metadata");
    Console.WriteLine("PASS duplicated text layer gets its own Id and keeps its definition");
}

static void CheckAnnotations()
{
    var asm = typeof(BridgeEffect).Assembly;
    object Op(string name, object parameters) => Activator.CreateInstance(asm.GetType("PaintDotNetMcp.Bridge." + name)!, parameters)!;
    void Apply(object op, Surface surface) => op.GetType().GetMethod("Apply")!.Invoke(op, [surface]);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    var clear = ColorBgra.FromBgra(0, 0, 0, 0);

    using var surface = new Surface(300, 200);
    surface.Fill(clear);
    Apply(Op("DrawRectOp", new DrawRectangleParams { X = 10, Y = 10, Width = 40, Height = 30, R = 255, Fill = true, CornerRadius = 8 }), surface);
    Check(surface[10, 10].A == 0 && surface[30, 25].A == 255 && surface[10, 25].A == 255, "Rounded rectangle clips corners only");

    surface.Fill(clear);
    Apply(Op("DrawArrowOp", new DrawArrowParams { X1 = 20, Y1 = 60, X2 = 180, Y2 = 60, R = 255 }), surface);
    Check(surface[180, 60].A == 255 && surface[183, 60].A == 0, "Arrow tip lands on the target");
    Check(surface[171, 57].A == 255 && surface[100, 57].A == 0 && surface[100, 60].A == 255, "Arrow head is wider than its shaft");
    surface.Fill(clear);
    Apply(Op("DrawArrowOp", new DrawArrowParams { X1 = 20, Y1 = 60, X2 = 180, Y2 = 60, R = 255, BothEnds = true }), surface);
    Check(surface[20, 60].A == 255 && surface[28, 57].A == 255 && surface[171, 57].A == 255, "Double-headed arrow");

    surface.Fill(clear);
    Apply(Op("DrawMarkerOp", new DrawMarkerParams { X = 50, Y = 50, Label = "1", Radius = 16 }), surface);
    Check(surface[64, 50].R == 220 && surface[68, 50].A == 0, "Marker circle has the requested radius");
    int minX = 999, minY = 999, maxX = -1, maxY = -1;
    for (int y = 30; y < 70; y++)
        for (int x = 30; x < 70; x++)
            if (surface[x, y].G > 200) { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
    Check(maxX >= 0 && Math.Abs((minX + maxX) / 2.0 - 50) <= 2 && Math.Abs((minY + maxY) / 2.0 - 50) <= 2, "Marker label is visually centered");

    surface.Fill(clear);
    var callout = Op("DrawCalloutOp", new DrawCalloutParams { X = 20, Y = 20, Text = "SPD", TargetX = 250, TargetY = 150 });
    Apply(callout, surface);
    var box = JsonSerializer.SerializeToElement(callout.GetType().GetProperty("Info")!.GetValue(callout)).GetProperty("box");
    int bw = box.GetProperty("width").GetInt32(), bh = box.GetProperty("height").GetInt32();
    Check(bw > 16 && bh > 16 && bw < 120 && bh < 60, "Callout box fits its text plus padding");
    Check(surface[20, 20].A == 0 && surface[30, 20] == ColorBgra.FromBgra(0, 0, 0, 255), "Callout box has rounded corners and a border");
    Check(surface[23, 20 + bh / 2] == ColorBgra.FromBgra(255, 255, 255, 255), "Callout box is filled");
    Check(surface[250, 150].A == 255 && surface[250, 30].A == 0, "Callout leader reaches the target");
    Console.WriteLine("PASS annotation primitives: rounded rectangle, arrow, marker, callout");
}

static void CheckLayerTransforms()
{
    var type = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.TransformLayerOp")!;
    void Apply(Surface surface, TransformLayerParams parameters)
        => type.GetMethod("Apply")!.Invoke(Activator.CreateInstance(type, parameters), [surface]);
    static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    using var surface = new Surface(4, 4);
    var clear = ColorBgra.FromBgra(0, 0, 0, 0);
    var red = ColorBgra.FromBgra(0, 0, 255, 255);
    surface.Fill(clear); surface[0, 1] = red;
    Apply(surface, new() { OffsetX = 1, OffsetY = 2, Interpolation = "nearest" });
    Check(surface[1, 3] == red && surface[0, 1].A == 0, "Translation moves pixels and clears old location");
    surface.Fill(clear); surface[0, 1] = red;
    Apply(surface, new() { AngleDegrees = 90, Interpolation = "nearest" });
    Check(surface[2, 0] == red && surface[0, 1].A == 0, "Clockwise rotation around canvas center");
    surface.Fill(clear); surface[0, 0] = red;
    Apply(surface, new() { ScaleX = 2, ScaleY = 2, PivotX = 0, PivotY = 0, Interpolation = "nearest" });
    Check(surface[0, 0] == red && surface[1, 1] == red && surface[2, 0].A == 0, "Scale around explicit origin");
    surface.Fill(clear); surface[0, 0] = red; surface[1, 0] = ColorBgra.FromBgra(0, 255, 0, 0);
    Apply(surface, new() { OffsetX = 0.5 });
    Check(surface[1, 0].R == 255 && surface[1, 0].G == 0 && surface[1, 0].A == 128, "Bilinear alpha interpolation avoids transparent-color halos");
    surface.Fill(clear); surface[3, 3] = red;
    Apply(surface, new() { OffsetX = 1, OffsetY = 1 });
    Check(Enumerable.Range(0, 16).All(i => surface[i % 4, i / 4].A == 0), "Pixels outside canvas are clipped");
    surface.Fill(red);
    Apply(surface, new());
    Check(Enumerable.Range(0, 16).All(i => surface[i % 4, i / 4] == red), "Identity preserves every pixel");
    Console.WriteLine("PASS layer translation, clockwise rotation, pivot scaling, alpha-aware interpolation, clipping, and identity");

    var align = typeof(BridgeEffect).Assembly.GetType("PaintDotNetMcp.Bridge.AlignLayerOp")!;
    void Align(Surface s, AlignLayerParams parameters)
        => align.GetMethod("Apply")!.Invoke(Activator.CreateInstance(align, parameters), [s]);
    using var canvas = new Surface(10, 8);
    // Red 2x2 block at (1,1); blue pixel marks its top-left so orientation is checked too.
    void Block() { canvas.Fill(clear); for (int i = 0; i < 4; i++) canvas[1 + i % 2, 1 + i / 2] = red; canvas[1, 1] = ColorBgra.FromBgra(255, 0, 0, 255); }
    List<(int X, int Y)> Opaque() => Enumerable.Range(0, 80).Where(i => canvas[i % 10, i / 10].A > 0).Select(i => (i % 10, i / 10)).ToList();
    Block(); Align(canvas, new() { Horizontal = "center", Vertical = "middle" });
    Check(Opaque().SequenceEqual([(4, 3), (5, 3), (4, 4), (5, 4)]) && canvas[4, 3].B == 255 && canvas[5, 4] == red, "Center moves content losslessly");
    Block(); Align(canvas, new() { Horizontal = "right", Vertical = "bottom", Margin = 1 });
    Check(Opaque().SequenceEqual([(7, 5), (8, 5), (7, 6), (8, 6)]), "Right/bottom respects margin");
    Block(); Align(canvas, new() { Horizontal = "left" });
    Check(Opaque().SequenceEqual([(0, 1), (1, 1), (0, 2), (1, 2)]), "Omitted axis keeps its position");
    Block(); Align(canvas, new() { Fit = "contain", Interpolation = "nearest" });
    var fitted = Opaque();
    Check(fitted.Count == 64 && fitted.Min(p => p.X) == 1 && fitted.Max(p => p.X) == 8 && fitted.Min(p => p.Y) == 0 && fitted.Max(p => p.Y) == 7,
        "Contain scales uniformly to the limiting side and centers the other");
    Block(); Align(canvas, new() { Horizontal = "left", Vertical = "top", TargetX = 6, TargetY = 4, TargetWidth = 4, TargetHeight = 4 });
    Check(Opaque().SequenceEqual([(6, 4), (7, 4), (6, 5), (7, 5)]), "Explicit target box");
    canvas.Fill(clear); Align(canvas, new() { Horizontal = "center" });
    Check(Opaque().Count == 0, "Empty layer is left alone");
    foreach (var bad in new AlignLayerParams[] { new(), new() { Horizontal = "middle" }, new() { Fit = "stretch" }, new() { Horizontal = "left", Margin = -1 },
        new() { Horizontal = "left", TargetX = 0 }, new() { Horizontal = "left", TargetX = 0, TargetY = 0, TargetWidth = 4, TargetHeight = 4, Margin = 2 } })
        Check(Throws(() => Activator.CreateInstance(align, bad)), "Invalid align input rejected");
    static bool Throws(Action action) { try { action(); return false; } catch (TargetInvocationException) { return true; } }
    Console.WriteLine("PASS align_layer center, margin, kept axis, contain fit, target box, empty layer and input validation");
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

sealed class TestCompositeApp
{
    public TestCompositeWorkspace ActiveDocumentWorkspace { get; set; } = null!;
}

sealed class TestCompositeWorkspace(Document document, BitmapLayer layer)
{
    public Document Document { get; } = document;
    public BitmapLayer ActiveLayer { get; } = layer;
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
