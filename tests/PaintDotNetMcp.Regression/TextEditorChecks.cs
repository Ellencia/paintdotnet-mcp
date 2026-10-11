using System.Drawing;
using System.Globalization;
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
                foreach (var name in new[] { "it-IT", "en-US", "fr-FR" })
                {
                    CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
                    Check();
                }
                CheckDocumentNavigation();
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        if (failure is not null) throw new Exception("Text editor control regression", failure);
        Console.WriteLine("PASS text editor menu registration is idempotent; saved properties load; invalid Apply and Cancel do not mutate");
    }

    private static void Check()
    {
        var assembly = typeof(BridgeEffect).Assembly;
        var services = assembly.GetType("PaintDotNetMcp.Bridge.AppServices")!;
        var cache = (Dictionary<string, object?>)services.GetField("_cache", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        using var menu = new MenuStrip();
        var italian = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "it";
        var effectsMenu = new ToolStripMenuItem(italian ? "&Effetti" : "&Effects");
        menu.Items.Add(effectsMenu);
        cache["appws"] = new TestEditorApp { ToolBar = new TestEditorToolbar { MainMenu = menu } };
        try
        {
            var install = assembly.GetType("PaintDotNetMcp.Bridge.TextEditor")!.GetMethod("InstallMenu", BindingFlags.NonPublic | BindingFlags.Static)!;
            if (!(bool)install.Invoke(null, null)! || !(bool)install.Invoke(null, null)! || menu.Items.Count != 1 || effectsMenu.DropDownItems.Count != 1)
                throw new Exception("Menu must register exactly once");
            var root = (ToolStripMenuItem)effectsMenu.DropDownItems[0];
            if (root.Text != "MCP" || root.DropDownItems.Count != 1 || root.DropDownItems[0].Text != (italian ? "Modifica testo..." : "Edit text..."))
                throw new Exception("Manual editor entry point is missing");
            var initial = new TextLayerResult { LayerIndex = 2, Name = "Date", Id = "test",
                Text = new DrawTextParams { Text = "Evento\n2026.11.14", FontFamily = "Malgun Gothic", FontSize = 26,
                    X = 108, Y = 611, R = 99, G = 218, B = 236, A = 180, Bold = true }, PixelsModified = true };
            using var form = (Form)Activator.CreateInstance(assembly.GetType("PaintDotNetMcp.Bridge.TextEditorForm")!,
                BindingFlags.Instance | BindingFlags.NonPublic, null, [initial, new object(), new object(), new object()], null)!;
            Control Field(string name) => (Control)form.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(form)!;
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
        var first = new TestDocumentWorkspace { FilePath = @"C:\\pictures\\banner.pdn" };
        var second = new TestDocumentWorkspace { FilePath = @"C:\\pictures\\logo.pdn" };
        var app = new TestDocumentApp
        {
            DocumentWorkspaces = [first, second],
            ActiveDocumentWorkspace = first
        };
        cache["mainForm"] = form;
        cache["appws"] = app;
        try
        {
            var list = nav.GetMethod("List", BindingFlags.NonPublic | BindingFlags.Static)!;
            var activate = nav.GetMethod("Activate", BindingFlags.NonPublic | BindingFlags.Static)!;
            var result = list.Invoke(null, null)!;
            if ((int)result.GetType().GetProperty("Count")!.GetValue(result)! != 2)
                throw new Exception("Open document listing must include inactive tabs");
            var switched = activate.Invoke(null, [1])!;
            if (!ReferenceEquals(app.ActiveDocumentWorkspace, second) ||
                (string?)switched.GetType().GetProperty("Name")!.GetValue(switched) != "logo.pdn")
                throw new Exception("Document activation should target the requested tab");
        }
        finally { cache.Clear(); }
    }

    public sealed class TestDocumentWorkspace
    {
        public object Document { get; } = new();
        public string? FilePath { get; init; }
    }

    public sealed class TestDocumentApp
    {
        public TestDocumentWorkspace[] DocumentWorkspaces { get; init; } = [];
        public TestDocumentWorkspace? ActiveDocumentWorkspace { get; set; }
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

    public sealed class TestEditorApp { public TestEditorToolbar ToolBar { get; init; } = new(); }
    public sealed class TestEditorToolbar { public MenuStrip MainMenu { get; init; } = null!; }
}
