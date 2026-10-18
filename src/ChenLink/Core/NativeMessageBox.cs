using System.Runtime.InteropServices;

namespace ChenLink.Core;

/// <summary>
/// 原生消息框。用于界面还没起来（或起不来）时的兜底提示 ——
/// 此时 WinUI 的 ContentDialog 不可用，必须走 Win32。
/// </summary>
public static class NativeMessageBox
{
    const uint MB_OK = 0x00000000;
    const uint MB_ICONERROR = 0x00000010;
    const uint MB_SETFOREGROUND = 0x00010000;
    const uint MB_TOPMOST = 0x00040000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int MessageBoxW(nint hWnd, string text, string caption, uint type);

    public static void Error(string text, string caption = "ChenLink")
    {
        try
        {
            MessageBoxW(0, text, caption, MB_OK | MB_ICONERROR | MB_SETFOREGROUND | MB_TOPMOST);
        }
        catch
        {
            // 弹窗失败也没有别的办法了
        }
    }
}
