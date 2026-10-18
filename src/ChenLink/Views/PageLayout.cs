using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChenLink.Views;

/// <summary>
/// 页面宽度约束。
/// 不要用 XAML 的 MaxWidth + HorizontalAlignment="Stretch"：在 ScrollViewer 里实测会算错居中位置，
/// 内容整体右移一大截并被窗口右边界裁掉。显式给宽度再居中才是可靠的。
/// </summary>
static class PageLayout
{
    /// <summary>按视口宽度给根容器设上限并居中。</summary>
    public static void Cap(FrameworkElement root, ScrollViewer scroll, double maxWidth)
    {
        var viewport = scroll.ViewportWidth > 0 ? scroll.ViewportWidth : scroll.ActualWidth;
        if (viewport <= 0) return;

        var width = Math.Min(viewport, maxWidth);
        // 注意：Width 默认是 NaN，NaN 参与比较恒为 false，必须先判 NaN
        if (double.IsNaN(root.Width) || Math.Abs(root.Width - width) > 0.5)
        {
            root.Width = width;
            root.HorizontalAlignment = HorizontalAlignment.Center;
        }
    }
}
