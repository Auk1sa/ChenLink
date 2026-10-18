using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace ChenLink.Views;

public sealed partial class DiagnosticsPage : UserControl
{
    public DiagnosticsPage()
    {
        InitializeComponent();

        // 首屏焦点会落到第一个输入框并把页面滚下去，加载完成后强制回到顶部
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            PageScroll.ChangeView(null, 0, null, true);
            PageLayout.Cap(RootLayout, PageScroll, 1120);
        });

        PageScroll.SizeChanged += (_, _) => PageLayout.Cap(RootLayout, PageScroll, 1120);
    }
}
