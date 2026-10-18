using System.Diagnostics;
using System.Reflection;
using ChenLink.Core;
using ChenLink.Services;

namespace ChenLink.ViewModels;

/// <summary>诊断页：会话指标 + 分级日志 + 环境信息。日志可筛选、复制、打开目录。</summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    readonly LogService _log;

    public DiagnosticsViewModel(LogService log, ConnectViewModel connect)
    {
        _log = log;
        Connection = connect;

        CopyLogCommand = new RelayCommand(() =>
        {
            if (ClipboardHelper.TryCopy(_log.Dump())) _log.Success("已复制全部日志");
            else _log.Warn("复制日志失败");
        });

        ClearLogCommand = new RelayCommand(() =>
        {
            _log.Clear();
            _log.Info("日志已清空");
        });

        OpenFolderCommand = new RelayCommand(OpenFolder);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        VersionText = $"v{version.Major}.{version.Minor}.{version.Build}";
        Version = version;
        LogPath = log.Directory;
        LocalIpText = LocalNetwork.IPv4() is { Length: > 0 } ips ? string.Join("、", ips) : "未发现局域网 IP";
        AdminText = EasyTier.EasyTierRunner.IsAdministrator ? "已获得管理员权限" : "非管理员运行（免公网模式需要）";
    }

    public ConnectViewModel Connection { get; }
    public LogService Log => _log;

    public string VersionText { get; }
    public Version Version { get; }
    public string LogPath { get; }
    public string LocalIpText { get; }
    public string AdminText { get; }
    public bool IsAdmin => EasyTier.EasyTierRunner.IsAdministrator;

    public RelayCommand CopyLogCommand { get; }
    public RelayCommand ClearLogCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }

    // 日志级别筛选：用三个 ToggleButton 呈现，互斥由这里保证
    public bool ShowAll
    {
        get => _log.Filter == LogFilter.All;
        set { if (value) Apply(LogFilter.All); else OnPropertyChanged(); }
    }

    public bool ShowWarning
    {
        get => _log.Filter == LogFilter.Warning;
        set { if (value) Apply(LogFilter.Warning); else OnPropertyChanged(); }
    }

    public bool ShowError
    {
        get => _log.Filter == LogFilter.Error;
        set { if (value) Apply(LogFilter.Error); else OnPropertyChanged(); }
    }

    void Apply(LogFilter filter)
    {
        _log.SetFilter(filter);
        Raise(nameof(ShowAll), nameof(ShowWarning), nameof(ShowError));
    }

    void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开日志目录失败：" + ex.Message);
        }
    }

    void CopyDiagnostics()
    {
        var s = Connection.Status;
        var text = string.Join(Environment.NewLine,
            "ChenLink 诊断信息",
            $"版本：{VersionText}",
            $"时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}",
            $"系统：{Environment.OSVersion}",
            $"管理员：{IsAdmin}",
            $"状态：{s.Title} — {s.Detail}",
            $"模式：{s.Mode}　延迟：{s.Latency}　连接数：{s.Channels}",
            $"流量：↑ {s.Up}　↓ {s.Down}",
            $"局域网 IP：{LocalIpText}",
            $"日志目录：{LogPath}");

        if (ClipboardHelper.TryCopy(text)) _log.Success("已复制诊断信息");
        else _log.Warn("复制诊断信息失败");
    }
}
