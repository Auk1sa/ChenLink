namespace ChenLink.Models;

public enum GameProtocol { Tcp, Udp, Both }

/// <summary>
/// 常用局域网游戏预设。旧版把 24 个 ComboBoxItem 在 XAML 里硬编码了两遍（两块面板各一份），
/// 现在集中为唯一数据源，界面用 ItemsSource 绑定，新增游戏只改这一处。
/// </summary>
public sealed class GamePreset
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public int Port { get; init; }
    public GameProtocol Protocol { get; init; } = GameProtocol.Tcp;
    /// <summary>补充说明，例如「联机 MOD」「TCP 8888 也可」。</summary>
    public string Note { get; init; } = "";

    public string ProtocolText => Protocol switch
    {
        GameProtocol.Tcp => "TCP",
        GameProtocol.Udp => "UDP",
        _ => "TCP/UDP",
    };

    public string PortText => $"{ProtocolText} {Port}";

    public string DisplayName => $"{Name} · {PortText}";

    /// <summary>自定义端口：不预设端口，用户手填。</summary>
    public bool IsCustom => Port == 0;

    public override string ToString() => DisplayName;
}

public static class GameCatalog
{
    /// <summary>按热度排序；最后一行为「自定义端口」。</summary>
    public static IReadOnlyList<GamePreset> All { get; } = new List<GamePreset>
    {
        new() { Id = "mc-java",      Name = "我的世界 Java 版", Port = 25565, Protocol = GameProtocol.Tcp },
        new() { Id = "mc-bedrock",   Name = "我的世界 基岩版", Port = 19132, Protocol = GameProtocol.Udp },
        new() { Id = "dsp",          Name = "戴森球计划",      Port = 8469,  Protocol = GameProtocol.Tcp, Note = "Nebula 联机 MOD" },
        new() { Id = "terraria",     Name = "泰拉瑞亚",        Port = 7777,  Protocol = GameProtocol.Tcp },
        new() { Id = "dst",          Name = "饥荒联机版",      Port = 10999, Protocol = GameProtocol.Udp },
        new() { Id = "duckov",       Name = "逃离鸭科夫",      Port = 9050,  Protocol = GameProtocol.Udp, Note = "联机 MOD" },
        new() { Id = "satisfactory", Name = "幸福工厂",        Port = 7777,  Protocol = GameProtocol.Udp, Note = "TCP 8888 也用" },
        new() { Id = "vrising",      Name = "夜族崛起",        Port = 9876,  Protocol = GameProtocol.Udp, Note = "查询端口 9877" },
        new() { Id = "pz",           Name = "僵尸毁灭工程",    Port = 16261, Protocol = GameProtocol.Udp, Note = "查询端口 16262" },
        new() { Id = "7dtd",         Name = "七日杀",          Port = 26900, Protocol = GameProtocol.Udp, Note = "26900~26902" },
        new() { Id = "soulmask",     Name = "灵魂面甲",        Port = 8777,  Protocol = GameProtocol.Udp, Note = "查询端口 27015" },
        new() { Id = "scum",         Name = "人渣",            Port = 7777,  Protocol = GameProtocol.Udp, Note = "查询端口 7779" },
        new() { Id = "astroneer",    Name = "异星旅人",        Port = 8777,  Protocol = GameProtocol.Udp },
        new() { Id = "mindustry",    Name = "像素工厂",        Port = 6567,  Protocol = GameProtocol.Both },
        new() { Id = "romestead",    Name = "罗马拓荒录",      Port = 8050,  Protocol = GameProtocol.Udp },
        new() { Id = "dayz",         Name = "DayZ",            Port = 2302,  Protocol = GameProtocol.Udp, Note = "2302~2305" },
        new() { Id = "palworld",     Name = "幻兽帕鲁",        Port = 8211,  Protocol = GameProtocol.Udp },
        new() { Id = "stardew",      Name = "星露谷物语",      Port = 24642, Protocol = GameProtocol.Udp },
        new() { Id = "valheim",      Name = "英灵神殿",        Port = 2456,  Protocol = GameProtocol.Udp, Note = "2456~2458" },
        new() { Id = "factorio",     Name = "异星工厂",        Port = 34197, Protocol = GameProtocol.Tcp },
        new() { Id = "starbound",    Name = "星界边境",        Port = 21025, Protocol = GameProtocol.Tcp },
        new() { Id = "ark",          Name = "方舟：生存进化",  Port = 7777,  Protocol = GameProtocol.Udp, Note = "查询端口 27015" },
        new() { Id = "rust",         Name = "腐蚀",            Port = 28015, Protocol = GameProtocol.Udp },
        new() { Id = "conan",        Name = "流放者柯南",      Port = 7777,  Protocol = GameProtocol.Udp },
        new() { Id = "custom",       Name = "自定义端口",      Port = 0,     Protocol = GameProtocol.Both },
    };

    public static GamePreset Custom => All[^1];

    public static GamePreset ByIdOrDefault(string? id)
        => All.FirstOrDefault(g => g.Id == id) ?? All[0];

    /// <summary>按端口找预设（用于回填上次使用的游戏）。</summary>
    public static GamePreset? ByPort(int port)
        => All.FirstOrDefault(g => !g.IsCustom && g.Port == port);
}
