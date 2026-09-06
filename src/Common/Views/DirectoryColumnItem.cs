using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZwcadBatchPlot;

/// <summary>
/// 目录列设置行模型：驱动 WPF DataGrid 与顺序预览，属性变更即时通知 UI 刷新。
/// Key 与 DirectoryColumnSetting.Key 对应；自定义行 Key 形如 Custom1/Custom2。
/// 列宽保留用户原始输入（WidthText），保存时统一解析校验，与旧版行为一致。
/// </summary>
public sealed class DirectoryColumnItem : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _centered = true;
    private string _header = "";
    private string _widthText = "";
    private string _customText = "";

    public string Key { get; set; } = "";

    /// <summary>是否为自定义行；自定义行的目录列名与自定义内容可编辑。</summary>
    public bool IsCustom { get; set; }

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool Centered
    {
        get => _centered;
        set => SetField(ref _centered, value);
    }

    public string Header
    {
        get => _header;
        set => SetField(ref _header, value);
    }

    public string WidthText
    {
        get => _widthText;
        set => SetField(ref _widthText, value);
    }

    public string CustomText
    {
        get => _customText;
        set => SetField(ref _customText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
