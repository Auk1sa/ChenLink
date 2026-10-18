using System.Diagnostics;
using System.Reflection;
using ChenLink.Core;
using ChenLink.Models;
using ChenLink.Services;
using Microsoft.Win32;

namespace ChenLink.ViewModels;

/// <summary>设置页：外观、行为、身份、默认游戏、日志与关于。全部改动即时落盘。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    const string AutoRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RepoUrl = "https://github.com/Auk1sa/ChenLink";

    readonly SettingsService _settings;
    readonly ThemeService _theme;
    readonly LogService _log;

    public SettingsViewModel(SettingsService settings, ThemeService theme, LogService log)
    {
        _settings = settings;
        _theme = theme;
        _log = log;

        var version = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0);
        VersionText = $"ChenLink v{version.Major}.{version.Minor}.{version.Build}";
        LogPath = log.Directory;

        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        OpenRepoCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true }));
        ResetNodesCommand = new RelayCommand(() =>
        {
            _settings.Update(s => s.EasyTierNodes = "");
            _log.Success("已恢复内置默认节点");
        });
    }

    public IReadOnlyList<GamePreset> Games => GameCatalog.All;

    // ---------- 外观 ----------

    public int ThemeIndex
    {
        get => _settings.Current.Theme;
        set
        {
            if (_settings.Current.Theme == value) return;
            _settings.Update(s => s.Theme = value);
            _theme.Apply(value);
            Raise(nameof(ThemeIndex), nameof(ThemeSystem), nameof(ThemeLight), nameof(ThemeDark));
        }
    }

    public bool ThemeSystem
    {
        get => ThemeIndex == 0;
        set { if (value) ThemeIndex = 0; else OnPropertyChanged(); }
    }

    public bool ThemeLight
    {
        get => ThemeIndex == 1;
        set { if (value) ThemeIndex = 1; else OnPropertyChanged(); }
    }

    public bool ThemeDark
    {
        get => ThemeIndex == 2;
        set { if (value) ThemeIndex = 2; else OnPropertyChanged(); }
    }

    // ---------- 行为 ----------

    public bool AutoStart
    {
        get => _settings.Current.AutoStart;
        set
        {
            if (_settings.Current.AutoStart == value) return;
            _settings.Update(s => s.AutoStart = value);
            ApplyAutoStart(value);
            _log.Success(value ? "已开启开机自启动" : "已关闭开机自启动");
            OnPropertyChanged();
        }
    }

    public bool AutoProbe
    {
        get => _settings.Current.AutoProbe;
        set
        {
            if (_settings.Current.AutoProbe == value) return;
            _settings.Update(s => s.AutoProbe = value);
            OnPropertyChanged();
        }
    }

    public int CloseActionIndex
    {
        get => _settings.Current.CloseAction;
        set
        {
            if (_settings.Current.CloseAction == value) return;
            _settings.Update(s => s.CloseAction = value);
            OnPropertyChanged();
        }
    }

    // ---------- 身份 ----------

    public string Nickname
    {
        get => _settings.Current.Nickname;
        set
        {
            var trimmed = value ?? "";
            if (trimmed.Length > 20) trimmed = trimmed[..20];
            if (_settings.Current.Nickname == trimmed) return;
            _settings.Update(s => s.Nickname = trimmed);
            Raise(nameof(Nickname), nameof(NicknameHint));
        }
    }

    public string NicknameHint => string.IsNullOrWhiteSpace(Nickname)
        ? $"留空则使用计算机名：{Environment.MachineName}"
        : "好友在房间/虚拟网络中看到的名字";

    // ---------- 默认游戏 ----------

    public GamePreset DefaultGame
    {
        get => GameCatalog.ByIdOrDefault(_settings.Current.DefaultGameId);
        set
        {
            if (value is null || DefaultGame.Id == value.Id) return;
            _settings.Update(s =>
            {
                s.DefaultGameId = value.Id;
                if (!value.IsCustom) s.DefaultPort = value.Port;
            });
            Raise(nameof(DefaultGame), nameof(DefaultPort), nameof(DefaultGameHint));
        }
    }

    public double DefaultPort
    {
        get => _settings.Current.DefaultPort;
        set
        {
            var port = (int)Math.Round(value);
            if (double.IsNaN(value) || port is < 1 or > 65535 || _settings.Current.DefaultPort == port) return;
            _settings.Update(s => s.DefaultPort = port);
            OnPropertyChanged();
        }
    }

    public string DefaultGameHint => DefaultGame.IsCustom
        ? "自定义端口，连接页会直接使用这里填的端口"
        : $"{DefaultGame.ProtocolText} · {DefaultGame.Port}，连接页默认选中";

    // ---------- 其他 ----------

    public string VersionText { get; }
    public string LogPath { get; }
    public string NodesSummary => string.IsNullOrWhiteSpace(_settings.Current.EasyTierNodes)
        ? "使用内置默认节点"
        : _settings.Current.EasyTierNodes;

    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand OpenRepoCommand { get; }
    public RelayCommand ResetNodesCommand { get; }

    void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", LogPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开日志目录失败：" + ex.Message);
        }
    }

    /// <summary>开机自启动：写 HKCU Run，无需管理员。</summary>
    void ApplyAutoStart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(AutoRunKey, writable: true);
            if (key is null) return;
            if (on) key.SetValue("ChenLink", "\"" + Environment.ProcessPath + "\"");
            else key.DeleteValue("ChenLink", throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            _log.Warn("写入开机自启动失败：" + ex.Message);
        }
    }
}
