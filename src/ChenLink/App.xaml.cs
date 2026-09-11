using System.IO;
using Microsoft.UI.Xaml;

namespace ChenLink;

public sealed partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        // 全局异常兜底：XAML 线程异常 + 非 XAML 线程异常都落盘，便于排查崩溃
        UnhandledException += (_, e) => AppLog.Write("UnhandledException: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            AppLog.Write("AppDomain UnhandledException: " + e.ExceptionObject);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppLog.Write("=== ChenLink 启动 ===");
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}

/// <summary>
/// 极简文件日志：%LOCALAPPDATA%\ChenLink\logs\app-yyyyMMdd.log，按天滚动。
/// 与 UI 日志同步写入，崩溃后可回溯。
/// </summary>
public static class AppLog
{
    static readonly object _lock = new();
    static string? _file;
    static DateTime _date;

    public static void Write(string msg)
    {
        try
        {
            lock (_lock)
            {
                var today = DateTime.Today;
                if (_file is null || _date != today)
                {
                    var dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "ChenLink", "logs");
                    Directory.CreateDirectory(dir);
                    _file = Path.Combine(dir, $"app-{today:yyyyMMdd}.log");
                    _date = today;
                }
                File.AppendAllText(_file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
            }
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
