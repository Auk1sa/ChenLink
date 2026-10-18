using Windows.ApplicationModel.DataTransfer;

namespace ChenLink.Services;

public static class ClipboardHelper
{
    /// <summary>复制文本，返回是否成功（剪贴板偶发被别的程序占用）。</summary>
    public static bool TryCopy(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            package.SetText(text);
            Clipboard.SetContent(package);
            Clipboard.Flush(); // 关掉程序后剪贴板内容仍在
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取剪贴板文本（粘贴邀请码用），失败返回空串。</summary>
    public static async Task<string> TryGetTextAsync()
    {
        try
        {
            var view = Clipboard.GetContent();
            if (!view.Contains(StandardDataFormats.Text)) return "";
            return await view.GetTextAsync();
        }
        catch
        {
            return "";
        }
    }
}
