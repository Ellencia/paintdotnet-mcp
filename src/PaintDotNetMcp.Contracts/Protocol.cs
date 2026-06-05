using System.Text.Json.Serialization;

namespace PaintDotNetMcp.Contracts;

// Bridge IPC protocol. Server (MCP) is the client; Bridge (Paint.NET plugin) is the named-pipe server.
// Pipe name: "PaintDotNetMcp.Bridge.v1"
// Wire format: one JSON object per line (newline-delimited UTF-8).
//   Request:  { "id": <int>, "method": "<name>", "params": { ... } }
//   Response: { "id": <int>, "ok": true, "result": { ... } } | { "id": <int>, "ok": false, "error": "msg" }
//
// v0.2 protocol additions:
//   - Drawing primitives: draw_line, draw_ellipse, draw_polygon, draw_text
//   - Pixel ops:          flood_fill, gradient_fill
//   - Image I/O:          get_canvas_png, paste_image, save_png
//   - Region/matting:     extract_region, remove_background
//   - Commit control:     commit (force apply queued ops)

public static class PipeNames
{
    public const string Default = "PaintDotNetMcp.Bridge.v1";
}

public sealed class RpcRequest
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("params")] public System.Text.Json.JsonElement? Params { get; set; }
}

public sealed class RpcResponse
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public System.Text.Json.JsonElement? Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

// ---- Method-specific param/result DTOs ----

public sealed class PingResult
{
    public string Version { get; set; } = "";
    public bool DocumentOpen { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? LayerCount { get; set; }
    public bool AutoCommitAvailable { get; set; }
    public int PendingOpCount { get; set; }
    /// <summary>Which reflection-based services resolved on this Paint.NET build. Diagnostic.</summary>
    public Dictionary<string, bool>? Probe { get; set; }
}

public sealed class FillParams
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class DrawRectangleParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    public int Thickness { get; set; } = 1;
    public bool Fill { get; set; }
}

public sealed class DrawLineParams
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    public int Thickness { get; set; } = 1;
}

public sealed class DrawEllipseParams
{
    // Bounding-box form (consistent with rectangle).
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    public int Thickness { get; set; } = 1;
    public bool Fill { get; set; }
}

public sealed class Point2I
{
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class DrawPolygonParams
{
    public List<Point2I> Points { get; set; } = new();
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    public int Thickness { get; set; } = 1;
    public bool Fill { get; set; }
    public bool Closed { get; set; } = true;
}

public sealed class DrawTextParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 16f;
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    /// <summary>If true, anti-aliased glyph rendering. Default true.</summary>
    public bool AntiAlias { get; set; } = true;
}

public sealed class FloodFillParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
    public byte A { get; set; } = 255;
    /// <summary>Color tolerance per channel (0-255). 0 = exact match.</summary>
    public int Tolerance { get; set; } = 0;
}

public sealed class GradientFillParams
{
    public int X1 { get; set; }
    public int Y1 { get; set; }
    public int X2 { get; set; }
    public int Y2 { get; set; }
    public byte R1 { get; set; }
    public byte G1 { get; set; }
    public byte B1 { get; set; }
    public byte A1 { get; set; } = 255;
    public byte R2 { get; set; }
    public byte G2 { get; set; }
    public byte B2 { get; set; }
    public byte A2 { get; set; } = 255;
    /// <summary>"linear" (default) or "radial".</summary>
    public string Mode { get; set; } = "linear";
    /// <summary>Optional bounds; omitted = whole surface.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class PasteImageParams
{
    /// <summary>base64-encoded image bytes. Format auto-detected (PNG/WebP/JPEG).</summary>
    public string PngBase64 { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    /// <summary>"normal" (default), "replace" (overwrites alpha).</summary>
    public string BlendMode { get; set; } = "normal";
}

// Common bag of fields shared by image-producing operations. Not used directly; mirrored on each
// request type to keep MCP tool signatures explicit.
//   Format:        "auto" (default; from path extension), "png", "webp", or "jpeg".
//   Quality:       1-100. Applied to lossy formats; ignored for PNG.
//   IncludeBase64: when null, defaults to true if no SavePath, false if SavePath is set.

public sealed class GetCanvasPngParams
{
    /// <summary>Optional crop. Omit to get full canvas.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>If true, force a fresh render snapshot before reading. Default false (use last seen).</summary>
    public bool ForceRefresh { get; set; }
    /// <summary>"auto" (default) | "png" | "webp" | "jpeg".</summary>
    public string Format { get; set; } = "auto";
    /// <summary>Lossy quality 1-100. Default 85. Ignored for PNG.</summary>
    public int Quality { get; set; } = 85;
}

public sealed class GetCanvasPngResult
{
    /// <summary>Base64-encoded image in the requested format.</summary>
    public string ImageBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>True if snapshot was taken before pending ops were applied (i.e. could be stale).</summary>
    public bool MaybeStale { get; set; }
    /// <summary>The format actually used ("png", "webp", "jpeg").</summary>
    public string Format { get; set; } = "png";
    public string MimeType { get; set; } = "image/png";
    public int Bytes { get; set; }
}

public sealed class SavePngParams
{
    /// <summary>Absolute path on the host filesystem. Extension drives auto-format.</summary>
    public string Path { get; set; } = "";
    /// <summary>Optional crop (defaults to full canvas).</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>"auto" (default; from extension) | "png" | "webp" | "jpeg".</summary>
    public string Format { get; set; } = "auto";
    /// <summary>Lossy quality 1-100. Default 85. Ignored for PNG.</summary>
    public int Quality { get; set; } = 85;
}

public sealed class SavePngResult
{
    public string Path { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Bytes { get; set; }
    public string Format { get; set; } = "png";
    public string MimeType { get; set; } = "image/png";
}

public sealed class ExtractRegionParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Optional save path; if set, also writes the encoded image to disk.</summary>
    public string? SavePath { get; set; }
    /// <summary>"auto" | "png" | "webp" | "jpeg".</summary>
    public string Format { get; set; } = "auto";
    /// <summary>Lossy quality 1-100.</summary>
    public int Quality { get; set; } = 85;
    /// <summary>Whether to embed the encoded bytes in the response. Null = auto (false if SavePath set).</summary>
    public bool? IncludeBase64 { get; set; }
}

public sealed class ExtractRegionResult
{
    public string ImageBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string? SavedPath { get; set; }
    public string Format { get; set; } = "png";
    public string MimeType { get; set; } = "image/png";
    public int Bytes { get; set; }
}

public sealed class RemoveBackgroundParams
{
    /// <summary>
    /// Algorithm: "color_key" (default), "auto_corners" (sample 4 corners as bg),
    /// or "ai" (shell out to rembg CLI for U^2-Net-based matting — handles hair,
    /// gradients, non-uniform backgrounds; requires rembg installed).
    /// </summary>
    public string Method { get; set; } = "color_key";

    /// <summary>For method=ai: rembg model name (u2net, u2netp, isnet-general-use, etc). Empty = default.</summary>
    public string AiModel { get; set; } = "";
    public byte? KeyR { get; set; }
    public byte? KeyG { get; set; }
    public byte? KeyB { get; set; }
    /// <summary>Color distance tolerance (0-441, where ~441 ≈ max RGB euclid). Default 32.</summary>
    public int Tolerance { get; set; } = 32;
    /// <summary>If true, soft alpha based on distance instead of hard cut.</summary>
    public bool Feather { get; set; } = true;
    /// <summary>Optional region; defaults to whole surface.</summary>
    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    /// <summary>If set, also save the matted region to this path.</summary>
    public string? SavePath { get; set; }
    /// <summary>If true, also queue the matted result back to the active layer.</summary>
    public bool ApplyToLayer { get; set; }
    /// <summary>"auto" | "png" | "webp" | "jpeg".</summary>
    public string Format { get; set; } = "auto";
    /// <summary>Lossy quality 1-100.</summary>
    public int Quality { get; set; } = 85;
    /// <summary>Whether to embed the encoded bytes in the response. Null = auto (false if SavePath set).</summary>
    public bool? IncludeBase64 { get; set; }
}

public sealed class RemoveBackgroundResult
{
    public string ImageBase64 { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string? SavedPath { get; set; }
    public byte UsedKeyR { get; set; }
    public byte UsedKeyG { get; set; }
    public byte UsedKeyB { get; set; }
    public string Format { get; set; } = "png";
    public string MimeType { get; set; } = "image/png";
    public int Bytes { get; set; }
}

public sealed class CommitResult
{
    /// <summary>True if commit was triggered automatically; false if user must invoke the menu.</summary>
    public bool AutoTriggered { get; set; }
    public int AppliedOpCount { get; set; }
    public string Note { get; set; } = "";
}

// ---- v0.4: object detection ---------------------------------------------

public class DetectObjectsParams
{
    public int? RegionX { get; set; }
    public int? RegionY { get; set; }
    public int? RegionW { get; set; }
    public int? RegionH { get; set; }

    /// <summary>Background key. If any is null, auto-sample the four corners of the region.</summary>
    public byte? BgR { get; set; }
    public byte? BgG { get; set; }
    public byte? BgB { get; set; }

    /// <summary>Color-distance threshold (0-441). Default 32.</summary>
    public int Tolerance { get; set; } = 32;

    /// <summary>Min bounding-box dimension in pixels. Default 16 (drops tiny noise).</summary>
    public int MinSize { get; set; } = 16;

    /// <summary>Max bounding-box dimension in pixels.</summary>
    public int MaxSize { get; set; } = int.MaxValue;

    /// <summary>Pad detected bboxes by this many pixels (clipped to canvas). Default 0.</summary>
    public int Padding { get; set; } = 0;

    /// <summary>Merge bboxes whose inflated rects overlap. Useful for compound icons. Default 0 (no merging).</summary>
    public int GroupGap { get; set; } = 0;

    /// <summary>Override GroupGap on the X axis. Null = use GroupGap. Allows aggressive horizontal merging while keeping vertical merging tight (so an icon doesn't bleed into the caption below).</summary>
    public int? GroupGapX { get; set; }

    /// <summary>Override GroupGap on the Y axis. Null = use GroupGap.</summary>
    public int? GroupGapY { get; set; }

    /// <summary>Drop bboxes whose long/short ratio exceeds this. Default 6.0 (catches text rows).</summary>
    public double MaxAspectRatio { get; set; } = 6.0;

    /// <summary>Min foreground pixel count to keep. Default 0.</summary>
    public int MinArea { get; set; } = 0;
}

public sealed class DetectedBbox
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Area { get; set; }
}

public sealed class DetectObjectsResult
{
    public int Count { get; set; }
    public List<DetectedBbox> Bboxes { get; set; } = new();
    /// <summary>Background color actually used (auto-sampled or user-provided).</summary>
    public byte UsedBgR { get; set; }
    public byte UsedBgG { get; set; }
    public byte UsedBgB { get; set; }
}

public sealed class ExtractObjectsParams : DetectObjectsParams
{
    /// <summary>
    /// File path template. {i} is replaced with 0-based index, {n} with 1-based, {x}/{y}/{w}/{h} with
    /// bbox coords. Format specifier supported: "{i:000}" → "001". Extension drives the image format
    /// unless Format is explicit.
    /// </summary>
    public string SavePathTemplate { get; set; } = "";

    /// <summary>"auto" | "png" | "webp" | "jpeg".</summary>
    public string Format { get; set; } = "auto";

    /// <summary>Lossy quality 1-100.</summary>
    public int Quality { get; set; } = 85;

    /// <summary>Whether to embed any base64 in the response. Default false (paths are usually enough).</summary>
    public bool IncludeBase64 { get; set; } = false;
}

public sealed class ExtractedObject
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string SavedPath { get; set; } = "";
    public long Bytes { get; set; }
    public string ImageBase64 { get; set; } = "";
}

public sealed class ExtractObjectsResult
{
    public int Count { get; set; }
    public List<ExtractedObject> Items { get; set; } = new();
    public string Format { get; set; } = "png";
    public string MimeType { get; set; } = "image/png";
    public byte UsedBgR { get; set; }
    public byte UsedBgG { get; set; }
    public byte UsedBgB { get; set; }
}

// ============================================================
// v0.5: Layer management, save_pdn, effects (reflection-based)
// ============================================================

public sealed class LayerDescriptor
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsActive { get; set; }
    public bool IsVisible { get; set; }
    public double Opacity { get; set; } = 1.0;
}

public sealed class ListLayersResult
{
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
    public List<LayerDescriptor> Layers { get; set; } = new();
}

public sealed class AddLayerParams
{
    public string Name { get; set; } = "Layer";
}

public sealed class DeleteLayerParams
{
    public int Index { get; set; }
}

public sealed class SelectLayerParams
{
    public int Index { get; set; }
}

public sealed class LayerOpResult
{
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
}

public sealed class SavePdnParams
{
    public string Path { get; set; } = "";
}

public sealed class SavePdnResult
{
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
}

public sealed class EffectEntry
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Category { get; set; } = "";
    public string Assembly { get; set; } = "";
}

public sealed class ListEffectsResult
{
    public int Count { get; set; }
    public List<EffectEntry> Effects { get; set; } = new();
}

public sealed class ApplyEffectParams
{
    /// <summary>Short class name (e.g. "GaussianBlurEffect") or full namespace name.</summary>
    public string Name { get; set; } = "";
}

public sealed class ApplyEffectResult
{
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
}

// ============================================================
// v0.6: Selection
// ============================================================

public sealed class SetSelectionRectParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class SetSelectionPolygonParams
{
    public List<Point2I> Points { get; set; } = new();
}

public sealed class SelectionResult
{
    public bool Ok { get; set; }
    public string Note { get; set; } = "";
}

// ============================================================
// v0.6: OCR
// ============================================================

public sealed class OcrRegionParams
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    /// <summary>Tesseract language code, e.g. "eng", "kor", "eng+kor". Default "eng".</summary>
    public string Lang { get; set; } = "eng";
}

public sealed class OcrRegionResult
{
    public bool Ok { get; set; }
    public string Text { get; set; } = "";
    public string Note { get; set; } = "";
}

// ============================================================
// Diagnostic — used to fill in reflection candidate-name arrays
// ============================================================

public sealed class DiagnoseServicesResult
{
    public string ServiceContainerType { get; set; } = "";
    public List<string> CandidateInterfaces { get; set; } = new();
    public List<string> RegisteredServices { get; set; } = new();
    public List<string> InterestingProperties { get; set; } = new();
    public List<string> StaticEntryPoints { get; set; } = new();
    public string WpfApplicationCurrent { get; set; } = "";
    public string WpfMainWindowType { get; set; } = "";
    public string WpfMainWindowDataContext { get; set; } = "";
    public List<string> WpfMainWindowProperties { get; set; } = new();
    public List<string> ProgramInstanceMembers { get; set; } = new();
    public List<string> OpenForms { get; set; } = new();
    public List<string> MainFormMembers { get; set; } = new();
    public List<string> AppWorkspaceMembers { get; set; } = new();
    public List<string> DocumentWorkspaceMembers { get; set; } = new();
    public List<string> DocumentMembers { get; set; } = new();
}

public sealed class SetAutoCommitParams
{
    public bool Enabled { get; set; }
}

public sealed class SetAutoCommitResult
{
    public bool Enabled { get; set; }
}
