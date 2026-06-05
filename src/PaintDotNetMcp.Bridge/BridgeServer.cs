using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PaintDotNet;
using PaintDotNet.Effects;
using PaintDotNetMcp.Contracts;
using SkiaSharp;

namespace PaintDotNetMcp.Bridge;

// Long-lived Named Pipe server. Static singleton — survives Effect instance disposal.
//
// v0.2 changes:
//   - Snapshots the destination surface after every render so read-only methods work without a live
//     render context. (See ImageIO.CaptureSnapshot.)
//   - Dispatches new methods: draw_line, draw_ellipse, draw_polygon, draw_text, flood_fill,
//     gradient_fill, paste_image, get_canvas_png, save_png, extract_region, remove_background, commit.
//   - Tries best-effort auto-commit after a queued op so the user doesn't have to keep clicking the menu.
internal static class BridgeServer
{
    public const string Version = "0.5.12";

    private static readonly object _gate = new();
    private static bool _started;
    private static CancellationTokenSource? _cts;

    // Queue of operations to apply on the next render pass.
    private static readonly ConcurrentQueue<PendingOp> _pendingOps = new();

    // Last seen Effect instance (set on construction). Used by read-only queries and auto-commit reflection.
    private static volatile BridgeEffect? _lastEffect;

    public static int PendingCount => _pendingOps.Count;

    public static void EnsureStarted(BridgeEffect effect)
    {
        _lastEffect = effect;
        AutoCommit.EnsureHwndCaptured();
        AppServices.Capture(effect);
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            _cts = new CancellationTokenSource();
            var t = new Thread(() => AcceptLoop(_cts.Token))
            {
                IsBackground = true,
                Name = "PaintDotNetMcp.BridgeServer",
            };
            t.Start();
        }
    }

    public static void OnRenderPass(
        BridgeEffect effect,
        RenderArgs srcArgs,
        RenderArgs dstArgs,
        Rectangle[] rois,
        int startIndex,
        int length)
    {
        _lastEffect = effect;
        AutoCommit.EnsureHwndCaptured();
        AppServices.Capture(effect);

        // Default: copy source to destination unchanged (no-op effect).
        for (int i = startIndex; i < startIndex + length; i++)
        {
            var roi = rois[i];
            dstArgs.Surface.CopySurface(srcArgs.Surface, roi.Location, roi);
        }

        // Apply pending mutations on top.
        while (_pendingOps.TryDequeue(out var op))
        {
            try { op.Apply(dstArgs.Surface); }
            catch { /* swallow; one bad op shouldn't break the render */ }
        }

        // Snapshot the destination so read-only methods can serve it without a live render context.
        // The first ROI list of a render pass covers the whole image, but to be safe we always
        // capture from the dst surface (which has been fully populated above).
        try { ImageIO.CaptureSnapshot(dstArgs.Surface); }
        catch { }
    }

    private static void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    PipeNames.Default,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                pipe.WaitForConnection();
                _ = Task.Run(() => HandleClient(pipe, ct), ct);
            }
            catch (Exception)
            {
                Thread.Sleep(500);
            }
        }
    }

    private static async Task HandleClient(NamedPipeServerStream pipe, CancellationToken ct)
    {
        try
        {
            using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

            string? line;
            while (!ct.IsCancellationRequested && (line = await reader.ReadLineAsync(ct)) != null)
            {
                var resp = Dispatch(line);
                await writer.WriteLineAsync(JsonSerializer.Serialize(resp));
            }
        }
        catch { /* client gone */ }
        finally { try { pipe.Dispose(); } catch { } }
    }

    private static RpcResponse Dispatch(string line)
    {
        RpcRequest? req;
        try { req = JsonSerializer.Deserialize<RpcRequest>(line); }
        catch (Exception ex) { return Err(0, "parse: " + ex.Message); }
        if (req is null) return Err(0, "null request");

        try
        {
            return req.Method switch
            {
                "ping"               => Ok(req.Id, BuildPingResult()),
                "fill"               => QueueOp<FillParams>(req, p => new FillOp(p)),
                "draw_rect"          => QueueOp<DrawRectangleParams>(req, p => new DrawRectOp(p)),
                "draw_line"          => QueueOp<DrawLineParams>(req, p => new DrawLineOp(p)),
                "draw_ellipse"       => QueueOp<DrawEllipseParams>(req, p => new DrawEllipseOp(p)),
                "draw_polygon"       => QueueOp<DrawPolygonParams>(req, p => new DrawPolygonOp(p)),
                "draw_text"          => QueueOp<DrawTextParams>(req, p => new DrawTextOp(p)),
                "flood_fill"         => QueueOp<FloodFillParams>(req, p => new FloodFillOp(p)),
                "gradient_fill"      => QueueOp<GradientFillParams>(req, p => new GradientFillOp(p)),
                "paste_image"        => QueueOp<PasteImageParams>(req, p => new PasteImageOp(p)),
                "get_canvas_png"     => HandleGetCanvasPng(req),
                "save_png"           => HandleSavePng(req),
                "extract_region"     => HandleExtractRegion(req),
                "remove_background"  => HandleRemoveBackground(req),
                "commit"             => HandleCommit(req),
                "set_auto_commit"    => HandleSetAutoCommit(req),
                "detect_objects"     => HandleDetectObjects(req),
                "extract_objects"    => HandleExtractObjects(req),
                // v0.5 — reflection-based
                "list_layers"        => HandleListLayers(req),
                "add_layer"          => HandleAddLayer(req),
                "delete_layer"       => HandleDeleteLayer(req),
                "select_layer"       => HandleSelectLayer(req),
                "save_pdn"           => HandleSavePdn(req),
                "list_effects"       => HandleListEffects(req),
                "apply_effect"       => HandleApplyEffect(req),
                // v0.6 — selection / OCR
                "set_selection_rect"    => HandleSetSelectionRect(req),
                "set_selection_polygon" => HandleSetSelectionPolygon(req),
                "clear_selection"       => HandleClearSelection(req),
                "ocr_region"            => HandleOcrRegion(req),
                "diagnose_services"     => HandleDiagnoseServices(req),
                _ => Err(req.Id, "unknown method: " + req.Method),
            };
        }
        catch (Exception ex) { return Err(req.Id, ex.Message); }
    }

    // ---- Generic queue handler ----------------------------------------------

    private static RpcResponse QueueOp<TParams>(RpcRequest req, Func<TParams, PendingOp> factory)
    {
        if (req.Params is null) return Err(req.Id, "missing params");
        var p = req.Params.Value.Deserialize<TParams>()
            ?? throw new InvalidOperationException("could not deserialize params");
        _pendingOps.Enqueue(factory(p));

        bool autoTried = AutoCommit.TryTrigger(_lastEffect, out string note);
        return Ok(req.Id, new
        {
            queued = true,
            pending = _pendingOps.Count,
            auto_committed = autoTried,
            commit_note = note,
        });
    }

    // ---- Read-only handlers -------------------------------------------------

    private static PingResult BuildPingResult()
    {
        var r = new PingResult { Version = Version };
        r.AutoCommitAvailable = AutoCommit.Available;
        r.PendingOpCount = _pendingOps.Count;

        var eff = _lastEffect;
        if (eff is not null)
        {
            try
            {
                var env = eff.EnvironmentParameters;
                if (env is not null)
                {
                    r.DocumentOpen = true;
                    r.Width = env.SourceSurface.Width;
                    r.Height = env.SourceSurface.Height;
                }
            }
            catch { }
        }
        // Snapshot dims as fallback if EnvironmentParameters not available.
        if (r.Width is null && ImageIO.HasSnapshot)
        {
            r.Width = ImageIO.SnapshotWidth;
            r.Height = ImageIO.SnapshotHeight;
            r.DocumentOpen = true;
        }
        // Reflection probe — tells the caller which v0.5+ features should work on this Paint.NET build.
        try { r.Probe = AppServices.Probe(); } catch { }
        return r;
    }

    private static RpcResponse HandleGetCanvasPng(RpcRequest req)
    {
        var p = req.Params?.Deserialize<GetCanvasPngParams>() ?? new GetCanvasPngParams();
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        int x = p.X ?? 0, y = p.Y ?? 0;
        int rw = p.Width ?? w, rh = p.Height ?? h;
        var fmt = ImageIO.ResolveFormat(p.Format, null);
        var bytes = ImageIO.EncodeImage(buf, w, h, x, y, rw, rh, fmt, p.Quality);
        return Ok(req.Id, new GetCanvasPngResult
        {
            ImageBase64 = Convert.ToBase64String(bytes),
            Width = Math.Min(rw, w - x),
            Height = Math.Min(rh, h - y),
            MaybeStale = _pendingOps.Count > 0,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
            Bytes = bytes.Length,
        });
    }

    private static RpcResponse HandleSavePng(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SavePngParams>() ?? throw new InvalidOperationException("missing params");
        if (string.IsNullOrWhiteSpace(p.Path)) return Err(req.Id, "path required");
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        int x = p.X ?? 0, y = p.Y ?? 0;
        int rw = p.Width ?? w, rh = p.Height ?? h;
        var fmt = ImageIO.ResolveFormat(p.Format, p.Path);
        var bytes = ImageIO.EncodeImage(buf, w, h, x, y, rw, rh, fmt, p.Quality);
        var dir = Path.GetDirectoryName(p.Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(p.Path, bytes);
        return Ok(req.Id, new SavePngResult
        {
            Path = p.Path,
            Width = Math.Min(rw, w - x),
            Height = Math.Min(rh, h - y),
            Bytes = bytes.LongLength,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
        });
    }

    private static RpcResponse HandleExtractRegion(RpcRequest req)
    {
        var p = req.Params?.Deserialize<ExtractRegionParams>() ?? throw new InvalidOperationException("missing params");
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        var fmt = ImageIO.ResolveFormat(p.Format, p.SavePath);
        var bytes = ImageIO.EncodeImage(buf, w, h, p.X, p.Y, p.Width, p.Height, fmt, p.Quality);
        string? saved = null;
        if (!string.IsNullOrWhiteSpace(p.SavePath))
        {
            var dir = Path.GetDirectoryName(p.SavePath!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(p.SavePath!, bytes);
            saved = p.SavePath;
        }
        bool include = p.IncludeBase64 ?? string.IsNullOrEmpty(p.SavePath);
        return Ok(req.Id, new ExtractRegionResult
        {
            ImageBase64 = include ? Convert.ToBase64String(bytes) : "",
            Width = p.Width,
            Height = p.Height,
            SavedPath = saved,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
            Bytes = bytes.Length,
        });
    }

    private static RpcResponse HandleRemoveBackground(RpcRequest req)
    {
        var p = req.Params?.Deserialize<RemoveBackgroundParams>() ?? throw new InvalidOperationException("missing params");
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        int x = p.X ?? 0, y = p.Y ?? 0;
        int rw = p.Width ?? w, rh = p.Height ?? h;

        // Route to AI matting if requested. Falls back to color_key on failure with a note in
        // the response so the caller can decide whether to retry or fix their rembg install.
        byte[] region;
        byte kr = 0, kg = 0, kb = 0;
        string usedMethod = p.Method ?? "color_key";
        if (string.Equals(p.Method, "ai", StringComparison.OrdinalIgnoreCase))
        {
            var ai = AiMatting.RunOnRegion(buf, w, h, x, y, rw, rh, p.AiModel ?? "");
            if (!ai.Ok || ai.Bgra is null)
                return Err(req.Id, "AI matting failed: " + ai.Note);
            region = ai.Bgra;
            // AI matting uses the model's mask; we don't need to compute a color key.
            usedMethod = "ai";
        }
        else
        {
            region = ImageIO.CropBuffer(buf, w, h, x, y, rw, rh);
            (kr, kg, kb) = ImageIO.RemoveBackground(
                region, rw, rh, 0, 0, rw, rh,
                p.Method, p.KeyR, p.KeyG, p.KeyB, p.Tolerance, p.Feather);
        }

        var fmt = ImageIO.ResolveFormat(p.Format, p.SavePath);
        var bytes = ImageIO.EncodeImage(region, rw, rh, 0, 0, rw, rh, fmt, p.Quality);
        string? saved = null;
        if (!string.IsNullOrWhiteSpace(p.SavePath))
        {
            var dir = Path.GetDirectoryName(p.SavePath!);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(p.SavePath!, bytes);
            saved = p.SavePath;
        }

        if (p.ApplyToLayer)
        {
            // Queue a paste of the matted region back onto the active layer at (x,y), replacing alpha.
            // Always use PNG for the in-process paste — it's lossless and the decoder handles it natively.
            var pngForPaste = fmt == SKEncodedImageFormat.Png
                ? bytes
                : ImageIO.EncodeImage(region, rw, rh, 0, 0, rw, rh, SKEncodedImageFormat.Png, 100);
            _pendingOps.Enqueue(new PasteImageOp(new PasteImageParams
            {
                PngBase64 = Convert.ToBase64String(pngForPaste),
                X = x, Y = y, BlendMode = "replace",
            }));
            AutoCommit.TryTrigger(_lastEffect, out _);
        }

        bool include = p.IncludeBase64 ?? string.IsNullOrEmpty(p.SavePath);
        return Ok(req.Id, new RemoveBackgroundResult
        {
            ImageBase64 = include ? Convert.ToBase64String(bytes) : "",
            Width = rw,
            Height = rh,
            SavedPath = saved,
            UsedKeyR = kr,
            UsedKeyG = kg,
            UsedKeyB = kb,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
            Bytes = bytes.Length,
        });
    }

    private static RpcResponse HandleCommit(RpcRequest req)
    {
        int before = _pendingOps.Count;
        // Force-commit ignores debounce.
        var prev = AutoCommit.Enabled;
        AutoCommit.Enabled = true;
        AutoCommit.ResetDebounce();
        bool tried;
        string note;
        try { tried = AutoCommit.TryTrigger(_lastEffect, out note); }
        finally { AutoCommit.Enabled = prev; }
        return Ok(req.Id, new CommitResult
        {
            AutoTriggered = tried,
            AppliedOpCount = before,
            Note = note,
        });
    }

    private static RpcResponse HandleSetAutoCommit(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SetAutoCommitParams>() ?? throw new InvalidOperationException("missing params");
        AutoCommit.Enabled = p.Enabled;
        return Ok(req.Id, new SetAutoCommitResult { Enabled = AutoCommit.Enabled });
    }

    private static RpcResponse HandleDetectObjects(RpcRequest req)
    {
        var p = req.Params?.Deserialize<DetectObjectsParams>() ?? new DetectObjectsParams();
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        var opt = BuildDetectOptions(p);
        var (bgR, bgG, bgB) = EnsureBg(buf, w, h, p, opt);
        var rects = ObjectDetection.Detect(buf, w, h, opt);

        return Ok(req.Id, new DetectObjectsResult
        {
            Count = rects.Count,
            Bboxes = rects.Select(r => new DetectedBbox
            {
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height, Area = r.Area,
            }).ToList(),
            UsedBgR = bgR, UsedBgG = bgG, UsedBgB = bgB,
        });
    }

    private static RpcResponse HandleExtractObjects(RpcRequest req)
    {
        var p = req.Params?.Deserialize<ExtractObjectsParams>() ?? throw new InvalidOperationException("missing params");
        if (string.IsNullOrWhiteSpace(p.SavePathTemplate)) return Err(req.Id, "savePathTemplate required");
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");

        var opt = BuildDetectOptions(p);
        var (bgR, bgG, bgB) = EnsureBg(buf, w, h, p, opt);
        var rects = ObjectDetection.Detect(buf, w, h, opt);

        var fmt = ImageIO.ResolveFormat(p.Format, p.SavePathTemplate);
        var items = new List<ExtractedObject>(rects.Count);
        for (int i = 0; i < rects.Count; i++)
        {
            var r = rects[i];
            var bytes = ImageIO.EncodeImage(buf, w, h, r.X, r.Y, r.Width, r.Height, fmt, p.Quality);
            var path = ApplyPathTemplate(p.SavePathTemplate, i, i + 1, r.X, r.Y, r.Width, r.Height);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, bytes);
            items.Add(new ExtractedObject
            {
                Index = i,
                X = r.X, Y = r.Y, Width = r.Width, Height = r.Height,
                SavedPath = path,
                Bytes = bytes.LongLength,
                ImageBase64 = p.IncludeBase64 ? Convert.ToBase64String(bytes) : "",
            });
        }

        return Ok(req.Id, new ExtractObjectsResult
        {
            Count = items.Count,
            Items = items,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
            UsedBgR = bgR, UsedBgG = bgG, UsedBgB = bgB,
        });
    }

    private static ObjectDetection.Options BuildDetectOptions(DetectObjectsParams p)
    {
        return new ObjectDetection.Options
        {
            RegionX = p.RegionX ?? 0,
            RegionY = p.RegionY ?? 0,
            RegionW = p.RegionW ?? -1,
            RegionH = p.RegionH ?? -1,
            BgR = p.BgR, BgG = p.BgG, BgB = p.BgB,
            Tolerance = p.Tolerance,
            MinSize = p.MinSize,
            MaxSize = p.MaxSize,
            Padding = p.Padding,
            GroupGap = p.GroupGap,
            GroupGapX = p.GroupGapX,
            GroupGapY = p.GroupGapY,
            MaxAspectRatio = p.MaxAspectRatio,
            MinArea = p.MinArea,
        };
    }

    /// <summary>
    /// Resolve background to concrete bytes (auto-corner-sampled if not provided) so the result
    /// can echo what we actually used. Mirrors ObjectDetection's internal logic.
    /// </summary>
    private static (byte r, byte g, byte b) EnsureBg(byte[] bgra, int w, int h, DetectObjectsParams p, ObjectDetection.Options opt)
    {
        if (opt.BgR.HasValue && opt.BgG.HasValue && opt.BgB.HasValue)
            return (opt.BgR.Value, opt.BgG.Value, opt.BgB.Value);

        int rx = Math.Max(0, opt.RegionX);
        int ry = Math.Max(0, opt.RegionY);
        int rw = opt.RegionW < 0 ? w - rx : Math.Min(opt.RegionW, w - rx);
        int rh = opt.RegionH < 0 ? h - ry : Math.Min(opt.RegionH, h - ry);
        long sumR = 0, sumG = 0, sumB = 0; int n = 0;
        void Sample(int x, int y)
        {
            if ((uint)x >= (uint)w || (uint)y >= (uint)h) return;
            int i = (y * w + x) * 4;
            if (bgra[i + 3] < 128) return; // skip transparent corners (matting residue)
            sumB += bgra[i]; sumG += bgra[i + 1]; sumR += bgra[i + 2]; n++;
        }
        Sample(rx, ry);
        Sample(rx + rw - 1, ry);
        Sample(rx, ry + rh - 1);
        Sample(rx + rw - 1, ry + rh - 1);
        if (n == 0)
        {
            Sample(rx + 4, ry + 4);
            Sample(rx + rw - 5, ry + 4);
            Sample(rx + 4, ry + rh - 5);
            Sample(rx + rw - 5, ry + rh - 5);
        }
        return n == 0 ? ((byte)255, (byte)255, (byte)255)
                      : ((byte)(sumR / n), (byte)(sumG / n), (byte)(sumB / n));
    }

    /// <summary>
    /// Replace placeholders in a path template. Supports {i}, {n}, {x}, {y}, {w}, {h} with optional
    /// numeric format ({i:000} → "007").
    /// </summary>
    private static string ApplyPathTemplate(string template, int i, int n, int x, int y, int w, int h)
    {
        var values = new Dictionary<string, int>
        {
            ["i"] = i, ["n"] = n, ["x"] = x, ["y"] = y, ["w"] = w, ["h"] = h,
        };
        return System.Text.RegularExpressions.Regex.Replace(
            template, @"\{(\w+)(?::([^}]+))?\}",
            m =>
            {
                var key = m.Groups[1].Value;
                if (!values.TryGetValue(key, out int v)) return m.Value;
                return m.Groups[2].Success ? v.ToString(m.Groups[2].Value) : v.ToString();
            });
    }

    // -------------------- v0.5 reflection-based handlers --------------------

    private static RpcResponse HandleListLayers(RpcRequest req)
    {
        var r = LayerOps.List();
        var result = new ListLayersResult { Ok = r.Ok, Note = r.Note };
        if (r.Ok && r.Data is List<LayerOps.LayerInfo> rows)
        {
            foreach (var row in rows)
            {
                result.Layers.Add(new LayerDescriptor
                {
                    Index = row.Index, Name = row.Name, Width = row.Width, Height = row.Height,
                    IsActive = row.IsActive, IsVisible = row.IsVisible, Opacity = row.Opacity,
                });
            }
        }
        return Ok(req.Id, result);
    }

    private static RpcResponse HandleAddLayer(RpcRequest req)
    {
        var p = req.Params?.Deserialize<AddLayerParams>() ?? new AddLayerParams();
        var r = LayerOps.Add(p.Name);
        return Ok(req.Id, new LayerOpResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleDeleteLayer(RpcRequest req)
    {
        var p = req.Params?.Deserialize<DeleteLayerParams>() ?? throw new InvalidOperationException("missing params");
        var r = LayerOps.Delete(p.Index);
        return Ok(req.Id, new LayerOpResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleSelectLayer(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SelectLayerParams>() ?? throw new InvalidOperationException("missing params");
        var r = LayerOps.Select(p.Index);
        return Ok(req.Id, new LayerOpResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleSavePdn(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SavePdnParams>() ?? throw new InvalidOperationException("missing params");
        var r = SavePdn.Save(p.Path);
        return Ok(req.Id, new SavePdnResult
        {
            Ok = r.Ok, Note = r.Note, Path = r.Path ?? p.Path, Bytes = r.Bytes,
        });
    }

    private static RpcResponse HandleListEffects(RpcRequest req)
    {
        var list = EffectsCatalog.List();
        var result = new ListEffectsResult { Count = list.Count };
        foreach (var e in list)
        {
            result.Effects.Add(new EffectEntry
            {
                Name = e.Name, FullName = e.FullName, Category = e.Category, Assembly = e.Assembly,
            });
        }
        return Ok(req.Id, result);
    }

    private static RpcResponse HandleApplyEffect(RpcRequest req)
    {
        var p = req.Params?.Deserialize<ApplyEffectParams>() ?? throw new InvalidOperationException("missing params");
        var r = EffectsCatalog.Apply(p.Name);
        return Ok(req.Id, new ApplyEffectResult { Ok = r.Ok, Note = r.Note });
    }

    // -------------------- v0.6 selection / OCR handlers ---------------------

    private static RpcResponse HandleSetSelectionRect(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SetSelectionRectParams>() ?? throw new InvalidOperationException("missing params");
        var r = SelectionOps.SetRectangle(p.X, p.Y, p.Width, p.Height);
        return Ok(req.Id, new SelectionResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleSetSelectionPolygon(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SetSelectionPolygonParams>() ?? throw new InvalidOperationException("missing params");
        var pts = new List<System.Drawing.Point>(p.Points.Count);
        foreach (var pt in p.Points) pts.Add(new System.Drawing.Point(pt.X, pt.Y));
        var r = SelectionOps.SetPolygon(pts);
        return Ok(req.Id, new SelectionResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleClearSelection(RpcRequest req)
    {
        var r = SelectionOps.Clear();
        return Ok(req.Id, new SelectionResult { Ok = r.Ok, Note = r.Note });
    }

    private static RpcResponse HandleOcrRegion(RpcRequest req)
    {
        var p = req.Params?.Deserialize<OcrRegionParams>() ?? throw new InvalidOperationException("missing params");
        var buf = ImageIO.GetSnapshotCopy(out int w, out int h);
        if (buf is null) return Err(req.Id, "no snapshot yet — invoke Effects > Tools > MCP Bridge once");
        var r = Ocr.RunOnRegion(buf, w, h, p.X, p.Y, p.Width, p.Height, p.Lang);
        return Ok(req.Id, new OcrRegionResult { Ok = r.Ok, Text = r.Text, Note = r.Note });
    }

    private static RpcResponse HandleDiagnoseServices(RpcRequest req)
    {
        var d = AppServices.Diagnose();
        return Ok(req.Id, new DiagnoseServicesResult
        {
            ServiceContainerType = d.ServiceContainerType,
            CandidateInterfaces = d.CandidateInterfaces,
            RegisteredServices = d.RegisteredServices,
            InterestingProperties = d.InterestingProperties,
            StaticEntryPoints = d.StaticEntryPoints,
            WpfApplicationCurrent = d.WpfApplicationCurrent,
            WpfMainWindowType = d.WpfMainWindowType,
            WpfMainWindowDataContext = d.WpfMainWindowDataContext,
            WpfMainWindowProperties = d.WpfMainWindowProperties,
            ProgramInstanceMembers = d.ProgramInstanceMembers,
            OpenForms = d.OpenForms,
            MainFormMembers = d.MainFormMembers,
            AppWorkspaceMembers = d.AppWorkspaceMembers,
            DocumentWorkspaceMembers = d.DocumentWorkspaceMembers,
            DocumentMembers = d.DocumentMembers,
        });
    }

    private static RpcResponse Ok(int id, object? result)
    {
        var json = result is null ? null : (JsonElement?)JsonSerializer.SerializeToElement(result);
        return new RpcResponse { Id = id, Ok = true, Result = json };
    }

    private static RpcResponse Err(int id, string msg)
        => new() { Id = id, Ok = false, Error = msg };
}
