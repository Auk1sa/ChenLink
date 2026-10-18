using System.IO;
using ChenLink.Services;
using ChenLink.ViewModels;
using Microsoft.UI.Xaml;

namespace ChenLink;

public sealed partial class App : Application
{
    public static Window? MainWindow { get; private set; }
    public static SettingsService Settings { get; private set; } = null!;
    public static LogService Log { get; private set; } = null!;
    public static ThemeService Theme { get; private set; } = null!;
    public static MainViewModel ViewModel { get; private set; } = null!;

    static readonly SingleInstance Instance = new();

    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) =>
        {
            Log?.Error("未处理异常：" + e.Exception);
            e.Handled = false;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log?.Error("后台线程异常：" + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 单实例：重复启动时把已有窗口拉到前台，避免两个进程抢同一个端口 / 两份 easytier-core
        if (!Instance.TryAcquire())
        {
            SingleInstance.ActivateExisting();
            Exit();
            return;
        }

        Settings = new SettingsService();
        Log = new LogService();
        Theme = new ThemeService();

        var connect = new ConnectViewModel(Settings, Log);
        ViewModel = new MainViewModel(connect, new DiagnosticsViewModel(Log, connect), new SettingsViewModel(Settings, Theme, Log));

        Log.Info("=== ChenLink 启动 ===");
        Log.Info($"配置目录：{Path.GetDirectoryName(Settings.FilePath)}");

        try
        {
            MainWindow = new MainWindow();
        }
        catch (Microsoft.UI.Xaml.Markup.XamlParseException ex)
        {
            // WinUI 未打包模式的资源是按「可执行文件名」定位的（ChenLink.exe → ChenLink.pri），
            // 单文件版被改名后（含浏览器重复下载产生的 "ChenLink (1).exe"）资源就找不到了。
            // 这种情况界面已经起不来，只能退回 Win32 消息框说清楚，别让用户对着闪退发呆。
            Log.Error("界面资源加载失败：" + ex.Message);
            Core.NativeMessageBox.Error(
                "ChenLink 启动失败：找不到界面资源。\n\n" +
                "最常见的原因是程序文件被改了名字。ChenLink 的界面资源与文件名绑定，" +
                "必须保持为 ChenLink.exe（去掉 \"(1)\"、\"- 副本\" 之类的后缀）后再运行。\n\n" +
                $"当前文件名：{Path.GetFileName(Environment.ProcessPath ?? "?")}\n" +
                "详细日志：" + Log.Directory);
            Exit();
            return;
        }

        Theme.Attach(MainWindow);
        Theme.Apply(Settings.Current.Theme);
        MainWindow.Activate();
    }
}
