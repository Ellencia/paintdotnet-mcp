using System.Drawing;
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
        if (menu.Items.ContainsKey(MenuName)) return true;
        var root = new ToolStripMenuItem("MCP") { Name = MenuName };
        var edit = new ToolStripMenuItem("텍스트 편집…") { Name = "PaintDotNetMcp.EditText" };
        edit.Click += (_, _) =>
        {
            try { Open(); }
            catch (Exception ex) { MessageBox.Show(AppServices.GetMainForm() as IWin32Window, ex.Message,
                "MCP 텍스트 편집", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        root.DropDownItems.Add(edit);
        menu.Items.Add(root);
        menu.PerformLayout();
        (toolbar as Control)?.PerformLayout();
        return true;
    }

    public static object Open()
    {
        // Never wait on an effect from its own UI thread.
        if (AutoCommit.IsExecuting || BridgeServer.PendingCount > 0 || HistoryOps.BatchActive)
            throw new InvalidOperationException("진행 중인 그리기나 작업 묶음을 마친 뒤 텍스트 편집을 여세요.");
        bool menuInstalled = false;
        if (!AppServices.InvokeOnUiThread(() =>
        {
            menuInstalled = InstallMenu();
            if (_editor is { IsDisposed: false }) { _editor.Activate(); return; }
            var workspace = AppServices.DocumentWorkspaceService() ?? throw new InvalidOperationException("먼저 문서를 여세요.");
            var document = NativeEditing.Property(workspace, "Document");
            var layer = NativeEditing.Property(workspace, "ActiveLayer");
            TextLayerResult current;
            try { current = (TextLayerResult)TextLayers.Get(new()); }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException("레이어 목록에서 MCP로 만든 텍스트 레이어를 선택한 뒤 다시 여세요. 일반 사진에 그려진 글자는 편집할 수 없습니다.", ex);
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
    private readonly CheckBox _bold = new() { Text = "굵게", AutoSize = true }, _italic = new() { Text = "기울임", AutoSize = true };
    private readonly CheckBox _smooth = new() { Text = "글자 가장자리 부드럽게", AutoSize = true };
    private readonly CheckBox _replace = new() { Text = "추가로 그린 그림을 지우고 텍스트만 다시 만들기", AutoSize = true };
    private readonly Label _error = new() { ForeColor = Color.Firebrick, Dock = DockStyle.Fill, AutoSize = true };

    internal TextEditorForm(TextLayerResult initial, object workspace, object document, object layer)
    {
        _initial = initial; _workspace = workspace; _document = document; _layer = layer;
        Text = "MCP 텍스트 편집 — " + initial.Name;
        Name = "PaintDotNetMcp.TextEditor";
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(620, 600); MinimumSize = new Size(580, 610);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Controls.Add(layout);
        void Row(int row, string label, Control control, int height)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
            layout.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, 0, row);
            layout.Controls.Add(control, 1, row);
        }
        Row(0, "문구", _text, 160);
        Row(1, "글꼴", _font, 38);
        Row(2, "글자 크기", _size, 38);
        Row(3, "스타일", Flow(_bold, _italic, _smooth), 38);
        Row(4, "위치 (픽셀)", Flow(new Label { Text = "X", AutoSize = true }, _x, new Label { Text = "Y", AutoSize = true }, _y), 38);
        var chooseColor = new Button { Text = "색 고르기…", AutoSize = true };
        chooseColor.Click += (_, _) =>
        {
            using var dialog = new ColorDialog { FullOpen = true, Color = Color.FromArgb((int)_r.Value, (int)_g.Value, (int)_b.Value) };
            if (dialog.ShowDialog(this) == DialogResult.OK) { _r.Value = dialog.Color.R; _g.Value = dialog.Color.G; _b.Value = dialog.Color.B; }
        };
        Row(5, "색상 (RGB)", Flow(chooseColor, _r, _g, _b), 38);
        Row(6, "불투명도", Flow(_a, new Label { Text = "0: 투명 / 255: 불투명", AutoSize = true }), 38);
        Row(7, "그림 변경", _replace, 38);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(_error, 0, 8); layout.SetColumnSpan(_error, 2);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        var apply = new Button { Text = "적용", AutoSize = true, Name = "ApplyText" };
        var cancel = new Button { Text = "취소", AutoSize = true, DialogResult = DialogResult.Cancel };
        layout.Controls.Add(Flow(apply, cancel), 1, 9);
        CancelButton = cancel;
        cancel.Click += (_, _) => Close();
        apply.Click += (_, _) => ApplyChanges();
        foreach (var family in FontFamily.Families) { _font.Items.Add(family.Name); family.Dispose(); }
        var p = initial.Text;
        _text.Text = p.Text; _font.Text = p.FontFamily; _size.Value = (decimal)p.FontSize;
        _x.Value = p.X; _y.Value = p.Y; _r.Value = p.R; _g.Value = p.G; _b.Value = p.B; _a.Value = p.A;
        _bold.Checked = p.Bold; _italic.Checked = p.Italic; _smooth.Checked = p.AntiAlias;
        if (initial.PixelsModified) _error.Text = "텍스트 생성 후 그림이 변경되었습니다. 적용하면 이 레이어 전체를 다시 만듭니다. 위 확인란을 선택해야 적용할 수 있습니다.";
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
                throw new InvalidOperationException("진행 중인 그리기를 마친 뒤 다시 적용하세요.");
            if (string.IsNullOrWhiteSpace(_text.Text) || _text.Text.Length > 4096)
                throw new InvalidOperationException("문구는 공백만 넣을 수 없으며, 최대 4096자까지 입력할 수 있습니다.");
            if (_size.Value <= 0) throw new InvalidOperationException("글자 크기는 0보다 커야 합니다.");
            // Modeless window: fail safely if the user or MCP changed the target while it was open.
            if (!ReferenceEquals(AppServices.DocumentWorkspaceService(), _workspace) ||
                !ReferenceEquals(NativeEditing.Property(_workspace, "Document"), _document) ||
                _initial.LayerIndex >= ((PaintDotNet.Document)_document).Layers.Count ||
                !ReferenceEquals(((PaintDotNet.Document)_document).Layers[_initial.LayerIndex], _layer))
                throw new InvalidOperationException("문서나 대상 레이어가 변경되었습니다. 창을 닫고 편집할 레이어에서 다시 여세요.");
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
