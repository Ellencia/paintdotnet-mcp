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
            try { Check(); } catch (Exception ex) { failure = ex; }
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
        cache["appws"] = new TestEditorApp { ToolBar = new TestEditorToolbar { MainMenu = menu } };
        try
        {
            var install = assembly.GetType("PaintDotNetMcp.Bridge.TextEditor")!.GetMethod("InstallMenu", BindingFlags.NonPublic | BindingFlags.Static)!;
            if (!(bool)install.Invoke(null, null)! || !(bool)install.Invoke(null, null)! || menu.Items.Count != 1)
                throw new Exception("Menu must register exactly once");
            var root = (ToolStripMenuItem)menu.Items[0];
            if (root.Text != "MCP" || root.DropDownItems.Count != 1 || root.DropDownItems[0].Text != "텍스트 편집…")
                throw new Exception("Manual editor entry point is missing");
            var initial = new TextLayerResult { LayerIndex = 2, Name = "Date", Id = "test",
                Text = new DrawTextParams { Text = "행사\n2026.11.14", FontFamily = "Malgun Gothic", FontSize = 26,
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
