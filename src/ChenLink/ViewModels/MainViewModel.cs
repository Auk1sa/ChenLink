using ChenLink.Core;

namespace ChenLink.ViewModels;

/// <summary>窗口级视图模型：导航选择 + 标题栏状态摘要。三个页面共用同一批子模型，状态不会分叉。</summary>
public sealed class MainViewModel : ObservableObject
{
    int _selectedIndex;

    public MainViewModel(ConnectViewModel connect, DiagnosticsViewModel diagnostics, SettingsViewModel settings)
    {
        Connect = connect;
        Diagnostics = diagnostics;
        Settings = settings;

        Connect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(ConnectViewModel.Status))
            {
                Raise(nameof(TitleStatusText));
            }
        };
    }

    public ConnectViewModel Connect { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public SettingsViewModel Settings { get; }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (!SetProperty(ref _selectedIndex, value)) return;
            Raise(nameof(IsConnectPage), nameof(IsDiagnosticsPage), nameof(IsSettingsPage));
        }
    }

    public bool IsConnectPage => _selectedIndex == 0;
    public bool IsDiagnosticsPage => _selectedIndex == 1;
    public bool IsSettingsPage => _selectedIndex == 2;

    /// <summary>标题栏右侧的迷你状态，任何时候都能看到连接进展。</summary>
    public string TitleStatusText => Connect.Status.Title;
}
