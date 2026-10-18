using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ChenLink.Services;

/// <summary>
/// 主题：跟随系统 / 浅色 / 深色。旧版把 RequestedTheme="Dark" 写死在根节点上，浅色系统下也是黑窗口。
/// 这里统一从这里下发，并同步系统标题栏按钮的前景/悬停颜色。
/// </summary>
public sealed class ThemeService
{
    Window? _window;

    public bool IsDark { get; private set; }

    public event Action? Changed;

    public void Attach(Window window)
    {
        _window = window;
        if (window.Content is FrameworkElement root)
        {
            root.ActualThemeChanged += (_, _) => ApplyTitleBarColors();
        }
    }

    public void Apply(int mode)
    {
        if (_window?.Content is not FrameworkElement root) return;

        root.RequestedTheme = mode switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        IsDark = root.ActualTheme == ElementTheme.Dark;
        ApplyTitleBarColors();
        Changed?.Invoke();
    }

    /// <summary>系统标题栏按钮着色：透明底 + 与主题相反的前景，融入自绘顶栏。</summary>
    void ApplyTitleBarColors()
    {
        if (_window?.Content is FrameworkElement root)
            IsDark = root.ActualTheme == ElementTheme.Dark;

        try
        {
            var tb = _window?.AppWindow.TitleBar;
            if (tb is null) return;

            var fg = IsDark ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(235, 24, 28, 36);
            var fgInactive = IsDark ? Color.FromArgb(120, 255, 255, 255) : Color.FromArgb(110, 24, 28, 36);

            tb.BackgroundColor = Colors.Transparent;
            tb.InactiveBackgroundColor = Colors.Transparent;
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = fg;
            tb.ButtonInactiveForegroundColor = fgInactive;
            tb.ButtonHoverBackgroundColor = IsDark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
            tb.ButtonHoverForegroundColor = fg;
            tb.ButtonPressedBackgroundColor = IsDark ? Color.FromArgb(60, 255, 255, 255) : Color.FromArgb(36, 0, 0, 0);
            tb.ButtonPressedForegroundColor = fg;
            tb.ForegroundColor = fg;
            tb.InactiveForegroundColor = fgInactive;
        }
        catch
        {
            // 老系统不支持自定义标题栏颜色，忽略
        }
    }
}
