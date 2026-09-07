using System;

namespace ZwcadBatchPlot;

/// <summary>
/// WPF 启动器：进程内首次创建任何 ElementHost/WPF 控件前调用，
/// 确保 WPF Application 单次初始化，避免每个对话框各持一份初始化逻辑。
/// </summary>
public static class WpfBootstrapper
{
    private static bool _initialized;

    public static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        if (System.Windows.Application.Current == null)
        {
            new System.Windows.Application();
        }
    }
}
