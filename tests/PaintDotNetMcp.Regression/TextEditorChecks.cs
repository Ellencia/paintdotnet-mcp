using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using PaintDotNetMcp.Bridge;
using PaintDotNetMcp.Contracts;

internal static class TextEditorChecks
{
    public static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                foreach (var (culture, title) in new[] { ("ko-KR", "MCP 텍스트 편집"), ("en-US", "MCP Text Editor"), ("it-IT", "Editor di testo MCP"), ("fr-FR", "MCP Text Editor") })
                {
                    System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
                    Check(title);
                }
                CheckDocumentNavigation();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Text editor control regression", failure);
        Console.WriteLine("PASS text editor effect entry point; saved properties load; invalid Apply and Cancel do not mutate; tabs list and switch");
    }

    private static void Check(string title)
    {
        var assembly = typeof(BridgeEffect).Assembly;
        var services = assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!;
        var cache = (Dictionary<string, object?>)services.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        try
        {
            // Entry point is an effect (Effects > Tools), never an item injected into Paint.NET's menus.
            var entry = Activator.CreateInstance(assembly.GetType("PaintDotNetMcp.Bridge.TextEditorEffect")!)!;
            string? Prop(string n) => entry.GetType().GetProperty(n)!.GetValue(entry) as string;
            if (Prop("Name") != title || Prop("SubMenuName") != "Tools")
                throw new Exception($"Manual editor entry point is missing or not localized ({Prop("Name")})");
            ((IDisposable)entry).Dispose();
            var initial = new TextLayerResult { LayerIndex = 2, Name = "Date", Id = "test",
                Text = new DrawTextParams { Text = "행사\n2026.11.14", FontFamily = "Malgun Gothic", FontSize = 26,
                    X = 108, Y = 611, R = 99, G = 218, B = 236, A = 180, Bold = true }, PixelsModified = true };
            using var form = (Form)Activator.CreateInstance(assembly.GetType("PaintDotNetMcp.Bridge.TextEditorForm")!,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [initial, new object(), new object(), new object()], null)!;
            Control Field(string name) => (Control)form.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
            if (form.Text != title + " — Date") throw new Exception($"Editor title not localized: {form.Text}");
            if (Field("_text").Text.Replace("\r\n", "\n") != initial.Text.Text || Field("_font").Text != "Malgun Gothic" ||
                ((NumericUpDown)Field("_size")).Value != 26 || ((NumericUpDown)Field("_a")).Value != 180 ||
                !((CheckBox)Field("_bold")).Checked || ((CheckBox)Field("_replace")).Checked || string.IsNullOrEmpty(Field("_error").Text))
                throw new Exception("Stored properties or explicit replacement consent were lost");
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000); form.Show();
            Field("_text").Text = " ";
            var apply = Find(form, "ApplyText") as Button ?? throw new Exception("Apply button missing");
            apply.PerformClick();
            if (form.IsDisposed || !Field("_error").Text.Contains("4096")) throw new Exception("Invalid text must leave editor open with a readable error");
            ((Button)form.CancelButton!).PerformClick();
            if (!form.IsDisposed) throw new Exception("Cancel must close without calling the document transaction");
        }
        finally { cache.Clear(); }
    }

    private static void CheckDocumentNavigation()
    {
        var assembly = typeof(BridgeEffect).Assembly;
        var nav = assembly.GetType("PaintDotNetMcp.Bridge.DocumentNavigation")!;
        var cache = (Dictionary<string, object?>)assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!
            .GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var form = new Form();
        var first = new TestTab { FilePath = @"C:\pictures\banner.pdn" };
        var second = new TestTab();
        var app = new TestTabsApp { DocumentWorkspaces = [first, second], ActiveDocumentWorkspace = first };
        cache["mainForm"] = form;
        cache["appws"] = app;
        try
        {
            object Call(string name, params object[] args)
            {
                try { return nav.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!; }
                catch (TargetInvocationException ex) { throw ex.InnerException!; }
            }
            object? Get(object o, string p) => o.GetType().GetProperty(p)!.GetValue(o);
            var list = Call("List");
            if ((int)Get(list, "Count")! != 2) throw new Exception("Listing must include inactive tabs");
            var switched = Call("Activate", 1);
            if (!ReferenceEquals(app.ActiveDocumentWorkspace, second) || (string?)Get(switched, "Name") != "Untitled" || Get(switched, "Path") is not null)
                throw new Exception("Activation must switch to the requested tab and report it");
            try { Call("Activate", 2); throw new Exception("Out-of-range index must be rejected"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("Only 2")) { }
            app.Refuse = true;
            try { Call("Activate", 0); throw new Exception("A refused switch must not report success"); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("refused")) { }
            if (!ReferenceEquals(app.ActiveDocumentWorkspace, second)) throw new Exception("Refused switch changed the active tab");
        }
        finally { cache.Clear(); }
    }

    public sealed class TestTab
    {
        public object Document { get; } = new();
        public string? FilePath { get; init; }
        public string GetFileDisplayName() => FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
    }

    public sealed class TestTabsApp
    {
        public TestTab[] DocumentWorkspaces { get; init; } = [];
        public TestTab? ActiveDocumentWorkspace { get; set; }
        public bool Refuse { get; set; }
        public bool TrySetActiveDocumentWorkspace(TestTab tab) { if (Refuse) return false; ActiveDocumentWorkspace = tab; return true; }
    }

    private static Control? Find(Control parent, string name)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.Name == name) return child;
            if (Find(child, name) is { } found) return found;
        }
        return null;
    }
}
