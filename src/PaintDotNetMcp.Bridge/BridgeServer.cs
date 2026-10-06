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
    public const string Version = PipeNames.BridgeVersion;

    private static readonly object _gate = new();
    private static bool _started;
    private static CancellationTokenSource? _cts;

    // Queue of operations to apply on the next render pass.
    private static readonly ConcurrentQueue<PendingOp> _pendingOps = new();
    private static long _queuedRevision;
    private static long _completedRevision;
    private static string? _renderError;
    private static long _batchRenderedRevision = -1;

    public static void AcceptBatchRender()
    {
        lock (_gate)
        {
            if (_batchRenderedRevision != _queuedRevision)
                throw new InvalidOperationException("Batch tiles did not finish; retry end_batch.");
            for (long i = _completedRevision; i < _queuedRevision; i++) _pendingOps.TryDequeue(out _);
            _completedRevision = _queuedRevision;
            _renderError = null;
            _batchRenderedRevision = -1;
            Monitor.PulseAll(_gate);
        }
    }

    // Last seen Effect instance (set on construction). Used by read-only queries and auto-commit reflection.
    private static volatile BridgeEffect? _lastEffect;

    public static int PendingCount => _pendingOps.Count;

    public static void RecordTriggerError(string error)
    {
        lock (_gate)
        {
            _renderError = "automatic effect execution failed: " + error;
            Monitor.PulseAll(_gate);
        }
    }

    public static void EnsureStarted(BridgeEffect effect)
    {
        _lastEffect = effect;
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

    public static RenderBatch PrepareRenderPass(BridgeEffect effect, RenderArgs srcArgs,
        IReadOnlyList<Rectangle>? selectionScans = null)
    {
        HistoryOps.ValidateRender();
        _lastEffect = effect;
        AppServices.Capture(effect);
        selectionScans ??= effect.EnvironmentParameters.GetSelectionAsScans();
        long revision;
        PendingOp[] operations;
        lock (_gate)
        {
            revision = _queuedRevision;
            // Keep operations queued until rendering succeeds. A cancelled render can retry.
            operations = _pendingOps.ToArray();
            _renderError = null;
            if (HistoryOps.BatchActive) _batchRenderedRevision = -1;
        }
        try
        {
            return new RenderBatch(srcArgs.Surface, operations, snapshot =>
            {
                lock (_gate)
                {
                    if (revision < _completedRevision) return;
                    try
                    {
                        ImageIO.CaptureSnapshot(snapshot);
                        // A batch is accepted only after the native host adds its Undo step.
                        if (HistoryOps.BatchActive)
                        {
                            _batchRenderedRevision = revision;
                            return;
                        }
                        for (long i = _completedRevision; i < revision; i++) _pendingOps.TryDequeue(out _);
                        _completedRevision = revision;
                        _renderError = null;
                    }
                    catch (Exception ex)
                    {
                        _renderError = "snapshot failed: " + ex.Message;
                    }
                    finally { Monitor.PulseAll(_gate); }
                }
            }, selectionScans);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                _renderError = "render failed: " + ex.Message;
                Monitor.PulseAll(_gate);
            }
            throw;
        }
    }

    private static long Enqueue(PendingOp operation)
    {
        lock (_gate)
        {
            _pendingOps.Enqueue(operation);
            return ++_queuedRevision;
        }
    }

    private static object WaitForIdle(int timeoutMs)
    {
        if (timeoutMs < 0 || timeoutMs > 60000)
            throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be 0-60000");
        if (HistoryOps.BatchActive && PendingCount > 0)
            throw new InvalidOperationException("A drawing batch is pending; call end_batch before waiting, reading, or saving.");
        lock (_gate)
        {
            long target = _queuedRevision;
            long deadline = Environment.TickCount64 + timeoutMs;
            while (_completedRevision < target)
            {
                if (_renderError is not null) throw new InvalidOperationException(_renderError);
                long remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                    throw new TimeoutException("Rendering did not finish. Invoke Effects > Tools > MCP Bridge, then retry wait_for_idle. No fresh read/save was performed.");
                Monitor.Wait(_gate, (int)remaining);
            }
            return new { completed = true, revision = target, completed_revision = _completedRevision,
                note = "All render ROIs copied and snapshot updated; host history acceptance is not observed." };
        }
    }

    private static void AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pipe = new NamedPipeServerStream(
                    PipeNames.Current,
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
            if (HistoryOps.BatchActive && req.Method is "open_image" or "new_canvas" or "commit" or "set_auto_commit" or
                "add_layer" or "delete_layer" or "select_layer" or "apply_effect" or
                "set_selection_rect" or "set_selection_polygon" or "clear_selection")
                throw new InvalidOperationException("Finish the active batch with end_batch before this operation.");
            // Snapshot consumers must not race an outstanding drawing operation.
            if (req.Method is "get_canvas_png" or "save_png" or "extract_region" or
                "remove_background" or "detect_objects" or "extract_objects" or "ocr_region")
            {
                if (HistoryOps.BatchActive && req.Method is "get_canvas_png" or "save_png")
                    throw new InvalidOperationException("Finish the active batch with end_batch before reading or exporting images.");
                WaitForIdle(5000);
                if (AppServices.GetMainForm() is not null)
                {
                    var state = BuildPingResult();
                    if (state.ConnectionStatus != "ready" || !state.SnapshotReady)
                        throw new InvalidOperationException(state.ConnectionStatus + ": " + state.RecoveryAction);
                }
            }
            return req.Method switch
            {
                "ping"               => Ok(req.Id, BuildPingResult()),
                "open_image"         => Ok(req.Id, DocumentOps.Open(req.Params?.Deserialize<OpenImageParams>() ?? new())),
                "new_canvas"         => Ok(req.Id, DocumentOps.Create(req.Params?.Deserialize<NewCanvasParams>() ?? new())),
                "copy_selection_to_layer" => Ok(req.Id, NativeEditing.CopySelection(req.Params?.Deserialize<CopySelectionToLayerParams>() ?? new())),
                "resize_canvas"      => Ok(req.Id, NativeEditing.Resize(req.Params?.Deserialize<ResizeCanvasParams>() ?? new())),
                "crop_to_selection"  => Ok(req.Id, NativeEditing.Crop()),
                "fill"               => QueueOp<FillParams>(req, p => new FillOp(p)),
                "draw_rect"          => QueueOp<DrawRectangleParams>(req, p => new DrawRectOp(p)),
                "draw_line"          => QueueOp<DrawLineParams>(req, p => new DrawLineOp(p)),
                "draw_arrow"         => QueueOp<DrawArrowParams>(req, p => new DrawArrowOp(p)),
                "draw_marker"        => QueueOp<DrawMarkerParams>(req, p => new DrawMarkerOp(p)),
                "draw_callout"       => QueueOp<DrawCalloutParams>(req, p => new DrawCalloutOp(p)),
                "draw_ellipse"       => QueueOp<DrawEllipseParams>(req, p => new DrawEllipseOp(p)),
                "draw_polygon"       => QueueOp<DrawPolygonParams>(req, p => new DrawPolygonOp(p)),
                "draw_text"          => QueueOp<DrawTextParams>(req, p => new DrawTextOp(p)),
                "create_text_layer"  => Ok(req.Id, TextLayers.Create(req.Params?.Deserialize<CreateTextLayerParams>() ?? new())),
                "update_text_layer"  => Ok(req.Id, TextLayers.Update(req.Params?.Deserialize<UpdateTextLayerParams>() ?? new())),
                "get_text_layer"     => Ok(req.Id, TextLayers.Get(req.Params?.Deserialize<TextLayerIndexParams>() ?? new())),
                "list_text_layers"   => Ok(req.Id, TextLayers.List()),
                "set_layer_properties" => Ok(req.Id, LayerOps.SetProperties(req.Params?.Deserialize<SetLayerPropertiesParams>() ?? new())),
                "layer_function"     => Ok(req.Id, LayerOps.ApplyFunction(req.Params?.Deserialize<LayerFunctionParams>() ?? new())),
                "open_text_editor"   => Ok(req.Id, TextEditor.Open()),
                "flood_fill"         => QueueOp<FloodFillParams>(req, p => new FloodFillOp(p)),
                "gradient_fill"      => QueueOp<GradientFillParams>(req, p => new GradientFillOp(p)),
                "paste_image"        => QueueOp<PasteImageParams>(req, p => new PasteImageOp(p)),
                "transform_layer"    => QueueOp<TransformLayerParams>(req, p => new TransformLayerOp(p)),
                "align_layer"        => QueueOp<AlignLayerParams>(req, p => new AlignLayerOp(p)),
                "get_canvas_png"     => HandleGetCanvasPng(req),
                "save_png"           => HandleSavePng(req),
                "extract_region"     => HandleExtractRegion(req),
                "remove_background"  => HandleRemoveBackground(req),
                "commit"             => HandleCommit(req),
                "begin_batch"        => Ok(req.Id, HistoryOps.BeginBatch()),
                "end_batch"          => Ok(req.Id, HistoryOps.EndBatch()),
                "undo"               => Ok(req.Id, HistoryOps.Step(redo: false)),
                "redo"               => Ok(req.Id, HistoryOps.Step(redo: true)),
                "wait_for_idle"      => Ok(req.Id, WaitForIdle(req.Params?.Deserialize<WaitForIdleParams>()?.TimeoutMs ?? 5000)),
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
                "get_effect_properties" => Ok(req.Id, EffectsCatalog.Properties(req.Params?.Deserialize<ApplyEffectParams>()?.Name ?? "")),
                // v0.6 — selection / OCR
                "set_selection_rect"    => HandleSetSelectionRect(req),
                "set_selection_polygon" => HandleSetSelectionPolygon(req),
                "clear_selection"       => HandleClearSelection(req),
                "get_selection"         => Ok(req.Id, NativeSelection.Get()),
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
        if (HistoryOps.BatchActive && !AppServices.InvokeOnUiThread(HistoryOps.ValidateBatchTarget, out var batchNote))
            throw new InvalidOperationException(batchNote);
        if (req.Params is null) return Err(req.Id, "missing params");
        var p = req.Params.Value.Deserialize<TParams>()
            ?? throw new InvalidOperationException("could not deserialize params");
        var op = factory(p);
        long revision = Enqueue(op);

        bool autoTried = AutoCommit.TryTrigger(_lastEffect, out string note);
        return Ok(req.Id, new
        {
            queued = true,
            pending = _pendingOps.Count,
            revision,
            completed = false,
            auto_triggered = autoTried,
            auto_committed = false,
            commit_note = note,
            info = op.Info,
        });
    }

    // ---- Read-only handlers -------------------------------------------------

    private static PingResult BuildPingResult()
    {
        var r = new PingResult { Version = Version };
        r.AutoCommitAvailable = AutoCommit.Available;
        r.PendingOpCount = _pendingOps.Count;
        lock (_gate)
        {
            r.QueuedRevision = _queuedRevision;
            r.CompletedRevision = _completedRevision;
            r.RenderError = _renderError;
        }

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
        // Constructor discovery starts the pipe before any manual effect invocation.
        // Read the live layer directly to initialize the snapshot without creating Undo history.
        r.DocumentOpen = false;
        r.Width = r.Height = null;
        ConnectionState.Update(r);
        if (r.RenderError is not null)
        {
            r.ConnectionStatus = "render_failed";
            r.RecoveryAction = "Retry commit, then wait_for_idle. " + r.RenderError;
        }
        return r;
    }

    private static RpcResponse HandleGetCanvasPng(RpcRequest req)
    {
        var p = req.Params?.Deserialize<GetCanvasPngParams>() ?? new GetCanvasPngParams();
        var buf = ImageIO.ReadImageSource(p.Source, out int w, out int h);
        var crop = ImageIO.ImageCrop(w, h, p.X, p.Y, p.Width, p.Height);
        var fmt = ImageIO.ResolveFormat(p.Format, null);
        var bytes = ImageIO.EncodeImage(buf, w, h, crop.X, crop.Y, crop.Width, crop.Height, fmt, p.Quality);
        return Ok(req.Id, new GetCanvasPngResult
        {
            Source = p.Source,
            ImageBase64 = Convert.ToBase64String(bytes),
            Width = crop.Width,
            Height = crop.Height,
            MaybeStale = false,
            Format = fmt.ToString().ToLowerInvariant(),
            MimeType = ImageIO.MimeFor(fmt),
            Bytes = bytes.Length,
        });
    }

    private static RpcResponse HandleSavePng(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SavePngParams>() ?? throw new InvalidOperationException("missing params");
        if (string.IsNullOrWhiteSpace(p.Path)) return Err(req.Id, "path required");
        if (!Path.IsPathFullyQualified(p.Path)) return Err(req.Id, "An absolute export path is required.");
        var buf = ImageIO.ReadImageSource(p.Source, out int w, out int h);
        var crop = ImageIO.ImageCrop(w, h, p.X, p.Y, p.Width, p.Height);
        var fmt = ImageIO.ResolveFormat(p.Format, p.Path);
        var bytes = ImageIO.EncodeImage(buf, w, h, crop.X, crop.Y, crop.Width, crop.Height, fmt, p.Quality);
        var dir = Path.GetDirectoryName(p.Path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(p.Path, bytes);
        return Ok(req.Id, new SavePngResult
        {
            Source = p.Source,
            Path = p.Path,
            Width = crop.Width,
            Height = crop.Height,
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
            Enqueue(new PasteImageOp(new PasteImageParams
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
        // Explicit commit also executes when automatic execution is disabled.
        var prev = AutoCommit.Enabled;
        AutoCommit.Enabled = true;
        bool tried;
        string note;
        try { tried = AutoCommit.TryTrigger(_lastEffect, out note); }
        finally { AutoCommit.Enabled = prev; }
        return Ok(req.Id, new CommitResult
        {
            AutoTriggered = tried,
            AppliedOpCount = 0,
            QueuedOpCount = before,
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
                    IsActive = row.IsActive, IsVisible = row.IsVisible, Opacity = row.Opacity, BlendMode = row.BlendMode,
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
        var r = EffectsCatalog.ApplyHeadless(p.Name, p.Properties);
        return Ok(req.Id, r is EffectsCatalog.InvokeResult ir ? new ApplyEffectResult { Ok = ir.Ok, Note = ir.Note } : r);
    }

    // -------------------- v0.6 selection / OCR handlers ---------------------

    private static RpcResponse HandleSetSelectionRect(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SetSelectionRectParams>() ?? throw new InvalidOperationException("missing params");
        return Ok(req.Id, NativeSelection.Rectangle(p.X, p.Y, p.Width, p.Height));
    }

    private static RpcResponse HandleSetSelectionPolygon(RpcRequest req)
    {
        var p = req.Params?.Deserialize<SetSelectionPolygonParams>() ?? throw new InvalidOperationException("missing params");
        var pts = new List<System.Drawing.Point>(p.Points.Count);
        foreach (var pt in p.Points) pts.Add(new System.Drawing.Point(pt.X, pt.Y));
        return Ok(req.Id, NativeSelection.Polygon(pts));
    }

    private static RpcResponse HandleClearSelection(RpcRequest req)
    {
        return Ok(req.Id, NativeSelection.Clear());
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
