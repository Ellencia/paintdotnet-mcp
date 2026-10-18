using System.Drawing;
using System.Windows.Forms;
using PaintDotNetMcp.Contracts;
using static PaintDotNetMcp.Bridge.EditorStrings;

namespace PaintDotNetMcp.Bridge;

// A document editor, not an Effect: Apply uses the same native layer/history transaction as MCP.
internal static class TextEditor
{
    private static TextEditorForm? _editor;

    public static object Open()
    {
        // Never wait on an effect from its own UI thread.
        if (AutoCommit.IsExecuting || BridgeServer.PendingCount > 0 || HistoryOps.BatchActive)
            throw new InvalidOperationException(L("진행 중인 그리기나 작업 묶음을 마친 뒤 텍스트 편집을 여세요.",
                "Finish the current drawing operation or batch before opening the text editor.",
                "Termina il disegno o il gruppo di operazioni in corso prima di aprire l'editor di testo."));
        if (!AppServices.InvokeOnUiThread(Show, out var note)) throw new InvalidOperationException(note);
        return new { Opened = true };
    }

    // Effects > Tools > EditorTitle. The host UI thread is waiting on the effect, so post instead of Invoke.
    // Paint.NET rejects plugin items in its own menus ("Invalid menu item detected"), hence an effect.
    public static void OpenFromEffect() => AppServices.PostOnUiThread(() =>
    {
        try { Show(); }
        catch (Exception ex) { MessageBox.Show(AppServices.GetMainForm() as IWin32Window, ex.Message,
            EditorTitle, MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }, out _);

    private static void Show()
    {
        if (_editor is { IsDisposed: false }) { _editor.Activate(); return; }
        var workspace = AppServices.DocumentWorkspaceService() ?? throw new InvalidOperationException(
            L("먼저 문서를 여세요.", "Open an image or document first.", "Apri prima un'immagine o un documento."));
        var document = NativeEditing.Property(workspace, "Document");
        var layer = NativeEditing.Property(workspace, "ActiveLayer");
        TextLayerResult current;
        try { current = (TextLayerResult)TextLayers.Get(new()); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(L(
                "레이어 목록에서 MCP로 만든 텍스트 레이어를 선택한 뒤 다시 여세요. 일반 사진에 그려진 글자는 편집할 수 없습니다.",
                "Select an MCP-created text layer in the Layers panel, then reopen the editor. Text already painted onto a regular image cannot be edited here.",
                "Seleziona nel pannello Livelli un livello di testo creato con MCP, quindi riapri l'editor. Il testo già disegnato su un'immagine normale non può essere modificato con questo strumento."), ex);
        }
        _editor = new TextEditorForm(current, workspace, document, layer);
        _editor.Show(AppServices.GetMainForm() as IWin32Window);
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
    private readonly CheckBox _bold = new() { Text = L("굵게", "Bold", "Grassetto"), AutoSize = true }, _italic = new() { Text = L("기울임", "Italic", "Corsivo"), AutoSize = true };
    private readonly CheckBox _smooth = new() { Text = L("글자 가장자리 부드럽게", "Smooth text edges", "Smussa i bordi del testo"), AutoSize = true };
    private readonly CheckBox _replace = new() { Text = L("추가로 그린 그림을 지우고 텍스트만 다시 만들기", "Erase later drawing and rebuild the text only", "Sovrascrivi le modifiche grafiche del livello"), AutoSize = true };
    private readonly Label _error = new() { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };

    internal TextEditorForm(TextLayerResult initial, object workspace, object document, object layer)
    {
        _initial = initial; _workspace = workspace; _document = document; _layer = layer;
        Text = EditorTitle + " — " + initial.Name;
        Name = "PaintDotNetMcp.TextEditor";
        AutoScaleMode = AutoScaleMode.Dpi;
        // Sized for the longest (Italian) labels.
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
        Row(0, L("문구", "Text", "Testo"), _text, 160);
        Row(1, L("글꼴", "Font", "Carattere"), _font, 38);
        Row(2, L("글자 크기", "Size", "Dimensione"), _size, 38);
        Row(3, L("스타일", "Style", "Stile"), Flow(_bold, _italic, _smooth), 38);
        Row(4, L("위치 (픽셀)", "Position (pixels)", "Posizione (pixel)"), Flow(new Label { Text = "X", AutoSize = true }, _x, new Label { Text = "Y", AutoSize = true }, _y), 38);
        var chooseColor = new Button { Text = L("색 고르기…", "Choose color…", "Scegli colore…"), AutoSize = true };
        chooseColor.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)_r.Value, (int)_g.Value, (int)_b.Value) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _r.Value = dialog.Color.R; _g.Value = dialog.Color.G; _b.Value = dialog.Color.B; }
        };
        Row(5, L("색상 (RGB)", "Color (RGB)", "Colore (RGB)"), Flow(chooseColor, _r, _g, _b), 38);
        Row(6, L("불투명도", "Opacity", "Opacità"), Flow(_a, new Label { Text = L("0: 투명 / 255: 불투명", "0: transparent / 255: opaque", "0: trasparente / 255: opaco"), AutoSize = true }), 38);
        Row(7, L("그림 변경", "Pixel changes", "Modifiche grafiche"), _replace, 52);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_error, 0, 8); layout.SetColumnSpan(_error, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var apply = new Button { Text = L("적용", "Apply", "Applica"), AutoSize = true, Name = "ApplyText" };
        var cancel = new Button { Text = L("취소", "Cancel", "Annulla"), AutoSize = true, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Flow(apply, cancel), 1, 9);
        CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => ApplyChanges();
        foreach (var family in FontFamily.Families) { _font.Items.Add(family.Name); family.Dispose(); }
        var p = initial.Text;
        _text.Text = p.Text; _font.Text = p.FontFamily; _size.Value = (decimal)p.FontSize;
        _x.Value = p.X; _y.Value = p.Y; _r.Value = p.R; _g.Value = p.G; _b.Value = p.B; _a.Value = p.A;
        _bold.Checked = p.Bold; _italic.Checked = p.Italic; _smooth.Checked = p.AntiAlias;
        if (initial.PixelsModified) _error.Text = L(
            "텍스트 생성 후 그림이 변경되었습니다. 적용하면 이 레이어 전체를 다시 만듭니다. 위 확인란을 선택해야 적용할 수 있습니다.",
            "Pixels have changed since this text layer was created. Applying will rebuild the layer. Select the checkbox above to confirm.",
            "Il livello è stato modificato dopo la creazione del testo. Applicando le modifiche, il livello verrà ricreato. Seleziona la casella qui sopra per confermare.");
    }

    private static NumericUpDown Number(decimal min, decimal max, int places = 0) => new()
        { Minimum = min, Maximum = max, DecimalPlaces = places, Width = places == 0 && max == 255 ? 76 : 110 };
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
                throw new InvalidOperationException(L("진행 중인 그리기를 마친 뒤 다시 적용하세요.",
                    "Finish the current drawing operation before applying changes.",
                    "Termina l'operazione di disegno in corso prima di applicare le modifiche."));
            if (string.IsNullOrWhiteSpace(_text.Text) || _text.Text.Length > 4096)
                throw new InvalidOperationException(L("문구는 공백만 넣을 수 없으며, 최대 4096자까지 입력할 수 있습니다.",
                    "Text cannot be empty or contain only spaces. Maximum length: 4096 characters.",
                    "Il testo non può essere vuoto o contenere soltanto spazi. Lunghezza massima: 4096 caratteri."));
            if (_size.Value <= 0) throw new InvalidOperationException(L("글자 크기는 0보다 커야 합니다.",
                "Text size must be greater than zero.", "La dimensione del testo deve essere maggiore di zero."));
            // Modeless window: fail safely if the user or MCP changed the target while it was open.
            if (!ReferenceEquals(AppServices.DocumentWorkspaceService(), _workspace) ||
                !ReferenceEquals(NativeEditing.Property(_workspace, "Document"), _document) ||
                _initial.LayerIndex >= ((PaintDotNet.Document)_document).Layers.Count ||
                !ReferenceEquals(((PaintDotNet.Document)_document).Layers[_initial.LayerIndex], _layer))
                throw new InvalidOperationException(L("문서나 대상 레이어가 변경되었습니다. 창을 닫고 편집할 레이어에서 다시 여세요.",
                    "The document or target layer changed. Close this window, select the intended layer, and reopen the editor.",
                    "Il documento o il livello selezionato è cambiato. Chiudi questa finestra, seleziona nuovamente il livello da modificare e riapri l'editor."));
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
