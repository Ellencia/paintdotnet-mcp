using System.Diagnostics;
using System.IO;

namespace PaintDotNetMcp.Bridge;

// AI-based background removal via the rembg CLI (https://github.com/danielgatis/rembg).
// We shell out to keep the bridge dependency-free; the user installs once:
//   pip install rembg[cli]
//
// On first run rembg downloads the U^2-Net ONNX model (~170MB) to ~/.u2net/. Subsequent runs
// are fast (~1-2s for an icon-sized region). The model handles hair, fur, gradients, and
// non-uniform backgrounds that color_key matting can't.
internal static class AiMatting
{
    public sealed record MattingResult(bool Ok, byte[]? Bgra, int W, int H, string Note);

    /// <summary>
    /// Run rembg on a region of the snapshot. Returns a BGRA buffer with background pixels
    /// set to alpha=0. The Bgra buffer is the same size as the region (w*h*4).
    /// </summary>
    public static MattingResult RunOnRegion(byte[] bgra, int canvasW, int canvasH, int x, int y, int w, int h, string model,
        IEnumerable<string>? extraArgs = null)
    {
        var exe = FindRembgExecutable();
        if (exe is null)
        {
            return new(false, null, 0, 0,
                "rembg not found. Install with: `pip install \"rembg[cpu,cli]\"` (first run downloads the model).");
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "paintdotnet-mcp-rembg");
        Directory.CreateDirectory(tempDir);
        string inputPath  = Path.Combine(tempDir, "in-"  + Guid.NewGuid().ToString("N") + ".png");
        string outputPath = Path.Combine(tempDir, "out-" + Guid.NewGuid().ToString("N") + ".png");

        try
        {
            var png = ImageIO.EncodeImage(bgra, canvasW, canvasH, x, y, w, h,
                SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(inputPath, png);

            // rembg CLI signature: `rembg i [-m model] input output`
            var args = new List<string> { "i" };
            if (!string.IsNullOrWhiteSpace(model)) args.AddRange(["-m", model]);
            args.AddRange(extraArgs ?? []);
            args.AddRange([inputPath, outputPath]);
            // First-run model download can take a minute; allow 60s.
            if (Exec("rembg", exe, args, outputPath) is { } error) return new(false, null, 0, 0, error);
            var outBgra = ImageIO.DecodeImage(File.ReadAllBytes(outputPath), out int ow, out int oh);
            return new(true, outBgra, ow, oh, "ok" + (string.IsNullOrWhiteSpace(model) ? "" : " (model=" + model + ")"));
        }
        catch (Exception ex)
        {
            return new(false, null, 0, 0, "AI matting exception: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(inputPath))  File.Delete(inputPath); } catch { }
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
        }
    }

    /// <summary>
    /// Soft edges for a binary mask (matte.py: trimap of ±band px around the edge, closed-form alpha matting,
    /// background bled out of edge colors). imagePng and maskPng are the same size; returns straight-alpha BGRA.
    /// </summary>
    public static MattingResult RefineEdges(byte[] imagePng, byte[] maskPng, int band)
    {
        var python = FindRembgPython();
        if (python is null)
            return new(false, null, 0, 0, "rembg not found, or its Python could not be located. Install with: `pip install \"rembg[cpu,cli]\"`.");
        string tempDir = Path.Combine(Path.GetTempPath(), "paintdotnet-mcp-rembg");
        Directory.CreateDirectory(tempDir);
        string id = Guid.NewGuid().ToString("N");
        string script = Path.Combine(tempDir, "matte-" + id + ".py"), image = Path.Combine(tempDir, "img-" + id + ".png"),
            mask = Path.Combine(tempDir, "mask-" + id + ".png"), output = Path.Combine(tempDir, "matte-" + id + ".png");
        try
        {
            using (var resource = typeof(AiMatting).Assembly.GetManifestResourceStream("matte.py")!)
            using (var file = File.Create(script)) resource.CopyTo(file);
            File.WriteAllBytes(image, imagePng);
            File.WriteAllBytes(mask, maskPng);
            if (Exec("matte.py", python, [script, image, mask, output, band.ToString()], output) is { } error) return new(false, null, 0, 0, error);
            var bgra = ImageIO.DecodeImage(File.ReadAllBytes(output), out int w, out int h);
            return new(true, bgra, w, h, "ok");
        }
        catch (Exception ex)
        {
            return new(false, null, 0, 0, "edge matting exception: " + ex.Message);
        }
        finally
        {
            foreach (var f in new[] { script, image, mask, output })
                try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    // Runs a process to completion (60 s); returns null on success or the reason it failed.
    private static string? Exec(string label, string exe, IEnumerable<string> args, string expectedOutput)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var proc = Process.Start(psi);
        if (proc is null) return "failed to start " + label;
        // Drain stderr while waiting so a chatty process cannot block on a full pipe.
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit(60000);
        if (!proc.HasExited)
        {
            try { proc.Kill(true); } catch { }
            return label + " timed out (60s) — first run downloads the model.";
        }
        if (proc.ExitCode != 0) return label + " exit " + proc.ExitCode + ": " + stderr.Result.Trim();
        return File.Exists(expectedOutput) ? null : label + " produced no output";
    }

    // The interpreter rembg runs on is the one that has pymatting (a rembg dependency). pip's rembg.exe launcher
    // carries it as a #!"...python.exe" line; a venv keeps python.exe beside rembg.exe.
    private static string? FindRembgPython()
    {
        var exe = FindRembgExecutable();
        if (exe is null) return null;
        var text = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(exe));
        var shebang = System.Text.RegularExpressions.Regex.Match(text, "#!\"?([^\"\\r\\n]*python\\.exe)");
        if (shebang.Success && File.Exists(shebang.Groups[1].Value)) return shebang.Groups[1].Value;
        var beside = Path.Combine(Path.GetDirectoryName(exe)!, "python.exe");
        return File.Exists(beside) ? beside : null;
    }

    private static string? FindRembgExecutable()
    {
        var inPath = ResolveInPath("rembg.exe") ?? ResolveInPath("rembg");
        if (inPath is not null) return inPath;
        return null;
    }

    private static string? ResolveInPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            try
            {
                var full = Path.Combine(dir, fileName);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
