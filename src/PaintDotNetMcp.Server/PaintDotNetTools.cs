using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Server;

[McpServerToolType]
public sealed class PaintDotNetTools(BridgeClient bridge)
{
    // ---- Connectivity ------------------------------------------------------

    [McpServerTool, Description(
        "Ping the Paint.NET MCP Bridge plugin. Returns version, whether a document is open, " +
        "canvas dimensions, pending op count, and whether auto-commit is available. Requires " +
        "Paint.NET running with the bridge effect having been invoked at least once " +
        "(Effects > Tools > MCP Bridge).")]
    public async Task<string> Ping(CancellationToken ct)
    {
        var result = await bridge.CallAsync("ping", null, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Force-commit any queued operations by triggering Paint.NET's 'Repeat last effect' " +
        "(Ctrl+F) on the main window. Best-effort; if it fails the user must invoke " +
        "Effects > Tools > MCP Bridge manually. Returns auto_triggered=true on success.")]
    public async Task<string> Commit(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("commit", null, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Toggle automatic commit. When enabled (default), each queued op tries to trigger Ctrl+F " +
        "after a short debounce. Disable when you want to batch many ops and commit explicitly via " +
        "the commit tool.")]
    public async Task<string> SetAutoCommit(bool enabled, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("set_auto_commit", new SetAutoCommitParams { Enabled = enabled }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- Drawing primitives (queued) ---------------------------------------

    [McpServerTool, Description(
        "Queue a fill operation on the active layer. The fill applies on the next render pass " +
        "(auto-committed when possible; otherwise user must invoke Effects > Tools > MCP Bridge). " +
        "If x/y/width/height are omitted the entire surface is filled.")]
    public async Task<string> Fill(
        [Description("Red 0-255")] byte r,
        [Description("Green 0-255")] byte g,
        [Description("Blue 0-255")] byte b,
        [Description("Alpha 0-255 (default 255)")] byte a = 255,
        [Description("Optional X")] int? x = null,
        [Description("Optional Y")] int? y = null,
        [Description("Optional width")] int? width = null,
        [Description("Optional height")] int? height = null,
        CancellationToken ct = default)
    {
        var p = new FillParams { R = r, G = g, B = b, A = a, X = x, Y = y, Width = width, Height = height };
        var res = await bridge.CallAsync("fill", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a rectangle draw on the active layer. Stroked by default; set fill=true for a filled box. " +
        "Applies on the next render pass.")]
    public async Task<string> DrawRectangle(
        int x, int y, int width, int height,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 1,
        bool fill = false,
        CancellationToken ct = default)
    {
        var p = new DrawRectangleParams
        {
            X = x, Y = y, Width = width, Height = height,
            R = r, G = g, B = b, A = a,
            Thickness = thickness, Fill = fill,
        };
        var res = await bridge.CallAsync("draw_rect", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a line draw between (x1,y1) and (x2,y2) on the active layer. Thickness is square-pixel.")]
    public async Task<string> DrawLine(
        int x1, int y1, int x2, int y2,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 1,
        CancellationToken ct = default)
    {
        var p = new DrawLineParams
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            R = r, G = g, B = b, A = a, Thickness = thickness,
        };
        var res = await bridge.CallAsync("draw_line", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue an ellipse draw inside the bounding box (x,y,width,height) on the active layer. " +
        "Stroked by default; set fill=true for a filled ellipse.")]
    public async Task<string> DrawEllipse(
        int x, int y, int width, int height,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 1,
        bool fill = false,
        CancellationToken ct = default)
    {
        var p = new DrawEllipseParams
        {
            X = x, Y = y, Width = width, Height = height,
            R = r, G = g, B = b, A = a, Thickness = thickness, Fill = fill,
        };
        var res = await bridge.CallAsync("draw_ellipse", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a polygon draw on the active layer. Points is a JSON array of {\"x\":int,\"y\":int}. " +
        "Stroked by default; set fill=true for a filled polygon (even-odd rule). " +
        "Closed=true (default) connects last vertex back to first.")]
    public async Task<string> DrawPolygon(
        [Description("JSON array of points, e.g. [{\"x\":10,\"y\":10},{\"x\":50,\"y\":10},{\"x\":30,\"y\":40}]")]
        string pointsJson,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 1,
        bool fill = false,
        bool closed = true,
        CancellationToken ct = default)
    {
        List<Point2I>? pts;
        try { pts = JsonSerializer.Deserialize<List<Point2I>>(pointsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { throw new ArgumentException("pointsJson invalid: " + ex.Message); }
        if (pts is null || pts.Count < 2) throw new ArgumentException("pointsJson must contain at least 2 points");

        var p = new DrawPolygonParams
        {
            Points = pts, R = r, G = g, B = b, A = a,
            Thickness = thickness, Fill = fill, Closed = closed,
        };
        var res = await bridge.CallAsync("draw_polygon", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue text rendering on the active layer at (x,y) using a system font. Anti-aliased by default. " +
        "Note: requires the named font to exist on the host; falls back to a generic sans-serif if not.")]
    public async Task<string> DrawText(
        int x, int y,
        string text,
        byte r, byte g, byte b,
        byte a = 255,
        string fontFamily = "Segoe UI",
        float fontSize = 16f,
        bool bold = false,
        bool italic = false,
        bool antiAlias = true,
        CancellationToken ct = default)
    {
        var p = new DrawTextParams
        {
            X = x, Y = y, Text = text,
            FontFamily = fontFamily, FontSize = fontSize, Bold = bold, Italic = italic,
            R = r, G = g, B = b, A = a, AntiAlias = antiAlias,
        };
        var res = await bridge.CallAsync("draw_text", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a flood fill (paint bucket) starting at seed pixel (x,y). " +
        "Tolerance per channel 0-255 (0 = exact match).")]
    public async Task<string> FloodFill(
        int x, int y,
        byte r, byte g, byte b,
        byte a = 255,
        int tolerance = 0,
        CancellationToken ct = default)
    {
        var p = new FloodFillParams { X = x, Y = y, R = r, G = g, B = b, A = a, Tolerance = tolerance };
        var res = await bridge.CallAsync("flood_fill", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a gradient fill. Mode is 'linear' (default) or 'radial'. " +
        "(x1,y1)→(x2,y2) defines the gradient axis. Optional bounds restrict the fill region.")]
    public async Task<string> GradientFill(
        int x1, int y1, int x2, int y2,
        byte r1, byte g1, byte b1,
        byte r2, byte g2, byte b2,
        byte a1 = 255, byte a2 = 255,
        string mode = "linear",
        int? x = null, int? y = null, int? width = null, int? height = null,
        CancellationToken ct = default)
    {
        var p = new GradientFillParams
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
            R1 = r1, G1 = g1, B1 = b1, A1 = a1,
            R2 = r2, G2 = g2, B2 = b2, A2 = a2,
            Mode = mode, X = x, Y = y, Width = width, Height = height,
        };
        var res = await bridge.CallAsync("gradient_fill", p, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- Image I/O ---------------------------------------------------------

    [McpServerTool, Description(
        "Queue pasting a base64-encoded image (PNG / WebP / JPEG; auto-detected) onto the active layer " +
        "at (x,y). BlendMode 'normal' (alpha-over, default) or 'replace' (overwrite RGBA verbatim).")]
    public async Task<string> PasteImage(
        [Description("base64-encoded image bytes (PNG / WebP / JPEG)")] string pngBase64,
        int x, int y,
        string blendMode = "normal",
        CancellationToken ct = default)
    {
        var p = new PasteImageParams { PngBase64 = pngBase64, X = x, Y = y, BlendMode = blendMode };
        var res = await bridge.CallAsync("paste_image", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Get the current canvas as an encoded image (base64). Optional crop. Reads from the bridge's " +
        "last-rendered snapshot — invoke Effects > Tools > MCP Bridge once to seed it. If maybe_stale=true " +
        "in the result, queued ops haven't been committed yet. " +
        "Format options: 'png' (default), 'webp', 'jpeg'. Quality 1-100 applies to lossy formats.")]
    public async Task<string> GetCanvasPng(
        int? x = null, int? y = null, int? width = null, int? height = null,
        [Description("'auto' | 'png' | 'webp' | 'jpeg'. Default 'auto' (= png here).")] string format = "auto",
        [Description("Lossy quality 1-100. Ignored for PNG.")] int quality = 85,
        CancellationToken ct = default)
    {
        var p = new GetCanvasPngParams
        {
            X = x, Y = y, Width = width, Height = height,
            Format = format, Quality = quality,
        };
        var res = await bridge.CallAsync("get_canvas_png", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Save the current canvas (or a region) as an image file on the host filesystem. Path must be " +
        "absolute. Format auto-detected from file extension (.png, .webp, .jpg/.jpeg) or specified " +
        "explicitly. Uses the bridge's last-rendered snapshot.")]
    public async Task<string> SavePng(
        [Description("Absolute path on host. Extension drives format if format='auto'.")] string path,
        int? x = null, int? y = null, int? width = null, int? height = null,
        [Description("'auto' (from extension) | 'png' | 'webp' | 'jpeg'.")] string format = "auto",
        [Description("Lossy quality 1-100. Ignored for PNG.")] int quality = 85,
        CancellationToken ct = default)
    {
        var p = new SavePngParams
        {
            Path = path, X = x, Y = y, Width = width, Height = height,
            Format = format, Quality = quality,
        };
        var res = await bridge.CallAsync("save_png", p, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- Region / matting --------------------------------------------------

    [McpServerTool, Description(
        "Extract a rectangular region from the canvas as an encoded image. Optionally save to disk. " +
        "When savePath is provided, includeBase64 defaults to false (avoids huge response payloads); " +
        "set it explicitly to true to also receive base64. Uses the last-rendered snapshot.")]
    public async Task<string> ExtractRegion(
        int x, int y, int width, int height,
        [Description("Optional absolute path to also save the region. Extension drives format if format='auto'.")] string? savePath = null,
        [Description("'auto' | 'png' | 'webp' | 'jpeg'.")] string format = "auto",
        [Description("Lossy quality 1-100.")] int quality = 85,
        [Description("Whether to embed the encoded bytes in the response. null = auto (false if savePath set).")] bool? includeBase64 = null,
        CancellationToken ct = default)
    {
        var p = new ExtractRegionParams
        {
            X = x, Y = y, Width = width, Height = height,
            SavePath = savePath,
            Format = format, Quality = quality, IncludeBase64 = includeBase64,
        };
        var res = await bridge.CallAsync("extract_region", p, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- Object detection (v0.4) -------------------------------------------

    [McpServerTool, Description(
        "Detect distinct objects (e.g. icons on a uniform background) using connected-components. " +
        "Auto-samples the four corners of the region for the background color unless bgR/G/B given. " +
        "Returns sorted bounding boxes (top-to-bottom, left-to-right). Tune with: tolerance " +
        "(color distance), minSize/maxSize (px), padding (expand bbox), groupGap (merge nearby " +
        "fragments), maxAspectRatio (drop wide text rows), minArea (drop sparse noise).")]
    public async Task<string> DetectObjects(
        int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null,
        byte? bgR = null, byte? bgG = null, byte? bgB = null,
        int tolerance = 32,
        int minSize = 16,
        int maxSize = int.MaxValue,
        int padding = 0,
        int groupGap = 0,
        [Description("Override groupGap on X axis (null = use groupGap). Useful when fragments are split horizontally (chart bars, side-by-side compound icons).")] int? groupGapX = null,
        [Description("Override groupGap on Y axis (null = use groupGap). Keep this small to prevent merging an icon with its caption text below.")] int? groupGapY = null,
        double maxAspectRatio = 6.0,
        int minArea = 0,
        CancellationToken ct = default)
    {
        var p = new DetectObjectsParams
        {
            RegionX = regionX, RegionY = regionY, RegionW = regionW, RegionH = regionH,
            BgR = bgR, BgG = bgG, BgB = bgB,
            Tolerance = tolerance, MinSize = minSize, MaxSize = maxSize,
            Padding = padding, GroupGap = groupGap,
            GroupGapX = groupGapX, GroupGapY = groupGapY,
            MaxAspectRatio = maxAspectRatio, MinArea = minArea,
        };
        var res = await bridge.CallAsync("detect_objects", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Detect objects AND save each one to disk in a single call. savePathTemplate placeholders: " +
        "{i} 0-based index, {n} 1-based, {x}/{y}/{w}/{h} bbox coords. Format specifiers supported, " +
        "e.g. \"C:\\\\out\\\\icon_{i:000}.webp\" → icon_000.webp, icon_001.webp, ... " +
        "Format auto-detected from template extension (.png/.webp/.jpg) or set explicitly.")]
    public async Task<string> ExtractObjects(
        [Description("e.g. \"C:\\\\out\\\\icon_{i:000}.webp\"")] string savePathTemplate,
        int? regionX = null, int? regionY = null, int? regionW = null, int? regionH = null,
        byte? bgR = null, byte? bgG = null, byte? bgB = null,
        int tolerance = 32,
        int minSize = 16,
        int maxSize = int.MaxValue,
        int padding = 0,
        int groupGap = 0,
        [Description("Override groupGap on X axis (null = use groupGap). Useful when fragments are split horizontally (chart bars, side-by-side compound icons).")] int? groupGapX = null,
        [Description("Override groupGap on Y axis (null = use groupGap). Keep this small to prevent merging an icon with its caption text below.")] int? groupGapY = null,
        double maxAspectRatio = 6.0,
        int minArea = 0,
        string format = "auto",
        int quality = 85,
        bool includeBase64 = false,
        CancellationToken ct = default)
    {
        var p = new ExtractObjectsParams
        {
            SavePathTemplate = savePathTemplate,
            RegionX = regionX, RegionY = regionY, RegionW = regionW, RegionH = regionH,
            BgR = bgR, BgG = bgG, BgB = bgB,
            Tolerance = tolerance, MinSize = minSize, MaxSize = maxSize,
            Padding = padding, GroupGap = groupGap,
            GroupGapX = groupGapX, GroupGapY = groupGapY,
            MaxAspectRatio = maxAspectRatio, MinArea = minArea,
            Format = format, Quality = quality, IncludeBase64 = includeBase64,
        };
        var res = await bridge.CallAsync("extract_objects", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Background removal (누끼 따기) on a region. Methods:\n" +
        "  - 'color_key' (default): pixels close to keyR/G/B become transparent\n" +
        "  - 'auto_corners': sample the four corners of the region as the bg color\n" +
        "  - 'ai': shell out to rembg CLI for U^2-Net matting (handles hair, gradients, etc).\n" +
        "         Requires `pip install rembg[cli]`. First run downloads ~170MB model.\n" +
        "Tolerance 0-441 (RGB euclid) applies to color_key/auto_corners only. Feather grades alpha " +
        "smoothly with distance. savePath writes the matted image (extension drives format). " +
        "applyToLayer pushes the matted region back onto the active layer (always lossless PNG internally). " +
        "When savePath is set, includeBase64 defaults to false to keep responses small.")]
    public async Task<string> RemoveBackground(
        int x, int y, int width, int height,
        [Description("'color_key' | 'auto_corners' | 'ai'")] string method = "color_key",
        byte? keyR = null, byte? keyG = null, byte? keyB = null,
        int tolerance = 32,
        bool feather = true,
        string? savePath = null,
        bool applyToLayer = false,
        [Description("rembg model name when method=ai (u2net, u2netp, isnet-general-use, ...). Empty = default.")] string aiModel = "",
        [Description("'auto' | 'png' | 'webp' | 'jpeg'.")] string format = "auto",
        [Description("Lossy quality 1-100.")] int quality = 85,
        [Description("Whether to embed the encoded bytes in the response. null = auto (false if savePath set).")] bool? includeBase64 = null,
        CancellationToken ct = default)
    {
        var p = new RemoveBackgroundParams
        {
            X = x, Y = y, Width = width, Height = height,
            Method = method, AiModel = aiModel,
            KeyR = keyR, KeyG = keyG, KeyB = keyB,
            Tolerance = tolerance, Feather = feather,
            SavePath = savePath, ApplyToLayer = applyToLayer,
            Format = format, Quality = quality, IncludeBase64 = includeBase64,
        };
        var res = await bridge.CallAsync("remove_background", p, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.5 Layer management (reflection) --------------------------------

    [McpServerTool, Description(
        "List all layers in the active document with index, name, dimensions, visibility, " +
        "and which one is active. Reflection-based; may return ok=false on unfamiliar Paint.NET builds.")]
    public async Task<string> ListLayers(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("list_layers", null, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Add a new transparent BitmapLayer to the active document. Reflection-based.")]
    public async Task<string> AddLayer(string name = "Layer", CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("add_layer", new AddLayerParams { Name = name }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Delete the layer at the given index. Cannot remove the only remaining layer. Reflection-based.")]
    public async Task<string> DeleteLayer(int index, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("delete_layer", new DeleteLayerParams { Index = index }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Set the active (selected) layer by index. Subsequent drawing ops target this layer. Reflection-based.")]
    public async Task<string> SelectLayer(int index, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("select_layer", new SelectLayerParams { Index = index }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.5 Save .pdn -----------------------------------------------------

    [McpServerTool, Description(
        "Save the active document as a .pdn file at the given absolute path. Reflection-based; " +
        "probes Document.Save / SaveAsync signatures.")]
    public async Task<string> SavePdn(
        [Description("Absolute path on host, e.g. C:\\\\Users\\\\me\\\\artwork.pdn")] string path,
        CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("save_pdn", new SavePdnParams { Path = path }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.5 Built-in effects ---------------------------------------------

    [McpServerTool, Description(
        "Enumerate all built-in Paint.NET effects discovered via reflection. Returns Name (short), " +
        "FullName (namespace-qualified), Category, and Assembly. Use the result's Name with apply_effect.")]
    public async Task<string> ListEffects(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("list_effects", null, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Apply a built-in Paint.NET effect by name to the active layer. v0.5 uses default settings " +
        "(no property bag yet). Reflection-based; probes RunEffect / PerformEffect on the workspace.")]
    public async Task<string> ApplyEffect(
        [Description("Short class name (e.g. \"GaussianBlurEffect\") or full namespace name.")] string name,
        CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("apply_effect", new ApplyEffectParams { Name = name }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.6 Selection -----------------------------------------------------

    [McpServerTool, Description(
        "Set a rectangular selection. All drawing ops will be clipped to this region. " +
        "Tries to set Paint.NET's native selection via reflection; falls back to a software-side " +
        "selection that the bridge enforces internally (Paint.NET UI won't show it in that case).")]
    public async Task<string> SetSelectionRect(int x, int y, int width, int height, CancellationToken ct = default)
    {
        var p = new SetSelectionRectParams { X = x, Y = y, Width = width, Height = height };
        var res = await bridge.CallAsync("set_selection_rect", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Set a polygon selection. JSON array of {\"x\":int,\"y\":int}. Software-side only " +
        "(Paint.NET native polygon selection via reflection is unreliable).")]
    public async Task<string> SetSelectionPolygon(
        [Description("JSON array of points, e.g. [{\"x\":10,\"y\":10},{\"x\":50,\"y\":10},{\"x\":30,\"y\":40}]")]
        string pointsJson,
        CancellationToken ct = default)
    {
        List<Point2I>? pts;
        try { pts = JsonSerializer.Deserialize<List<Point2I>>(pointsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (Exception ex) { throw new ArgumentException("pointsJson invalid: " + ex.Message); }
        if (pts is null || pts.Count < 3) throw new ArgumentException("pointsJson must contain at least 3 points");

        var res = await bridge.CallAsync("set_selection_polygon", new SetSelectionPolygonParams { Points = pts }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Clear any active selection (both native and software-side).")]
    public async Task<string> ClearSelection(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("clear_selection", null, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.6 OCR -----------------------------------------------------------

    [McpServerTool, Description(
        "Run OCR on a rectangular region of the canvas using the Tesseract CLI. Returns recognized text. " +
        "Requires tesseract installed on PATH (winget install UB-Mannheim.TesseractOCR). " +
        "Lang follows Tesseract conventions: 'eng', 'kor', 'eng+kor', etc.")]
    public async Task<string> OcrRegion(
        int x, int y, int width, int height,
        string lang = "eng",
        CancellationToken ct = default)
    {
        var p = new OcrRegionParams { X = x, Y = y, Width = width, Height = height, Lang = lang };
        var res = await bridge.CallAsync("ocr_region", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Diagnostic: dump every interface in loaded PaintDotNet.* assemblies whose name looks " +
        "service-shaped (Document/Workspace/Layer/Effect/App/...) and report which ones resolve " +
        "through the captured IServiceProvider. Use when v0.5 reflection-based tools (list_layers, " +
        "save_pdn, apply_effect, ...) return ok=false — the result tells you which type names to " +
        "add to AppServices.cs candidate arrays.")]
    public async Task<string> DiagnoseServices(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("diagnose_services", null, ct);
        return res?.ToString() ?? "{}";
    }
}
