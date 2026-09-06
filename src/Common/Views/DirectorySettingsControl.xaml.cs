using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace ZwcadBatchPlot;

/// <summary>
/// 图纸目录设置 WPF 控件：参数区 + 目录列表格 + 顺序预览。
/// 行数据由 ObservableCollection 驱动，插入/删除/拖拽均为纯数据操作，
/// 不存在 WinForms DataGridView 的编辑会话与行号恢复问题。
/// </summary>
public sealed partial class DirectorySettingsControl : UserControl
{
    private const string DefaultTextStyleDisplay = "(默认)";
    private const string DefaultPreviewFont = "Microsoft YaHei UI";

    /// <summary>用户点击行内“图中交互”时触发，参数为该列 Key。</summary>
    public event Action<string>? PickColumnWidthRequested;

    /// <summary>用户点击“图中交互”（目录行高）时触发。</summary>
    public event Action? PickRowHeightRequested;

    /// <summary>用户点击“点选目录文字”时触发。</summary>
    public event Action? PickTextAppearanceRequested;

    public ObservableCollection<DirectoryColumnItem> Columns { get; } = new();

    private int _contextRowIndex = -1;
    private int _dragSourceIndex = -1;
    private DirectoryDragAdorner? _dragAdorner;

    public DirectorySettingsControl()
    {
        InitializeComponent();

        for (var index = 0; index <= 256; index++)
        {
            ColorCombo.Items.Add(new AciColorItem(index, GetAciPreviewBrush(index)));
        }

        Columns.CollectionChanged += OnColumnsChanged;
        PreviewHost.SizeChanged += (_, _) => UpdatePreview();
        Loaded += (_, _) => UpdatePreview();
        TextHeightBox.TextChanged += (_, _) => UpdatePreview();
        WidthFactorBox.TextChanged += (_, _) => UpdatePreview();
        RowHeightBox.TextChanged += (_, _) => UpdatePreview();
        StyleCombo.SelectionChanged += (_, _) => UpdatePreview();
    }

    /// <summary>目录设置的整页读出结果，供 SettingsForm 映射回 AppSettings。</summary>
    public sealed class DirectorySettingsSnapshot
    {
        public int ColorIndex;
        public double TextHeight;
        public double TextWidthFactor;
        public double RowHeight;
        public string TextStyleName = "";
        public string LayerName = "0";
        public bool DrawHeader;
        public bool DrawGridLines;
        public List<DirectoryColumnSetting> Columns = new();
    }

    /// <summary>注入文字样式列表（首项应为“（默认）”占位），由 SettingsForm 从 CAD 读取。</summary>
    public void SetTextStyleNames(IReadOnlyList<string> names)
    {
        var items = new List<string> { DefaultTextStyleDisplay };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { DefaultTextStyleDisplay };
        foreach (var name in names)
        {
            var trimmed = name?.Trim() ?? "";
            if (trimmed.Length > 0 && seen.Add(trimmed))
            {
                items.Add(trimmed);
            }
        }

        // 即使当前图纸尚未建立“宋体”文字样式，也先在界面提供该默认项。
        if (!seen.Contains("宋体"))
        {
            items.Insert(1, "宋体");
        }

        StyleCombo.ItemsSource = items;
    }

    /// <summary>整页回填设置值（含目录列），并刷新预览。</summary>
    public void ApplySettings(AppSettings settings)
    {
        ColorCombo.SelectedIndex = Math.Max(0, Math.Min(256, settings.DirectoryColorIndex));
        TextHeightBox.Text = settings.DirectoryTextHeight.ToString("0.##", CultureInfo.CurrentCulture);
        WidthFactorBox.Text = settings.DirectoryTextWidthFactor.ToString("0.##", CultureInfo.CurrentCulture);
        RowHeightBox.Text = settings.DirectoryRowHeight.ToString("0.##", CultureInfo.CurrentCulture);
        LayerBox.Text = settings.DirectoryLayerName;
        DrawHeaderCheck.IsChecked = settings.DirectoryDrawHeader;
        DrawGridLinesCheck.IsChecked = settings.DirectoryDrawGridLines;

        var target = string.IsNullOrWhiteSpace(settings.DirectoryTextStyleName)
            ? DefaultTextStyleDisplay
            : settings.DirectoryTextStyleName;
        var styles = StyleCombo.ItemsSource as List<string> ?? new List<string>();
        StyleCombo.SelectedIndex = Math.Max(0, styles.FindIndex(x =>
            string.Equals(x, target, StringComparison.OrdinalIgnoreCase)));

        Columns.Clear();
        foreach (var column in settings.DirectoryColumns)
        {
            if (column == null || string.IsNullOrWhiteSpace(column.Key))
            {
                continue;
            }

            Columns.Add(new DirectoryColumnItem
            {
                Key = column.Key,
                IsCustom = column.IsCustom,
                Enabled = column.Enabled,
                Centered = column.Centered,
                Header = column.Header,
                WidthText = column.Width.ToString("0.##", CultureInfo.CurrentCulture),
                CustomText = column.CustomText ?? ""
            });
        }

        UpdatePreview();
    }

    /// <summary>
    /// 读取整页设置；校验失败时弹窗提示并定位问题行，返回 false。
    /// 校验规则与旧版一致：列名非空、列宽大于 0、至少启用一列。
    /// </summary>
    public bool TryReadSnapshot(out DirectorySettingsSnapshot snapshot)
    {
        snapshot = new DirectorySettingsSnapshot
        {
            ColorIndex = ColorCombo.SelectedItem is AciColorItem item ? item.Index : 7,
            LayerName = string.IsNullOrWhiteSpace(LayerBox.Text) ? "0" : LayerBox.Text.Trim(),
            DrawHeader = DrawHeaderCheck.IsChecked == true,
            DrawGridLines = DrawGridLinesCheck.IsChecked == true,
            TextStyleName = StyleCombo.SelectedItem as string == DefaultTextStyleDisplay
                || StyleCombo.SelectedItem is null
                ? ""
                : StyleCombo.SelectedItem.ToString() ?? ""
        };

        if (!TryParseNumber(TextHeightBox.Text, 1, 1000000, out var textHeight))
        {
            Warn("请输入有效的目录文字高度（1 ~ 1000000）。", TextHeightBox);
            return false;
        }
        if (!TryParseNumber(WidthFactorBox.Text, 0.1, 10, out var widthFactor))
        {
            Warn("请输入有效的宽度因子（0.1 ~ 10）。", WidthFactorBox);
            return false;
        }
        if (!TryParseNumber(RowHeightBox.Text, 1, 1000000, out var rowHeight))
        {
            Warn("请输入有效的目录行高（1 ~ 1000000）。", RowHeightBox);
            return false;
        }

        snapshot.TextHeight = textHeight;
        snapshot.TextWidthFactor = widthFactor;
        snapshot.RowHeight = rowHeight;

        foreach (var column in Columns)
        {
            var header = column.Header?.Trim() ?? "";
            if (header.Length == 0)
            {
                Warn("目录列名不能为空。", column);
                return false;
            }

            if (!TryParseNumber(column.WidthText, 0, double.MaxValue, out var width) || width <= 0)
            {
                Warn($"目录列“{header}”的列宽必须大于 0。", column);
                return false;
            }

            snapshot.Columns.Add(new DirectoryColumnSetting
            {
                Key = column.Key,
                Header = header,
                Enabled = column.Enabled,
                Centered = column.Centered,
                Width = width,
                IsCustom = column.IsCustom,
                CustomText = (column.CustomText ?? "").Trim()
            });
        }

        if (!snapshot.Columns.Any(x => x.Enabled))
        {
            Warn("请至少启用一个目录字段。", null);
            return false;
        }

        return true;
    }

    private void Warn(string message, object? target)
    {
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
        if (owner != null)
        {
            System.Windows.MessageBox.Show(owner, message, "批量打印设置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
        else
        {
            System.Windows.MessageBox.Show(message, "批量打印设置",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }

        if (target is DirectoryColumnItem column)
        {
            // 定位问题行：选中并滚动到可见。
            ColumnGrid.SelectedItem = column;
            ColumnGrid.ScrollIntoView(column);
        }
    }

    /// <summary>双文化解析数字（当前文化优先，失败退回 InvariantCulture）。</summary>
    private static bool TryParseFlexible(string text, out double value)
    {
        value = 0;
        var trimmed = text?.Trim() ?? "";
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseNumber(string text, double min, double max, out double value)
    {
        if (!TryParseFlexible(text, out value))
        {
            return false;
        }

        // 与旧版 NumericUpDown 一致：超界值收拢到范围边界。
        value = Math.Max(min, Math.Min(max, value));
        return true;
    }

    // ---------- 集合与预览 ----------

    private void OnColumnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (DirectoryColumnItem item in e.OldItems)
            {
                item.PropertyChanged -= OnColumnItemChanged;
            }
        }

        if (e.NewItems != null)
        {
            foreach (DirectoryColumnItem item in e.NewItems)
            {
                item.PropertyChanged += OnColumnItemChanged;
            }
        }

        UpdatePreview();
    }

    private void OnColumnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DirectoryColumnItem.Enabled)
            or nameof(DirectoryColumnItem.Header)
            or nameof(DirectoryColumnItem.WidthText))
        {
            UpdatePreview();
        }
    }

    /// <summary>
    /// 重建顺序预览：列宽/行高共用同一缩放比例，字高按与目录生成一致的限幅公式换算。
    /// </summary>
    private void UpdatePreview()
    {
        if (PreviewHost == null)
        {
            return;
        }

        PreviewHost.Child = null;
        var hostWidth = PreviewHost.RenderSize.Width;
        var hostHeight = PreviewHost.RenderSize.Height;
        if (hostWidth <= 0 || hostHeight <= 0)
        {
            return;
        }

        var parsed = new List<(DirectoryColumnItem Item, double Width)>();
        foreach (var column in Columns)
        {
            if (!column.Enabled
                || !TryParseFlexible(column.WidthText, out var width)
                || width <= 0)
            {
                continue;
            }

            parsed.Add((column, width));
        }

        if (parsed.Count == 0)
        {
            PreviewHost.Child = new TextBlock
            {
                Text = "请勾选需要生成的目录列",
                Foreground = Brushes.DimGray,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            return;
        }

        if (!TryParseFlexible(RowHeightBox.Text, out var rowHeight))
        {
            rowHeight = 1;
        }
        rowHeight = Math.Max(1, rowHeight);

        if (!TryParseFlexible(TextHeightBox.Text, out var textHeight))
        {
            textHeight = 1;
        }
        textHeight = Math.Max(1, textHeight);

        if (!TryParseFlexible(WidthFactorBox.Text, out var widthFactor))
        {
            widthFactor = 0.7;
        }
        widthFactor = Math.Max(0.1, widthFactor);

        var totalWidth = parsed.Sum(x => x.Width);
        var padding = 6.0;
        var availableWidth = Math.Max(1, hostWidth - padding * 2);
        var availableHeight = Math.Max(1, hostHeight - padding * 2);
        var scale = Math.Min(availableWidth / totalWidth, availableHeight / rowHeight);
        var previewWidth = totalWidth * scale;
        var previewHeight = rowHeight * scale;
        var x = (hostWidth - previewWidth) / 2;
        var y = (hostHeight - previewHeight) / 2;

        var styleName = StyleCombo.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(styleName) || styleName == DefaultTextStyleDisplay)
        {
            styleName = DefaultPreviewFont;
        }

        FontFamily previewFont;
        try
        {
            previewFont = new FontFamily(styleName);
        }
        catch
        {
            previewFont = new FontFamily(DefaultPreviewFont);
        }

        var canvas = new Canvas();
        var lineBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70));
        lineBrush.Freeze();
        var linePen = new Pen(lineBrush, 1);
        linePen.Freeze();

        foreach (var (item, width) in parsed)
        {
            var cellWidth = width * scale;
            var cell = new Rect(x, y, cellWidth, previewHeight);

            var border = new Border
            {
                Width = cellWidth,
                Height = previewHeight,
                BorderBrush = new SolidColorBrush(Color.FromRgb(70, 70, 70)),
                BorderThickness = new Thickness(1)
            };
            Canvas.SetLeft(border, x);
            Canvas.SetTop(border, y);
            canvas.Children.Add(border);

            // 与目录生成逻辑相同的限幅：字高不超过文字高度、行高 80% 和列宽可容纳值。
            var header = string.IsNullOrEmpty(item.Header) ? " " : item.Header;
            var byRow = rowHeight * 0.8;
            var byWidth = width * 0.9 / Math.Max(1, header.Length * widthFactor);
            var fontPixels = Math.Max(1, Math.Min(textHeight, Math.Min(byRow, byWidth)) * scale);

            var text = new TextBlock
            {
                Text = header,
                FontFamily = previewFont,
                FontSize = fontPixels,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Brushes.Black
            };
            if (item.Centered)
            {
                text.TextAlignment = TextAlignment.Center;
                text.Width = cellWidth;
                text.HorizontalAlignment = HorizontalAlignment.Center;
            }

            var textHost = new Canvas();
            textHost.Children.Add(text);
            Canvas.SetLeft(text, x);
            Canvas.SetTop(text, y);
            canvas.Children.Add(textHost);

            x += cellWidth;
        }

        PreviewHost.Child = canvas;
    }

    // ---------- 行内按钮（图中交互/上移/下移） ----------

    private void OnGridButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not Button button
            || button.DataContext is not DirectoryColumnItem item)
        {
            return;
        }

        var index = Columns.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        switch (button.Tag as string)
        {
            case "PickWidth":
                PickColumnWidthRequested?.Invoke(item.Key);
                break;
            case "MoveUp":
                if (index > 0)
                {
                    Columns.Move(index, index - 1);
                    ColumnGrid.SelectedItem = item;
                }
                break;
            case "MoveDown":
                if (index < Columns.Count - 1)
                {
                    Columns.Move(index, index + 1);
                    ColumnGrid.SelectedItem = item;
                }
                break;
        }
    }

    // ---------- 右键菜单 ----------

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _contextRowIndex = HitTestRowIndex(Mouse.GetPosition(ColumnGrid));
        DeleteMenuItem.IsEnabled = _contextRowIndex >= 0;
    }

    private void OnInsertCustomAbove(object sender, RoutedEventArgs e)
    {
        InsertCustomRow(_contextRowIndex >= 0 ? _contextRowIndex : 0);
    }

    private void OnInsertCustomBelow(object sender, RoutedEventArgs e)
    {
        InsertCustomRow(_contextRowIndex >= 0 ? _contextRowIndex + 1 : Columns.Count);
    }

    private void OnDeleteCustomRow(object sender, RoutedEventArgs e)
    {
        if (_contextRowIndex < 0 || _contextRowIndex >= Columns.Count)
        {
            return;
        }

        Columns.RemoveAt(_contextRowIndex);
    }

    /// <summary>在指定位置插入一个自定义行；新行列名、内容均可编辑，默认启用。</summary>
    private void InsertCustomRow(int insertIndex)
    {
        insertIndex = Math.Max(0, Math.Min(insertIndex, Columns.Count));
        var item = new DirectoryColumnItem
        {
            Key = NextCustomColumnKey(),
            IsCustom = true,
            Enabled = true,
            Centered = false,
            Header = "自定义",
            WidthText = "2000",
            CustomText = ""
        };
        Columns.Insert(insertIndex, item);
        ColumnGrid.SelectedItem = item;
        ColumnGrid.ScrollIntoView(item);
    }

    private string NextCustomColumnKey()
    {
        var maxIndex = 0;
        foreach (var column in Columns)
        {
            if (!column.IsCustom || string.IsNullOrWhiteSpace(column.Key))
            {
                continue;
            }
            if (column.Key.Length > "Custom".Length
                && int.TryParse(column.Key.Substring("Custom".Length), out var index))
            {
                maxIndex = Math.Max(maxIndex, index);
            }
        }

        return $"Custom{maxIndex + 1}";
    }

    // ---------- 拖拽排序（Thumb + 插入线 Adorner） ----------

    private void OnRowDragStarted(object sender, DragStartedEventArgs e)
    {
        _dragSourceIndex = RowIndexOfVisual(e.OriginalSource as DependencyObject);
        if (_dragSourceIndex < 0)
        {
            return;
        }

        var layer = AdornerLayer.GetAdornerLayer(ColumnGrid);
        if (layer == null)
        {
            return;
        }

        _dragAdorner = new DirectoryDragAdorner(ColumnGrid);
        layer.Add(_dragAdorner);
        UpdateDragAdorner();
        e.Handled = true;
    }

    private void OnRowDragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_dragAdorner == null)
        {
            return;
        }

        UpdateDragAdorner();
        e.Handled = true;
    }

    private void OnRowDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_dragAdorner != null)
        {
            var layer = AdornerLayer.GetAdornerLayer(ColumnGrid);
            layer?.Remove(_dragAdorner);
            _dragAdorner = null;
        }

        var source = _dragSourceIndex;
        _dragSourceIndex = -1;
        if (source < 0 || source >= Columns.Count)
        {
            return;
        }

        var dropIndex = GetDropInsertIndex(Mouse.GetPosition(ColumnGrid));
        if (dropIndex < 0 || dropIndex > Columns.Count
            || dropIndex == source || dropIndex == source + 1)
        {
            return;
        }

        // Move 语义：插入点在源行之后时，移除源行后目标位置前移一位。
        Columns.Move(source, source < dropIndex ? dropIndex - 1 : dropIndex);
    }

    private void UpdateDragAdorner()
    {
        if (_dragAdorner == null || _dragSourceIndex < 0 || _dragSourceIndex >= Columns.Count)
        {
            return;
        }

        var position = Mouse.GetPosition(ColumnGrid);
        var dropIndex = GetDropInsertIndex(position);
        var lineY = GetInsertionLineY(dropIndex);
        _dragAdorner.Update(position, Columns[_dragSourceIndex].Header, lineY);
    }

    private int RowIndexOfVisual(DependencyObject? visual)
    {
        while (visual != null && !ReferenceEquals(visual, ColumnGrid))
        {
            if (visual is DataGridRow row)
            {
                return ColumnGrid.ItemContainerGenerator.IndexFromContainer(row);
            }

            visual = VisualTreeHelper.GetParent(visual);
        }

        return -1;
    }

    /// <summary>鼠标位置对应的插入位置：行上半部 → 插入该行前；越过最后一行 → 追加到末尾。</summary>
    private int GetDropInsertIndex(Point position)
    {
        var hasContainer = false;
        for (var i = 0; i < Columns.Count; i++)
        {
            if (ColumnGrid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row)
            {
                continue;
            }

            hasContainer = true;
            var top = row.TranslatePoint(new Point(), ColumnGrid).Y;
            if (position.Y < top + row.ActualHeight / 2)
            {
                return i;
            }
        }

        return hasContainer ? Columns.Count : -1;
    }

    private double GetInsertionLineY(int dropIndex)
    {
        if (dropIndex >= 0 && dropIndex < Columns.Count
            && ColumnGrid.ItemContainerGenerator.ContainerFromIndex(dropIndex) is DataGridRow row)
        {
            return row.TranslatePoint(new Point(), ColumnGrid).Y;
        }

        if (Columns.Count > 0
            && ColumnGrid.ItemContainerGenerator.ContainerFromIndex(Columns.Count - 1) is DataGridRow last)
        {
            return last.TranslatePoint(new Point(), ColumnGrid).Y + last.ActualHeight;
        }

        return double.NaN;
    }

    private int HitTestRowIndex(Point position)
    {
        for (var i = 0; i < Columns.Count; i++)
        {
            if (ColumnGrid.ItemContainerGenerator.ContainerFromIndex(i) is not DataGridRow row)
            {
                continue;
            }

            var top = row.TranslatePoint(new Point(), ColumnGrid).Y;
            if (position.Y >= top && position.Y <= top + row.ActualHeight)
            {
                return i;
            }
        }

        return -1;
    }

    // ---------- 参数区按钮 ----------

    private void OnPickTextAppearanceClick(object sender, RoutedEventArgs e)
    {
        PickTextAppearanceRequested?.Invoke();
    }

    private void OnPickRowHeightClick(object sender, RoutedEventArgs e)
    {
        PickRowHeightRequested?.Invoke();
    }

    // ---------- ACI 颜色（与旧版目录颜色下拉一致的预览色） ----------

    private sealed class AciColorItem
    {
        public AciColorItem(int index, Brush brush)
        {
            Index = index;
            Brush = brush;
            Label = index switch
            {
                0 => "0（随块）",
                256 => "256（随层）",
                _ => index.ToString(CultureInfo.InvariantCulture)
            };
        }

        public int Index { get; }
        public Brush Brush { get; }
        public string Label { get; }
    }

    private static Brush GetAciPreviewBrush(int index)
    {
        return new SolidColorBrush(GetAciPreviewColor(index));
    }

    private static Color GetAciPreviewColor(int index)
    {
        var fixedColors = new[]
        {
            Color.FromRgb(105, 105, 105),
            Color.FromRgb(255, 0, 0),
            Color.FromRgb(255, 255, 0),
            Color.FromRgb(0, 255, 0),
            Color.FromRgb(0, 255, 255),
            Color.FromRgb(0, 0, 255),
            Color.FromRgb(255, 0, 255),
            Color.FromRgb(255, 255, 255),
            Color.FromRgb(128, 128, 128),
            Color.FromRgb(192, 192, 192)
        };
        if (index >= 0 && index < fixedColors.Length)
        {
            return fixedColors[index];
        }

        if (index >= 10 && index <= 249)
        {
            // ACI 10～249 每 10 个索引为一个色相组，偶数为纯色、奇数为同亮度的浅色。
            var hue = ((index - 10) / 10) * 15.0;
            var tone = (index - 10) % 10;
            var brightnessLevels = new[] { 255, 255, 165, 165, 127, 127, 76, 76, 38, 38 };
            var saturation = tone % 2 == 0 ? 1.0 : 0.5;
            return ColorFromHsv(hue, saturation, brightnessLevels[tone] / 255.0);
        }

        var grays = new byte[] { 51, 80, 105, 130, 190, 255 };
        if (index >= 250 && index <= 255)
        {
            var gray = grays[index - 250];
            return Color.FromRgb(gray, gray, gray);
        }

        return Color.FromRgb(105, 105, 105);
    }

    private static Color ColorFromHsv(double hue, double saturation, double value)
    {
        var sector = hue / 60.0;
        var wholeSector = (int)Math.Floor(sector) % 6;
        var fraction = sector - Math.Floor(sector);
        var p = value * (1 - saturation);
        var q = value * (1 - fraction * saturation);
        var t = value * (1 - (1 - fraction) * saturation);
        var (red, green, blue) = wholeSector switch
        {
            0 => (value, t, p),
            1 => (q, value, p),
            2 => (p, value, t),
            3 => (p, q, value),
            4 => (t, p, value),
            _ => (value, p, q)
        };
        return Color.FromRgb(
            (byte)Math.Round(red * 255),
            (byte)Math.Round(green * 255),
            (byte)Math.Round(blue * 255));
    }

    /// <summary>拖拽指示：插入线 + 跟随鼠标的半透明行内容预览。</summary>
    private sealed class DirectoryDragAdorner : Adorner
    {
        private Point _position;
        private double _lineY = double.NaN;
        private string _text = "";

        public DirectoryDragAdorner(UIElement adornedElement) : base(adornedElement)
        {
            IsHitTestVisible = false;
        }

        public void Update(Point position, string text, double lineY)
        {
            _position = position;
            _text = text;
            _lineY = lineY;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (!double.IsNaN(_lineY))
            {
                var width = AdornedElement.RenderSize.Width;
                var lineBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9));
                lineBrush.Freeze();
                var pen = new Pen(lineBrush, 2);
                pen.Freeze();
                dc.DrawLine(pen, new Point(0, _lineY), new Point(width, _lineY));
            }

            if (!string.IsNullOrEmpty(_text))
            {
                var typeface = new Typeface("Microsoft YaHei");
                var formatted = new FormattedText(
                    _text,
                    CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    12,
                    Brushes.Black,
                    pixelsPerDip);
                var rect = new Rect(_position.X + 10, _position.Y + 10, formatted.Width + 12, formatted.Height + 6);
                var background = new SolidColorBrush(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));
                background.Freeze();
                var borderPen = new Pen(Brushes.DimGray, 1);
                borderPen.Freeze();
                dc.DrawRectangle(background, borderPen, rect);
                dc.DrawText(formatted, new Point(rect.X + 6, rect.Y + 3));
            }
        }
    }
}
