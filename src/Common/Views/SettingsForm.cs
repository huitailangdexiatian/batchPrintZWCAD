using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
#if AUTOCAD
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
#if ACAD_CORE
using CadApp = Autodesk.AutoCAD.ApplicationServices.Core.Application;
#else
using CadApp = Autodesk.AutoCAD.ApplicationServices.Application;
#endif
#else
using ZwSoft.ZwCAD.ApplicationServices;
using ZwSoft.ZwCAD.DatabaseServices;
using CadApp = ZwSoft.ZwCAD.ApplicationServices.Application;
#endif

namespace ZwcadBatchPlot;

public sealed class SettingsForm : Form
{
    private const string DefaultTextStyleDisplay = "(默认)";

    private readonly NumericUpDown _paperTolerance = new();
    private readonly ComboBox _rasterDpi = new();
    private readonly CheckBox _recognizeFourLineRectangleFrames = new();
    private readonly CheckBox _hideFrameBoundaryWhenPlotting = new();
    private readonly ComboBox _longPaperNameFormat = new();
    private readonly NumericUpDown _longPaperSnapTolerance = new();
    private readonly CheckBox _addSequenceWhenPdfExists = new();
    private readonly CheckBox _openExternalDwgForPlot = new();
    private readonly CheckBox _useFileNameAsPdfBookmark = new();
    private readonly CheckBox _mergePdfByPaperSize = new();
    private readonly CheckBox _openOutputDirectoryAfterBatchPrint = new();
    private readonly CheckBox _openMergedPdfAfterMerge = new();
    private readonly CheckBox _generatePrintLog = new();
    private readonly CheckBox _plotTransparency = new();
    private readonly CheckBox _convertTextToGeometryWhenPlotting = new();
    // 目录设置页：WinForms 壳内嵌 WPF 控件（ElementHost），行数据与编辑状态彻底解耦。
    private DirectorySettingsControl _directoryControl = null!;

    // 文件名设置
    private readonly TextBox _fileNamePattern = new();
    private readonly Label _fileNamePreview = new();
    private readonly NumericUpDown _fileNameSequenceStart = new();
    private readonly NumericUpDown _fileNameSequenceDigits = new();
    private readonly CheckBox _autoFileNameSequenceDigits = new();

    // 比例设置
    private readonly ListBox _scaleList = new();
    // 属性图框设置：字段 → 属性关键字列表（多个关键字用逗号分隔）。
    private readonly DataGridView _attributeKeywordsGrid = new();
    private readonly TextBox _scaleInput = new();
    private readonly Label _scaleListSummary = new();
    private readonly Label _scaleSelectionHint = new();
    private Button _removeScaleButton = null!;

    public string? RequestedDirectoryColumnKey { get; private set; }
    public bool RequestPickDirectoryRowHeight { get; private set; }
    public bool RequestPickDirectoryTextAppearance { get; private set; }
    public bool RequestPickScaleFromCad { get; private set; }

    /// <summary>
    /// 设置/获取下次打开设置窗口时默认显示的标签页索引（0=常规, 1=文件名, 2=图纸目录, 3=比例设置）。
    /// 调用方在窗体关闭后读取 <see cref="SelectedTabIndex"/> 并传入下一次构造，实现图中交互后回到原标签页。
    /// </summary>
    public static int InitialTabIndex { get; set; }

    /// <summary>
    /// 窗体关闭前记录当前标签页索引，供调用方传给 <see cref="InitialTabIndex"/>。
    /// </summary>
    public int SelectedTabIndex { get; private set; }

    private TabControl _tabs = null!;

    public SettingsForm()
    {
        InitializeComponents();
        LoadSettings();
    }

    private void InitializeComponents()
    {
        Text = "批量打印设置";
        UiLayout.ConfigureForm(this, 760, 600, 680, 540);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog;

        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(UiLayout.Scale(10))
        };
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(BuildGeneralTab());
        _tabs.TabPages.Add(BuildFileNameTab());
        _tabs.TabPages.Add(BuildDirectoryTab());
        _tabs.TabPages.Add(BuildScaleTab());
        _tabs.TabPages.Add(BuildAttributeTitleBlockTab());

        var hint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            AutoEllipsis = true
        };
        void UpdateFooterHint()
        {
            hint.Text = _tabs.SelectedIndex switch
            {
                0 => "常规设置会同时影响图框块、矩形框和单张打印中的对应功能。",
                1 => "文件名预览会随规则即时更新；保存后应用于后续打印和拆图任务。",
                2 => "图纸目录会写入当前 CAD 当前空间；目录列与批量打印实际识别出的图框字段保持一致。",
                3 => "图框块录入后自动支持任意比例；比例列表只控制矩形框批量打印的识别范围。",
                4 => "属性图框按关键字从块属性提取字段值；扫描“新增属性图框”录入的图框时生效。",
                _ => ""
            };
        }
        _tabs.SelectedIndexChanged += (_, _) => UpdateFooterHint();
        UpdateFooterHint();

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft,
            WrapContents = false
        };
        var save = UiLayout.CreateButton("保存", 82);
        save.Click += (_, _) => SaveSettings();
        var reset = UiLayout.CreateButton("恢复默认", 96);
        reset.Click += (_, _) => ResetDefaults();
        var cancel = UiLayout.CreateButton("取消", 82);
        cancel.Click += (_, _) => Close();
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(reset);

        root.Controls.Add(_tabs, 0, 0);
        root.Controls.Add(hint, 0, 1);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);

        // 恢复上次关闭时的标签页（如从 CAD 交互返回后回到"图纸目录"而非"常规"）
        if (InitialTabIndex >= 0 && InitialTabIndex < _tabs.TabCount)
        {
            _tabs.SelectedIndex = InitialTabIndex;
        }
    }

    private TabPage BuildGeneralTab()
    {
        var page = new TabPage("常规")
        {
            Padding = new Padding(UiLayout.Scale(10)),
            AutoScroll = true
        };
        var categories = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 7,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        categories.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        ConfigureNumber(_paperTolerance, 0.5M, 20M, 0.5M, 1);
        ConfigureNumber(_longPaperSnapTolerance, 0.5M, 20M, 0.5M, 1);
        _longPaperSnapTolerance.ValueChanged += (_, _) => UpdateFileNamePreview();
        var longPaperSnapTip = new ToolTip();
        longPaperSnapTip.SetToolTip(
            _longPaperSnapTolerance,
            "加长图长边吸附到最近 1/8 模数标准加长图的容差；同时影响实际打印纸张和输出名称。");

        _addSequenceWhenPdfExists.Text = "PDF 已存在时自动加序号";
        _addSequenceWhenPdfExists.AutoSize = true;
        _addSequenceWhenPdfExists.Dock = DockStyle.Fill;

        _openExternalDwgForPlot.Text = "跨文件打印时临时打开 DWG";
        _openExternalDwgForPlot.AutoSize = true;
        _openExternalDwgForPlot.Dock = DockStyle.Fill;

        var paperTable = CreateSettingsTable(2);
        UiLayout.AddRow(paperTable, 0, "纸张匹配容差(mm)", _paperTolerance);
        UiLayout.AddRow(paperTable, 1, "加长图长边吸附容差(mm)", _longPaperSnapTolerance);

        _recognizeFourLineRectangleFrames.Text = "识别四条直线或直线型 PL 首尾相连组成的矩形框";
        _recognizeFourLineRectangleFrames.AutoSize = true;
        _recognizeFourLineRectangleFrames.Dock = DockStyle.Fill;
        var frameRecognitionTip = new ToolTip();
        frameRecognitionTip.SetToolTip(
            _recognizeFourLineRectangleFrames,
            "仅在四个独立实体的端点严格首尾相连并通过矩形几何校验时识别；后续沿用原有 PL 矩形框打印流程。");
        var frameRecognitionTable = CreateSettingsTable(1);
        UiLayout.AddRow(frameRecognitionTable, 0, "", _recognizeFourLineRectangleFrames);

        _hideFrameBoundaryWhenPlotting.Text = "不打印图框的外边框";
        _hideFrameBoundaryWhenPlotting.AutoSize = true;
        _hideFrameBoundaryWhenPlotting.Dock = DockStyle.Fill;
        var hideFrameTip = new ToolTip();
        hideFrameTip.SetToolTip(
            _hideFrameBoundaryWhenPlotting,
            "勾选后，正式打印把内容四边各裁 1mm，外框线不再输出；纸张、比例和留白不变。");

        _plotTransparency.Text = "打印透明度";
        _plotTransparency.AutoSize = true;
        _plotTransparency.Dock = DockStyle.Fill;
        var plotTransparencyTip = new ToolTip();
        plotTransparencyTip.SetToolTip(
            _plotTransparency,
            "勾选后按对象透明度输出，对应 CAD 打印对话框中的“打印透明度”；默认开启。");

        _generatePrintLog.Text = "生成打印日志";
        _generatePrintLog.AutoSize = true;
        _generatePrintLog.Dock = DockStyle.Fill;
        var printLogTip = new ToolTip();
        printLogTip.SetToolTip(
            _generatePrintLog,
            "插件日志总开关。勾选后允许生成打印、拆图、扫描警告和图框录入诊断日志；默认关闭。日志目录：" + BatchPlotLogger.LogDirectory);

        _convertTextToGeometryWhenPlotting.Text = "文字自动转图形打印";
        _convertTextToGeometryWhenPlotting.AutoSize = true;
        _convertTextToGeometryWhenPlotting.Dock = DockStyle.Fill;
        var textGeometryTip = new ToolTip();
        textGeometryTip.SetToolTip(
            _convertTextToGeometryWhenPlotting,
            "勾选后，PDF/DWF 输出会把 TrueType 文字转换为图形轮廓，避免接收方缺少字体；不会炸开或修改原 DWG 文字。PNG/JPG 本身已是图像输出。默认关闭。");

        var plotTable = CreateSettingsTable(5);
        UiLayout.AddRow(plotTable, 0, "", _openExternalDwgForPlot);
        UiLayout.AddRow(plotTable, 1, "", _hideFrameBoundaryWhenPlotting);
        UiLayout.AddRow(plotTable, 2, "", _plotTransparency);
        UiLayout.AddRow(plotTable, 3, "", _generatePrintLog);
        UiLayout.AddRow(plotTable, 4, "", _convertTextToGeometryWhenPlotting);

        var outputTable = CreateSettingsTable(1);
        UiLayout.AddRow(outputTable, 0, "", _addSequenceWhenPdfExists);

        _useFileNameAsPdfBookmark.Text = "文件名作为书签";
        _useFileNameAsPdfBookmark.AutoSize = true;
        _useFileNameAsPdfBookmark.Dock = DockStyle.Fill;

        _mergePdfByPaperSize.Text = "按纸张大小合并";
        _mergePdfByPaperSize.AutoSize = true;
        _mergePdfByPaperSize.Dock = DockStyle.Fill;

        var mergeExplanation = new Label
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            ForeColor = Color.DimGray,
            Text = "同一纸张尺寸合并到一个 PDF；一批图纸包含多种尺寸时，按尺寸分别生成多个 PDF。"
        };
        var mergeTable = CreateSettingsTable(3);
        UiLayout.AddRow(mergeTable, 0, "", _useFileNameAsPdfBookmark);
        UiLayout.AddRow(mergeTable, 1, "", _mergePdfByPaperSize);
        UiLayout.AddRow(mergeTable, 2, "", mergeExplanation);

        _openOutputDirectoryAfterBatchPrint.Text = "批量打印单张后，打开所在文件夹";
        _openOutputDirectoryAfterBatchPrint.AutoSize = true;
        _openOutputDirectoryAfterBatchPrint.Dock = DockStyle.Fill;

        _openMergedPdfAfterMerge.Text = "PDF 合并完成后，打开该文件";
        _openMergedPdfAfterMerge.AutoSize = true;
        _openMergedPdfAfterMerge.Dock = DockStyle.Fill;

        var completedActionTable = CreateSettingsTable(2);
        UiLayout.AddRow(completedActionTable, 0, "", _openOutputDirectoryAfterBatchPrint);
        UiLayout.AddRow(completedActionTable, 1, "", _openMergedPdfAfterMerge);

        _rasterDpi.DropDownStyle = ComboBoxStyle.DropDownList;
        _rasterDpi.Width = UiLayout.Scale(140);
        _rasterDpi.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        _rasterDpi.Items.AddRange(new object[] { 150, 300, 600 });
        var rasterDpiTip = new ToolTip();
        rasterDpiTip.SetToolTip(
            _rasterDpi,
            "PNG/JPG 输出的统一分辨率（DPI），默认 300。输出像素 = 图框毫米 × DPI ÷ 25.4；分辨率越高 PNG/JPG 越清晰，输出文件越大。");
        var rasterTable = CreateSettingsTable(1);
        UiLayout.AddRow(rasterTable, 0, "PNG/JPG 分辨率(DPI)", _rasterDpi);

        categories.Controls.Add(CreateSettingsGroup("纸张匹配", paperTable), 0, 0);
        categories.Controls.Add(CreateSettingsGroup("矩形框识别", frameRecognitionTable), 0, 1);
        categories.Controls.Add(CreateSettingsGroup("打印行为", plotTable), 0, 2);
        categories.Controls.Add(CreateSettingsGroup("栅格输出(PNG/JPG)", rasterTable), 0, 3);
        categories.Controls.Add(CreateSettingsGroup("输出文件", outputTable), 0, 4);
        categories.Controls.Add(CreateSettingsGroup("PDF 合并", mergeTable), 0, 5);
        categories.Controls.Add(CreateSettingsGroup("完成后操作", completedActionTable), 0, 6);
        page.Controls.Add(categories);
        return page;
    }

    private static GroupBox CreateSettingsGroup(string title, Control content)
    {
        var group = new GroupBox
        {
            Text = title,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(UiLayout.Scale(4)),
            Margin = new Padding(0, 0, 0, UiLayout.Scale(4))
        };
        content.Dock = DockStyle.Top;
        content.AutoSize = true;
        group.Controls.Add(content);
        return group;
    }

    private TabPage BuildScaleTab()
    {
        var page = new TabPage("比例设置")
        {
            Padding = new Padding(UiLayout.Scale(10)),
            BackColor = SystemColors.Control
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(66)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // 页面顶部直接说明两种批打模式的边界，避免用户为了图框块任意比例反复维护列表。
        var scopePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(239, 247, 255),
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), UiLayout.Scale(7)),
            Margin = Padding.Empty
        };
        var scopeText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        scopeText.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        scopeText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scopeText.Controls.Add(new Label
        {
            Text = "比例识别作用范围",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new System.Drawing.Font(UiLayout.DefaultFont, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 78, 121),
            Margin = Padding.Empty
        }, 0, 0);
        scopeText.Controls.Add(new Label
        {
            Text = "图框块：按录入纸张短边自动识别任意比例    ·    矩形框：仅识别下方内置及自定义比例",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 76, 94),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 1);
        scopePanel.Controls.Add(scopeText);
        root.Controls.Add(scopePanel, 0, 0);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(8), 0, 0)
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56));
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        _scaleList.Dock = DockStyle.Fill;
        _scaleList.SelectionMode = SelectionMode.MultiExtended;
        _scaleList.IntegralHeight = false;
        _scaleList.HorizontalScrollbar = true;
        _scaleList.Margin = Padding.Empty;
        _scaleList.SelectedIndexChanged += (_, _) => UpdateScaleListState();
        var listGroup = new GroupBox
        {
            Text = "矩形框可识别比例",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(8)),
            Margin = new Padding(0, 0, UiLayout.Scale(5), 0)
        };
        var listLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        listLayout.Controls.Add(_scaleList, 0, 0);
        _scaleListSummary.Dock = DockStyle.Fill;
        _scaleListSummary.TextAlign = ContentAlignment.MiddleLeft;
        _scaleListSummary.ForeColor = Color.DimGray;
        _scaleListSummary.Margin = Padding.Empty;
        listLayout.Controls.Add(_scaleListSummary, 0, 1);
        listGroup.Controls.Add(listLayout);
        content.Controls.Add(listGroup, 0, 0);

        var actionsGroup = new GroupBox
        {
            Dock = DockStyle.Fill,
            Text = "自定义比例",
            Padding = new Padding(UiLayout.Scale(10), UiLayout.Scale(8), UiLayout.Scale(10), UiLayout.Scale(8)),
            Margin = new Padding(UiLayout.Scale(5), 0, 0, 0)
        };
        var actions = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 8,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(54)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(34)));
        actions.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(44)));
        actions.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        actions.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "手动输入",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new System.Drawing.Font(UiLayout.DefaultFont, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 0);

        var inputRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        inputRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(82)));
        _scaleInput.Dock = DockStyle.Fill;
        _scaleInput.Margin = new Padding(0, UiLayout.Scale(2), UiLayout.Scale(6), UiLayout.Scale(2));
        _scaleInput.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            AddCustomScale();
            e.Handled = true;
            e.SuppressKeyPress = true;
        };
        var addButton = UiLayout.CreateButton("添加", 72);
        addButton.Dock = DockStyle.Fill;
        addButton.Margin = new Padding(0, UiLayout.Scale(2), 0, UiLayout.Scale(2));
        addButton.Click += (_, _) => AddCustomScale();
        inputRow.Controls.Add(_scaleInput, 0, 0);
        inputRow.Controls.Add(addButton, 1, 0);
        actions.Controls.Add(inputRow, 0, 1);
        actions.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "示例：143 = 1:143\r\n0.25 = 4:1，也支持直接输入 1:143 或 4:1",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Margin = Padding.Empty
        }, 0, 2);
        actions.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "快捷操作",
            TextAlign = ContentAlignment.BottomLeft,
            Font = new System.Drawing.Font(UiLayout.DefaultFont, FontStyle.Bold),
            Margin = Padding.Empty
        }, 0, 3);

        var pickButton = UiLayout.CreateButton("从图中拾取比例…", 150);
        pickButton.Dock = DockStyle.Fill;
        pickButton.Margin = new Padding(0, UiLayout.Scale(2), 0, UiLayout.Scale(2));
        pickButton.Click += (_, _) => RequestScaleFromCad();
        actions.Controls.Add(pickButton, 0, 4);

        _removeScaleButton = UiLayout.CreateButton("删除选中的自定义比例", 170);
        _removeScaleButton.Dock = DockStyle.Fill;
        _removeScaleButton.Margin = new Padding(0, UiLayout.Scale(2), 0, UiLayout.Scale(2));
        _removeScaleButton.Click += (_, _) => RemoveSelectedCustomScales();
        actions.Controls.Add(_removeScaleButton, 0, 5);

        _scaleSelectionHint.Dock = DockStyle.Fill;
        _scaleSelectionHint.TextAlign = ContentAlignment.MiddleLeft;
        _scaleSelectionHint.ForeColor = Color.DimGray;
        _scaleSelectionHint.AutoEllipsis = true;
        _scaleSelectionHint.Margin = Padding.Empty;
        actions.Controls.Add(_scaleSelectionHint, 0, 6);
        actions.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            Text = "从图中拾取时，框选图框并选择 A0~A4 图幅，程序按短边计算比例。新增和删除操作将在点击“保存”后生效。",
            Margin = Padding.Empty
        }, 0, 7);
        actionsGroup.Controls.Add(actions);
        content.Controls.Add(actionsGroup, 1, 0);
        root.Controls.Add(content, 0, 1);

        page.Controls.Add(root);
        return page;
    }

    /// <summary>比例列表项；自定义项可删除，内置项只读展示。</summary>
    private sealed class ScaleListItem
    {
        public ScaleListItem(double value, bool isCustom)
        {
            Value = value;
            IsCustom = isCustom;
        }

        public double Value { get; }
        public bool IsCustom { get; }

        public override string ToString()
        {
            return PaperSizeDetector.ToScaleText(Value) + (IsCustom ? "（自定义）" : "");
        }
    }

    private void ReloadScaleList(AppSettings settings)
    {
        _scaleList.Items.Clear();
        foreach (var scale in PaperSizeDetector.BuiltInScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, false));
        }

        foreach (var scale in settings.CustomScales)
        {
            _scaleList.Items.Add(new ScaleListItem(scale, true));
        }

        UpdateScaleListState();
    }

    /// <summary>同步比例数量、选中提示和删除按钮状态，避免用户点击后才知道内置比例不可删除。</summary>
    private void UpdateScaleListState()
    {
        var items = _scaleList.Items.Cast<ScaleListItem>().ToList();
        var selected = _scaleList.SelectedItems.Cast<ScaleListItem>().ToList();
        var builtInCount = items.Count(item => !item.IsCustom);
        var customCount = items.Count - builtInCount;
        _scaleListSummary.Text = $"内置 {builtInCount} 个 · 自定义 {customCount} 个（Ctrl/Shift 可多选）";

        var canDelete = selected.Count > 0 && selected.All(item => item.IsCustom);
        if (_removeScaleButton != null)
        {
            _removeScaleButton.Enabled = canDelete;
        }

        _scaleSelectionHint.Text = selected.Count switch
        {
            0 => "请选择自定义比例后删除；内置比例始终保留。",
            _ when canDelete => $"已选择 {selected.Count} 个自定义比例，可以删除。",
            _ => "当前选择包含内置比例，内置比例不可删除。"
        };
        _scaleSelectionHint.ForeColor = selected.Count > 0 && !canDelete
            ? Color.FromArgb(174, 100, 25)
            : Color.DimGray;
    }

    private List<double> ReadCustomScalesFromList()
    {
        return _scaleList.Items
            .Cast<ScaleListItem>()
            .Where(x => x.IsCustom)
            .Select(x => x.Value)
            .ToList();
    }

    /// <summary>
    /// 属性图框设置标签页：7 个图框字段各自配置属性关键字（多个用逗号分隔）。
    /// 扫描属性图框时按关键字匹配块属性标签提取字段值。
    /// </summary>
    private TabPage BuildAttributeTitleBlockTab()
    {
        var page = new TabPage("属性图框设置")
        {
            Padding = new Padding(UiLayout.Scale(10)),
            BackColor = SystemColors.Control
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(66)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));

        // 顶部说明：关键字的作用范围与匹配规则。
        var scopePanel = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.FromArgb(239, 247, 255),
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(7), UiLayout.Scale(12), UiLayout.Scale(7)),
            Margin = Padding.Empty
        };
        var scopeText = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        scopeText.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(24)));
        scopeText.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        scopeText.Controls.Add(new Label
        {
            Text = "属性图框字段关键字",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new System.Drawing.Font(UiLayout.DefaultFont, FontStyle.Bold),
            ForeColor = Color.FromArgb(28, 78, 121),
            Margin = Padding.Empty
        }, 0, 0);
        scopeText.Controls.Add(new Label
        {
            Text = "仅对“新增属性图框”录入的图框生效    ·    按关键字顺序取第一个命中属性，忽略大小写",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(55, 76, 94),
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 1);
        scopePanel.Controls.Add(scopeText);
        root.Controls.Add(scopePanel, 0, 0);

        var group = new GroupBox
        {
            Text = "字段与属性关键字（多个关键字用逗号分隔）",
            Dock = DockStyle.Fill,
            Padding = new Padding(UiLayout.Scale(8)),
            Margin = Padding.Empty
        };

        _attributeKeywordsGrid.Dock = DockStyle.Fill;
        _attributeKeywordsGrid.AllowUserToAddRows = false;
        _attributeKeywordsGrid.AllowUserToDeleteRows = false;
        _attributeKeywordsGrid.AllowUserToResizeRows = false;
        _attributeKeywordsGrid.RowHeadersVisible = false;
        _attributeKeywordsGrid.SelectionMode = DataGridViewSelectionMode.CellSelect;
        _attributeKeywordsGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _attributeKeywordsGrid.BackgroundColor = SystemColors.Window;
        _attributeKeywordsGrid.BorderStyle = BorderStyle.FixedSingle;
        _attributeKeywordsGrid.Columns.Add("Field", "图框字段");
        _attributeKeywordsGrid.Columns.Add("Keywords", "属性关键字（逗号分隔，按顺序优先）");
        _attributeKeywordsGrid.Columns["Field"]!.ReadOnly = true;
        _attributeKeywordsGrid.Columns["Field"]!.FillWeight = 22;
        _attributeKeywordsGrid.Columns["Keywords"]!.FillWeight = 78;

        group.Controls.Add(_attributeKeywordsGrid);
        root.Controls.Add(group, 0, 1);

        // 底部提示：字段值提取后会自动去掉格式控制字符。
        root.Controls.Add(new Label
        {
            Text = "扫描属性图框时，块属性标签（Tag）命中关键字即取该属性值作为字段值，属性值会自动去除 \\P、%%C 等格式控制字符。",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.DimGray,
            AutoEllipsis = true,
            Margin = Padding.Empty
        }, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    /// <summary>从设置加载属性图框关键字到网格；缺失字段回填默认关键字。</summary>
    private void LoadAttributeKeywords(AppSettings settings)
    {
        _attributeKeywordsGrid.Rows.Clear();
        foreach (var fieldKey in AppSettingsStore.AttributeTitleBlockFieldKeys)
        {
            var keywords = settings.AttributeTitleBlockKeywords.TryGetValue(fieldKey, out var list) && list != null
                ? list
                : new List<string>();
            var rowIndex = _attributeKeywordsGrid.Rows.Add(
                AppSettingsStore.GetAttributeFieldDisplayName(fieldKey),
                string.Join(", ", keywords));
            _attributeKeywordsGrid.Rows[rowIndex].Tag = fieldKey;
        }
    }

    /// <summary>从网格读取属性图框关键字；同一字段去重去空，保存时全空字段由 Normalize 回填默认关键字。</summary>
    private Dictionary<string, List<string>> ReadAttributeKeywordsFromGrid()
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (DataGridViewRow row in _attributeKeywordsGrid.Rows)
        {
            if (row.IsNewRow || row.Tag is not string fieldKey)
            {
                continue;
            }

            var keywords = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var keyword in (row.Cells["Keywords"].Value?.ToString() ?? "").Split(',', ';'))
            {
                var trimmed = keyword.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    keywords.Add(trimmed);
                }
            }

            result[fieldKey] = keywords;
        }

        return result;
    }

    private void AddCustomScale()
    {
        if (!PaperSizeDetector.TryParseScale(_scaleInput.Text, out var scale))
        {
            MessageBox.Show(
                "无法识别比例输入。请输入 143（表示 1:143）、0.25（表示 4:1）或 1:143 形式。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        if (_scaleList.Items.Cast<ScaleListItem>().Any(x => Math.Abs(x.Value - scale) < 1e-6))
        {
            MessageBox.Show(
                $"比例 {PaperSizeDetector.ToScaleText(scale)} 已在列表中，无需重复添加。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // 自定义项按值升序插入，与保存时 NormalizeCustomScales 的排序一致，避免重开窗口后顺序变化。
        var insertIndex = _scaleList.Items.Count;
        for (var i = 0; i < _scaleList.Items.Count; i++)
        {
            if (_scaleList.Items[i] is ScaleListItem existing && existing.IsCustom && existing.Value > scale)
            {
                insertIndex = i;
                break;
            }
        }

        var added = new ScaleListItem(scale, true);
        _scaleList.Items.Insert(insertIndex, added);
        _scaleList.SelectedItem = added;
        UpdateScaleListState();
        _scaleInput.Clear();
        _scaleInput.Focus();
    }

    private void RemoveSelectedCustomScales()
    {
        var selected = _scaleList.SelectedItems.Cast<ScaleListItem>().ToList();
        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Any(x => !x.IsCustom))
        {
            MessageBox.Show(
                "内置比例不可删除，只能移除“（自定义）”比例。",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        foreach (var item in selected)
        {
            _scaleList.Items.Remove(item);
        }
        UpdateScaleListState();
    }

    private void RequestScaleFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 与目录行高/列宽交互一致：先保存当前页面编辑并退出模态窗体，再回到 CAD 命令上下文框选。
        AppSettingsStore.Save(settings);
        RequestPickScaleFromCad = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private TabPage BuildFileNameTab()
    {
        var page = new TabPage("文件名")
        {
            Padding = new Padding(UiLayout.Scale(12))
        };
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(178)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(68)));

        var instructionPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            Padding = new Padding(UiLayout.Scale(12), UiLayout.Scale(8), UiLayout.Scale(12), UiLayout.Scale(8)),
            BackColor = Color.FromArgb(247, 252, 247),
            BorderStyle = BorderStyle.FixedSingle
        };
        var instructionLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(30)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(54)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(28)));
        instructionLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ForeColor = Color.FromArgb(35, 145, 55),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "文件命名用以下字母表示各类信息："
        }, 0, 0);
        var tokenGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 2,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        for (var column = 0; column < 4; column++)
        {
            tokenGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        }
        tokenGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        tokenGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        var tokens = new[]
        {
            "A：图号", "B：版次", "C：图名", "D：日期",
            "E：信息1", "F：信息2", "G：设计阶段", "T：图幅"
        };
        for (var index = 0; index < tokens.Length; index++)
        {
            tokenGrid.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                ForeColor = Color.FromArgb(35, 145, 55),
                TextAlign = ContentAlignment.MiddleLeft,
                Text = tokens[index]
            }, index % 4, index / 4);
        }
        instructionLayout.Controls.Add(tokenGrid, 0, 1);
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            ForeColor = Color.FromArgb(35, 145, 55),
            TextAlign = ContentAlignment.MiddleLeft,
            Text = "N：序号（顺序与打印顺序一致）"
        }, 0, 2);
        instructionLayout.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = Color.FromArgb(35, 145, 55),
            Text = "输入这些字母本身时请用 \\ 转义，例如 \\A 输出 A。"
        }, 0, 3);
        instructionPanel.Controls.Add(instructionLayout);
        root.Controls.Add(instructionPanel, 0, 0);

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 5,
            Margin = Padding.Empty,
            Padding = new Padding(0, UiLayout.Scale(10), 0, 0)
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(125)));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            editor.RowStyles.Add(new RowStyle(SizeType.Absolute, UiLayout.Scale(42)));
        }
        editor.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        Label CreateRowLabel(string text, Color? color = null)
        {
            return new Label
            {
                Dock = DockStyle.Fill,
                Margin = Padding.Empty,
                Text = text,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = color ?? SystemColors.ControlText
            };
        }

        editor.Controls.Add(CreateRowLabel("文件命名："), 0, 0);
        _fileNamePattern.Dock = DockStyle.None;
        _fileNamePattern.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        _fileNamePattern.Margin = Padding.Empty;
        _fileNamePattern.MaxLength = 240;
        _fileNamePattern.TextChanged += (_, _) => UpdateFileNamePreview();
        editor.Controls.Add(_fileNamePattern, 1, 0);

        editor.Controls.Add(CreateRowLabel("开始序号："), 0, 1);
        var startEditor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        startEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(115)));
        startEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureNumber(_fileNameSequenceStart, 0, 999999999, 1, 0);
        _fileNameSequenceStart.Dock = DockStyle.None;
        _fileNameSequenceStart.Anchor = AnchorStyles.Left;
        _fileNameSequenceStart.Margin = Padding.Empty;
        _fileNameSequenceStart.Width = UiLayout.Scale(105);
        _fileNameSequenceStart.ValueChanged += (_, _) => UpdateFileNamePreview();
        startEditor.Controls.Add(_fileNameSequenceStart, 0, 0);
        startEditor.Controls.Add(CreateRowLabel("例如从 100 开始"), 1, 0);
        editor.Controls.Add(startEditor, 1, 1);

        editor.Controls.Add(CreateRowLabel("序号位数："), 0, 2);
        var digitsEditor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(80)));
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(120)));
        digitsEditor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        ConfigureNumber(_fileNameSequenceDigits, 0, 10, 1, 0);
        _fileNameSequenceDigits.Dock = DockStyle.None;
        _fileNameSequenceDigits.Anchor = AnchorStyles.Left;
        _fileNameSequenceDigits.Margin = Padding.Empty;
        _fileNameSequenceDigits.Width = UiLayout.Scale(70);
        _fileNameSequenceDigits.ValueChanged += (_, _) => UpdateFileNamePreview();
        digitsEditor.Controls.Add(_fileNameSequenceDigits, 0, 0);
        digitsEditor.Controls.Add(CreateRowLabel("0 表示不补零"), 1, 0);
        _autoFileNameSequenceDigits.Text = "按清单总张数自动推断";
        _autoFileNameSequenceDigits.AutoSize = true;
        _autoFileNameSequenceDigits.Dock = DockStyle.None;
        _autoFileNameSequenceDigits.Anchor = AnchorStyles.Left;
        _autoFileNameSequenceDigits.Margin = Padding.Empty;
        _autoFileNameSequenceDigits.CheckedChanged += (_, _) =>
        {
            UpdateSequenceDigitsState();
            UpdateFileNamePreview();
        };
        digitsEditor.Controls.Add(_autoFileNameSequenceDigits, 2, 0);
        editor.Controls.Add(digitsEditor, 1, 2);

        editor.Controls.Add(CreateRowLabel("输出示例：", Color.Navy), 0, 3);
        _fileNamePreview.Dock = DockStyle.Fill;
        _fileNamePreview.Margin = Padding.Empty;
        _fileNamePreview.TextAlign = ContentAlignment.MiddleLeft;
        _fileNamePreview.ForeColor = Color.Navy;
        _fileNamePreview.AutoSize = false;
        editor.Controls.Add(_fileNamePreview, 1, 3);
        root.Controls.Add(editor, 0, 1);

        // ── 加长图图名设置 ──
        var longPaperGroup = new GroupBox
        {
            Text = "加长图图名设置",
            Dock = DockStyle.Fill,
            Margin = new Padding(0, UiLayout.Scale(6), 0, 0),
            Padding = new Padding(UiLayout.Scale(8), UiLayout.Scale(4), UiLayout.Scale(8), UiLayout.Scale(4))
        };
        var longPaperRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = System.Windows.Forms.FlowDirection.LeftToRight,
            WrapContents = false
        };
        longPaperRow.Controls.Add(new Label
        {
            Text = "加长图命名格式：",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, UiLayout.Scale(4), UiLayout.Scale(6), 0)
        });
        _longPaperNameFormat.DropDownStyle = ComboBoxStyle.DropDownList;
        _longPaperNameFormat.Width = UiLayout.Scale(240);
        _longPaperNameFormat.Margin = new Padding(0, UiLayout.Scale(2), 0, 0);
        _longPaperNameFormat.Items.AddRange(new object[]
        {
            "配置1（分数）：A3+1/8、A2+3/4（分数形式）",
            "配置2（小数）：A3+0.125、A2+0.75（小数形式）",
            "配置3（预留）",
            "配置4（预留）",
            "配置5（预留）",
            "配置6（预留）",
        });
        _longPaperNameFormat.SelectedIndex = 0;
        _longPaperNameFormat.SelectedIndexChanged += (_, _) => UpdateFileNamePreview();
        longPaperRow.Controls.Add(_longPaperNameFormat);
        longPaperGroup.Controls.Add(longPaperRow);
        root.Controls.Add(longPaperGroup, 0, 2);

        page.Controls.Add(root);
        return page;
    }

    private void UpdateFileNamePreview()
    {
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
            _fileNamePreview.Text = "（请输入文件命名规则）";
            return;
        }

        var example = new PlotJob
        {
            DrawingNumber = "岩施003",
            Revision = "1.0版",
            Title = "基坑支护平面布置图",
            Date = "2026-07-18",
            Info1 = "信息1",
            Info2 = "信息2",
            Phase = "施工图",
            PaperName = "A2"
        };
        var startNumber = (int)_fileNameSequenceStart.Value;
        var sequenceDigits = FileNameSanitizer.ResolveSequenceDigits(
            _autoFileNameSequenceDigits.Checked,
            (int)_fileNameSequenceDigits.Value,
            startNumber,
            1);
        _fileNamePreview.Text = FileNameSanitizer.FormatFileNamePattern(
            _fileNamePattern.Text,
            example,
            startNumber,
            sequenceDigits,
            (LongPaperNameFormat)Math.Max(0, _longPaperNameFormat.SelectedIndex),
            (double)_longPaperSnapTolerance.Value);
        if (_autoFileNameSequenceDigits.Checked)
        {
            _fileNamePreview.Text += "（实际位数按图框列表总张数计算）";
        }
    }

    private void UpdateSequenceDigitsState()
    {
        _fileNameSequenceDigits.Enabled = !_autoFileNameSequenceDigits.Checked;
    }

    private TabPage BuildDirectoryTab()
    {
        var page = new TabPage("图纸目录") { Padding = new Padding(UiLayout.Scale(5)) };

        // 目录设置页整体 WPF 化：ElementHost 承载 DirectorySettingsControl。
        // 行插入/删除/拖拽均为 ObservableCollection 数据操作，不存在 DataGridView 的
        // 编辑会话与行号恢复问题（旧版右键插入自定义行反复抛 rowIndex 越界的根源）。
        WpfBootstrapper.EnsureInitialized();
        _directoryControl = new DirectorySettingsControl();
        _directoryControl.PickColumnWidthRequested += RequestColumnWidthFromCad;
        _directoryControl.PickRowHeightRequested += RequestRowHeightFromCad;
        _directoryControl.PickTextAppearanceRequested += RequestTextAppearanceFromCad;
        _directoryControl.SetTextStyleNames(LoadTextStyles());

        var host = new ElementHost
        {
            Dock = DockStyle.Fill,
            Child = _directoryControl
        };
        page.Controls.Add(host);
        return page;
    }

    private static TableLayoutPanel CreateSettingsTable(int rows)
    {
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows,
            Padding = new Padding(UiLayout.Scale(8))
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiLayout.Scale(145)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    private static void ConfigureNumber(NumericUpDown input, decimal min, decimal max, decimal increment, int decimals)
    {
        input.DecimalPlaces = decimals;
        input.Minimum = min;
        input.Maximum = max;
        input.Increment = increment;
        input.Dock = DockStyle.Left;
        input.Width = UiLayout.Scale(130);
    }


    private void LoadSettings()
    {
        Apply(AppSettingsStore.Load());
    }

    private void Apply(AppSettings settings)
    {
        _paperTolerance.Value = UiLayout.Clamp(_paperTolerance, settings.PaperMatchToleranceMm);
        _rasterDpi.SelectedItem = _rasterDpi.Items.Contains(settings.RasterDpi)
            ? settings.RasterDpi
            : 300;
        _recognizeFourLineRectangleFrames.Checked = settings.RecognizeFourLineRectangleFrames;
        _hideFrameBoundaryWhenPlotting.Checked = settings.HideFrameBoundaryWhenPlotting;
        _plotTransparency.Checked = settings.PlotTransparency;
        _addSequenceWhenPdfExists.Checked = settings.AddSequenceWhenPdfExists;
        _useFileNameAsPdfBookmark.Checked = settings.UseFileNameAsPdfBookmark;
        _mergePdfByPaperSize.Checked = settings.MergePdfByPaperSize;
        _openOutputDirectoryAfterBatchPrint.Checked = settings.OpenOutputDirectoryAfterBatchPrint;
        _openMergedPdfAfterMerge.Checked = settings.OpenMergedPdfAfterMerge;
        _generatePrintLog.Checked = settings.GeneratePrintLog;
        _convertTextToGeometryWhenPlotting.Checked = settings.ConvertTextToGeometryWhenPlotting;
        _fileNamePattern.Text = settings.PdfFileNamePattern;
        _fileNameSequenceStart.Value = Math.Max(
            _fileNameSequenceStart.Minimum,
            Math.Min(_fileNameSequenceStart.Maximum, settings.FileNameSequenceStartNumber));
        _fileNameSequenceDigits.Value = Math.Max(
            _fileNameSequenceDigits.Minimum,
            Math.Min(_fileNameSequenceDigits.Maximum, settings.FileNameSequenceDigits));
        _autoFileNameSequenceDigits.Checked = settings.AutoFileNameSequenceDigits;
        UpdateSequenceDigitsState();
        UpdateFileNamePreview();
        _openExternalDwgForPlot.Checked = settings.OpenExternalDwgForPlot;
        _directoryControl.ApplySettings(settings);
        _longPaperNameFormat.SelectedIndex = Math.Max(0, Math.Min(5, (int)settings.LongPaperNameFormat));
        _longPaperSnapTolerance.Value = UiLayout.Clamp(
            _longPaperSnapTolerance,
            settings.LongPaperSnapToleranceMm);
        ReloadScaleList(settings);
        LoadAttributeKeywords(settings);
    }

    private void SaveSettings()
    {
        if (!TryReadSettingsFromControls(out var current))
        {
            return;
        }

        AppSettingsStore.Save(current);
        DialogResult = DialogResult.OK;
        Close();
    }

    private bool TryReadSettingsFromControls(out AppSettings current)
    {
        current = AppSettingsStore.Load();
        if (!_directoryControl.TryReadSnapshot(out var directory))
        {
            return false;
        }

        current.PaperMatchToleranceMm = (double)_paperTolerance.Value;
        current.RasterDpi = _rasterDpi.SelectedItem is int dpi && (dpi == 150 || dpi == 300 || dpi == 600)
            ? dpi
            : 300;
        current.RecognizeFourLineRectangleFrames = _recognizeFourLineRectangleFrames.Checked;
        current.HideFrameBoundaryWhenPlotting = _hideFrameBoundaryWhenPlotting.Checked;
        current.PlotTransparency = _plotTransparency.Checked;
        current.AddSequenceWhenPdfExists = _addSequenceWhenPdfExists.Checked;
        current.UseFileNameAsPdfBookmark = _useFileNameAsPdfBookmark.Checked;
        current.MergePdfByPaperSize = _mergePdfByPaperSize.Checked;
        current.OpenOutputDirectoryAfterBatchPrint = _openOutputDirectoryAfterBatchPrint.Checked;
        current.OpenMergedPdfAfterMerge = _openMergedPdfAfterMerge.Checked;
        current.GeneratePrintLog = _generatePrintLog.Checked;
        current.ConvertTextToGeometryWhenPlotting = _convertTextToGeometryWhenPlotting.Checked;
        if (string.IsNullOrWhiteSpace(_fileNamePattern.Text))
        {
            MessageBox.Show("请输入文件命名规则。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        current.PdfFileNamePattern = _fileNamePattern.Text;
        current.FileNameSequenceStartNumber = (int)_fileNameSequenceStart.Value;
        current.FileNameSequenceDigits = (int)_fileNameSequenceDigits.Value;
        current.AutoFileNameSequenceDigits = _autoFileNameSequenceDigits.Checked;
        current.OpenExternalDwgForPlot = _openExternalDwgForPlot.Checked;
        current.DirectoryColorIndex = directory.ColorIndex;
        current.DirectoryTextHeight = directory.TextHeight;
        current.DirectoryTextWidthFactor = directory.TextWidthFactor;
        current.DirectoryRowHeight = directory.RowHeight;
        current.DirectoryTextHeightRatio = Math.Max(0.01, Math.Min(0.9, directory.TextHeight / directory.RowHeight));
        current.DirectoryTextStyleName = directory.TextStyleName;
        current.DirectoryLayerName = directory.LayerName;
        current.DirectoryDrawHeader = directory.DrawHeader;
        current.DirectoryDrawGridLines = directory.DrawGridLines;
        current.DirectoryColumns = directory.Columns;
        current.LongPaperNameFormat = (LongPaperNameFormat)Math.Max(0, Math.Min(5, _longPaperNameFormat.SelectedIndex));
        current.LongPaperSnapToleranceMm = (double)_longPaperSnapTolerance.Value;
        current.CustomScales = ReadCustomScalesFromList();
        current.AttributeTitleBlockKeywords = ReadAttributeKeywordsFromGrid();
        return true;
    }

    /// <summary>读取当前图纸的文字样式列表（含“（默认）”占位与“宋体”兜底），注入 WPF 目录设置控件。</summary>
    private List<string> LoadTextStyles()
    {
        var items = new List<string> { DefaultTextStyleDisplay };

        var document = GetActiveDocument();
        if (document != null)
        {
            try
            {
                using var tr = document.Database.TransactionManager.StartTransaction();
                var table = (TextStyleTable)tr.GetObject(document.Database.TextStyleTableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    var record = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                    if (!string.IsNullOrWhiteSpace(record.Name))
                    {
                        items.Add(record.Name);
                    }
                }

                tr.Commit();
            }
            catch
            {
            }
        }

        // 即使当前图纸尚未建立“宋体”文字样式，也先在界面提供该默认项；生成目录时会在本图内自动创建。
        if (!items.Any(x => string.Equals(x, "宋体", StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(1, "宋体");
        }

        return items;
    }

    private void RequestColumnWidthFromCad(string key)
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(key) || !TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // CAD 取点必须在设置窗体关闭后执行；先保存全部未提交编辑，再由调用方回到命令上下文框选。
        AppSettingsStore.Save(settings);
        RequestedDirectoryColumnKey = key;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestRowHeightFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 与列宽交互一致，先保存当前页面编辑，再关闭模态窗体回到 CAD 命令上下文量取高度。
        AppSettingsStore.Save(settings);
        RequestPickDirectoryRowHeight = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private void RequestTextAppearanceFromCad()
    {
        if (GetActiveDocument() == null)
        {
            MessageBox.Show("当前没有可用的 CAD 文档。", "批量打印设置", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!TryReadSettingsFromControls(out var settings))
        {
            return;
        }

        // 和列宽/行高一致：先保存页面编辑并退出模态窗体，再回到 CAD 命令上下文点选实体。
        AppSettingsStore.Save(settings);
        RequestPickDirectoryTextAppearance = true;
        DialogResult = DialogResult.OK;
        Close();
    }

    private Document? GetActiveDocument()
    {
        try
        {
            // 不缓存 ObjectId 或旧文档：每次按钮状态/点选请求都以当前 MDI 活动图纸为准。
            return CadApp.DocumentManager.MdiActiveDocument;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        SelectedTabIndex = _tabs.SelectedIndex;
        InitialTabIndex = SelectedTabIndex;
        base.OnFormClosing(e);
    }

    private void ResetDefaults()
    {
        Apply(AppSettingsStore.Default());
    }
}
