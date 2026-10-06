using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using ModelContextProtocol.Protocol;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Server;

[McpServerToolType]
public sealed class PaintDotNetTools(BridgeClient bridge)
{
    [McpServerTool, Description("Create an MCP-editable text layer above the active layer. Stores text/font/position/color in native layer metadata, including in saved .pdn files. Renders the whole text layer without selection clipping. One native Undo step; finish pending drawing or a batch first. Edit it later with update_text_layer; Paint.NET sees a bitmap layer, not a native text object.")]
    public async Task<string> CreateTextLayer(string text, int x = 0, int y = 0, string name = "Text",
        string fontFamily = "Segoe UI", float fontSize = 16, bool bold = false, bool italic = false,
        byte r = 0, byte g = 0, byte b = 0, byte a = 255, bool antiAlias = true, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("create_text_layer", new CreateTextLayerParams
        {
            Name = name, Text = new DrawTextParams { Text = text, X = x, Y = y, FontFamily = fontFamily,
                FontSize = fontSize, Bold = bold, Italic = italic, R = r, G = g, B = b, A = a, AntiAlias = antiAlias }
        }, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Update saved text layer properties and regenerate its pixels from text. Omitted properties are preserved; layerIndex=-1 means active layer. One native Undo step (zero for identical settings), preserving layer visibility, opacity and blend mode. If pixels or canvas size changed, rejects replacement unless replaceModifiedPixels=true explicitly permits regenerating the entire layer. Prefer x/y/fontSize edits here to moving/scaling rasterized text with transform_layer.")]
    public async Task<string> UpdateTextLayer(int layerIndex = -1, string? text = null, int? x = null, int? y = null,
        string? name = null, string? fontFamily = null, float? fontSize = null, bool? bold = null,
        bool? italic = null, byte? r = null, byte? g = null, byte? b = null, byte? a = null,
        bool? antiAlias = null, bool replaceModifiedPixels = false, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("update_text_layer", new UpdateTextLayerParams
        {
            LayerIndex = layerIndex, Text = text, X = x, Y = y, Name = name, FontFamily = fontFamily,
            FontSize = fontSize, Bold = bold, Italic = italic, R = r, G = g, B = b, A = a,
            AntiAlias = antiAlias, ReplaceModifiedPixels = replaceModifiedPixels
        }, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Read an MCP text layer's stored text, font, position, color and PixelsModified status. layerIndex=-1 means active layer. Other bitmap layers have no editable text definition.")]
    public async Task<string> GetTextLayer(int layerIndex = -1, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("get_text_layer", new TextLayerIndexParams { LayerIndex = layerIndex }, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("List all MCP-editable text layers in the active document, including stored properties and whether pixels changed after rendering. No Undo history is added.")]
    public async Task<string> ListTextLayers(CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("list_text_layers", null, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Open the native Paint.NET text editor window for the active MCP text layer, so the user can edit text, font, size, position and color directly. Returns after showing the window; it does not apply edits. The user chooses Apply or Cancel. Also available from Paint.NET's MCP > Text edit menu. Finish pending drawing/batches first; ordinary bitmap text cannot be edited.")]
    public async Task<string> OpenTextEditor(CancellationToken ct = default)
        => (await bridge.CallAsync("open_text_editor", null, ct))?.ToString() ?? "{}";

    [McpServerTool, Description("Preview the full document as an MCP image content block. Uses Paint.NET's native composition of visible layers, including opacity and blend modes. Optional crop; formats png, webp or jpeg. Leaves source layers and selection intact and adds no Undo history. Finish a drawing batch first. Uncommitted interactive tool overlays are not included.")]
    public async Task<CallToolResult> GetDocumentImage(int? x = null, int? y = null, int? width = null, int? height = null,
        string format = "png", int quality = 85, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("get_canvas_png", new GetCanvasPngParams
            { Source = "composite", X = x, Y = y, Width = width, Height = height, Format = format, Quality = quality }, ct);
        var value = result!.Value.Deserialize<GetCanvasPngResult>()!;
        string data = value.ImageBase64;
        value.ImageBase64 = "";
        return new CallToolResult { Content = [
            new TextContentBlock { Text = JsonSerializer.Serialize(value) },
            new ImageContentBlock { Data = data, MimeType = value.MimeType }
        ] };
    }

    [McpServerTool, Description("Export the full document's visible-layer composition to an absolute file path, without flattening or changing the source document. Uses native layer opacity and blend modes. PNG preserves transparency; WebP/JPEG quality applies to lossy encoding. Optional crop. Finish pending drawing or a batch first.")]
    public async Task<string> ExportDocument(string path, int? x = null, int? y = null, int? width = null, int? height = null,
        string format = "auto", int quality = 85, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("save_png", new SavePngParams
            { Source = "composite", Path = path, X = x, Y = y, Width = width, Height = height, Format = format, Quality = quality }, ct);
        return result?.ToString() ?? "{}";
    }
    [McpServerTool, Description("Copy the active layer's selected pixels into a new transparent layer directly above it, preserving canvas coordinates and source pixels. Requires a native selection. Uses pixel coverage scans, without feathering. Selects the new layer; one native Undo step. Clear selection before moving the whole new layer with transform_layer. Finish pending drawing or an active batch first.")]
    public async Task<string> CopySelectionToLayer(string name = "Selection", CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("copy_selection_to_layer", new CopySelectionToLayerParams { Name = name }, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Change canvas dimensions without rescaling pixels, for all layers. Anchor: top_left, top, top_right, left, center, right, bottom_left, bottom, bottom_right. Added space uses RGBA fill, transparent by default. Smaller dimensions clip pixels. Clears selection; one native Undo step, or zero for unchanged size. Finish pending drawing or a batch first.")]
    public async Task<string> ResizeCanvas(int width, int height, string anchor = "center", byte r = 0, byte g = 0, byte b = 0, byte a = 0, CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("resize_canvas", new ResizeCanvasParams { Width = width, Height = height, Anchor = anchor, R = r, G = g, B = b, A = a }, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Crop all layers to the current native selection using Paint.NET's Crop to Selection. Nonrectangular selections mask pixels outside their shape to transparency. Requires a nonempty selection; clears selection and creates one native Undo step. Finish pending drawing or a batch first.")]
    public async Task<string> CropToSelection(CancellationToken ct = default)
    {
        var result = await bridge.CallAsync("crop_to_selection", null, ct);
        return result?.ToString() ?? "{}";
    }

    // ---- Connectivity ------------------------------------------------------

    [McpServerTool, Description(
        "Ping the Paint.NET MCP Bridge plugin. Returns version, whether a document is open, " +
        "canvas dimensions, pending op count, snapshot readiness, ConnectionStatus and RecoveryAction. " +
        "Open Paint.NET and a canvas; the Bridge starts during plugin discovery and initializes " +
        "the snapshot automatically. The Tools menu is a fallback for connection problems.")]
    public async Task<string> Ping(CancellationToken ct)
    {
        var result = await bridge.CallAsync("ping", null, ct);
        return result?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Request execution of the registered MCP Bridge effect on Paint.NET's UI thread " +
        "to apply queued operations. Best-effort; if it fails the user must invoke " +
        "Effects > Tools > MCP Bridge manually. AutoTriggered means a trigger was sent, not " +
        "render completion. Call wait_for_idle to confirm rendering and snapshot readiness.")]
    public async Task<string> Commit(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("commit", null, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Wait until all currently queued drawing operations have rendered every requested tile " +
        "and updated the read/save snapshot. Does not trigger a commit. Times out with an error " +
        "if rendering never starts, is cancelled, or remains incomplete. Paint.NET undo-history " +
        "acceptance is not observed. Snapshot read/save tools also wait automatically up to 5 seconds.")]
    public async Task<string> WaitForIdle(int timeoutMs = 5000, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("wait_for_idle", new WaitForIdleParams { TimeoutMs = timeoutMs }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Toggle automatic commit. When enabled (default), queued ops request direct execution " +
        "of MCP Bridge on the UI thread. Disable when you want to batch many ops and commit explicitly via " +
        "the commit tool.")]
    public async Task<string> SetAutoCommit(bool enabled, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("set_auto_commit", new SetAutoCommitParams { Enabled = enabled }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- Drawing primitives (queued) ---------------------------------------

    [McpServerTool, Description(
        "Transform pixels on the active layer: scale around the pivot, rotate clockwise, then translate. " +
        "Offsets are pixels; scales are positive factors (1 unchanged, 0.5 half, 2 double); angle is degrees. " +
        "Pivot defaults to the canvas center, with coordinates measured from the top-left canvas edge. " +
        "Canvas size stays fixed: pixels outside are clipped and uncovered pixels become transparent. " +
        "Interpolation is bilinear (smooth, alpha-aware) or nearest (pixel art). " +
        "The current selection clips the destination; clear_selection first to transform the entire layer. " +
        "Supports begin_batch/end_batch and native Undo/Redo. Queued like drawing: wait_for_idle before reading or saving.")]
    public async Task<string> TransformLayer(double offsetX = 0, double offsetY = 0,
        double scaleX = 1, double scaleY = 1, double angleDegrees = 0,
        double? pivotX = null, double? pivotY = null, string interpolation = "bilinear", CancellationToken ct = default)
        => (await bridge.CallAsync("transform_layer", new TransformLayerParams
        {
            OffsetX = offsetX, OffsetY = offsetY, ScaleX = scaleX, ScaleY = scaleY,
            AngleDegrees = angleDegrees, PivotX = pivotX, PivotY = pivotY, Interpolation = interpolation
        }, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Align the active layer's visible content (bounding box of pixels with alpha > 0) inside a target box, " +
        "so you don't compute offsets yourself. Target defaults to the whole canvas; pass targetX/Y/Width/Height together for another box. " +
        "margin insets the target on every side (snap-to-margin). horizontal: left|center|right; vertical: top|middle|bottom; " +
        "an omitted axis keeps its position unless fit is set, then it centers. fit: none (keep size), contain (scale to fit inside), " +
        "cover (scale to fill, overflow clipped); scaling is uniform. Large bilinear upscales feather edges about scale/2 px past the target (and margin); use interpolation=nearest for hard edges. Fully opaque layers (e.g. a background photo) already fill the canvas, so nothing moves. " +
        "Rasterizes editable text: for text layers prefer update_text_layer x/y. Same queue, selection clipping, batch and Undo rules as transform_layer.")]
    public async Task<string> AlignLayer(string? horizontal = null, string? vertical = null, string fit = "none", int margin = 0,
        int? targetX = null, int? targetY = null, int? targetWidth = null, int? targetHeight = null,
        string interpolation = "bilinear", CancellationToken ct = default)
        => (await bridge.CallAsync("align_layer", new AlignLayerParams
        {
            Horizontal = horizontal, Vertical = vertical, Fit = fit, Margin = margin, TargetX = targetX, TargetY = targetY,
            TargetWidth = targetWidth, TargetHeight = targetHeight, Interpolation = interpolation
        }, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Open an existing image or .pdn document by absolute local path in Paint.NET and activate it. " +
        "Uses Paint.NET's native file loader and immediately prepares the read/save snapshot. " +
        "Existing edited documents remain open. Finish any active batch or pending drawing first. " +
        "Paint.NET may show a format-specific loading or error dialog.")]
    public async Task<string> OpenImage(string path, CancellationToken ct = default)
        => (await bridge.CallAsync("open_image", new OpenImageParams { Path = path }, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Create and activate a new white canvas using Paint.NET's native document creation, at 96 DPI. " +
        "Width/height default to 800x600; each must be 1..16384 pixels, at most 64 million pixels total. " +
        "Prepares the read/save snapshot immediately and preserves existing edited documents. " +
        "Finish any active batch or pending drawing first.")]
    public async Task<string> NewCanvas(int width = 800, int height = 600, CancellationToken ct = default)
        => (await bridge.CallAsync("new_canvas", new NewCanvasParams { Width = width, Height = height }, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Begin a drawing batch on the active document and layer. Drawing is queued until end_batch " +
        "runs MCP Bridge once, producing one Undo step. Rejects nested batches and pending drawing. " +
        "Do not change the active tab, layer, selection, or edit manually during a batch.")]
    public async Task<string> BeginBatch(CancellationToken ct = default)
        => (await bridge.CallAsync("begin_batch", null, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Apply the active drawing batch in one MCP Bridge execution and return applied_operations " +
        "and history_steps. Restores the previous auto-commit setting. An empty batch creates no " +
        "history step. On rendering failure the batch remains active for retry.")]
    public async Task<string> EndBatch(CancellationToken ct = default)
        => (await bridge.CallAsync("end_batch", null, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Undo one native Paint.NET history step on the active document, including manual edits. " +
        "Refreshes the active-layer read/save snapshot without creating a new history step. " +
        "Rejects an active batch or unapplied drawing. Returns changed=false when no Undo is available.")]
    public async Task<string> Undo(CancellationToken ct = default)
        => (await bridge.CallAsync("undo", null, ct))?.ToString() ?? "{}";

    [McpServerTool, Description(
        "Redo one native Paint.NET history step and refresh the active-layer read/save snapshot " +
        "without clearing remaining Redo history. Rejects an active batch or unapplied drawing. " +
        "Returns changed=false when no Redo is available.")]
    public async Task<string> Redo(CancellationToken ct = default)
        => (await bridge.CallAsync("redo", null, ct))?.ToString() ?? "{}";

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
        "cornerRadius > 0 rounds the corners. Applies on the next render pass.")]
    public async Task<string> DrawRectangle(
        int x, int y, int width, int height,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 1,
        bool fill = false,
        int cornerRadius = 0,
        CancellationToken ct = default)
    {
        var p = new DrawRectangleParams
        {
            X = x, Y = y, Width = width, Height = height,
            R = r, G = g, B = b, A = a,
            Thickness = thickness, Fill = fill, CornerRadius = cornerRadius,
        };
        var res = await bridge.CallAsync("draw_rect", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue an arrow from (x1,y1) to (x2,y2) on the active layer; the filled head's tip lands exactly on (x2,y2). " +
        "headSize is the head length in px (0 = max(10, thickness*4)). bothEnds=true adds a head at (x1,y1).")]
    public async Task<string> DrawArrow(
        int x1, int y1, int x2, int y2,
        byte r, byte g, byte b,
        byte a = 255,
        int thickness = 3,
        int headSize = 0,
        bool bothEnds = false,
        CancellationToken ct = default)
    {
        var p = new DrawArrowParams
        {
            X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, R = r, G = g, B = b, A = a,
            Thickness = thickness, HeadSize = headSize, BothEnds = bothEnds,
        };
        var res = await bridge.CallAsync("draw_arrow", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a numbered marker: a filled circle centered at (x,y) with a bold label (1..8 chars, e.g. \"1\", \"A\", \"12\") " +
        "visually centered inside. The font auto-sizes to the radius (4..256). Default red circle, white label.")]
    public async Task<string> DrawMarker(
        int x, int y,
        string label,
        int radius = 16,
        byte r = 220, byte g = 30, byte b = 30,
        byte a = 255,
        byte textR = 255, byte textG = 255, byte textB = 255,
        string fontFamily = "Segoe UI",
        CancellationToken ct = default)
    {
        var p = new DrawMarkerParams
        {
            X = x, Y = y, Label = label, Radius = radius, R = r, G = g, B = b, A = a,
            TextR = textR, TextG = textG, TextB = textB, FontFamily = fontFamily,
        };
        var res = await bridge.CallAsync("draw_marker", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Queue a callout: a rounded box whose top-left is (x,y), auto-sized to the text plus padding, with an optional " +
        "leader arrow from the nearest box edge to (targetX,targetY). r/g/b color the text, border and leader; bg* is the box fill. " +
        "The response's info.box gives the computed box so further callouts can be placed without overlap. " +
        "Pixels, not an editable text layer.")]
    public async Task<string> DrawCallout(
        int x, int y,
        string text,
        byte r = 0, byte g = 0, byte b = 0,
        byte bgR = 255, byte bgG = 255, byte bgB = 255, byte bgA = 255,
        float fontSize = 16f,
        string fontFamily = "Segoe UI",
        bool bold = false,
        int borderThickness = 2,
        int padding = 8,
        int cornerRadius = 6,
        int? targetX = null, int? targetY = null,
        CancellationToken ct = default)
    {
        var p = new DrawCalloutParams
        {
            X = x, Y = y, Text = text, R = r, G = g, B = b,
            BgR = bgR, BgG = bgG, BgB = bgB, BgA = bgA,
            FontSize = fontSize, FontFamily = fontFamily, Bold = bold,
            BorderThickness = borderThickness, Padding = padding, CornerRadius = cornerRadius,
            TargetX = targetX, TargetY = targetY,
        };
        var res = await bridge.CallAsync("draw_callout", p, ct);
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
        "Supports newlines, bold, italic, RGBA color, selection clipping and native Undo/Redo or batches. " +
        "Text is rasterized into pixels, not an editable text object. Text length 1..4096; fontSize in pixels, greater than zero and up to 512. " +
        "Requires an installed font family and available style; invalid input is rejected before queueing.")]
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
        "Get the active bitmap layer as an encoded image (base64), with optional crop. Waits for pending drawing and reads a fresh layer snapshot. " +
        "Use get_document_image for the visible-layer composition. " +
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
        "Save the active bitmap layer (or a region) as an image file on the host filesystem. Path must be " +
        "absolute. Format auto-detected from file extension (.png, .webp, .jpg/.jpeg) or specified " +
        "explicitly. Waits for pending drawing and reads a fresh layer snapshot. Use export_document for all visible layers.")]
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
        "List all layers in the active document with index, name, dimensions, visibility, opacity, blend mode, " +
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

    [McpServerTool, Description(
        "Change a layer's name, visibility, opacity (0..1) and/or blend mode. Omitted properties are preserved; " +
        "layerIndex=-1 means active layer. BlendMode: Normal, Multiply, Additive, ColorBurn, ColorDodge, Reflect, Glow, " +
        "Overlay, Difference, Negation, Lighten, Darken, Screen, Xor. Pixels are untouched. One native Undo step " +
        "(zero when nothing changes), same as Paint.NET's Layer Properties dialog. Finish pending drawing or a batch first.")]
    public async Task<string> SetLayerProperties(int layerIndex = -1, string? name = null, bool? visible = null,
        double? opacity = null, string? blendMode = null, CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("set_layer_properties", new SetLayerPropertiesParams
            { LayerIndex = layerIndex, Name = name, Visible = visible, Opacity = opacity, BlendMode = blendMode }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Duplicate a layer (layerIndex=-1 means active) directly above itself, including MCP text definitions. " +
        "One native Undo step, same as Paint.NET's Layers > Duplicate Layer. Finish pending drawing or a batch first.")]
    public Task<string> DuplicateLayer(int layerIndex = -1, CancellationToken ct = default)
        => LayerFunction("duplicate", layerIndex, -1, ct);

    [McpServerTool, Description(
        "Move a layer from layerIndex (-1 = active) to toIndex. Index 0 is the bottom layer; higher indexes draw on top. " +
        "One native Undo step (zero when the index is unchanged). Finish pending drawing or a batch first.")]
    public Task<string> MoveLayer(int toIndex, int layerIndex = -1, CancellationToken ct = default)
        => LayerFunction("move", layerIndex, toIndex, ct);

    [McpServerTool, Description(
        "Merge a layer (-1 = active) into the layer directly below it, like Paint.NET's Layers > Merge Layer Down. " +
        "Fails on the bottom layer. A merged MCP text layer keeps its definition but update_text_layer will refuse to " +
        "regenerate it unless replaceModifiedPixels=true. One native Undo step.")]
    public Task<string> MergeLayerDown(int layerIndex = -1, CancellationToken ct = default)
        => LayerFunction("merge_down", layerIndex, -1, ct);

    [McpServerTool, Description(
        "Flatten all layers into one, like Paint.NET's Image > Flatten. One native Undo step (zero with a single layer).")]
    public Task<string> FlattenImage(CancellationToken ct = default)
        => LayerFunction("flatten", -1, -1, ct);

    private async Task<string> LayerFunction(string function, int layerIndex, int toIndex, CancellationToken ct)
    {
        var res = await bridge.CallAsync("layer_function", new LayerFunctionParams
            { Function = function, LayerIndex = layerIndex, ToIndex = toIndex }, ct);
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
        "List a built-in effect's settings: Name, Kind, current default Value, Min/Max, and Choices for list settings. " +
        "Use the Names as keys for apply_effect's properties. Fails for effects that are not property based (Curves, Levels).")]
    public async Task<string> GetEffectProperties(
        [Description("Effect name from list_effects.")] string name,
        CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("get_effect_properties", new ApplyEffectParams { Name = name }, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Apply a built-in Paint.NET effect to the active layer, clipped to the selection, as one Undo step. " +
        "Property-based effects run without a dialog using defaults plus the given properties " +
        "(bool/int/double/string, or a list setting by its Choice text). Curves and Levels open their dialog instead.")]
    public async Task<string> ApplyEffect(
        [Description("Effect name from list_effects.")] string name,
        [Description("Optional settings, e.g. {\"Radius\": 8}; names from get_effect_properties.")] Dictionary<string, JsonElement>? properties = null,
        CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("apply_effect", new ApplyEffectParams { Name = name, Properties = properties }, ct);
        return res?.ToString() ?? "{}";
    }

    // ---- v0.6 Selection -----------------------------------------------------

    [McpServerTool, Description(
        "Replace the native selection with a rectangle visible as Paint.NET's selection outline. " +
        "Does not change image pixels. Drawing is clipped to the selection. Supports Undo/Redo. " +
        "Finish pending drawing or an active batch first. Returns native bounds and visibility.")]
    public async Task<string> SetSelectionRect(int x, int y, int width, int height, CancellationToken ct = default)
    {
        var p = new SetSelectionRectParams { X = x, Y = y, Width = width, Height = height };
        var res = await bridge.CallAsync("set_selection_rect", p, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description(
        "Replace the native selection with a polygon visible as Paint.NET's selection outline. " +
        "JSON array of {\"x\":int,\"y\":int}. Does not change image pixels. Supports Undo/Redo " +
        "and manual adjustment in Paint.NET. Finish pending drawing or an active batch first.")]
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
        "Clear native selection and any legacy software mask. Supports Undo/Redo; clearing " +
        "an already empty selection adds no history. Finish pending drawing or an active batch first.")]
    public async Task<string> ClearSelection(CancellationToken ct = default)
    {
        var res = await bridge.CallAsync("clear_selection", null, ct);
        return res?.ToString() ?? "{}";
    }

    [McpServerTool, Description("Read current native selection bounds and visibility, including manual UI adjustments. IsEmpty means no selection and drawing uses the entire canvas. Does not change pixels or history.")]
    public async Task<string> GetSelection(CancellationToken ct = default)
        => (await bridge.CallAsync("get_selection", null, ct))?.ToString() ?? "{}";

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
