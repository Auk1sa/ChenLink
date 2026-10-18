using System.Diagnostics;
using ChenLink.Core;
using ChenLink.EasyTier;
using ChenLink.Engine;
using ChenLink.Models;
using ChenLink.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ChenLink.ViewModels;

public enum ConnectionMode { EasyTier = 0, Relay = 1 }

/// <summary>
/// 连接页的唯一编排者：持有会话 / EasyTier 子进程，保证同一时刻只有一种联机方式在跑，
/// 并把底层零散事件收敛成一个可绑定的状态快照。
/// 旧版这些逻辑全部塞在 MainWindow.xaml.cs（860 行）里，UI 与网络互锁，状态文案互相覆盖。
/// </summary>
public sealed class ConnectViewModel : ObservableObject
{
    readonly SettingsService _settings;
    readonly LogService _log;
    readonly DispatcherQueue _dq;

    Session? _session;
    EasyTierRunner? _runner;
    CancellationTokenSource? _serverCts;
    DispatcherTimer? _noticeTimer;

    ConnectionMode _mode;
    bool _busy;
    SessionStatus _status = new();
    bool _noticeOpen;
    string _noticeText = "";
    InfoBarSeverity _noticeSeverity = InfoBarSeverity.Informational;
    string _embeddedHint = "";

    int _easyPort = 25565;
    int _scanning;
    string _lastPeerSignature = "";

    public ConnectViewModel(SettingsService settings, LogService log)
    {
        _settings = settings;
        _log = log;
        _dq = DispatcherQueue.GetForCurrentThread();

        _mode = (ConnectionMode)settings.Current.LastMode;

        var game = GameCatalog.ByIdOrDefault(settings.Current.DefaultGameId);
        var port = settings.Current.DefaultPort;
        Relay.Restore(game, port, settings.Current.Server);
        EasyTier.Restore(game, port, ResolveNodes());

        CreateRoomCommand = new AsyncRelayCommand(CreateRelayRoomAsync, () => CanStart);
        JoinRoomCommand = new AsyncRelayCommand(JoinRelayRoomAsync, () => CanStart);
        CreateEasyRoomCommand = new AsyncRelayCommand(CreateEasyRoomAsync, () => CanStart);
        JoinEasyRoomCommand = new AsyncRelayCommand(JoinEasyRoomAsync, () => CanStart);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => CanStop);
        ProbeNodesCommand = new AsyncRelayCommand(ProbeNodesAsync, () => !_busy);
        CopyAddressCommand = new RelayCommand(CopyAddress, () => Status.HasAddress);
        CopyInviteCommand = new RelayCommand(() => Copy(EasyTier.Invite, "邀请码"), () => EasyTier.HasInvite);
        CopyRoomCodeCommand = new RelayCommand(() => Copy(Relay.RoomCode, "房间码"), () => Relay.HasRoomCode);
        CopyFoundCommand = new RelayCommand(() => Copy(EasyTier.FoundAddress, "连接地址"), () => EasyTier.HasFound);
        PasteInviteCommand = new AsyncRelayCommand(PasteInviteAsync, () => CanEditForm);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        UseDefaultNodesCommand = new RelayCommand(UseDefaultNodes, () => CanEditForm);

        Relay.Revalidate();
        EasyTier.Revalidate();

        if (_settings.Current.AutoProbe && string.IsNullOrWhiteSpace(_settings.Current.EasyTierNodes))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(1200);
                _dq.TryEnqueue(() => ProbeNodesCommand.Execute(null));
            });
        }

        RefreshStatus();
    }

    // ---------------- 表单 ----------------

    public RelayFormViewModel Relay { get; } = new();
    public EasyTierFormViewModel EasyTier { get; } = new();

    public string Nickname => string.IsNullOrWhiteSpace(_settings.Current.Nickname)
        ? Environment.MachineName
        : _settings.Current.Nickname.Trim();

    // ---------------- 状态 ----------------

    public SessionStatus Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool IsBusy => _busy;
    public bool CanStart => !_busy && _session is null && _runner is null;
    public bool CanStop => !_busy && (_session is not null || _runner is not null);
    public bool CanSwitchMode => !_busy && _session is null && _runner is null;
    public bool CanEditForm => !_busy && _session is null && _runner is null;

    // 用 bool? 与 ToggleButton.IsChecked 精确对齐，避免 bool/bool? 绑定转换问题
    public bool? IsEasyTierMode
    {
        get => _mode == ConnectionMode.EasyTier;
        set
        {
            if (value == true) SetMode(ConnectionMode.EasyTier);
            else OnPropertyChanged(); // 取消勾选时不切模式，仅回正 UI
        }
    }

    public bool? IsRelayMode
    {
        get => _mode == ConnectionMode.Relay;
        set
        {
            if (value == true) SetMode(ConnectionMode.Relay);
            else OnPropertyChanged();
        }
    }

    public bool ShowEasyPanel => _mode == ConnectionMode.EasyTier;
    public bool ShowRelayPanel => _mode == ConnectionMode.Relay;

    public string EmbeddedHint
    {
        get => _embeddedHint;
        private set => SetProperty(ref _embeddedHint, value);
    }

    void SetMode(ConnectionMode mode)
    {
        if (!CanSwitchMode)
        {
            Raise(nameof(IsEasyTierMode), nameof(IsRelayMode));
            ShowNotice("请先断开当前连接，再切换联机方式", true);
            return;
        }
        if (_mode == mode) return;

        _mode = mode;
        _settings.Update(s => s.LastMode = (int)mode);
        Raise(nameof(IsEasyTierMode), nameof(IsRelayMode), nameof(ShowEasyPanel), nameof(ShowRelayPanel));
    }

    // ---------------- 提示条 ----------------

    public bool IsNoticeOpen
    {
        get => _noticeOpen;
        set => SetProperty(ref _noticeOpen, value);
    }

    public string NoticeText
    {
        get => _noticeText;
        private set => SetProperty(ref _noticeText, value);
    }

    public InfoBarSeverity NoticeSeverity
    {
        get => _noticeSeverity;
        private set => SetProperty(ref _noticeSeverity, value);
    }

    void ShowNotice(string text, bool error)
    {
        NoticeText = text;
        NoticeSeverity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success;
        IsNoticeOpen = true;
        _noticeTimer ??= CreateNoticeTimer();
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    DispatcherTimer CreateNoticeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            IsNoticeOpen = false;
        };
        return timer;
    }

    // ---------------- 命令 ----------------

    public AsyncRelayCommand CreateRoomCommand { get; }
    public AsyncRelayCommand JoinRoomCommand { get; }
    public AsyncRelayCommand CreateEasyRoomCommand { get; }
    public AsyncRelayCommand JoinEasyRoomCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand ProbeNodesCommand { get; }
    public AsyncRelayCommand PasteInviteCommand { get; }
    public RelayCommand CopyAddressCommand { get; }
    public RelayCommand CopyInviteCommand { get; }
    public RelayCommand CopyRoomCodeCommand { get; }
    public RelayCommand CopyFoundCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand UseDefaultNodesCommand { get; }

    // ---------------- 自建服务器流程 ----------------

    async Task CreateRelayRoomAsync()
    {
        Relay.Revalidate();
        if (!Relay.HostValid) { ShowNotice(Relay.HostError, true); return; }

        await RunBusyAsync(async () =>
        {
            await StopEasyTierCoreAsync();
            var session = NewSession(Relay.HostSecret.Trim());
            try
            {
                await session.CreateRoomAsync(RelayFormViewModel.Port(Relay.HostPortValue));
            }
            catch
            {
                await session.DisconnectAsync();
                throw;
            }

            Attach(session);
            _session = session;
            Relay.RoomCode = session.Room;
            Relay.IsHosting = true;
            Relay.Hint = "把房间码发给好友；他加入后你在游戏里照常开服即可。";
            PersistForm();
            ShowNotice($"房间 {session.Room} 已创建，把房间码发给好友", false);
        });
        RefreshStatus();
        UpdateCommands();
    }

    async Task JoinRelayRoomAsync()
    {
        Relay.Revalidate();
        if (!Relay.JoinValid) { ShowNotice(Relay.JoinError, true); return; }

        await RunBusyAsync(async () =>
        {
            await StopEasyTierCoreAsync();
            var session = NewSession(Relay.JoinSecret.Trim());
            try
            {
                await session.JoinRoomAsync(Relay.JoinCode, RelayFormViewModel.Port(Relay.JoinPortValue));
            }
            catch
            {
                await session.DisconnectAsync();
                throw;
            }

            Attach(session);
            _session = session;
            Relay.IsJoined = true;
            Relay.Hint = "等待房主就绪，链路打通后这里会显示游戏连接地址。";
            PersistForm();
        });
        RefreshStatus();
        UpdateCommands();
    }

    Session NewSession(string secret)
    {
        var session = new Session(Relay.Server, Nickname, secret);
        return session;
    }

    void Attach(Session session)
    {
        session.Log += message => _dq.TryEnqueue(() => _log.Add(Classify(message), message));
        session.Changed += () => _dq.TryEnqueue(RefreshStatus);
        session.PeerLeft += () => _dq.TryEnqueue(() =>
        {
            if (session.Role == Role.Join)
            {
                _log.Warn("房主已离开，连接已断开");
                _ = StopRelayAsync("房主已离开");
            }
            else
            {
                _log.Warn("好友已离开，房间保留，可等待再次加入");
                RefreshStatus();
            }
        });
    }

    static LogLevel Classify(string message)
    {
        if (message.Contains("❌", StringComparison.Ordinal)) return LogLevel.Error;
        if (message.Contains("⚠", StringComparison.Ordinal)) return LogLevel.Warning;
        if (message.Contains("✅", StringComparison.Ordinal) || message.Contains("🔒", StringComparison.Ordinal))
            return LogLevel.Success;
        return LogLevel.Info;
    }

    async Task StopRelayAsync(string reason)
    {
        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { await session.DisconnectAsync(); } catch { }
            _log.Info(reason);
        }

        Relay.ResetSession();
        Relay.RoomCode = "";
        RefreshStatus();
        UpdateCommands();
    }

    // ---------------- 免公网流程 ----------------

    async Task CreateEasyRoomAsync()
    {
        EasyTier.Revalidate();
        if (!EasyTier.HostValid) { ShowNotice(EasyTier.HostError, true); return; }

        var port = RelayFormViewModel.Port(EasyTier.PortValue);
        var invite = $"C{RandomToken(5)}|{RandomToken(8)}|{port}";

        await RunBusyAsync(async () =>
        {
            await StopRelayAsync("已切换到免公网联机");
            _easyPort = port;
            _lastPeerSignature = "";

            EasyTier.Invite = invite;
            EasyTier.IsHosting = true;
            EasyTier.Hint = "正在启动虚拟网卡并连接公共节点，首次会申请管理员授权…";

            var runner = NewRunner();
            try
            {
                await runner.StartAsync(invite.Split('|')[0], invite.Split('|')[1], ParseNodes(EasyTier.Nodes), Nickname, "host");
            }
            catch
            {
                if (_runner == runner) _runner = null;
                await runner.StopAsync();
                throw;
            }

            PersistForm();
        });
        RefreshStatus();
        UpdateCommands();
    }

    async Task JoinEasyRoomAsync()
    {
        if (!EasyTierFormViewModel.TryParseInvite(EasyTier.InviteInput, out var network, out var secret, out var port, out var error))
        {
            ShowNotice(error, true);
            return;
        }

        await RunBusyAsync(async () =>
        {
            await StopRelayAsync("已切换到免公网联机");
            _easyPort = port;
            _lastPeerSignature = "";
            EasyTier.FoundAddress = "";
            EasyTier.IsJoined = true;
            EasyTier.Hint = "正在组网，连上房主后会自动探测他的游戏端口…";

            var runner = NewRunner();
            try
            {
                await runner.StartAsync(network, secret, ParseNodes(EasyTier.Nodes), Nickname, "guest");
            }
            catch
            {
                if (_runner == runner) _runner = null;
                await runner.StopAsync();
                throw;
            }

            PersistForm();
        });
        RefreshStatus();
        UpdateCommands();
    }

    EasyTierRunner NewRunner()
    {
        var runner = new EasyTierRunner();
        runner.Log += message => _dq.TryEnqueue(() => _log.Add(Classify(message), message));
        runner.Changed += () => _dq.TryEnqueue(() => OnRunnerChanged(runner));
        _runner = runner;
        return runner;
    }

    void OnRunnerChanged(EasyTierRunner runner)
    {
        if (!ReferenceEquals(runner, _runner)) return;

        EasyTier.OwnIp = runner.OwnIp;
        EasyTier.PeersSummary = runner.Peers.Count == 0
            ? "已加入虚拟网络，等待对方上线…"
            : "网络内其他设备：" + string.Join("、", runner.Peers.Select(p => $"{p.Hostname} {p.Ipv4}"));

        if (!string.IsNullOrWhiteSpace(runner.StatusText))
            EasyTier.Hint = runner.Ready
                ? (EasyTier.IsHosting ? "组网就绪，把邀请码发给好友" : "组网就绪，正在寻找房主的游戏服务器…")
                : runner.StatusText;

        RefreshStatus();

        if (!EasyTier.IsHosting && runner.Ready && !EasyTier.HasFound)
            _ = ScanForHostAsync(runner);
    }

    /// <summary>玩家侧自动找房主的游戏端口：优先探测 hostname 带「房主」的节点，再退回全部节点。</summary>
    async Task ScanForHostAsync(EasyTierRunner runner)
    {
        if (!runner.Ready) return;
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;

        try
        {
            var peers = runner.Peers
                .Where(p => !string.IsNullOrEmpty(p.Ipv4) && p.Ipv4 != runner.OwnIp)
                .ToList();
            if (peers.Count == 0) return;

            var signature = string.Join(",", peers.Select(p => p.Ipv4 + p.Hostname));
            var peersChanged = signature != _lastPeerSignature;
            if (!peersChanged && EasyTier.HasFound) return;
            _lastPeerSignature = signature;

            var ordered = peers.OrderByDescending(p => p.Hostname.Contains("房主")).ToList();
            var results = await Task.WhenAll(ordered.Select(p => EasyTierRunner.IsPortOpenAsync(p.Ipv4, _easyPort)));

            if (!ReferenceEquals(runner, _runner)) return;

            var hitIndex = Array.FindIndex(results, ok => ok);
            var hit = hitIndex >= 0 ? ordered[hitIndex] : ordered[0];
            EasyTier.FoundAddress = $"{hit.Ipv4}:{_easyPort}";
            EasyTier.Hint = hitIndex >= 0
                ? "已找到房主的游戏服务器，进游戏直接连接下面的地址"
                : $"未探测到 TCP 游戏服；若游戏走 UDP，直接连 {hit.Ipv4}:{_easyPort}";

            if (hitIndex >= 0) _log.Success($"找到房主的游戏服务器：{EasyTier.FoundAddress}");
            else _log.Info($"已就绪，游戏地址：{EasyTier.FoundAddress}（UDP 游戏直接连这个地址）");

            RefreshStatus();
            UpdateCommands();
        }
        catch (Exception ex)
        {
            _log.Warn("自动寻找房主服务器失败：" + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _scanning, 0);
        }
    }

    async Task ProbeNodesAsync()
    {
        _log.Info("正在获取并探测可用公共节点…");
        EasyTier.PingSummary = "正在逐个测速…";
        try
        {
            var nodes = await EasyTierRunner.AutoFetchNodesAsync();
            if (nodes.Length == 0)
            {
                EasyTier.PingSummary = "未探测到可用节点（可能网络受限），继续使用输入框里的节点。";
                _log.Warn("没有探测到可达的公共节点");
                ShowNotice("没有探测到可用节点，可手动填写自建节点", true);
                return;
            }

            EasyTier.Nodes = string.Join(", ", nodes.Select(n => n.Node));
            EasyTier.PingSummary = "节点延迟（升序）：" + string.Join(" · ",
                nodes.Take(8).Select(n => $"{n.Node.Replace("tcp://", "")} {n.Ms}ms"));
            _log.Success($"探测到 {nodes.Length} 个可用公共节点");
            ShowNotice($"已获取 {nodes.Length} 个可用节点（下次联机生效）", false);
        }
        catch (Exception ex)
        {
            EasyTier.PingSummary = "探测失败：" + ex.Message;
            _log.Error("自动获取节点失败：" + ex.Message);
        }
    }

    async Task StopEasyTierCoreAsync()
    {
        var runner = _runner;
        _runner = null;
        EasyTier.ResetSession();
        if (runner is not null)
        {
            try { await runner.StopAsync(); } catch { }
        }
    }

    async Task StopEasyTierAsync()
    {
        if (_runner is null) return;
        await StopEasyTierCoreAsync();
        _log.Info("虚拟网络已断开");
        RefreshStatus();
        UpdateCommands();
    }

    async Task DisconnectAsync()
    {
        if (_session is not null) await StopRelayAsync("已手动断开");
        else if (_runner is not null) await StopEasyTierAsync();
        UpdateCommands();
    }

    // ---------------- 内嵌服务器 ----------------

    public bool EmbeddedServer
    {
        get => Relay.EmbeddedServer;
        set
        {
            if (Relay.EmbeddedServer == value) return;
            Relay.EmbeddedServer = value;
            OnPropertyChanged();
            if (value) StartEmbeddedServer();
            else StopEmbeddedServer();
        }
    }

    void StartEmbeddedServer()
    {
        var port = (int)Math.Round(Relay.ServerPortValue);
        if (port is < 1 or > 65535)
        {
            Relay.EmbeddedServer = false;
            OnPropertyChanged(nameof(EmbeddedServer));
            ShowNotice("内嵌服务器端口无效（1–65535）", true);
            return;
        }

        try
        {
            _serverCts?.Cancel();
            _serverCts = new CancellationTokenSource();
            var token = _serverCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ChenLinkServer.ChenLinkServerCore.RunAsync(port, token);
                }
                catch (Exception ex) when (!token.IsCancellationRequested)
                {
                    _dq.TryEnqueue(() =>
                    {
                        _log.Error("内置信令服务器异常退出：" + ex.Message);
                        Relay.EmbeddedServer = false;
                        OnPropertyChanged(nameof(EmbeddedServer));
                    });
                }
            });

            var lan = LocalNetwork.IPv4();
            EmbeddedHint = lan.Length > 0
                ? $"好友「服务器地址」填 {lan[0]}:{port}"
                : "本机未发现局域网 IP，跨网使用需公网 IP 或端口映射";
            _log.Success($"内置信令服务器已启动：0.0.0.0:{port}");
            _log.Info(EmbeddedHint);
            _settings.Update(s => s.EmbeddedServerPort = port);
        }
        catch (Exception ex)
        {
            Relay.EmbeddedServer = false;
            OnPropertyChanged(nameof(EmbeddedServer));
            _log.Error("内置信令服务器启动失败：" + ex.Message);
        }
    }

    void StopEmbeddedServer()
    {
        _serverCts?.Cancel();
        _serverCts = null;
        EmbeddedHint = "";
        _log.Info("内置信令服务器已停止");
    }

    // ---------------- 状态计算 ----------------

    void RefreshStatus()
    {
        Status = _session is not null ? BuildRelayStatus(_session)
            : _runner is not null ? BuildEasyTierStatus(_runner)
            : BuildIdleStatus();
        UpdateCommands();
    }

    SessionStatus BuildIdleStatus() => new()
    {
        Title = "未连接",
        Detail = "选一种联机方式开始：免公网模式开箱即用，自建服务器模式适合有 VPS 的场景。",
        Tone = StatusTone.Idle,
        Channels = "—",
        Up = "—",
        Down = "—",
        NextStep = "接下来：房主点「创建房间」拿到房间码，好友在「我加入」里粘贴房间码。",
    };

    SessionStatus BuildRelayStatus(Session session)
    {
        var state = session.State;
        var ready = state == SessionState.Ready;
        var isJoin = session.Role == Role.Join;

        var title = state switch
        {
            SessionState.Waiting => isJoin ? "等待房主就绪" : "等待好友加入",
            SessionState.Punching => "正在打通链路",
            SessionState.Reconnecting => "链路中断，正在自动重连",
            SessionState.Ready => "已就绪，可以进游戏了",
            SessionState.Failed => "连接失败",
            SessionState.Stopped => "已断开",
            _ => "连接中",
        };

        var tone = state switch
        {
            SessionState.Ready => StatusTone.Success,
            SessionState.Failed => StatusTone.Danger,
            SessionState.Reconnecting => StatusTone.Warning,
            SessionState.Stopped => StatusTone.Idle,
            _ => StatusTone.Info,
        };

        var address = ready
            ? (isJoin ? session.LocalAddress : session.Room)
            : "";

        var nextStep = state switch
        {
            SessionState.Ready when isJoin => $"在游戏的「多人游戏 / 直接连接」里填 {session.LocalAddress}",
            SessionState.Ready => $"把房间码 {session.Room} 发给好友，他加入后即可进入你的游戏",
            SessionState.Waiting when isJoin => $"等待房主就绪；若迟迟没有反应，确认房间码 {session.Room} 是否正确",
            SessionState.Waiting => $"把房间码 {session.Room} 发给好友，等待他加入",
            _ => session.Detail,
        };

        return new SessionStatus
        {
            Title = title,
            Detail = string.IsNullOrWhiteSpace(session.Detail) ? nextStep : session.Detail,
            Tone = tone,
            ShowProgress = state is SessionState.Punching or SessionState.Reconnecting or SessionState.Waiting,
            HasSession = true,
            Mode = session.Mode,
            Latency = Format.Latency(session.LatencyMs),
            Channels = session.Channels.ToString(),
            Up = Format.Bytes(session.BytesUp),
            Down = Format.Bytes(session.BytesDown),
            NextStep = nextStep,
            Address = address,
            AddressLabel = isJoin ? "复制连接地址" : "复制房间码",
            HasAddress = address.Length > 0,
        };
    }

    SessionStatus BuildEasyTierStatus(EasyTierRunner runner)
    {
        var host = EasyTier.IsHosting;
        var ready = runner.Ready;

        var title = !ready
            ? "正在组建虚拟网络"
            : host ? "组网就绪，等待好友加入"
            : EasyTier.HasFound ? "已找到房主，可以进游戏了" : "组网就绪，正在寻找房主";

        var tone = !ready ? StatusTone.Info
            : host ? StatusTone.Success
            : EasyTier.HasFound ? StatusTone.Success : StatusTone.Warning;

        var address = !ready ? ""
            : host ? (runner.OwnIp.Length > 0 ? $"{runner.OwnIp}:{_easyPort}" : "")
            : EasyTier.FoundAddress;

        var nextStep = !ready
            ? runner.StatusText
            : host
                ? $"把邀请码发给好友；他进游戏后连接 {runner.OwnIp}:{_easyPort}"
                : EasyTier.HasFound
                    ? $"在游戏里直接连接 {EasyTier.FoundAddress}"
                    : "已组网，正在探测房主的游戏端口；UDP 游戏可直接用他的虚拟 IP";

        var detail = !ready
            ? (string.IsNullOrWhiteSpace(runner.StatusText) ? "正在申请虚拟 IP…" : runner.StatusText)
            : host
                ? $"本机虚拟 IP {runner.OwnIp}，端口 {_easyPort}"
                : EasyTier.HasFound ? EasyTier.Hint : EasyTier.PeersSummary;

        return new SessionStatus
        {
            Title = title,
            Detail = detail,
            Tone = tone,
            ShowProgress = !ready,
            HasSession = true,
            Mode = ready ? "虚拟局域网" : "组网中",
            Latency = "—",
            Channels = "—",
            Up = "—",
            Down = "—",
            NextStep = nextStep,
            Address = address,
            AddressLabel = host ? "复制接入地址" : "复制连接地址",
            HasAddress = address.Length > 0,
        };
    }

    // ---------------- 辅助 ----------------

    async Task RunBusyAsync(Func<Task> action)
    {
        if (_busy) return;
        SetBusy(true);
        try
        {
            await action();
        }
        catch (SessionException ex)
        {
            _log.Error(ex.Message);
            ShowNotice(ex.Message, true);
        }
        catch (Exception ex)
        {
            _log.Error("操作失败：" + ex.Message);
            ShowNotice("操作失败：" + ex.Message, true);
        }
        finally
        {
            SetBusy(false);
            RefreshStatus();
        }
    }

    void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        UpdateCommands();
        Raise(nameof(IsBusy), nameof(CanStart), nameof(CanStop), nameof(CanSwitchMode), nameof(CanEditForm));
    }

    void UpdateCommands()
    {
        CreateRoomCommand.RaiseCanExecuteChanged();
        JoinRoomCommand.RaiseCanExecuteChanged();
        CreateEasyRoomCommand.RaiseCanExecuteChanged();
        JoinEasyRoomCommand.RaiseCanExecuteChanged();
        DisconnectCommand.RaiseCanExecuteChanged();
        ProbeNodesCommand.RaiseCanExecuteChanged();
        CopyAddressCommand.RaiseCanExecuteChanged();
        CopyInviteCommand.RaiseCanExecuteChanged();
        CopyRoomCodeCommand.RaiseCanExecuteChanged();
        CopyFoundCommand.RaiseCanExecuteChanged();
        PasteInviteCommand.RaiseCanExecuteChanged();
        UseDefaultNodesCommand.RaiseCanExecuteChanged();

        Raise(nameof(IsBusy), nameof(CanStart), nameof(CanStop), nameof(CanSwitchMode), nameof(CanEditForm));
    }

    void Copy(string text, string what)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (ClipboardHelper.TryCopy(text)) ShowNotice($"已复制{what}：{text}", false);
        else ShowNotice("复制失败，请手动选择文本复制", true);
    }

    void CopyAddress() => Copy(Status.Address, Status.AddressLabel.Replace("复制", ""));

    async Task PasteInviteAsync()
    {
        try
        {
            var text = await ClipboardHelper.TryGetTextAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowNotice("剪贴板里没有文本", true);
                return;
            }
            EasyTier.InviteInput = text.Trim();
        }
        catch (Exception ex)
        {
            ShowNotice("读取剪贴板失败：" + ex.Message, true);
        }
    }

    void UseDefaultNodes()
    {
        EasyTier.Nodes = string.Join(", ", EasyTierRunner.DefaultNodes);
        EasyTier.PingSummary = "已恢复内置默认节点";
    }

    void OpenLogFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", _log.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开日志目录失败：" + ex.Message);
        }
    }

    string ResolveNodes()
    {
        var saved = _settings.Current.EasyTierNodes;
        return string.IsNullOrWhiteSpace(saved) ? string.Join(", ", EasyTierRunner.DefaultNodes) : saved;
    }

    static string[] ParseNodes(string text)
    {
        var list = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.StartsWith("tcp://") || x.StartsWith("udp://") || x.StartsWith("ws://"))
            .ToArray();
        return list.Length > 0 ? list : EasyTierRunner.DefaultNodes;
    }

    static string RandomToken(int length)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return new string(Enumerable.Range(0, length).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
    }

    /// <summary>把表单现场写回配置，下次启动直接续上。</summary>
    void PersistForm()
    {
        var game = EasyTier.SelectedGame;
        var port = RelayFormViewModel.Port(EasyTier.PortValue);
        _settings.Update(s =>
        {
            s.LastMode = (int)_mode;
            s.Server = Relay.Server;
            s.DefaultGameId = game.Id;
            s.DefaultPort = port;
            s.EasyTierNodes = EasyTier.Nodes;
        });
    }

    /// <summary>关窗时释放：停内嵌服务器、断会话、杀 EasyTier 子进程。</summary>
    public async Task ShutdownAsync()
    {
        try { _serverCts?.Cancel(); } catch { }
        await StopEasyTierCoreAsync();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            try { await session.DisconnectAsync(); } catch { }
        }
    }
}
