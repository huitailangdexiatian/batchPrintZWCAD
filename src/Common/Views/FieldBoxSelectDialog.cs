using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
#if AUTOCAD
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
#else
using ZwSoft.ZwCAD.EditorInput;
using ZwSoft.ZwCAD.Geometry;
#endif

namespace ZwcadBatchPlot;

/// <summary>编辑图框库记录时传入的已配置字段与纸张。</summary>
public sealed class FieldBoxSelectInitialState
{
    public LocalRectangle TitleRegion { get; set; } = new();
    public LocalRectangle DrawingNumberRegion { get; set; } = new();
    public LocalRectangle DateRegion { get; set; } = new();
    public LocalRectangle RevisionRegion { get; set; } = new();
    public LocalRectangle PhaseRegion { get; set; } = new();
    public LocalRectangle Info1Region { get; set; } = new();
    public LocalRectangle Info2Region { get; set; } = new();
    public string PaperName { get; set; } = "";
    public double PaperWidthMm { get; set; }
    public double PaperHeightMm { get; set; }
}

/// <summary>属性图框录入时展示的块属性预览项：标签、当前值与按关键字命中的字段名。</summary>
public sealed class AttributePreviewItem
{
    public AttributePreviewItem(string tag, string value, string matchedField)
    {
        Tag = tag;
        Value = value;
        MatchedField = matchedField;
    }

    public string Tag { get; }
    public string Value { get; }
    /// <summary>命中的字段显示名（如“图名”）；未命中为空字符串。</summary>
    public string MatchedField { get; }
}

/// <summary>
/// 新增或编辑图框字段框选对话框。
/// 图名/图号为必选，日期/版次/设计阶段/信息1/信息2可选框选。
/// 每个已框选字段在图中以红色临时矩形框 + 对角线 + 字段名文字标识。
/// </summary>
public sealed class FieldBoxSelectDialog : Form
{
    private readonly Editor _editor;
    private readonly Matrix3d _inverseBlockTransform;
    private readonly Matrix3d _blockTransform;
    private readonly TransientFrameMarkers _markers;

    // 存储每个已选字段的世界坐标角点，用于对话框重新显示时刷新全部临时标识。
    private readonly Dictionary<string, (Point3d Corner1, Point3d Corner2)> _fieldCorners = new(StringComparer.Ordinal);

    // 打印范围（外框）的世界坐标角点，随用户重新框选而更新。
    private (Point3d Corner1, Point3d Corner2) _printAreaCorners;

    // 属性图框模式：隐藏字段框选区，改为展示块属性与关键字命中结果。
    private readonly bool _attributeMode;
    private readonly IReadOnlyList<AttributePreviewItem> _attributePreview = Array.Empty<AttributePreviewItem>();

    private readonly Label _printAreaStatus;
    private readonly Label _titleStatus;
    private readonly Label _numberStatus;
    private readonly Label _dateStatus;
    private readonly Label _revisionStatus;
    private readonly Label _phaseStatus;
    private readonly Label _info1Status;
    private readonly Label _info2Status;

    /// <summary>打印范围（块局部坐标），对话框关闭后由调用方读取。</summary>
    public LocalRectangle ReferenceFrame { get; set; }

    public LocalRectangle TitleRegion { get; private set; } = new();
    public LocalRectangle DrawingNumberRegion { get; private set; } = new();
    public LocalRectangle DateRegion { get; private set; } = new();
    public LocalRectangle RevisionRegion { get; private set; } = new();
    public LocalRectangle PhaseRegion { get; private set; } = new();
    public LocalRectangle Info1Region { get; private set; } = new();
    public LocalRectangle Info2Region { get; private set; } = new();

    // 纸张设置与矩形框批打共用同一候选识别策略；下拉项直接对应完整纸张结果，
    // 避免名称、物理尺寸和比例被分别修改后彼此不一致。
    private readonly ComboBox _paperName = new();
    private readonly PaperSizeDetector.DetectionOptions _paperDetectionOptions;
    private IReadOnlyList<PaperDetection> _paperOptions = Array.Empty<PaperDetection>();

    private PaperDetection? SelectedPaper =>
        _paperName.SelectedIndex >= 0 && _paperName.SelectedIndex < _paperOptions.Count
            ? _paperOptions[_paperName.SelectedIndex]
            : null;

    public string PaperName => SelectedPaper?.PaperName ?? "";
    public double PaperWidthMm => SelectedPaper?.PaperWidthMm ?? 0d;
    public double PaperHeightMm => SelectedPaper?.PaperHeightMm ?? 0d;

    public FieldBoxSelectDialog(Editor editor, Matrix3d inverseBlockTransform,
        Matrix3d blockTransform, TransientFrameMarkers markers, LocalRectangle referenceFrame,
        IReadOnlyList<PaperDetection> paperOptions,
        PaperSizeDetector.DetectionOptions paperDetectionOptions,
        FieldBoxSelectInitialState? initialState = null,
        IReadOnlyList<AttributePreviewItem>? attributePreview = null)
    {
        _editor = editor;
        _inverseBlockTransform = inverseBlockTransform;
        _blockTransform = blockTransform;
        _markers = markers;
        _paperDetectionOptions = paperDetectionOptions;
        ReferenceFrame = referenceFrame;
        _attributeMode = attributePreview != null;
        _attributePreview = attributePreview ?? Array.Empty<AttributePreviewItem>();

        // 用 4 个局部角点完整变换后取得世界包盒；不能只变换一对对角点，
        // 否则旋转块在窗口重新显示时会把初始红框替换成错误范围。
        var worldFrame = RectangleGeometry.TransformRectangle(referenceFrame, blockTransform);
        _printAreaCorners = (
            new Point3d(worldFrame.MinX, worldFrame.MinY, 0),
            new Point3d(worldFrame.MaxX, worldFrame.MaxY, 0));

        Text = _attributeMode ? "设置属性图框纸张" : "设置图框字段与纸张";
        UiLayout.ConfigureForm(this, 460, 436, 430, 410);
        // 打印范围、纸张各一行，纵向多两行。
        ClientSize = new Size(UiLayout.Scale(460), UiLayout.Scale(414));
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;
        ShowInTaskbar = false;

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        if (_attributeMode)
        {
            // 属性图框模式：Row 0 打印范围、Row 1 纸张、Row 2 属性预览、Row 3 提示、Row 4 按钮。
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(44)));
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));
        }
        else
        {
            // Row 0: 打印范围（可重新框选），Row 1-7: 字段（图名/图号必选，其余可选），Row 8: 纸张
            for (var i = 0; i < 9; i++)
            {
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
            }
            // Row 9: 提示
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            // Row 10: 按钮
            table.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(36)));
        }

        // 打印范围：显示当前尺寸，提供"框选"按钮供用户修正自动识别的外框。
        table.Controls.Add(MakeLabel("打印范围"), 0, 0);
        _printAreaStatus = MakeStatusLabel();
        UpdatePrintAreaStatus();
        table.Controls.Add(MakePrintAreaRow(_printAreaStatus, SelectPrintArea), 1, 0);

        // 只读状态标签统一初始化（属性模式下不加入布局，仅供字段方法安全引用）。
        _titleStatus = MakeStatusLabel();
        _numberStatus = MakeStatusLabel();
        _dateStatus = MakeStatusLabel();
        _revisionStatus = MakeStatusLabel();
        _phaseStatus = MakeStatusLabel();
        _info1Status = MakeStatusLabel();
        _info2Status = MakeStatusLabel();

        int paperRow;
        if (_attributeMode)
        {
            // 属性图框模式：不框选字段区域，改为展示块属性与关键字命中结果。
            paperRow = 1;

            var previewGrid = BuildAttributePreviewGrid();
            table.SetColumnSpan(previewGrid, 2);
            table.Controls.Add(previewGrid, 0, 2);
        }
        else
        {
            // 图名/图号为必选字段，保存前必须完成框选。
            table.Controls.Add(MakeLabel("图名 *"), 0, 1);
            table.Controls.Add(MakeFieldRow(_titleStatus, SelectTitle, ClearTitle), 1, 1);

            table.Controls.Add(MakeLabel("图号 *"), 0, 2);
            table.Controls.Add(MakeFieldRow(_numberStatus, SelectNumber, ClearNumber), 1, 2);

            // 日期
            table.Controls.Add(MakeLabel("日期"), 0, 3);
            table.Controls.Add(MakeFieldRow(_dateStatus, SelectDate, ClearDate), 1, 3);

            // 版次
            table.Controls.Add(MakeLabel("版次"), 0, 4);
            table.Controls.Add(MakeFieldRow(_revisionStatus, SelectRevision, ClearRevision), 1, 4);

            // 设计阶段
            table.Controls.Add(MakeLabel("设计阶段"), 0, 5);
            table.Controls.Add(MakeFieldRow(_phaseStatus, SelectPhase, ClearPhase), 1, 5);

            // 信息1/信息2为用户自定义可选字段，可用于后续文件名命名。
            table.Controls.Add(MakeLabel("信息1"), 0, 6);
            table.Controls.Add(MakeFieldRow(_info1Status, SelectInfo1, ClearInfo1), 1, 6);

            table.Controls.Add(MakeLabel("信息2"), 0, 7);
            table.Controls.Add(MakeFieldRow(_info2Status, SelectInfo2, ClearInfo2), 1, 7);
            paperRow = 8;
        }

        if (initialState != null && !_attributeMode)
        {
            ApplyInitialState(initialState);
        }

        // 纸张：默认按打印范围自动识别，重新框选打印范围时同步刷新，也可手动修改。
        table.Controls.Add(MakeLabel("纸张"), 0, paperRow);
        table.Controls.Add(MakePaperRow(), 1, paperRow);
        ApplyPaperOptions(
            paperOptions,
            initialState?.PaperName,
            initialState?.PaperWidthMm ?? 0d,
            initialState?.PaperHeightMm ?? 0d);

        // 提示
        var hintText = _attributeMode
            ? "属性图框：图名、图号等字段值将在扫描时按设置的关键字从块属性自动提取（此处列出属性标签与当前值，绿色为已命中字段）。关键字可在\"批量打印设置 → 属性图框设置\"中修改。"
            : "图名、图号为必选。点击\"框选\"在 CAD 中框选对应区域，已选区域以红色临时框标识；纸张按打印范围自动识别，可手动修改。";
        var hint = new Label
        {
            Text = hintText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Font = new Font(Font.FontFamily, Math.Max(Font.Size - 1, 7))
        };
        table.SetColumnSpan(hint, 2);
        table.Controls.Add(hint, 0, _attributeMode ? 3 : 9);

        // 按钮
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, UiLayout.Scale(4), 0, 0)
        };
        var ok = UiLayout.CreateButton("确定", 76);
        var skip = UiLayout.CreateButton("跳过", 76);
        var cancel = UiLayout.CreateButton("取消", 76);
        ok.Click += (_, _) =>
        {
            if (!ValidateRequiredFields())
            {
                return;
            }

            DialogResult = DialogResult.OK;
            Close();
        };
        if (_attributeMode)
        {
            // 属性图框无字段框选，无需"跳过"清理可选区域。
            skip.Visible = false;
        }
        else
        {
            skip.Click += (_, _) =>
            {
                // 跳过仅留空可选字段，图名/图号仍为必选。
                if (!ValidateRequiredFields())
                {
                    return;
                }

                ClearOptionalField("日期", r => DateRegion = r, _dateStatus);
                ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus);
                ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus);
                ClearOptionalField("信息1", r => Info1Region = r, _info1Status);
                ClearOptionalField("信息2", r => Info2Region = r, _info2Status);
                DialogResult = DialogResult.OK;
                Close();
            };
        }

        cancel.DialogResult = DialogResult.Cancel;
        AcceptButton = ok;
        CancelButton = cancel;
        buttons.Controls.Add(ok);
        buttons.Controls.Add(skip);
        buttons.Controls.Add(cancel);
        table.SetColumnSpan(buttons, 2);
        table.Controls.Add(buttons, 0, _attributeMode ? 4 : 10);

        Controls.Add(table);
    }

    private void ApplyInitialState(FieldBoxSelectInitialState state)
    {
        TitleRegion = state.TitleRegion;
        DrawingNumberRegion = state.DrawingNumberRegion;
        DateRegion = state.DateRegion;
        RevisionRegion = state.RevisionRegion;
        PhaseRegion = state.PhaseRegion;
        Info1Region = state.Info1Region;
        Info2Region = state.Info2Region;

        AddInitialField("图名", TitleRegion, _titleStatus);
        AddInitialField("图号", DrawingNumberRegion, _numberStatus);
        AddInitialField("日期", DateRegion, _dateStatus);
        AddInitialField("版次", RevisionRegion, _revisionStatus);
        AddInitialField("设计阶段", PhaseRegion, _phaseStatus);
        AddInitialField("信息1", Info1Region, _info1Status);
        AddInitialField("信息2", Info2Region, _info2Status);
    }

    private void AddInitialField(string fieldName, LocalRectangle region, Label statusLabel)
    {
        UpdateStatus(statusLabel, region);
        if (!region.HasArea())
        {
            return;
        }

        // 库内字段已转换为当前块的局部坐标；变回 WCS 后建立首次显示的红色临时框。
        var worldRegion = RectangleGeometry.TransformRectangle(region, _blockTransform);
        _fieldCorners[fieldName] = (
            new Point3d(worldRegion.MinX, worldRegion.MinY, 0),
            new Point3d(worldRegion.MaxX, worldRegion.MaxY, 0));
    }

    /// <summary>
    /// 对话框每次重新显示时（例如从 CAD 框选取点后返回），刷新所有已选字段的临时红色标识。
    /// 确保用户始终能看到自己已选择了哪些区域。
    /// </summary>
    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible)
        {
            RefreshAllMarkers();
        }
    }

    /// <summary>
    /// CAD 模态窗口建立自己的消息循环后再强制重绘一次，确保窗口首次出现时红色打印范围已经可见。
    /// OnVisibleChanged 继续负责从 CAD 框选返回后的刷新，两者职责不同。
    /// </summary>
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshAllMarkers();
        _markers.RefreshDisplay(regenerate: true);
    }

    /// <summary>
    /// 根据已存储的世界坐标角点，重新绘制所有已选字段及打印范围的临时红色标识。
    /// </summary>
    private void RefreshAllMarkers()
    {
        // 外框标识：不显示文字标签，避免外框过大时文字遮挡图面。
        _markers.SetBox("外框", _printAreaCorners.Corner1, _printAreaCorners.Corner2, null);
        foreach (var kv in _fieldCorners)
        {
            _markers.SetBox(kv.Key, kv.Value.Corner1, kv.Value.Corner2, kv.Key);
        }
    }

    /// <summary>
    /// 重新框选打印范围。用户可在 CAD 中手动框选以修正自动识别的外包框。
    /// </summary>
    private void SelectPrintArea()
    {
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var first = _editor.GetPoint(new PromptPointOptions("\n框选图框打印外边界第一个角点: "));
            if (first.Status != PromptStatus.OK)
            {
                return;
            }

            var cornerOptions = new PromptCornerOptions("\n框选图框打印外边界对角点: ", first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return;
            }

            // 更新世界坐标角点（用于外框标记显示）
            _printAreaCorners = (first.Value, second.Value);

            // 转换为块局部坐标，更新 ReferenceFrame
            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            ReferenceFrame = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);

            // 立即绘制新的外框临时标识
            _markers.SetBox("外框", first.Value, second.Value, null);
            UpdatePrintAreaStatus();

            // 打印范围变化后重新识别纸张，与原独立纸张界面使用最终外框检测的行为一致。
            // 识别不到标准纸张时要求用户输入绘图比例（对话框内嵌套弹窗，owner 传 this 保证置顶）。
            var detectedWidth = Math.Abs(second.Value.X - first.Value.X);
            var detectedHeight = Math.Abs(second.Value.Y - first.Value.Y);
            ApplyPaperOptions(ArbitraryPaperPicker.DetectCandidatesOrPrompt(
                detectedWidth,
                detectedHeight,
                _paperDetectionOptions,
                this));
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    /// <summary>
    /// 更新打印范围状态标签，显示当前外框的宽×高（世界坐标单位）。
    /// </summary>
    private void UpdatePrintAreaStatus()
    {
        var w = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var h = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        _printAreaStatus.Text = $"{(int)w} × {(int)h}（点击\"框选\"可修改）";
        _printAreaStatus.ForeColor = Color.Green;
    }

    private bool ValidateRequiredFields()
    {
        if (!_attributeMode
            && (!TitleRegion.HasArea() || !DrawingNumberRegion.HasArea()))
        {
            MessageBox.Show(this,
                "图名和图号为必选项，请先点击对应\"框选\"按钮完成框选。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        if (string.IsNullOrWhiteSpace(PaperName))
        {
            MessageBox.Show(this,
                "纸张名称不能为空。",
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        return true;
    }

    /// <summary>
    /// 属性图框模式的属性预览表格：列出块全部属性标签与当前值，并标注按关键字命中的字段。
    /// </summary>
    private DataGridView BuildAttributePreviewGrid()
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            ReadOnly = true,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, UiLayout.Scale(4), 0, UiLayout.Scale(4))
        };
        grid.Columns.Add("tag", "属性标签");
        grid.Columns.Add("value", "当前值");
        grid.Columns.Add("field", "命中字段");
        grid.Columns["tag"]!.FillWeight = 35;
        grid.Columns["value"]!.FillWeight = 45;
        grid.Columns["field"]!.FillWeight = 20;

        foreach (var item in _attributePreview)
        {
            var rowIndex = grid.Rows.Add(item.Tag, item.Value, item.MatchedField);
            if (item.MatchedField.Length > 0)
            {
                grid.Rows[rowIndex].Cells["field"].Style.ForeColor = Color.Green;
                grid.Rows[rowIndex].Cells["field"].Style.Font =
                    new Font(grid.Font, FontStyle.Bold);
            }
        }

        return grid;
    }

    private void SelectTitle() { if (TryBoxSelect("图名", out var r)) { TitleRegion = r; UpdateStatus(_titleStatus, TitleRegion); } }
    private void ClearTitle() { TitleRegion = new LocalRectangle(); _fieldCorners.Remove("图名"); _markers.Remove("图名"); UpdateStatus(_titleStatus, TitleRegion); }

    private void SelectNumber() { if (TryBoxSelect("图号", out var r)) { DrawingNumberRegion = r; UpdateStatus(_numberStatus, DrawingNumberRegion); } }
    private void ClearNumber() { DrawingNumberRegion = new LocalRectangle(); _fieldCorners.Remove("图号"); _markers.Remove("图号"); UpdateStatus(_numberStatus, DrawingNumberRegion); }

    private void SelectDate() { if (TryBoxSelect("日期", out var r)) { DateRegion = r; UpdateStatus(_dateStatus, DateRegion); } }
    private void ClearDate() { ClearOptionalField("日期", r => DateRegion = r, _dateStatus); }

    private void SelectRevision() { if (TryBoxSelect("版次", out var r)) { RevisionRegion = r; UpdateStatus(_revisionStatus, RevisionRegion); } }
    private void ClearRevision() { ClearOptionalField("版次", r => RevisionRegion = r, _revisionStatus); }

    private void SelectPhase() { if (TryBoxSelect("设计阶段", out var r)) { PhaseRegion = r; UpdateStatus(_phaseStatus, PhaseRegion); } }
    private void ClearPhase() { ClearOptionalField("设计阶段", r => PhaseRegion = r, _phaseStatus); }

    private void SelectInfo1() { if (TryBoxSelect("信息1", out var r)) { Info1Region = r; UpdateStatus(_info1Status, Info1Region); } }
    private void ClearInfo1() { ClearOptionalField("信息1", r => Info1Region = r, _info1Status); }

    private void SelectInfo2() { if (TryBoxSelect("信息2", out var r)) { Info2Region = r; UpdateStatus(_info2Status, Info2Region); } }
    private void ClearInfo2() { ClearOptionalField("信息2", r => Info2Region = r, _info2Status); }

    private void ClearOptionalField(string fieldName, Action<LocalRectangle> setRegion, Label statusLabel)
    {
        setRegion(new LocalRectangle());
        _fieldCorners.Remove(fieldName);
        _markers.Remove(fieldName);
        UpdateStatus(statusLabel, new LocalRectangle());
    }

    private bool TryBoxSelect(string fieldName, out LocalRectangle region)
    {
        region = new LocalRectangle();
        CadWindowFocus.HideForCadInput(this);
        try
        {
            var firstPrompt = $"框选{fieldName}区域第一个角点，或右键跳过: ";
            var secondPrompt = $"框选{fieldName}区域对角点: ";

            var first = _editor.GetPoint(new PromptPointOptions(firstPrompt));
            if (first.Status != PromptStatus.OK)
            {
                return false;
            }

            var cornerOptions = new PromptCornerOptions(secondPrompt, first.Value);
            var second = _editor.GetCorner(cornerOptions);
            if (second.Status != PromptStatus.OK)
            {
                return false;
            }

            var p1 = first.Value.TransformBy(_inverseBlockTransform);
            var p2 = second.Value.TransformBy(_inverseBlockTransform);
            region = LocalRectangle.FromPoints(p1.X, p1.Y, p2.X, p2.Y);

            // 存储世界坐标角点，用于对话框重新显示时刷新临时标识。
            _fieldCorners[fieldName] = (first.Value, second.Value);

            // 世界坐标绘制红色临时标识（红色矩形框 + 对角线 + 居中字段名文字），
            // 重新框选时自动替换旧标识。
            _markers.SetBox(fieldName, first.Value, second.Value, fieldName);
            return true;
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private static void UpdateStatus(Label label, LocalRectangle region)
    {
        if (region.HasArea())
        {
            label.Text = $"已选择 ({region.MinX:0.###},{region.MinY:0.###})-({region.MaxX:0.###},{region.MaxY:0.###})";
            label.ForeColor = Color.Green;
        }
        else
        {
            label.Text = "(未选择)";
            label.ForeColor = Color.DimGray;
        }
    }

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = true
    };

    private static Label MakeStatusLabel() => new()
    {
        Text = "(未选择)",
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = Color.DimGray,
        AutoEllipsis = true
    };

    private static Control MakeFieldRow(Label statusLabel, Action select, Action clear)
    {
        // 高 DPI 下状态文字和按钮都会放大，用表格布局让状态列自动收缩，避免右侧“清除”按钮被裁切。
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("框选", 60) + UiLayout.Scale(8)));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("清除", 60) + UiLayout.Scale(2)));

        var selectBtn = UiLayout.CreateButton("框选", 60);
        selectBtn.Dock = DockStyle.Fill;
        selectBtn.Click += (_, _) => select();

        var clearBtn = UiLayout.CreateButton("清除", 60);
        clearBtn.Dock = DockStyle.Fill;
        clearBtn.Click += (_, _) => clear();

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(selectBtn, 1, 0);
        panel.Controls.Add(clearBtn, 2, 0);

        return panel;
    }

    /// <summary>
    /// 打印范围行布局：只有"框选"按钮，无"清除"按钮（打印范围不可为空）。
    /// </summary>
    private static Control MakePrintAreaRow(Label statusLabel, Action select)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.ButtonWidth("框选", 60) + UiLayout.Scale(8)));

        var selectBtn = UiLayout.CreateButton("框选", 60);
        selectBtn.Dock = DockStyle.Fill;
        selectBtn.Click += (_, _) => select();

        panel.Controls.Add(statusLabel, 0, 0);
        panel.Controls.Add(selectBtn, 1, 0);

        return panel;
    }

    /// <summary>纸张行：候选下拉框 + "任意纸张…"按钮，允许用户主动指定任意纸张与比例。</summary>
    private Control MakePaperRow()
    {
        _paperName.Dock = DockStyle.Fill;
        _paperName.DropDownStyle = ComboBoxStyle.DropDownList;
        _paperName.DropDownWidth = UiLayout.Scale(330);

        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(110)));

        panel.Controls.Add(_paperName, 0, 0);
        var arbitraryButton = UiLayout.CreateButton("任意纸张…", 100);
        arbitraryButton.Dock = DockStyle.Right;
        arbitraryButton.Click += (_, _) => PickArbitraryPaper();
        panel.Controls.Add(arbitraryButton, 1, 0);

        return panel;
    }

    /// <summary>
    /// 主动指定任意纸张：按当前打印范围宽高弹出比例对话框。
    /// 长宽比接近标准图幅或 1/8 模数加长图时可选择目标图幅（任意比例）；否则按输入比例生成自定义纸张。
    /// 生成的候选加入下拉并选中（同名同尺寸项去重替换）。
    /// </summary>
    private void PickArbitraryPaper()
    {
        var width = Math.Abs(_printAreaCorners.Corner2.X - _printAreaCorners.Corner1.X);
        var height = Math.Abs(_printAreaCorners.Corner2.Y - _printAreaCorners.Corner1.Y);
        if (width <= 1e-6 || height <= 1e-6)
        {
            return;
        }

        var guessedScale = PaperSizeDetector.GuessScale(width, height);
        using var scaleForm = new CustomScaleForm(
            width,
            height,
            guessedScale,
            ArbitraryPaperPicker.HintText,
            allowAspectRatioPapers: !_paperDetectionOptions.IncludeGenericDynamicTitleBlockPaper);
        if (scaleForm.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var arbitrary = ArbitraryPaperPicker.CreatePaperFromScaleForm(scaleForm, width, height);

        // 同名同尺寸项去重替换，避免重复添加。
        var options = _paperOptions.ToList();
        var existingIndex = options.FindIndex(x =>
            string.Equals(x.PaperName, arbitrary.PaperName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(x.PaperWidthMm - arbitrary.PaperWidthMm) <= 0.01d
            && Math.Abs(x.PaperHeightMm - arbitrary.PaperHeightMm) <= 0.01d);
        if (existingIndex >= 0)
        {
            options[existingIndex] = arbitrary;
        }
        else
        {
            options.Add(arbitrary);
        }

        ApplyPaperOptions(options, arbitrary.PaperName, arbitrary.PaperWidthMm, arbitrary.PaperHeightMm);
    }

    private void ApplyPaperOptions(
        IReadOnlyList<PaperDetection> paperOptions,
        string? preferredPaperName = null,
        double preferredPaperWidthMm = 0d,
        double preferredPaperHeightMm = 0d)
    {
        if (paperOptions.Count == 0)
        {
            throw new ArgumentException("至少需要一个纸张候选项。", nameof(paperOptions));
        }

        _paperOptions = paperOptions;
        _paperName.BeginUpdate();
        try
        {
            _paperName.Items.Clear();
            foreach (var paper in _paperOptions)
            {
                _paperName.Items.Add(PaperSizeDetector.FormatOption(paper));
            }

            var preferredIndex = -1;
            if (!string.IsNullOrWhiteSpace(preferredPaperName))
            {
                for (var i = 0; i < _paperOptions.Count; i++)
                {
                    var paper = _paperOptions[i];
                    if (string.Equals(paper.PaperName, preferredPaperName, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(paper.PaperWidthMm - preferredPaperWidthMm) <= 0.01d
                        && Math.Abs(paper.PaperHeightMm - preferredPaperHeightMm) <= 0.01d)
                    {
                        preferredIndex = i;
                        break;
                    }
                }
            }

            _paperName.SelectedIndex = preferredIndex >= 0 ? preferredIndex : 0;
        }
        finally
        {
            _paperName.EndUpdate();
        }
    }
}
