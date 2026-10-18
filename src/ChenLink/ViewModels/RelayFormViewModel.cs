using ChenLink.Core;
using ChenLink.Models;

namespace ChenLink.ViewModels;

/// <summary>
/// 自建服务器（旧模式）表单。房主与玩家分成两组字段，各自独立校验，
/// 错误提示就地显示在对应输入框下 —— 旧版只把错误塞进最底部的日志。
/// </summary>
public sealed class RelayFormViewModel : ObservableObject
{
    public IReadOnlyList<GamePreset> Games { get; } = GameCatalog.All;

    GamePreset _game = GameCatalog.All[0];
    string _server = "127.0.0.1:9100";
    double _serverPort = 9100;
    double _hostPort = 25565;
    double _joinPort = 25565;
    string _hostSecret = "";
    string _joinSecret = "";
    string _roomCode = "";
    string _joinCode = "";
    string _hint = "";
    bool _isHosting;
    bool _isJoined;
    bool _embeddedServer;

    public GamePreset SelectedGame
    {
        get => _game;
        set
        {
            if (!SetProperty(ref _game, value)) return;
            if (value is { IsCustom: false })
            {
                HostPortValue = value.Port;
                JoinPortValue = value.IsCustom ? JoinPortValue : value.Port;
            }
            OnPropertyChanged(nameof(GameHint));
        }
    }

    /// <summary>当前游戏的一行说明（协议 + 备注），直接显示在预设下拉框下方。</summary>
    public string GameHint => _game.IsCustom
        ? "自定义端口：直接填游戏实际监听的端口，TCP / UDP 都会转发"
        : $"{_game.ProtocolText} · {_game.Port} 端口" + (string.IsNullOrEmpty(_game.Note) ? "，已自动填入" : $"，{_game.Note}");

    public string Server
    {
        get => _server;
        set { if (SetProperty(ref _server, value)) OnPropertyChanged(nameof(ServerHint)); }
    }

    public string ServerHint => string.IsNullOrWhiteSpace(_server)
        ? "填信令服务器地址，例如 1.2.3.4:9100"
        : "内嵌服务器运行时，好友填「本机IP:" + (int)ServerPortValue + "」";

    public double ServerPortValue
    {
        get => _serverPort;
        set { if (SetProperty(ref _serverPort, value)) OnPropertyChanged(nameof(ServerHint)); }
    }

    public bool EmbeddedServer
    {
        get => _embeddedServer;
        set => SetProperty(ref _embeddedServer, value);
    }

    public double HostPortValue
    {
        get => _hostPort;
        set { if (SetProperty(ref _hostPort, value)) Revalidate(); }
    }

    public double JoinPortValue
    {
        get => _joinPort;
        set { if (SetProperty(ref _joinPort, value)) Revalidate(); }
    }

    public string HostSecret
    {
        get => _hostSecret;
        set { if (SetProperty(ref _hostSecret, value)) OnPropertyChanged(nameof(HostSecretHint)); }
    }

    public string HostSecretHint => string.IsNullOrEmpty(_hostSecret)
        ? "留空则不加密（中继服务器能看到明文）"
        : "已启用端到端加密（AES-GCM），中继只能看到密文";

    public string JoinSecret
    {
        get => _joinSecret;
        set => SetProperty(ref _joinSecret, value);
    }

    public string RoomCode
    {
        get => _roomCode;
        set { if (SetProperty(ref _roomCode, value)) OnPropertyChanged(nameof(HasRoomCode)); }
    }

    public bool HasRoomCode => _roomCode.Length == 5;

    public string JoinCode
    {
        get => _joinCode;
        set
        {
            var normalized = (value ?? "").Trim().ToUpperInvariant();
            if (normalized.Length > 5) normalized = normalized[..5];
            if (SetProperty(ref _joinCode, normalized)) Revalidate();
        }
    }

    public string Hint
    {
        get => _hint;
        set => SetProperty(ref _hint, value);
    }

    public bool IsHosting
    {
        get => _isHosting;
        set => SetProperty(ref _isHosting, value);
    }

    public bool IsJoined
    {
        get => _isJoined;
        set => SetProperty(ref _isJoined, value);
    }

    public string HostError { get; private set; } = "";
    public string JoinError { get; private set; } = "";
    public bool HostValid { get; private set; }
    public bool JoinValid { get; private set; }

    public void Revalidate()
    {
        HostError = IsValidPort(HostPortValue) ? "" : "请填 1–65535 之间的游戏端口";

        // 空输入不报错，避免一进页面就满屏红字；开始输入或点按钮后才提示
        JoinError = JoinCode.Length == 0
            ? ""
            : JoinCode.Length != 5 ? "房间码为 5 位字母数字"
            : !IsValidPort(JoinPortValue) ? "请填 1–65535 之间的本机端口" : "";
        HostValid = HostError.Length == 0;
        JoinValid = JoinError.Length == 0;
        Raise(nameof(HostError), nameof(JoinError), nameof(HostValid), nameof(JoinValid));
    }

    public static bool IsValidPort(double v) => !double.IsNaN(v) && v is >= 1 and <= 65535;

    public static int Port(double v) => (int)Math.Round(v);

    /// <summary>表单恢复：上次使用的游戏与端口。</summary>
    public void Restore(GamePreset game, int port, string server)
    {
        _game = game;
        _hostPort = game.IsCustom ? port : game.Port;
        _joinPort = game.IsCustom ? port : game.Port;
        _server = string.IsNullOrWhiteSpace(server) ? "127.0.0.1:9100" : server;
        Raise(nameof(SelectedGame), nameof(HostPortValue), nameof(JoinPortValue), nameof(Server), nameof(GameHint), nameof(ServerHint));
        Revalidate();
    }

    public void ResetSession()
    {
        IsHosting = false;
        IsJoined = false;
        Hint = "";
    }
}
