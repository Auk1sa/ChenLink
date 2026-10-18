using System.Runtime.InteropServices;
using ChenLink.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace ChenLink;

public sealed partial class MainWindow : Window
{
    readonly TrayService _tray = new();
    readonly SettingsService _settings;

    bool _exiting;
    bool _closePromptOpen;
    bool _windowReady;

    public MainWindow()
    {
        InitializeComponent();

        _settings = App.Settings;
        if (Content is FrameworkElement root) root.DataContext = App.ViewModel;

        SetupTitleBar();
        SetupMinSize();

        _tray.Initialize();
        _tray.ShowRequested += () => DispatcherQueue.TryEnqueue(RestoreFromTray);
        _tray.ExitRequested += () => DispatcherQueue.TryEnqueue(() => _ = ExitAsync());

        AppWindow.Closing += OnClosing;
        Closed += OnClosed;
        Activated += OnActivated;

        Nav.SelectedItem = Nav.MenuItems[0];

        // 外壳自适应：宽屏展开侧栏，中等宽度收成图标轨道，窄屏走汉堡浮层
        RootGrid.SizeChanged += (_, e) => ApplyShellLayout(e.NewSize.Width);
        ApplyShellLayout(RootGrid.ActualWidth);
    }

    /// <summary>
    /// 导航栏形态切换。
    /// 不能用 PaneDisplayMode="Auto"：实测它在宽窗口下会让内容区按整窗宽度布局并整体右移，
    /// 1120 宽的页面会被右边界裁掉。显式指定模式，行为可预测。
    /// </summary>
    void ApplyShellLayout(double width)
    {
        if (width <= 0) return;

        // 1120 起才展开侧栏：低于这个宽度时内容区会挤到 900 以下，表单要堆叠，
        // 这时候留一条 48px 图标轨道比带着 212px 侧栏更实用。
        var mode = width >= 1120 ? NavigationViewPaneDisplayMode.Left
                 : width >= 700 ? NavigationViewPaneDisplayMode.LeftCompact
                 : NavigationViewPaneDisplayMode.LeftMinimal;

        if (Nav.PaneDisplayMode == mode) return;
        Nav.PaneDisplayMode = mode;
        Nav.IsPaneOpen = mode == NavigationViewPaneDisplayMode.Left;
    }

    // ---------------- 标题栏 ----------------

    void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragRegion);

        try
        {
            var bar = AppWindow.TitleBar;
            if (AppWindowTitleBar.IsCustomizationSupported())
            {
                bar.ExtendsContentIntoTitleBar = true;
                try { bar.PreferredHeightOption = TitleBarHeightOption.Tall; } catch { /* 老版本没有 Tall */ }
                // 顶栏高度跟系统实际标题栏高度对齐，避免三键与自绘内容错位
                TitleRow.Height = new GridLength(bar.Height);
                TitleDragRegion.Height = bar.Height;
                TitleBarInset.Width = bar.RightInset;                  // 给系统三键留位
            }
        }
        catch
        {
            // 旧系统不支持自定义标题栏高度，用默认高度
        }
    }

    void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_windowReady) return;
        _windowReady = true;

        // 窗口激活后再改尺寸。构造函数里改会让 XAML 内容区（island）跟不上窗口，
        // 底部会留出一条不绘制内容的空白。
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, RestoreWindowSize);

        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            TrayService.SetWindowIcon(hwnd);
        }
        catch
        {
            // 图标失败不影响功能
        }
    }

    // ---------------- 窗口尺寸 ----------------

    void SetupMinSize()
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter) return;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) is var dpi && dpi >= 96 ? dpi / 96.0 : 1.0;
        presenter.PreferredMinimumWidth = (int)Math.Round(760 * scale);
        presenter.PreferredMinimumHeight = (int)Math.Round(620 * scale);
    }

    void RestoreWindowSize()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) is var dpi && dpi >= 96 ? dpi / 96.0 : 1.0;

            var width = (int)Math.Round(_settings.Current.WindowWidth * scale);
            var height = (int)Math.Round(_settings.Current.WindowHeight * scale);

            // 换显示器或分辨率变小后，旧尺寸可能放不下；按工作区收一下并居中，
            // 否则窗口底部会跑到屏幕外（任务栏那一截看不见也点不到）。
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            var margin = (int)Math.Round(24 * scale);
            width = Math.Clamp(width, 320, Math.Max(work.Width - margin, 320));
            height = Math.Clamp(height, 240, Math.Max(work.Height - margin, 240));

            AppWindow.MoveAndResize(new RectInt32(
                work.X + (work.Width - width) / 2,
                work.Y + (work.Height - height) / 2,
                width, height));
        }
        catch
        {
            // 尺寸恢复失败就用系统默认
        }
    }

    void SaveWindowSize()
    {
        try
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
            {
                return; // 最小化/最大化时记录尺寸没有意义
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var scale = GetDpiForWindow(hwnd) is var dpi && dpi >= 96 ? dpi / 96.0 : 1.0;
            var size = AppWindow.Size;

            _settings.Update(s =>
            {
                s.WindowWidth = Math.Max(760, size.Width / scale);
                s.WindowHeight = Math.Max(620, size.Height / scale);
            });
        }
        catch
        {
            // 忽略
        }
    }

    // ---------------- 关闭策略 ----------------

    void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting) return;
        args.Cancel = true;
        RequestClose();
    }

    void RequestClose()
    {
        switch (_settings.Current.CloseAction)
        {
            case 1:
                AppWindow.Hide();
                return;
            case 2:
                _ = ExitAsync();
                return;
        }

        if (!_closePromptOpen) _ = PromptCloseAsync();
    }

    async Task PromptCloseAsync()
    {
        _closePromptOpen = true;
        try
        {
            var remember = new CheckBox { Content = "记住我的选择" };
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "关闭 ChenLink",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock { Text = "仍有人可能通过房间码连进来。要退出，还是留在托盘里？" },
                        remember,
                    },
                },
                PrimaryButtonText = "最小化到托盘",
                SecondaryButtonText = "完全退出",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.None) return;

            var action = result == ContentDialogResult.Primary ? 1 : 2;
            if (remember.IsChecked == true)
            {
                _settings.Update(s => s.CloseAction = action);
                App.ViewModel.Settings.CloseActionIndex = action;
            }

            if (action == 1) AppWindow.Hide();
            else await ExitAsync();
        }
        finally
        {
            _closePromptOpen = false;
        }
    }

    async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;

        SaveWindowSize();
        _tray.Dispose();

        try { await App.ViewModel.Connect.ShutdownAsync(); } catch { }
        Close();
    }

    void OnClosed(object sender, WindowEventArgs args)
    {
        _tray.Dispose();
    }

    void RestoreFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized)
            presenter.Restore();
        Activate();
    }

    // ---------------- 导航 ----------------

    void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem item
            && item.Tag is string tag
            && int.TryParse(tag, out var index))
        {
            App.ViewModel.SelectedIndex = index;
        }
    }

    [DllImport("user32.dll")]
    static extern uint GetDpiForWindow(IntPtr hwnd);
}
