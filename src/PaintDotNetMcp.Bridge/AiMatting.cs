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
    public static MattingResult RunOnRegion(byte[] bgra, int canvasW, int canvasH, int x, int y, int w, int h, string model)
    {
        var exe = FindRembgExecutable();
        if (exe is null)
        {
            return new(false, null, 0, 0,
                "rembg not found. Install with: `pip install rembg[cli]` (first run downloads ~170MB model).");
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

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            // rembg CLI signature: `rembg i [-m model] input output`
            psi.ArgumentList.Add("i");
            if (!string.IsNullOrWhiteSpace(model))
            {
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(model);
            }
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputPath);

            using var proc = Process.Start(psi);
            if (proc is null) return new(false, null, 0, 0, "failed to start rembg");
            // First-run model download can take a minute; allow 60s.
            proc.WaitForExit(60000);
            if (!proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
                return new(false, null, 0, 0, "rembg timed out (60s) — first run downloads the model.");
            }
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                return new(false, null, 0, 0, "rembg exit " + proc.ExitCode + ": " + err.Trim());
            }

            if (!File.Exists(outputPath)) return new(false, null, 0, 0, "rembg produced no output");
            var outBytes = File.ReadAllBytes(outputPath);
            var outBgra = ImageIO.DecodeImage(outBytes, out int ow, out int oh);
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
