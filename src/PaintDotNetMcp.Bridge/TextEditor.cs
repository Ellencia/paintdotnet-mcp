using System.Drawing;
using static PaintDotNetMcp.Bridge.EditorStrings;
using System.Windows.Forms;
using PaintDotNetMcp.Contracts;

namespace PaintDotNetMcp.Bridge;

// A document editor, not an Effect: Apply uses the same native layer/history transaction as MCP.
internal static class TextEditor
{
    private const string MenuName = "PaintDotNetMcp.TextMenu";
    private static int _registrationStarted;
    private static TextEditorForm? _editor;

    public static void EnsureMenuRegistration()
    {
        if (Interlocked.Exchange(ref _registrationStarted, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 120; attempt++)
            {
                bool installed = false;
                AppServices.InvokeOnUiThread(() => installed = InstallMenu(), out _);
                if (installed) return;
                await Task.Delay(500);
            }
            Interlocked.Exchange(ref _registrationStarted, 0);
        });
    }

    private static bool InstallMenu()
    {
        var workspace = AppServices.AppWorkspaceService();
        var toolbar = workspace is null ? null : AppServices.GetPropertyValue(workspace, "ToolBar");
        if (toolbar is null || AppServices.GetPropertyValue(toolbar, "MainMenu") is not MenuStrip menu) return false;
        var parent = menu.Items.OfType<ToolStripMenuItem>().FirstOrDefault(item =>
        {
            var label = item.Text?.Replace("&", "").Trim() ?? string.Empty;
            return string.Equals(label, "Effects", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(label, "Effetti", StringComparison.OrdinalIgnoreCase);
        });
        if (parent is null) return false;
        if (parent.DropDownItems.ContainsKey(MenuName)) return true;
        var root = new ToolStripMenuItem("MCP") { Name = MenuName };
        var edit = new ToolStripMenuItem(L("Modifica testo...", "Edit text...")) { Name = "PaintDotNetMcp.EditText" };
        edit.Click += (_, _) =>
        {
            try { Open(); }
            catch (Exception ex) { var message = ex.Message; const string prefix = "InvalidOperationException: "; if (message.StartsWith(prefix, StringComparison.Ordinal)) message = message[prefix.Length..]; MessageBox.Show(AppServices.GetMainForm() as IWin32Window, message,
                L("Editor di testo MCP", "MCP Text Editor"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        root.DropDownItems.Add(edit);
        parent.DropDownItems.Add(root);
        menu.PerformLayout();
        (toolbar as Control)?.PerformLayout();
        return true;
    }

    public static object Open()
    {
        // Never wait on an effect from its own UI thread.
        if (AutoCommit.IsExecuting || BridgeServer.PendingCount > 0 || HistoryOps.BatchActive)
            throw new InvalidOperationException(L("Termina il disegno o il gruppo di operazioni in corso prima di aprire l'editor di testo.", "Finish the current drawing operation or batch before opening the text editor."));
        bool menuInstalled = false;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            menuInstalled = InstallMenu();
            if (_editor is { IsDisposed: false }) { _editor.Activate(); return; }
            var workspace = AppServices.DocumentWorkspaceService() ?? throw new InvalidOperationException(L("Apri prima un'immagine o un documento.", "Open an image or document first."));
            var document = NativeEditing.Property(workspace, "Document");
            var layer = NativeEditing.Property(workspace, "ActiveLayer");
            TextLayerResult current;
            try { current = (TextLayerResult)TextLayers.Get(new()); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException(L("Seleziona nel pannello Livelli un livello di testo creato con MCP, quindi riapri l'editor. Il testo già disegnato su un'immagine normale non può essere modificato con questo strumento.", "Select an MCP-created text layer in the Layers panel, then reopen the editor. Text already painted onto a regular image cannot be edited here."), ex);
            }
            _editor = new TextEditorForm(current, workspace, document, layer);
            _editor.Show(AppServices.GetMainForm() as IWin32Window);
        }, out var note)) throw new InvalidOperationException(note);
        return new { Opened = true, MenuInstalled = menuInstalled };
    }
}

internal sealed class TextEditorForm : Form
{
    private readonly TextLayerResult _initial;
    private readonly object _workspace, _document, _layer;
    private readonly TextBox _text = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
    private readonly ComboBox _font = new() { DropDownStyle = ComboBoxStyle.DropDown, Dock = DockStyle.Fill };
    private readonly NumericUpDown _size = Number(0, 512, 2);
    private readonly NumericUpDown _x = Number(int.MinValue, int.MaxValue), _y = Number(int.MinValue, int.MaxValue);
    private readonly NumericUpDown _r = Number(0, 255), _g = Number(0, 255), _b = Number(0, 255), _a = Number(0, 255);
    private readonly CheckBox _bold = new() { Text = L("Grassetto", "Bold"), AutoSize = true }, _italic = new() { Text = L("Corsivo", "Italic"), AutoSize = true };
    private readonly CheckBox _smooth = new() { Text = L("Smussa i bordi del testo", "Smooth text edges"), AutoSize = true };
    private readonly CheckBox _replace = new() { Text = L("Sovrascrivi le modifiche grafiche del livello", "Replace subsequent pixel changes on this layer"), AutoSize = true };
    private readonly Label _error = new() { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };

    internal TextEditorForm(TextLayerResult initial, object workspace, object document, object layer)
    {
        _initial = initial; _workspace = workspace; _document = document; _layer = layer;
        Text = "Editor di testo MCP — " + initial.Name;
        Name = "PaintDotNetMcp.TextEditor";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(760, 700); MinimumSize = new Size(700, 650);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 142));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        void Row(int row, string label, Control control, int height)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
        Row(0, L("Testo", "Text"), _text, 160);
        Row(1, L("Carattere", "Font"), _font, 38);
        Row(2, L("Dimensione", "Size"), _size, 38);
        Row(3, L("Stile", "Style"), Flow(_bold, _italic, _smooth), 38);
        Row(4, L("Posizione (pixel)", "Position (pixels)"), Flow(new Label { Text = "X", AutoSize = true }, _x, new Label { Text = "Y", AutoSize = true }, _y), 38);
        var chooseColor = new Button { Text = L("Scegli colore...", "Choose color..."), AutoSize = true };
        chooseColor.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)_r.Value, (int)_g.Value, (int)_b.Value) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _r.Value = dialog.Color.R; _g.Value = dialog.Color.G; _b.Value = dialog.Color.B; }
        };
        Row(5, L("Colore (RGB)", "Color (RGB)"), Flow(chooseColor, _r, _g, _b), 38);
        Row(6, L("Opacità", "Opacity"), Flow(_a, new Label { Text = L("0: trasparente / 255: opaco", "0: transparent / 255: opaque"), AutoSize = true }), 38);
        Row(7, L("Modifiche grafiche", "Pixel changes"), _replace, 52);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_error, 0, 8); layout.SetColumnSpan(_error, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var apply = new Button { Text = L("Applica", "Apply"), AutoSize = true, Name = "ApplyText" };
        var cancel = new Button { Text = L("Annulla", "Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Flow(apply, cancel), 1, 9);
        CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => ApplyChanges();
        foreach (var family in FontFamily.Families) { _font.Items.Add(family.Name); family.Dispose(); }
        var p = initial.Text;
        _text.Text = p.Text; _font.Text = p.FontFamily; _size.Value = (decimal)p.FontSize;
        _x.Value = p.X; _y.Value = p.Y; _r.Value = p.R; _g.Value = p.G; _b.Value = p.B; _a.Value = p.A;
        _bold.Checked = p.Bold; _italic.Checked = p.Italic; _smooth.Checked = p.AntiAlias;
        if (initial.PixelsModified) _error.Text = L("Il livello è stato modificato dopo la creazione del testo. Applicando le modifiche, il livello verrà ricreato. Seleziona la casella qui sopra per confermare.", "Pixels have changed since this text layer was created. Applying will rebuild the layer. Select the checkbox above to confirm.");
    }

    private static NumericUpDown Number(decimal min, decimal max, int places = 0) => new()
        { Minimum = min, Maximum = max, DecimalPlaces = places, Width = places == 0 && max == 255 ? 58 : 110 };
    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        panel.Controls.AddRange(controls); return panel;
    }

    private void ApplyChanges()
    {
        try
        {
            if (AutoCommit.IsExecuting || HistoryOps.BatchActive || BridgeServer.PendingCount > 0)
                throw new InvalidOperationException(L("Termina l'operazione di disegno in corso prima di applicare le modifiche.", "Finish the current drawing operation before applying changes."));
            if (string.IsNullOrWhiteSpace(_text.Text) || _text.Text.Length > 4096)
                throw new InvalidOperationException(L("Il testo non può essere vuoto o contenere soltanto spazi. Lunghezza massima: 4096 caratteri.", "Text cannot be empty or contain only spaces. Maximum length: 4096 characters."));
            if (_size.Value <= 0) throw new InvalidOperationException(L("La dimensione del testo deve essere maggiore di zero.", "Text size must be greater than zero."));
            // Modeless window: fail safely if the user or MCP changed the target while it was open.
            if (!ReferenceEquals(AppServices.DocumentWorkspaceService(), _workspace) ||
                !ReferenceEquals(NativeEditing.Property(_workspace, "Document"), _document) ||
                _initial.LayerIndex >= ((PaintDotNet.Document)_document).Layers.Count ||
                !ReferenceEquals(((PaintDotNet.Document)_document).Layers[_initial.LayerIndex], _layer))
                throw new InvalidOperationException(L("Il documento o il livello selezionato è cambiato. Chiudi questa finestra, seleziona nuovamente il livello da modificare e riapri l'editor.", "The document or target layer changed. Close this window, select the intended layer, and reopen the editor."));
            TextLayers.Update(new UpdateTextLayerParams
            {
                LayerIndex = _initial.LayerIndex,
                Text = _text.Text.Replace("\r\n", "\n") == _initial.Text.Text.Replace("\r\n", "\n") ? _initial.Text.Text : _text.Text,
                FontFamily = _font.Text, FontSize = (float)_size.Value,
                X = (int)_x.Value, Y = (int)_y.Value, R = (byte)_r.Value, G = (byte)_g.Value, B = (byte)_b.Value, A = (byte)_a.Value,
                Bold = _bold.Checked, Italic = _italic.Checked, AntiAlias = _smooth.Checked, ReplaceModifiedPixels = _replace.Checked
            });
            Close();
        }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
}
