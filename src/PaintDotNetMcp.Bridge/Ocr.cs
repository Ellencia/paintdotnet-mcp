using System.Diagnostics;
using System.IO;

namespace PaintDotNetMcp.Bridge;

// OCR via Tesseract CLI shell-out. We don't bundle the binary — if `tesseract` isn't on PATH
// the tool returns ok=false with an install hint. This keeps the bridge dependency-free.
//
// Why Tesseract instead of Windows.Media.Ocr (WinRT)?
//   - WinRT projection from a Paint.NET plugin assembly is fragile under the host's load
//     context. Shell-out is rock-solid and language-agnostic (kor/eng/jpn/... swap by flag).
//   - The downside is the user has to install Tesseract once; on Windows this is a single
//     `winget install UB-Mannheim.TesseractOCR` command.
internal static class Ocr
{
    public sealed record OcrResult(bool Ok, string Text, string Note);

    /// <summary>
    /// Run Tesseract on a PNG region of the canvas. Returns recognized text or an error note.
    /// Language code follows Tesseract conventions: "eng", "kor", "eng+kor", etc.
    /// </summary>
    public static OcrResult RunOnRegion(byte[] bgra, int canvasW, int canvasH, int x, int y, int w, int h, string lang)
    {
        // Probe for tesseract executable.
        var exe = FindTesseractExecutable();
        if (exe is null)
        {
            return new(false, "", "tesseract executable not found on PATH. " +
                "Install with: `winget install UB-Mannheim.TesseractOCR` (Windows) " +
                "and ensure Korean language pack (kor.traineddata) is included.");
        }

        // Encode the requested region as PNG to a temp file.
        string tempDir = Path.Combine(Path.GetTempPath(), "paintdotnet-mcp-ocr");
        Directory.CreateDirectory(tempDir);
        string inputPath = Path.Combine(tempDir, "input-" + Guid.NewGuid().ToString("N") + ".png");
        string outputBase = Path.Combine(tempDir, "out-" + Guid.NewGuid().ToString("N"));

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
            psi.ArgumentList.Add(inputPath);
            psi.ArgumentList.Add(outputBase);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(lang) ? "eng" : lang);

            using var proc = Process.Start(psi);
            if (proc is null) return new(false, "", "failed to start tesseract");
            proc.WaitForExit(15000);
            if (!proc.HasExited)
            {
                try { proc.Kill(true); } catch { }
                return new(false, "", "tesseract timed out (15s)");
            }
            if (proc.ExitCode != 0)
            {
                var err = proc.StandardError.ReadToEnd();
                return new(false, "", "tesseract exit " + proc.ExitCode + ": " + err.Trim());
            }

            string txtPath = outputBase + ".txt";
            if (!File.Exists(txtPath)) return new(false, "", "tesseract produced no .txt output");
            var text = File.ReadAllText(txtPath).Trim();
            return new(true, text, "ok (lang=" + lang + ")");
        }
        catch (Exception ex)
        {
            return new(false, "", "OCR exception: " + ex.Message);
        }
        finally
        {
            try { if (File.Exists(inputPath)) File.Delete(inputPath); } catch { }
            try { if (File.Exists(outputBase + ".txt")) File.Delete(outputBase + ".txt"); } catch { }
        }
    }

    private static string? FindTesseractExecutable()
    {
        // PATH-resolved name first.
        var inPath = ResolveInPath("tesseract.exe") ?? ResolveInPath("tesseract");
        if (inPath is not null) return inPath;

        // Common Windows install locations.
        var probes = new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
        };
        foreach (var p in probes) if (File.Exists(p)) return p;
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
