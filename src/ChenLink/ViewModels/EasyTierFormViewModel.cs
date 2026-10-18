using ChenLink.Core;
using ChenLink.Models;

namespace ChenLink.ViewModels;

/// <summary>免公网（EasyTier）表单：房主生成邀请码，玩家粘贴邀请码，自动寻找房主的游戏端口。</summary>
public sealed class EasyTierFormViewModel : ObservableObject
{
    public IReadOnlyList<GamePreset> Games { get; } = GameCatalog.All;

    GamePreset _game = GameCatalog.All[0];
    string _nodes = "";
    string _pingSummary = "尚未探测。点「自动获取节点」会逐个测速，按延迟从低到高排序。";
    double _port = 25565;
    string _invite = "";
    string _inviteInput = "";
    string _foundAddress = "";
    string _ownIp = "";
    string _peersSummary = "";
    string _hint = "";
    bool _isHosting;
    bool _isJoined;

    public GamePreset SelectedGame
    {
        get => _game;
        set
        {
            if (!SetProperty(ref _game, value)) return;
            if (value is { IsCustom: false }) PortValue = value.Port;
            OnPropertyChanged(nameof(GameHint));
        }
    }

    public string GameHint => _game.IsCustom
        ? "自定义端口：直接填游戏监听的端口"
        : $"{_game.ProtocolText} · {_game.Port} 端口" + (string.IsNullOrEmpty(_game.Note) ? "，已自动填入" : $"，{_game.Note}");

    public string Nodes
    {
        get => _nodes;
        set => SetProperty(ref _nodes, value);
    }

    public string PingSummary
    {
        get => _pingSummary;
        set => SetProperty(ref _pingSummary, value);
    }

    public double PortValue
    {
        get => _port;
        set { if (SetProperty(ref _port, value)) Revalidate(); }
    }

    public string Invite
    {
        get => _invite;
        set { if (SetProperty(ref _invite, value)) OnPropertyChanged(nameof(HasInvite)); }
    }

    public bool HasInvite => _invite.Length > 0;

    public string InviteInput
    {
        get => _inviteInput;
        set { if (SetProperty(ref _inviteInput, value)) Revalidate(); }
    }

    public string FoundAddress
    {
        get => _foundAddress;
        set { if (SetProperty(ref _foundAddress, value)) OnPropertyChanged(nameof(HasFound)); }
    }

    public bool HasFound => _foundAddress.Length > 0;

    public string OwnIp
    {
        get => _ownIp;
        set { if (SetProperty(ref _ownIp, value)) OnPropertyChanged(nameof(HasOwnIp)); }
    }

    public bool HasOwnIp => _ownIp.Length > 0;

    public string PeersSummary
    {
        get => _peersSummary;
        set => SetProperty(ref _peersSummary, value);
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
        HostError = RelayFormViewModel.IsValidPort(PortValue) ? "" : "请填 1–65535 之间的游戏端口";
        JoinError = _inviteInput.Trim().Length == 0
            ? "" // 空输入不报错，点击「加入房间」时会再拦一次
            : TryParseInvite(_inviteInput, out _, out _, out _, out var parseError) ? "" : parseError;
        HostValid = HostError.Length == 0;
        JoinValid = JoinError.Length == 0;
        Raise(nameof(HostError), nameof(JoinError), nameof(HostValid), nameof(JoinValid));
    }

    /// <summary>邀请码格式：网络名|口令|端口，例如 C7HQ2K|a1b2c3d4|25565。兼容不带首字母的写法。</summary>
    public static bool TryParseInvite(string? text, out string network, out string secret, out int port, out string error)
    {
        network = secret = "";
        port = 0;
        error = "";

        var raw = (text ?? "").Trim();
        if (raw.Length == 0)
        {
            error = "请整行粘贴房主发来的邀请码";
            return false;
        }

        var parts = raw.Split('|', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts[0].Length < 3 || parts[1].Length < 3
            || !int.TryParse(parts[2], out port) || port is < 1 or > 65535)
        {
            error = "邀请码格式不对，应形如 C7HQ2K|a1b2c3d4|25565";
            return false;
        }

        network = parts[0];
        secret = parts[1];
        return true;
    }

    public void Restore(GamePreset game, int port, string nodes)
    {
        _game = game;
        _port = game.IsCustom ? port : game.Port;
        _nodes = nodes;
        Raise(nameof(SelectedGame), nameof(PortValue), nameof(Nodes), nameof(GameHint));
        Revalidate();
    }

    public void ResetSession()
    {
        IsHosting = false;
        IsJoined = false;
        FoundAddress = "";
        PeersSummary = "";
        Hint = "";
    }
}
