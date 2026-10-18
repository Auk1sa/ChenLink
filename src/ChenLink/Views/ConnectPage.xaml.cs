using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChenLink.Views;

public sealed partial class ConnectPage : UserControl
{
    /// <summary>低于这个宽度就把「房主 / 玩家」两列堆叠，避免输入框被挤成一条缝。</summary>
    const double NarrowBreakpoint = 900;

    bool? _narrow;

    public ConnectPage()
    {
        InitializeComponent();

        // 首屏焦点会落到第一个输入框并把页面滚下去，加载完成后强制回到顶部
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            PageScroll.ChangeView(null, 0, null, true);
            PageLayout.Cap(RootLayout, PageScroll, 1120);
        });

        RootLayout.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        PageScroll.SizeChanged += (_, _) => PageLayout.Cap(RootLayout, PageScroll, 1120);
    }

    /// <summary>
    /// 窄屏重排：两列表单 → 单列，状态卡按钮移到文案下方，模式卡片上下排列。
    /// 这里用代码而不是 VisualState —— 模板里的 AdaptiveTrigger 不随窗口宽度变化触发。
    /// </summary>
    void ApplyLayout(double width)
    {
        var narrow = width < NarrowBreakpoint;
        if (_narrow == narrow) return;
        _narrow = narrow;

        StackForms(EasyHostCard, EasyJoinCard, narrow);
        StackForms(RelayHostCard, RelayJoinCard, narrow);

        Grid.SetRow(RelayModeCard, narrow ? 1 : 0);
        Grid.SetColumn(RelayModeCard, narrow ? 0 : 1);
        Grid.SetColumnSpan(RelayModeCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(EasyModeCard, narrow ? 2 : 1);

        Grid.SetRow(StatusActions, narrow ? 1 : 0);
        Grid.SetColumn(StatusActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(StatusActions, narrow ? 2 : 1);
        StatusActions.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        StatusActions.Margin = narrow ? new Thickness(0, 14, 0, 0) : new Thickness(0);
    }

    static void StackForms(FrameworkElement host, FrameworkElement join, bool narrow)
    {
        Grid.SetRow(host, 0);
        Grid.SetColumn(host, 0);
        Grid.SetColumnSpan(host, narrow ? 2 : 1);

        Grid.SetRow(join, narrow ? 1 : 0);
        Grid.SetColumn(join, narrow ? 0 : 1);
        Grid.SetColumnSpan(join, narrow ? 2 : 1);
    }
}
