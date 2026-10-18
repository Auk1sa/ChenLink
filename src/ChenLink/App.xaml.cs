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

        MainWindow = new MainWindow();
        Theme.Attach(MainWindow);
        Theme.Apply(Settings.Current.Theme);
        MainWindow.Activate();
    }
}
