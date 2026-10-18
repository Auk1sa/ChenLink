using System.Runtime.InteropServices;
using System.Text;

namespace ChenLink.Services;

/// <summary>
/// 单实例守卫。旧版没有这层保护：重复启动会同时拉起两个 easytier-core、抢同一个服务器端口。
/// 已有实例时把它的主窗口拉到前台再退出本次启动。
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string MutexName = @"Local\ChenLink.SingleInstance.v2";
    Mutex? _mutex;

    public bool IsOwner { get; private set; }

    public bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var created);
            IsOwner = created;
        }
        catch
        {
            IsOwner = true; // 无法创建互斥体时不要阻断启动
        }
        return IsOwner;
    }

    public static void ActivateExisting()
    {
        try
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName("ChenLink"))
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;
                ShowWindow(hwnd, SwRestore);
                SetForegroundWindow(hwnd);
                return;
            }
        }
        catch
        {
            // 拉不起来就算了，用户自己点任务栏
        }
    }

    const int SwRestore = 9;

    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    public void Dispose()
    {
        try
        {
            if (IsOwner) _mutex?.ReleaseMutex();
            _mutex?.Dispose();
        }
        catch
        {
            // 退出路径上的异常无需处理
        }
    }
}
