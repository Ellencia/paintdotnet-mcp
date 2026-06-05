using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PaintDotNetMcp.Bridge;

// Best-effort auto-commit: trigger Paint.NET to re-run "MCP Bridge" without the user clicking the menu.
//
// Strategy (in order):
//   1) Reflection: walk the Effect.Services container and look for a "Repeat last effect" command.
//      Paint.NET 5.x exposes services via an internal IServicesProvider; the exact command type
//      moves between versions, so we sniff for likely candidates instead of hard-coding.
//   2) Win32 keystroke fallback: PostMessage Ctrl+F to the Paint.NET main window. This works as
//      long as the user has their default keybinding for "Repeat last effect".
//
// Either path is best-effort. Failures fall back to "queued; user must invoke menu" messaging.
internal static class AutoCommit
{
    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const int VK_CONTROL = 0x11;
    private const int VK_F = 0x46;

    /// <summary>True if we were able to capture a Paint.NET window handle previously.</summary>
    public static bool Available => _hwnd != IntPtr.Zero;

    private static IntPtr _hwnd;

    // Debounce so rapid queue-bursts collapse into a single Ctrl+F.
    private static long _lastTriggerTick;
    private const int DebounceMs = 350;

    /// <summary>If false, no automatic commit is attempted; user must call the commit tool explicitly.</summary>
    public static bool Enabled = true;

    public static void ResetDebounce() => Interlocked.Exchange(ref _lastTriggerTick, 0);

    /// <summary>Cache the Paint.NET main window HWND. Cheap; safe to call from any render pass.</summary>
    public static void EnsureHwndCaptured()
    {
        if (_hwnd != IntPtr.Zero) return;
        try
        {
            var p = Process.GetCurrentProcess();
            var h = p.MainWindowHandle;
            if (h == IntPtr.Zero)
            {
                // MainWindowHandle is sometimes 0 right at startup; refresh.
                p.Refresh();
                h = p.MainWindowHandle;
            }
            if (h != IntPtr.Zero) _hwnd = h;
        }
        catch { }
    }

    /// <summary>Best-effort attempt; returns true if we sent something. Doesn't guarantee Paint.NET acted.</summary>
    public static bool TryTrigger(object? servicesHolder, out string note)
    {
        if (!Enabled)
        {
            note = "auto-commit disabled";
            return false;
        }

        // Debounce.
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastTriggerTick);
        if (now - last < DebounceMs)
        {
            note = "debounced (recent commit within " + DebounceMs + "ms)";
            return false;
        }

        // Path 1: reflection across the effect's Services.
        if (TryReflectiveRepeat(servicesHolder, out note))
        {
            Interlocked.Exchange(ref _lastTriggerTick, now);
            return true;
        }

        // Path 2: Win32 keystroke.
        if (_hwnd == IntPtr.Zero) EnsureHwndCaptured();
        if (_hwnd == IntPtr.Zero)
        {
            note = "no Paint.NET window handle; user must invoke menu";
            return false;
        }
        try
        {
            // Send Ctrl+F (default "Repeat last effect" shortcut). PostMessage is async; doesn't block.
            // Caveat: this only does the right thing if MCP Bridge was the most recently executed effect.
            PostMessage(_hwnd, WM_KEYDOWN, (IntPtr)VK_CONTROL, IntPtr.Zero);
            PostMessage(_hwnd, WM_KEYDOWN, (IntPtr)VK_F, IntPtr.Zero);
            PostMessage(_hwnd, WM_KEYUP, (IntPtr)VK_F, IntPtr.Zero);
            PostMessage(_hwnd, WM_KEYUP, (IntPtr)VK_CONTROL, IntPtr.Zero);
            Interlocked.Exchange(ref _lastTriggerTick, now);
            note = "posted Ctrl+F to Paint.NET main window";
            return true;
        }
        catch (Exception ex)
        {
            note = "Win32 PostMessage failed: " + ex.Message;
            return false;
        }
    }

    // Reflection fallback. We probe several likely service interface names. If a Paint.NET version
    // moves the API, this returns false and we drop down to the keystroke path.
    private static bool TryReflectiveRepeat(object? servicesHolder, out string note)
    {
        note = "";
        if (servicesHolder is null) return false;

        try
        {
            var holderType = servicesHolder.GetType();
            // Find a "Services" property of IServiceProvider on the Effect / wrapper.
            var servicesProp = holderType.GetProperty("Services",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var services = servicesProp?.GetValue(servicesHolder) as IServiceProvider;
            if (services is null)
            {
                note = "no Services on " + holderType.Name;
                return false;
            }

            // Likely names — we don't bind to concrete types, just walk well-known interfaces.
            string[] candidates =
            {
                "PaintDotNet.AppModel.IDocumentWorkspaceService",
                "PaintDotNet.AppModel.IAppWorkspaceCommands",
                "PaintDotNet.IAppService",
            };
            foreach (var name in candidates)
            {
                var t = FindType(name);
                if (t is null) continue;
                var svc = services.GetService(t);
                if (svc is null) continue;
                // Look for any parameterless method whose name implies "repeat effect".
                var m = svc.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(mi =>
                        mi.GetParameters().Length == 0 &&
                        (mi.Name.Contains("RepeatLastEffect", StringComparison.OrdinalIgnoreCase) ||
                         mi.Name.Contains("RepeatEffect", StringComparison.OrdinalIgnoreCase)));
                if (m is null) continue;
                m.Invoke(svc, null);
                note = "invoked " + svc.GetType().Name + "." + m.Name;
                return true;
            }
            note = "no repeat-effect service found via reflection";
            return false;
        }
        catch (Exception ex)
        {
            note = "reflection commit failed: " + ex.Message;
            return false;
        }
    }

    private static Type? FindType(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(fullName); if (t is not null) return t; }
            catch { }
        }
        return null;
    }
}
