using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChenLink.Services;

/// <summary>
/// 应用设置。旧版只持久化了 3 个开关，其余全部每次重填（服务器地址、昵称、游戏、端口、节点）。
/// 这里把高频输入项全部纳入持久化，并保留窗口尺寸，第二次启动即是上次的现场。
/// 新增字段一律给默认值，旧配置文件可直接反序列化，无需版本迁移。
/// </summary>
public sealed class AppSettings
{
    // 外观
    public int Theme { get; set; }               // 0=跟随系统 1=浅色 2=深色
    public double WindowWidth { get; set; } = 1180;
    public double WindowHeight { get; set; } = 840;

    // 行为
    public bool AutoStart { get; set; }
    public bool AutoProbe { get; set; } = true;
    public int CloseAction { get; set; }         // 0=询问 1=最小化到托盘 2=退出

    // 玩家资料
    public string Nickname { get; set; } = "";

    // 联机表单现场
    public int LastMode { get; set; }            // 0=免公网 1=自建服务器
    public string Server { get; set; } = "127.0.0.1:9100";
    public int EmbeddedServerPort { get; set; } = 9100;
    public string DefaultGameId { get; set; } = "mc-java";
    public int DefaultPort { get; set; } = 25565;
    public string EasyTierNodes { get; set; } = "";
}

public sealed class SettingsService
{
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    readonly object _lock = new();

    public AppSettings Current { get; private set; } = new();

    public string FilePath { get; }

    /// <summary>任意设置变更（含路径），供主题、托盘等模块即时响应。</summary>
    public event Action? Changed;

    public SettingsService()
    {
        FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChenLink", "settings.json");
        Load();
    }

    void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings(); // 配置损坏就回到默认值，不让程序起不来
        }
        Sanitize();
    }

    void Sanitize()
    {
        if (Current.WindowWidth < 760) Current.WindowWidth = 1180;
        if (Current.WindowHeight < 620) Current.WindowHeight = 840;
        if (Current.Theme is < 0 or > 2) Current.Theme = 0;
        if (Current.CloseAction is < 0 or > 2) Current.CloseAction = 0;
        if (Current.LastMode is < 0 or > 1) Current.LastMode = 0;
        if (Current.EmbeddedServerPort is < 1 or > 65535) Current.EmbeddedServerPort = 9100;
        if (Current.DefaultPort is < 1 or > 65535) Current.DefaultPort = 25565;
    }

    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Save();
        Changed?.Invoke();
    }

    /// <summary>先写临时文件再替换，避免写一半断电导致配置损坏。</summary>
    public void Save()
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch
        {
            // 配置保存失败不影响使用
        }
    }
}
