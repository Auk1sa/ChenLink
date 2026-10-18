namespace ChenLink.ViewModels;

public enum StatusTone { Idle, Info, Success, Warning, Danger }

/// <summary>
/// 界面状态快照：整块重建后一次性通知，避免十几个零散属性各自刷新导致的闪烁与竞态。
/// 文案、指标、下一步指引、按钮可用性全部由这里派生，页面只负责展示。
/// </summary>
public sealed class SessionStatus
{
    public string Title { get; init; } = "未连接";
    public string Detail { get; init; } = "";
    public StatusTone Tone { get; init; } = StatusTone.Idle;
    public bool ShowProgress { get; init; }
    public bool HasSession { get; init; }

    public string Mode { get; init; } = "—";
    public string Latency { get; init; } = "—";
    public string Channels { get; init; } = "0";
    public string Up { get; init; } = "0 B";
    public string Down { get; init; } = "0 B";

    /// <summary>下一步要点（进游戏填什么 / 把什么发给好友）。</summary>
    public string NextStep { get; init; } = "";

    /// <summary>可直接复制的地址或房间码。</summary>
    public string Address { get; init; } = "";
    public string AddressLabel { get; init; } = "复制";
    public bool HasAddress { get; init; }

    public bool IsIdle => Tone == StatusTone.Idle;
    public bool IsInfo => Tone == StatusTone.Info;
    public bool IsSuccess => Tone == StatusTone.Success;
    public bool IsWarning => Tone == StatusTone.Warning;
    public bool IsDanger => Tone == StatusTone.Danger;
}
