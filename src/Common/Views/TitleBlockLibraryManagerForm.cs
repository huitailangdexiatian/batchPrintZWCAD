using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
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

/// <summary>
/// 图框信息库管理：标签 1 常规图框（字段按区域框选提取），标签 2 属性图框（字段按关键字从块属性提取）。
/// 两个标签共享同一份图框库数据，按 IsAttributeBased 分流展示；块名在两类图框间全局唯一。
/// </summary>
public sealed class TitleBlockLibraryManagerForm : Form
{
    private readonly BindingList<TitleBlockRow> _rows = new();
    private readonly DataGridView _regularGrid = new();
    private readonly DataGridView _attributeGrid = new();
    private readonly Label _status = new();
    private readonly ContextMenuStrip _rowMenu = new();
    private readonly TabControl _tabs = new();
    private bool _loading;
    private bool _dirty;
    private readonly Dictionary<DataGridView, GridState> _gridStates = new();

    /// <summary>单个标签页的网格与其独立的排序状态、绑定列表。</summary>
    private sealed class GridState
    {
        public DataGridView Grid = null!;
        public BindingList<TitleBlockRow> DisplayRows = new();
        public bool IsAttributeTab;
        public int SortColumnIndex = -1;
        public ListSortDirection SortDirection = ListSortDirection.Ascending;
    }

    public bool LibraryChanged { get; private set; }

    public TitleBlockLibraryManagerForm()
    {
        InitializeComponents();
        LoadRows();
        FormClosing += OnFormClosing;
        FormClosed += (_, _) => _rowMenu.Dispose();
    }

    private void InitializeComponents()
    {
        Text = "图框信息库管理";
        UiLayout.ConfigureForm(this, 760, 360, 700, 320);
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.Sizable;
        MaximizeBox = true;
        SizeGripStyle = SizeGripStyle.Show;

        var top = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = UiLayout.ButtonHeight() + UiLayout.Scale(12),
            Padding = new Padding(UiLayout.Scale(8), UiLayout.Scale(5), UiLayout.Scale(8), UiLayout.Scale(4)),
            WrapContents = false,
            AutoScroll = true
        };

        Button MakeButton(string text, int width)
        {
            return UiLayout.CreateButton(text, width);
        }

        var saveButton = MakeButton("保存修改", 96);
        saveButton.Click += (_, _) => SaveRows();

        var deleteButton = MakeButton("删除选中", 96);
        deleteButton.Click += (_, _) => DeleteSelected();

        var reloadButton = MakeButton("重新读取", 96);
        reloadButton.Click += (_, _) => LoadRows();

        var importButton = MakeButton("导入图框库", 104);
        importButton.Click += (_, _) => ImportLibrary();

        var exportButton = MakeButton("导出图框库", 104);
        exportButton.Click += (_, _) => ExportLibrary();

        var openFolderButton = MakeButton("打开配置目录", 112);
        openFolderButton.Click += (_, _) =>
        {
            ExecuteSafely("打开配置目录", () =>
            {
                Directory.CreateDirectory(TitleBlockLibraryStore.DefaultDirectory);
                Process.Start(new ProcessStartInfo
                {
                    FileName = TitleBlockLibraryStore.DefaultDirectory,
                    UseShellExecute = true
                });
            });
        };

        var closeButton = MakeButton("关闭", 72);
        closeButton.Click += (_, _) => Close();

        top.Controls.Add(saveButton);
        top.Controls.Add(deleteButton);
        top.Controls.Add(reloadButton);
        top.Controls.Add(importButton);
        top.Controls.Add(exportButton);
        top.Controls.Add(openFolderButton);
        top.Controls.Add(closeButton);

        _tabs.Dock = DockStyle.Fill;

        var regularState = new GridState { Grid = _regularGrid, IsAttributeTab = false };
        var attributeState = new GridState { Grid = _attributeGrid, IsAttributeTab = true };
        _gridStates[_regularGrid] = regularState;
        _gridStates[_attributeGrid] = attributeState;

        UiLayout.StyleGrid(_regularGrid, Font);
        UiLayout.StyleGrid(_attributeGrid, Font);
        AddRegularColumns();
        AddAttributeColumns();
        _regularGrid.DataSource = regularState.DisplayRows;
        _attributeGrid.DataSource = attributeState.DisplayRows;

        var regularTab = new TabPage("常规图框") { Padding = new Padding(0) };
        regularTab.Controls.Add(_regularGrid);
        var attributeTab = new TabPage("属性图框") { Padding = new Padding(0) };
        attributeTab.Controls.Add(_attributeGrid);
        _tabs.TabPages.Add(regularTab);
        _tabs.TabPages.Add(attributeTab);

        foreach (var state in _gridStates.Values)
        {
            var grid = state.Grid;
            grid.ColumnHeaderMouseClick += GridColumnHeaderMouseClick;
            grid.CellDoubleClick += (_, e) =>
            {
                if (e.RowIndex >= 0)
                {
                    EditSelectedDefinition();
                }
            };
            grid.CellMouseDown += GridCellMouseDown;
            grid.CellValueChanged += (_, _) => MarkDirty();
            grid.CurrentCellDirtyStateChanged += (_, _) =>
            {
                if (grid.IsCurrentCellDirty)
                {
                    grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                }
            };
            grid.DataError += (_, e) =>
            {
                e.ThrowException = false;
                MessageBox.Show("输入值格式不正确，请输入有效的数字。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
            grid.RowPrePaint += GridRowPrePaint;
            grid.ContextMenuStrip = _rowMenu;
        }

        var editItem = _rowMenu.Items.Add("编辑");
        editItem.Click += (_, _) => EditSelectedDefinition();
        _rowMenu.Opening += (_, e) => e.Cancel = ActiveGrid.SelectedRows.Count == 0;

        Activated += (_, _) => RefreshPresentBlocks();

        _status.Dock = DockStyle.Bottom;
        _status.Height = Math.Max(UiLayout.Scale(28), Font.Height + UiLayout.Scale(10));
        _status.TextAlign = ContentAlignment.MiddleLeft;
        _status.Padding = new Padding(UiLayout.Scale(8), 0, 0, 0);

        Controls.Add(_tabs);
        Controls.Add(_status);
        Controls.Add(top);
    }

    /// <summary>当前激活标签页的网格；删除、编辑等行级操作只作用于它。</summary>
    private DataGridView ActiveGrid => ReferenceEquals(_tabs.SelectedTab?.Controls[0], _attributeGrid)
        ? _attributeGrid
        : _regularGrid;

    private GridState StateOf(DataGridView grid) => _gridStates[grid];

    private void AddRegularColumns()
    {
        _regularGrid.Columns.Clear();
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.BlockName), HeaderText = "块名", Width = UiLayout.Scale(190) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.CreatedAt), HeaderText = "加入时间", Width = UiLayout.Scale(170), ReadOnly = true });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperName), HeaderText = "图幅", Width = UiLayout.Scale(80) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperWidthMm), HeaderText = "纸宽mm", Width = UiLayout.Scale(90) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperHeightMm), HeaderText = "纸高mm", Width = UiLayout.Scale(90) });
        _regularGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(TitleBlockRow.HasPrintRegion), HeaderText = "有打印边界", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinX), HeaderText = "边界MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinY), HeaderText = "边界MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxX), HeaderText = "边界MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxY), HeaderText = "边界MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMinX), HeaderText = "图名MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMinY), HeaderText = "图名MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMaxX), HeaderText = "图名MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.TitleMaxY), HeaderText = "图名MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMinX), HeaderText = "图号MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMinY), HeaderText = "图号MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMaxX), HeaderText = "图号MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.NumberMaxY), HeaderText = "图号MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.DateMinX), HeaderText = "日期MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.DateMinY), HeaderText = "日期MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.DateMaxX), HeaderText = "日期MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.DateMaxY), HeaderText = "日期MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.RevisionMinX), HeaderText = "版次MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.RevisionMinY), HeaderText = "版次MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.RevisionMaxX), HeaderText = "版次MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.RevisionMaxY), HeaderText = "版次MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PhaseMinX), HeaderText = "设计阶段MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PhaseMinY), HeaderText = "设计阶段MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PhaseMaxX), HeaderText = "设计阶段MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PhaseMaxY), HeaderText = "设计阶段MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info1MinX), HeaderText = "信息1MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info1MinY), HeaderText = "信息1MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info1MaxX), HeaderText = "信息1MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info1MaxY), HeaderText = "信息1MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info2MinX), HeaderText = "信息2MinX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info2MinY), HeaderText = "信息2MinY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info2MaxX), HeaderText = "信息2MaxX", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.Info2MaxY), HeaderText = "信息2MaxY", Width = UiLayout.Scale(96) });
        _regularGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.UpdatedAt), HeaderText = "更新时间", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = UiLayout.Scale(170), ReadOnly = true });

        foreach (DataGridViewColumn column in _regularGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        }
    }

    /// <summary>属性图框字段值来自块属性而非框选区域，不展示 36 个区域坐标列。</summary>
    private void AddAttributeColumns()
    {
        _attributeGrid.Columns.Clear();
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.BlockName), HeaderText = "块名", Width = UiLayout.Scale(200) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.CreatedAt), HeaderText = "加入时间", Width = UiLayout.Scale(170), ReadOnly = true });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperName), HeaderText = "图幅", Width = UiLayout.Scale(90) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperWidthMm), HeaderText = "纸宽mm", Width = UiLayout.Scale(90) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PaperHeightMm), HeaderText = "纸高mm", Width = UiLayout.Scale(90) });
        _attributeGrid.Columns.Add(new DataGridViewCheckBoxColumn { DataPropertyName = nameof(TitleBlockRow.HasPrintRegion), HeaderText = "有打印边界", Width = UiLayout.Scale(96) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinX), HeaderText = "边界MinX", Width = UiLayout.Scale(110) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMinY), HeaderText = "边界MinY", Width = UiLayout.Scale(110) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxX), HeaderText = "边界MaxX", Width = UiLayout.Scale(110) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.PrintMaxY), HeaderText = "边界MaxY", Width = UiLayout.Scale(110) });
        _attributeGrid.Columns.Add(new DataGridViewTextBoxColumn { DataPropertyName = nameof(TitleBlockRow.UpdatedAt), HeaderText = "更新时间", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = UiLayout.Scale(170), ReadOnly = true });

        foreach (DataGridViewColumn column in _attributeGrid.Columns)
        {
            column.SortMode = DataGridViewColumnSortMode.Programmatic;
        }
    }

    private void LoadRows()
    {
        if (_dirty
            && MessageBox.Show("当前有未保存的修改，确定重新读取并放弃这些修改吗？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        ExecuteSafely("重新读取图框库", () =>
        {
            _loading = true;
            try
            {
                _rows.Clear();
                foreach (var block in TitleBlockLibraryStore.Load().Blocks.OrderBy(x => x.BlockName, StringComparer.CurrentCultureIgnoreCase))
                {
                    _rows.Add(TitleBlockRow.FromDefinition(block));
                }

                RefreshDisplayRows();
                _dirty = false;
                RefreshPresentBlocks();
                RefreshStatus();
            }
            finally
            {
                _loading = false;
            }
        });
    }

    private void SaveRows()
    {
        if (!TryBuildLibrary(out var library))
        {
            return;
        }

        ExecuteSafely("保存图框信息库", () =>
        {
            TitleBlockLibraryStore.Save(library);
            LibraryChanged = true;
            _dirty = false;
            RefreshStatus();
            MessageBox.Show("图框信息库已保存。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void DeleteSelected()
    {
        var grid = ActiveGrid;
        if (grid.SelectedRows.Count == 0)
        {
            return;
        }

        if (MessageBox.Show("确定删除选中的图框定义吗？", "图框信息库管理", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        var selected = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(row => row.DataBoundItem)
            .OfType<TitleBlockRow>()
            .ToList();

        foreach (var row in selected)
        {
            _rows.Remove(row);
        }

        RefreshDisplayRows();
        _dirty = true;
        RefreshStatus();
        SaveRows();
    }

    private void GridCellMouseDown(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (sender is not DataGridView grid)
        {
            return;
        }

        if (e.Button != MouseButtons.Right)
        {
            return;
        }

        if (e.RowIndex < 0)
        {
            grid.ClearSelection();
            return;
        }

        // 右键所在行必须先成为当前选择，否则菜单“编辑”可能作用到此前选中的另一条记录。
        grid.ClearSelection();
        grid.Rows[e.RowIndex].Selected = true;
        if (e.ColumnIndex >= 0)
        {
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells[e.ColumnIndex];
        }
    }

    private void EditSelectedDefinition()
    {
        var grid = ActiveGrid;
        grid.EndEdit();
        var row = grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Select(item => item.DataBoundItem)
            .OfType<TitleBlockRow>()
            .FirstOrDefault();
        if (row == null)
        {
            return;
        }

        // 表格内若已有手工修改，先明确保存，避免编辑窗口从磁盘回读到另一份旧配置。
        if (_dirty)
        {
            var saveFirst = MessageBox.Show(
                "当前有未保存的表格修改。编辑图框前需要先保存，是否继续？",
                Text,
                MessageBoxButtons.OKCancel,
                MessageBoxIcon.Question);
            if (saveFirst != DialogResult.OK)
            {
                return;
            }

            SaveRows();
            if (_dirty)
            {
                return;
            }
        }

        CadWindowFocus.HideForCadInput(this);
        try
        {
            if (BatchPlotCommands.EditTitleBlockFromLibrary(row.BlockName))
            {
                LibraryChanged = true;
                LoadRows();
            }
        }
        finally
        {
            CadWindowFocus.RestoreDialog(this);
        }
    }

    private HashSet<string> _presentBlockNames = new(StringComparer.OrdinalIgnoreCase);

    private void RefreshPresentBlocks()
    {
        _presentBlockNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = CadApp.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            using var tr = doc.Database.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId ownerId in blockTable)
            {
                var owner = (BlockTableRecord)tr.GetObject(ownerId, OpenMode.ForRead);
                if (!owner.IsLayout)
                {
                    continue;
                }

                foreach (ObjectId id in owner)
                {
                    if (tr.GetObject(id, OpenMode.ForRead, false) is not BlockReference blockRef)
                    {
                        continue;
                    }

                    var blockName = CadTextExtractor.GetBlockName(blockRef, tr);
                    _presentBlockNames.Add(blockName);
                    _presentBlockNames.Add(CadTextExtractor.GetLibraryIdentityName(blockRef, tr));

                    if (CadTextExtractor.TryGetVisibleNestedBlockName(tr, blockRef, out var innerName))
                    {
                        _presentBlockNames.Add(blockName + "+" + innerName);
                    }
                }
            }

            tr.Commit();
        }
        catch
        {
            // 无激活文档或图纸不可读时不报错，列表行仅不回显粉色。
        }

        _regularGrid.Invalidate();
        _attributeGrid.Invalidate();
    }

    private void GridRowPrePaint(object? sender, DataGridViewRowPrePaintEventArgs e)
    {
        if (sender is not DataGridView grid
            || e.RowIndex < 0
            || grid.Rows[e.RowIndex].DataBoundItem is not TitleBlockRow row
            || string.IsNullOrWhiteSpace(row.BlockName)
            || !_presentBlockNames.Contains(row.BlockName))
        {
            return;
        }

        // 当前 CAD 中已存在的图框整行淡粉色，方便用户快速认出新录入条目
        using var brush = new SolidBrush(Color.FromArgb(255, 230, 230));
        for (var col = 0; col < grid.Columns.Count; col++)
        {
            grid.Rows[e.RowIndex].Cells[col].Style.BackColor = Color.FromArgb(255, 230, 230);
        }
    }

    private void GridColumnHeaderMouseClick(object? sender, DataGridViewCellMouseEventArgs e)
    {
        if (sender is not DataGridView grid
            || e.ColumnIndex < 0
            || e.ColumnIndex >= grid.Columns.Count)
        {
            return;
        }

        var state = StateOf(grid);
        // 同一列表头再次点击时切换升序/降序；切换到其他列时从升序开始。
        if (state.SortColumnIndex == e.ColumnIndex)
        {
            state.SortDirection = state.SortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
        }
        else
        {
            state.SortColumnIndex = e.ColumnIndex;
            state.SortDirection = ListSortDirection.Ascending;
        }

        grid.EndEdit();
        RefreshDisplayRows();
    }

    private void RefreshDisplayRows()
    {
        var wasLoading = _loading;
        _loading = true;
        try
        {
            // DataGridView 在重置绑定时可能触发 CellValueChanged；排序属于视图操作，不能误标记为未保存修改。
            foreach (var state in _gridStates.Values)
            {
                IEnumerable<TitleBlockRow> rows = _rows.Where(x => x.IsAttributeBased == state.IsAttributeTab);
                if (state.SortColumnIndex >= 0 && state.SortColumnIndex < state.Grid.Columns.Count)
                {
                    var propertyName = state.Grid.Columns[state.SortColumnIndex].DataPropertyName;
                    var property = TypeDescriptor.GetProperties(typeof(TitleBlockRow))[propertyName];
                    if (property != null)
                    {
                        var comparer = Comparer<TitleBlockRow>.Create((left, right) =>
                            CompareSortValues(property.GetValue(left), property.GetValue(right)));
                        rows = state.SortDirection == ListSortDirection.Ascending
                            ? rows.OrderBy(row => row, comparer)
                            : rows.OrderByDescending(row => row, comparer);
                    }
                }

                ReplaceBindingListContents(state.DisplayRows, rows);
                UpdateSortGlyph(state);
            }
        }
        finally
        {
            _loading = wasLoading;
        }
    }

    private static int CompareSortValues(object? left, object? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left == null)
        {
            return -1;
        }

        if (right == null)
        {
            return 1;
        }

        if (left is string leftText && right is string rightText)
        {
            return NaturalStringComparer.Instance.Compare(leftText, rightText);
        }

        return left is IComparable comparable
            ? comparable.CompareTo(right)
            : string.Compare(left.ToString(), right.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

    private void UpdateSortGlyph(GridState state)
    {
        foreach (DataGridViewColumn column in state.Grid.Columns)
        {
            column.HeaderCell.SortGlyphDirection = SortOrder.None;
        }

        if (state.SortColumnIndex >= 0 && state.SortColumnIndex < state.Grid.Columns.Count)
        {
            state.Grid.Columns[state.SortColumnIndex].HeaderCell.SortGlyphDirection =
                state.SortDirection == ListSortDirection.Ascending ? SortOrder.Ascending : SortOrder.Descending;
        }
    }

    /// <summary>
    /// 排序只替换界面绑定列表，不改变图框库的原始集合顺序；编辑、删除仍操作同一行对象。
    /// </summary>
    private static void ReplaceBindingListContents<T>(BindingList<T> target, IEnumerable<T> values)
    {
        var snapshot = values.ToList();
        target.RaiseListChangedEvents = false;
        try
        {
            target.Clear();
            foreach (var value in snapshot)
            {
                target.Add(value);
            }
        }
        finally
        {
            target.RaiseListChangedEvents = true;
            target.ResetBindings();
        }
    }

    private void ImportLibrary()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            Title = "导入图框库"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (MessageBox.Show("导入会覆盖当前图框信息库，确定继续吗？", Text, MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            return;
        }

        ExecuteSafely("导入图框库", () =>
        {
            var library = TitleBlockLibraryStore.Load(dialog.FileName);
            TitleBlockLibraryStore.Save(library);
            LibraryChanged = true;
            _dirty = false;
            LoadRows();
            MessageBox.Show("图框库已导入。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void ExportLibrary()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "图框库 (*.json)|*.json",
            FileName = "TitleBlockLibrary.json",
            Title = "导出图框库"
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        if (!TryBuildLibrary(out var library))
        {
            return;
        }

        ExecuteSafely("导出图框库", () =>
        {
            TitleBlockLibraryStore.Save(library, dialog.FileName);
            MessageBox.Show("图框库已导出。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
        });
    }

    private void RefreshStatus()
    {
        var regularCount = _rows.Count(x => !x.IsAttributeBased);
        var attributeCount = _rows.Count - regularCount;
        _status.Text = $"共 {_rows.Count} 个图框定义（常规 {regularCount} / 属性 {attributeCount}）。双击行或右键选择“编辑”。{(_dirty ? "有未保存修改。" : "")} 配置文件: {TitleBlockLibraryStore.DefaultPath}";
    }

    private bool TryBuildLibrary(out TitleBlockLibrary library)
    {
        library = new TitleBlockLibrary();
        _regularGrid.EndEdit();
        _attributeGrid.EndEdit();

        var invalid = _rows.FirstOrDefault(x => string.IsNullOrWhiteSpace(x.BlockName));
        if (invalid != null)
        {
            MessageBox.Show("块名不能为空。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var invalidNumber = _rows.FirstOrDefault(x => !x.HasFiniteNumbers());
        if (invalidNumber != null)
        {
            MessageBox.Show($"图框 {invalidNumber.BlockName} 含有无效数字。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var invalidPaper = _rows.FirstOrDefault(x =>
            !string.IsNullOrWhiteSpace(x.PaperName)
            && (x.PaperWidthMm <= 0 || x.PaperHeightMm <= 0));
        if (invalidPaper != null)
        {
            MessageBox.Show($"图框 {invalidPaper.BlockName} 设置了图幅，但纸宽/纸高必须大于 0。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var invalidRegion = _rows.FirstOrDefault(x =>
            x.HasPrintRegion
            && (Math.Abs(x.PrintMaxX - x.PrintMinX) <= 1e-6 || Math.Abs(x.PrintMaxY - x.PrintMinY) <= 1e-6));
        if (invalidRegion != null)
        {
            MessageBox.Show($"图框 {invalidRegion.BlockName} 的打印边界宽度和高度必须大于 0。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        // 块名在常规/属性两类图框间全局唯一，去重校验覆盖全部行。
        var duplicated = _rows
            .GroupBy(x => x.BlockName.Trim(), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicated != null)
        {
            MessageBox.Show("存在重复块名: " + duplicated.Key, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        foreach (var row in _rows)
        {
            library.Blocks.Add(row.ToDefinition());
        }

        return true;
    }

    private void MarkDirty()
    {
        if (_loading)
        {
            return;
        }

        _dirty = true;
        RefreshStatus();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_dirty)
        {
            return;
        }

        var result = MessageBox.Show(
            "当前有未保存的修改。是否先保存？",
            Text,
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question);
        if (result == DialogResult.Cancel)
        {
            e.Cancel = true;
        }
        else if (result == DialogResult.Yes)
        {
            SaveRows();
            e.Cancel = _dirty;
        }
    }

    private void ExecuteSafely(string action, Action work)
    {
        try
        {
            work();
        }
        catch (Exception ex)
        {
            MessageBox.Show(action + "失败: " + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private sealed class TitleBlockRow
    {
        public string BlockName { get; set; } = "";
        /// <summary>是否为属性图框：字段值按关键字从块属性提取，无区域坐标。</summary>
        public bool IsAttributeBased { get; set; }
        public string PaperName { get; set; } = "";
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
        public bool HasPrintRegion { get; set; }
        public string CoordinateMode { get; set; } = "Local";
        public double PrintMinX { get; set; }
        public double PrintMinY { get; set; }
        public double PrintMaxX { get; set; }
        public double PrintMaxY { get; set; }
        public double TitleMinX { get; set; }
        public double TitleMinY { get; set; }
        public double TitleMaxX { get; set; }
        public double TitleMaxY { get; set; }
        public double NumberMinX { get; set; }
        public double NumberMinY { get; set; }
        public double NumberMaxX { get; set; }
        public double NumberMaxY { get; set; }
        public double DateMinX { get; set; }
        public double DateMinY { get; set; }
        public double DateMaxX { get; set; }
        public double DateMaxY { get; set; }
        public double RevisionMinX { get; set; }
        public double RevisionMinY { get; set; }
        public double RevisionMaxX { get; set; }
        public double RevisionMaxY { get; set; }
        public double PhaseMinX { get; set; }
        public double PhaseMinY { get; set; }
        public double PhaseMaxX { get; set; }
        public double PhaseMaxY { get; set; }
        public double Info1MinX { get; set; }
        public double Info1MinY { get; set; }
        public double Info1MaxX { get; set; }
        public double Info1MaxY { get; set; }
        public double Info2MinX { get; set; }
        public double Info2MinY { get; set; }
        public double Info2MaxX { get; set; }
        public double Info2MaxY { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public static TitleBlockRow FromDefinition(TitleBlockDefinition definition)
        {
            return new TitleBlockRow
            {
                BlockName = definition.BlockName,
                IsAttributeBased = definition.IsAttributeBased,
                PaperName = definition.PaperName,
                PaperWidthMm = definition.PaperWidthMm,
                PaperHeightMm = definition.PaperHeightMm,
                HasPrintRegion = definition.HasPrintRegion,
                CoordinateMode = string.IsNullOrWhiteSpace(definition.CoordinateMode) ? "Local" : definition.CoordinateMode,
                PrintMinX = definition.PrintRegion.MinX,
                PrintMinY = definition.PrintRegion.MinY,
                PrintMaxX = definition.PrintRegion.MaxX,
                PrintMaxY = definition.PrintRegion.MaxY,
                TitleMinX = definition.TitleRegion.MinX,
                TitleMinY = definition.TitleRegion.MinY,
                TitleMaxX = definition.TitleRegion.MaxX,
                TitleMaxY = definition.TitleRegion.MaxY,
                NumberMinX = definition.DrawingNumberRegion.MinX,
                NumberMinY = definition.DrawingNumberRegion.MinY,
                NumberMaxX = definition.DrawingNumberRegion.MaxX,
                NumberMaxY = definition.DrawingNumberRegion.MaxY,
                DateMinX = definition.DateRegion.MinX,
                DateMinY = definition.DateRegion.MinY,
                DateMaxX = definition.DateRegion.MaxX,
                DateMaxY = definition.DateRegion.MaxY,
                RevisionMinX = definition.RevisionRegion.MinX,
                RevisionMinY = definition.RevisionRegion.MinY,
                RevisionMaxX = definition.RevisionRegion.MaxX,
                RevisionMaxY = definition.RevisionRegion.MaxY,
                PhaseMinX = definition.PhaseRegion.MinX,
                PhaseMinY = definition.PhaseRegion.MinY,
                PhaseMaxX = definition.PhaseRegion.MaxX,
                PhaseMaxY = definition.PhaseRegion.MaxY,
                Info1MinX = definition.Info1Region.MinX,
                Info1MinY = definition.Info1Region.MinY,
                Info1MaxX = definition.Info1Region.MaxX,
                Info1MaxY = definition.Info1Region.MaxY,
                Info2MinX = definition.Info2Region.MinX,
                Info2MinY = definition.Info2Region.MinY,
                Info2MaxX = definition.Info2Region.MaxX,
                Info2MaxY = definition.Info2Region.MaxY,
                CreatedAt = definition.CreatedAt,
                UpdatedAt = definition.UpdatedAt
            };
        }

        public TitleBlockDefinition ToDefinition()
        {
            return new TitleBlockDefinition
            {
                BlockName = BlockName.Trim(),
                IsAttributeBased = IsAttributeBased,
                PaperName = PaperName?.Trim() ?? "",
                PaperWidthMm = PaperWidthMm,
                PaperHeightMm = PaperHeightMm,
                HasPrintRegion = HasPrintRegion,
                CoordinateMode = string.IsNullOrWhiteSpace(CoordinateMode) ? "Local" : CoordinateMode.Trim(),
                PrintRegion = LocalRectangle.FromPoints(PrintMinX, PrintMinY, PrintMaxX, PrintMaxY),
                TitleRegion = LocalRectangle.FromPoints(TitleMinX, TitleMinY, TitleMaxX, TitleMaxY),
                DrawingNumberRegion = LocalRectangle.FromPoints(NumberMinX, NumberMinY, NumberMaxX, NumberMaxY),
                DateRegion = LocalRectangle.FromPoints(DateMinX, DateMinY, DateMaxX, DateMaxY),
                RevisionRegion = LocalRectangle.FromPoints(RevisionMinX, RevisionMinY, RevisionMaxX, RevisionMaxY),
                PhaseRegion = LocalRectangle.FromPoints(PhaseMinX, PhaseMinY, PhaseMaxX, PhaseMaxY),
                Info1Region = LocalRectangle.FromPoints(Info1MinX, Info1MinY, Info1MaxX, Info1MaxY),
                Info2Region = LocalRectangle.FromPoints(Info2MinX, Info2MinY, Info2MaxX, Info2MaxY),
                CreatedAt = CreatedAt == default ? DateTime.Now : CreatedAt,
                UpdatedAt = DateTime.Now
            };
        }

        public bool HasFiniteNumbers()
        {
            return IsFinite(PaperWidthMm)
                && IsFinite(PaperHeightMm)
                && IsFinite(PrintMinX)
                && IsFinite(PrintMinY)
                && IsFinite(PrintMaxX)
                && IsFinite(PrintMaxY)
                && IsFinite(TitleMinX)
                && IsFinite(TitleMinY)
                && IsFinite(TitleMaxX)
                && IsFinite(TitleMaxY)
                && IsFinite(NumberMinX)
                && IsFinite(NumberMinY)
                && IsFinite(NumberMaxX)
                && IsFinite(NumberMaxY)
                && IsFinite(DateMinX) && IsFinite(DateMinY) && IsFinite(DateMaxX) && IsFinite(DateMaxY)
                && IsFinite(RevisionMinX) && IsFinite(RevisionMinY) && IsFinite(RevisionMaxX) && IsFinite(RevisionMaxY)
                && IsFinite(PhaseMinX) && IsFinite(PhaseMinY) && IsFinite(PhaseMaxX) && IsFinite(PhaseMaxY)
                && IsFinite(Info1MinX) && IsFinite(Info1MinY) && IsFinite(Info1MaxX) && IsFinite(Info1MaxY)
                && IsFinite(Info2MinX) && IsFinite(Info2MinY) && IsFinite(Info2MaxX) && IsFinite(Info2MaxY);
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}
