using System.Runtime.InteropServices;
using H.NotifyIcon.Core;

namespace ChenLink.Services;

/// <summary>托盘图标 + 右键菜单。从旧版 MainWindow 里剥离出来，窗口只负责窗口本身的事。</summary>
public sealed class TrayService : IDisposable
{
    TrayIconWithContextMenu? _icon;

    public event Action? ShowRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        try
        {
            var handle = LoadIconHandle();
            var menu = new PopupMenu();
            menu.Items.Add(new PopupMenuItem("显示主窗口", (_, _) => ShowRequested?.Invoke()));
            menu.Items.Add(new PopupMenuSeparator());
            menu.Items.Add(new PopupMenuItem("退出 ChenLink", (_, _) => ExitRequested?.Invoke()));

            _icon = new TrayIconWithContextMenu
            {
                Icon = handle != IntPtr.Zero ? handle : System.Drawing.SystemIcons.Application.Handle,
                ToolTip = "ChenLink",
                ContextMenu = menu,
            };
            _icon.MessageWindow.MouseEventReceived += (_, e) =>
            {
                if (e.MouseEvent == MouseEvent.IconLeftDoubleClick) ShowRequested?.Invoke();
            };
            _icon.Create();
        }
        catch
        {
            // 托盘不可用（极端环境）不影响主界面
        }
    }

    /// <summary>从内嵌资源解出 ico 并加载为 HICON，保证单文件发布也有正确图标。</summary>
    static IntPtr LoadIconHandle()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChenLink");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "ChenLink.ico");

            var asm = typeof(TrayService).Assembly;
            using var src = asm.GetManifestResourceStream("assets.ChenLink.ico");
            if (src is null) return IntPtr.Zero;
            if (!File.Exists(dest) || new FileInfo(dest).Length != src.Length)
            {
                using var fs = File.Create(dest);
                src.CopyTo(fs);
            }
            return LoadImage(IntPtr.Zero, dest, ImageIcon, 0, 0, LrLoadFromFile);
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>任务栏图标兜底：直接 WM_SETICON 到窗口，单文件解包场景下最稳。</summary>
    public static void SetWindowIcon(IntPtr hwnd)
    {
        try
        {
            var handle = LoadIconHandle();
            if (hwnd == IntPtr.Zero || handle == IntPtr.Zero) return;
            SendMessage(hwnd, WmSetIcon, 1, handle);
            SendMessage(hwnd, WmSetIcon, 0, handle);
        }
        catch
        {
            // 图标设置失败不影响功能
        }
    }

    const int ImageIcon = 1;
    const uint LrLoadFromFile = 0x10;
    const uint WmSetIcon = 0x0080;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public void Dispose()
    {
        try
        {
            _icon?.Dispose();
        }
        catch
        {
            // 退出路径
        }
        _icon = null;
    }
}
