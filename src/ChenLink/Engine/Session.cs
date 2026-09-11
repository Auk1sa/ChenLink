// 会话：登记房间 -> 交换信息 -> 打通链路（直连优先，失败走服务器中继）
// -> 在链路上复用游戏连接：TCP 通道与 UDP 数据报都封装进同一条链路。host 侧把数据拨到本机游戏端口，
// join 侧监听本机映射端口，游戏客户端连 127.0.0.1:映射端口 即可。
// 对应 OpenP2P 的节点(P2PApp 端口转发)概念，UI 层只关心 State/Mode/Channels/日志。
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ChenLink.Engine;

public enum Role { Host, Join }

public enum SessionState
{
    Idle,       // 未开始
    Waiting,    // 房间已建/已加入，等对方
    Punching,   // 双方到齐，正在打通
    Reconnecting, // 链路中断，正在自动重连
    Ready,      // 链路就绪（Mode 区分 直连/中继）
    Stopped,    // 已停止
    Failed,     // 出错
}

public sealed class Session : IAsyncDisposable
{
    public const int DefaultPort = 9100;

    /// 自测钩子：置 true 则跳过直连、强制走中继。
    internal static bool ForceRelay = false;

    public string Name { get; }
    public Role Role { get; private set; }
    public string Room { get; private set; } = "";
    public string ServerText { get; }
    public string Secret { get; }   // 房间口令（可选，空表示无口令）

    public SessionState State { get; private set; } = SessionState.Idle;
    public string Mode { get; private set; } = "—";          // 直连 / 中继
    public string Detail { get; private set; } = "";
    int _channels;
    public int Channels => _channels;
    public long BytesUp { get; private set; }
    public long BytesDown { get; private set; }

    public event Action<string>? Log;      // 文本日志
    void EmitLog(string m) => Log?.Invoke(m);
    public event Action? Changed;          // 状态/计数变化，UI 统一刷新
    public event Action? PeerLeft;         // 对方离开（链路保留由 UI 决定是否重连）

    readonly string _serverHost;
    readonly int _serverPort;
    readonly CancellationTokenSource _lifetime = new();
    readonly System.Threading.Timer? _stats;

    int _gamePort;                          // host：本机游戏端口（转发目标）
    int _listenPort;                        // join：本机映射监听端口
    int _bindPort;                          // 直连尝试使用的本地端口
    string[] _lan = Array.Empty<string>();
    volatile string _myIp = "";

    TcpClient? _ctl;
    StreamReader? _cr;
    StreamWriter? _cw;
    PeerInfo? _peer;
    bool _gotGo;

    TcpClient? _linkClient;                 // 直连或中继的底层连接
    Mux? _mux;
    TcpListener? _listener;
    volatile bool _stopping;
    UdpClient? _udp;            // UDP 转发套接字（host: 发往游戏服 / join: 收本地游戏客户端）
    volatile IPEndPoint? _udpTarget;     // host: 游戏服 127.0.0.1:_gamePort；join: 本地游戏客户端最近来源
    volatile bool _udpActive;   // 已出现过 UDP 流量（连接数显示为 1）

    public Session(string server, string name, string secret = "")
    {
        ServerText = server;
        Name = string.IsNullOrWhiteSpace(name) ? Environment.MachineName : name.Trim();
        Secret = secret?.Trim() ?? "";
        var (h, p) = SplitHostPort(server, DefaultPort);
        _serverHost = h;
        _serverPort = p;
        _stats = new System.Threading.Timer(_ => RaiseStats(), null, Timeout.Infinite, Timeout.Infinite);
    }

    // ---------- 公共入口 ----------

    /// 房主：开一个房间，把自己的游戏端口暴露给好友。
    public async Task CreateRoomAsync(int gamePort, CancellationToken ct = default)
    {
        _gamePort = gamePort;
        await BeginAsync(Role.Host, await NewRoomCodeAsync(ct), ct);
        EmitLog($"房间已创建：{Room}（等待好友输入房间码加入）");
        SetState(SessionState.Waiting, $"等待好友加入… 房间码 {Room}");
    }

    /// 玩家：输入房间码加入，并把 127.0.0.1:listenPort 映射到房主的游戏端口。
    public async Task JoinRoomAsync(string room, int listenPort, CancellationToken ct = default)
    {
        _listenPort = listenPort;
        await BeginAsync(Role.Join, room.ToUpperInvariant().Trim(), ct);
        EmitLog($"已加入房间 {Room}，等待房主…");
        SetState(SessionState.Waiting, "等待房主就绪…");
    }

    public Task DisconnectAsync()
    {
        _stopping = true;
        _lifetime.Cancel();
        try { _ctl?.Close(); } catch { }
        _ctl = null;
        StopLink();
        _stats?.Change(Timeout.Infinite, Timeout.Infinite);
        SetState(SessionState.Stopped, "已断开");
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() { _lifetime.Cancel(); return ValueTask.CompletedTask; }

    // ---------- 登记与信令 ----------

    async Task BeginAsync(Role role, string room, CancellationToken ct)
    {
        Role = role;
        Room = room;
        _bindPort = FreePort();
        _lan = LocalIPv4();
        _stopping = false;

        _ctl = new TcpClient();
        await _ctl.ConnectAsync(_serverHost, _serverPort, ct);
        _cr = new StreamReader(_ctl.GetStream(), new UTF8Encoding(false));
        _cw = new StreamWriter(_ctl.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        EmitLog($"已连接信令服务器 {_serverHost}:{_serverPort}");

        await SendAsync(new HelloMsg { Room = Room, Role = role == Role.Host ? "host" : "join", Name = Name, Port = _bindPort, Lan = _lan, Secret = Secret }, ct);
        _ = Task.Run(() => ControlLoopAsync(ct));
        _ = Task.Run(() => HeartbeatLoopAsync(ct));
    }

    async Task ControlLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await _cr!.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object?>? root;
                try { root = Wire.Parse<Dictionary<string, object?>>(line); }
                catch { continue; } // 忽略无法解析的行
                if (root is null) continue;
                var t = root.GetValueOrDefault("t")?.ToString();
                switch (t)
                {
                    case Msg.You:
                        _myIp = Wire.Parse<YouMsg>(line)?.Ip ?? _myIp;
                        break;
                    case Msg.Peer:
                        _peer = Wire.Parse<PeerMsg>(line)?.Peer;
                        MaybeStartLink();
                        break;
                    case Msg.Go:
                        _gotGo = true;
                        MaybeStartLink();
                        break;
                    case Msg.Bye:
                        EmitLog($"对方已离开：{Wire.Parse<ByeMsg>(line)?.Why}");
                        PeerLeft?.Invoke();
                        StopLink();
                        SetState(SessionState.Waiting, "对方已离开，可等待重连或退出");
                        break;
                    case Msg.Err:
                        var em = Wire.Parse<ErrMsg>(line)?.Text ?? "未知错误";
                        EmitLog("服务器：" + em);
                        SetState(SessionState.Failed, em);
                        break;
                    case Msg.Ping:
                        try { await SendAsync(new PongMsg(), ct); } catch { }
                        break;
                    case Msg.Pong:
                        break; // 心跳应答，无需处理
                }
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested && !_stopping)
        {
            EmitLog("信令连接断开：" + e.Message);
            PeerLeft?.Invoke();
            SetState(SessionState.Failed, "与服务器断开");
        }
    }

    void MaybeStartLink()
    {
        if (_peer is null || !_gotGo || State is SessionState.Punching or SessionState.Ready or SessionState.Reconnecting) return;
        var peer = _peer;
        _ = Task.Run(async () =>
        {
            if (!await EstablishLinkAsync(peer))
                SetState(SessionState.Failed, "无法与对方建立链路，请重试");
        });
    }

    /// 控制连接心跳：每 25s 发 Ping，防 NAT 空闲断开；服务器侧有 90s 读取超时。
    async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(25), ct);
                if (ct.IsCancellationRequested) break;
                try { await SendAsync(new PingMsg(), ct); } catch { break; }
            }
        }
        catch (OperationCanceledException) { }
    }

    // ---------- 打通链路 ----------

    async Task<bool> EstablishLinkAsync(PeerInfo peer)
    {
        SetState(SessionState.Punching, $"对方 {peer.Name} 已就位，正在打通…");
        const int maxTries = 3;
        for (int i = 1; i <= maxTries && !_stopping; i++)
        {
            try
            {
                var direct = await TryDirectAsync(peer);
                Stream link;
                if (direct is not null)
                {
                    _linkClient = direct;
                    link = direct.GetStream();
                    Mode = "直连";
                    EmitLog("P2P 直连成功");
                }
                else
                {
                    Mode = "中继";
                    EmitLog("直连不可用，改用服务器中继");
                    var rc = new TcpClient();
                    await rc.ConnectAsync(_serverHost, _serverPort, _lifetime.Token);
                    _linkClient = rc;
                    var rs = rc.GetStream();
                    await Wire.WriteLineAsync(rs, Wire.Line(new RelayMsg { Room = Room, Role = Role == Role.Host ? "host" : "join" }), _lifetime.Token);
                    link = rs;
                }
                StartLinkAsync(link);
                return true;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                EmitLog($"第 {i}/{maxTries} 次打通失败：{e.Message}");
                StopLink();
                if (i < maxTries) await Task.Delay(1200, _lifetime.Token);
            }
            catch (OperationCanceledException) { return false; }
        }
        return false;
    }

    void StartLinkAsync(Stream link)
    {
        // 房间口令非空时启用端到端加密；双方口令经服务器校验一致，密钥相同
        if (!string.IsNullOrEmpty(Secret))
        {
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(Secret));
            link = new AesGcmStream(link, key);
            EmitLog("🔒 已启用端到端加密（AES-GCM）");
        }
        var mux = new Mux(link);
        _mux = mux;
        mux.Failed += msg => Task.Run(() => OnLinkBroken(msg));
        if (Role == Role.Host)
            mux.Opened += id => _ = Task.Run(() => OnHostChannelAsync(mux, id));
        _ = Task.Run(() => mux.RunAsync(_lifetime.Token));
        StartUdp(mux); // 除 TCP 外，同端口的 UDP 也一起转发

        if (Role == Role.Join && !StartAcceptLoop(mux))
        {
            StopLink();
            SetState(SessionState.Failed, $"无法监听 {_listenPort}，可能已被占用");
            return;
        }

        if (Role == Role.Join)
        {

            SetState(SessionState.Ready, Mode == "直连" ? "已直连房主" : "已通过中继连接房主");
            EmitLog(Mode == "直连"
                ? $"✅ 已就绪（直连）：在游戏里连接 127.0.0.1:{_listenPort}"
                : $"✅ 已就绪（中继）：在游戏里连接 127.0.0.1:{_listenPort}");
        }
        else
        {
            SetState(SessionState.Ready, Mode == "直连" ? "已与玩家直连" : "已通过中继连接玩家");
            EmitLog(Mode == "直连"
                ? "✅ 已就绪（直连）：等待玩家通过映射端口连入你的游戏"
                : "✅ 已就绪（中继）：等待玩家通过映射端口连入你的游戏");
        }
        _stats?.Change(1000, 1000);
    }

    // ---------- UDP 转发：数据报与 TCP 走同一条链路、同一端口号 ----------

    void StartUdp(Mux mux)
    {
        try
        {
            if (Role == Role.Host)
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                _udpTarget = new IPEndPoint(IPAddress.Loopback, _gamePort);
                EmitLog($"UDP 转发已开启：好友发来的 UDP 会转到本机游戏端口 {_gamePort}");
            }
            else
            {
                _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, _listenPort));
                EmitLog($"UDP 转发已开启：UDP 游戏请在游戏里填 127.0.0.1:{_listenPort}");
            }
            mux.UdpData += p => _ = Task.Run(() => OnUdpFromPeerAsync(mux, p));
            _ = Task.Run(() => UdpLoopAsync(mux));
        }
        catch (Exception e) { EmitLog("⚠️ UDP 转发启动失败（不影响 TCP）：" + e.Message); }
    }

    async Task UdpLoopAsync(Mux mux)
    {
        try
        {
            while (!_stopping && _udp is not null)
            {
                var r = await _udp.ReceiveAsync().WaitAsync(_lifetime.Token);
                if (Role == Role.Join) _udpTarget = r.RemoteEndPoint; // 记住本地游戏客户端，回包发回给它
                MarkUdpActive();
                await mux.SendUdpAsync(r.Buffer, _lifetime.Token);
            }
        }
        catch { }
    }

    async Task OnUdpFromPeerAsync(Mux mux, byte[] payload)
    {
        var target = _udpTarget;
        if (_udp is null || target is null) return;
        MarkUdpActive();
        try { await _udp.SendAsync(payload, payload.Length, target).WaitAsync(_lifetime.Token); }
        catch { }
    }

    void MarkUdpActive()
    {
        if (_udpActive) return;
        _udpActive = true;
        if (Interlocked.CompareExchange(ref _channels, 1, 0) == 0) RaiseStats();
    }

    void OnLinkBroken(string msg)
    {
        EmitLog("链路中断：" + msg);
        StopLink();
        // 仅在非 Failed 状态下自动重连；若已是 Failed（如本地端口占用），说明是本地错误，重连无意义
        if (_peer is not null && !_stopping && State != SessionState.Failed)
        {
            _ = Task.Run(() => ReconnectAsync());
        }
        else
        {
            _peer = null;
            _gotGo = false;
            if (State != SessionState.Failed)
                SetState(SessionState.Failed, "链路中断，请重新加入");
        }
    }

    /// 链路中断后自动重连：保留 _peer，递增等待后重新 EstablishLinkAsync。
    async Task ReconnectAsync()
    {
        const int maxReconnect = 2;
        for (int attempt = 1; attempt <= maxReconnect && !_stopping; attempt++)
        {
            SetState(SessionState.Reconnecting, $"链路中断，第 {attempt}/{maxReconnect} 次重连…");
            try { await Task.Delay(1500 * attempt, _lifetime.Token); }
            catch (OperationCanceledException) { return; }
            var peer = _peer;
            if (peer is null) break;
            if (await EstablishLinkAsync(peer)) return;
        }
        _peer = null;
        _gotGo = false;
        SetState(SessionState.Failed, "重连失败，请重新加入");
    }

    void StopLink()
    {
        var l = _listener;
        _listener = null;
        try { l?.Stop(); } catch { }
        var old = _linkClient;
        _linkClient = null;
        _mux = null;
        try { old?.Close(); } catch { }
        var u = _udp;
        _udp = null;
        _udpTarget = null;
        _udpActive = false;
        try { u?.Close(); } catch { }
        _channels = 0;
        _stats?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    // ---------- 通道：host 拨游戏 / join 收本地连接 ----------

    bool StartAcceptLoop(Mux mux)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, _listenPort);
            _listener = listener;
            listener.Start();
            EmitLog($"监听 127.0.0.1:{_listenPort} —— 游戏里“直接连接”这个地址");
            _ = Task.Run(async () =>
            {
                while (!_stopping)
                {
                    TcpClient local;
                    try { local = await listener.AcceptTcpClientAsync(_lifetime.Token); }
                    catch { break; }
                    _ = Task.Run(() => HandleLocalAsync(mux, local));
                }
            });
            return true;
        }
        catch (Exception e)
        {
            EmitLog("监听失败：" + e.Message);
            return false;
        }
    }

    async Task HandleLocalAsync(Mux mux, TcpClient local)
    {
        try
        {
            var id = mux.NextChannelId();
            EmitLog($"游戏连接进来，打开通道 #{id} …");
            if (!await mux.OpenAsync(id, _lifetime.Token))
            {
                local.Close();
                EmitLog($"通道 #{id} 打开失败（房主未响应或游戏未开），已关闭本地连接");
                return;
            }
            EmitLog($"通道 #{id} 已打通");
            var ch = new MuxChannel(mux, id, local);
            mux.Register(id, ch);
            Interlocked.Increment(ref _channels);
            ch.Ended += () => { Interlocked.Decrement(ref _channels); RaiseStats(); };
            ch.Start();
            RaiseStats();
        }
        catch { try { local.Close(); } catch { } }
    }

    async Task OnHostChannelAsync(Mux mux, int id)
    {
        EmitLog($"收到好友的联机请求，正在连接本机游戏端口 {_gamePort} …");
        TcpClient? game = null;
        try
        {
            game = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await game.ConnectAsync(IPAddress.Loopback, _gamePort, timeout.Token);
            var ch = new MuxChannel(mux, id, game);
            mux.Register(id, ch);
            await mux.SendAsync(Mux.OpenOk, id, default, _lifetime.Token);
            Interlocked.Increment(ref _channels);
            ch.Ended += () => { Interlocked.Decrement(ref _channels); RaiseStats(); };
            ch.Start();
            EmitLog("游戏通道已建立");
            RaiseStats();
        }
        catch (Exception ex)
        {
            EmitLog($"连接本机游戏端口失败：{ex.Message}");
            mux.Unregister(id);
            try { await mux.SendAsync(Mux.Close, id, default, _lifetime.Token); } catch { }
            try { game?.Close(); } catch { }
        }
    }

    // ---------- 直连尝试 ----------

    async Task<TcpClient?> TryDirectAsync(PeerInfo peer)
    {
        if (ForceRelay) { EmitLog("（自测）强制走中继"); return null; }
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        bool sameNet = peer.Ip == _myIp;
        if (sameNet)
        {
            EmitLog("对方与本机在同一网络，走局域网直连");
            if (Role == Role.Host) return await ListenOnceAsync(deadline);
            return await DialLanAsync(peer, deadline);
        }
        EmitLog($"跨网络打洞 {peer.Ip}:{peer.Port} …");
        return await PunchAsync(peer, deadline);
    }

    async Task<TcpClient?> ListenOnceAsync(DateTime deadline)
    {
        var l = new TcpListener(IPAddress.Any, _bindPort);
        l.Start();
        try
        {
            var remain = deadline - DateTime.UtcNow;
            if (remain <= TimeSpan.Zero) return null;
            var accept = l.AcceptTcpClientAsync();
            var done = await Task.WhenAny(accept, Task.Delay(remain));
            return done == accept ? await accept : null;
        }
        catch { return null; }
        finally { try { l.Stop(); } catch { } }
    }

    async Task<TcpClient?> DialLanAsync(PeerInfo peer, DateTime deadline)
    {
        var ips = peer.Lan.Length > 0 ? peer.Lan : new[] { peer.Ip };
        while (DateTime.UtcNow < deadline)
        {
            foreach (var ip in ips)
            {
                try
                {
                    var c = NewBoundClient();
                    await c.ConnectAsync(IPAddress.Parse(ip), peer.Port).WaitAsync(TimeSpan.FromMilliseconds(700));
                    EmitLog($"局域网直连 {ip}:{peer.Port}");
                    return c;
                }
                catch { }
            }
            await Task.Delay(150);
        }
        return null;
    }

    async Task<TcpClient?> PunchAsync(PeerInfo peer, DateTime deadline)
    {
        var ip = IPAddress.Parse(peer.Ip);
        while (DateTime.UtcNow < deadline)
        {
            TcpClient c;
            try { c = NewBoundClient(); }
            catch { await Task.Delay(150); continue; } // 端口暂未释放
            try
            {
                await c.ConnectAsync(ip, peer.Port).WaitAsync(TimeSpan.FromMilliseconds(800));
                return c;
            }
            catch { c.Dispose(); }
            await Task.Delay(120);
        }
        return null;
    }

    TcpClient NewBoundClient() => new(new IPEndPoint(IPAddress.Any, _bindPort));

    // ---------- 小工具 ----------

    async Task SendAsync<T>(T msg, CancellationToken ct)
    {
        if (_cw is null) return;
        await _cw.WriteAsync(Wire.Line(msg)).WaitAsync(ct);
    }

    void SetState(SessionState s, string detail)
    {
        State = s;
        Detail = detail;
        RaiseStats();
    }

    void RaiseStats()
    {
        if (_mux is not null)
        {
            BytesUp = Interlocked.Read(ref _mux.BytesUp);
            BytesDown = Interlocked.Read(ref _mux.BytesDown);
        }
        Changed?.Invoke();
    }

    Task<string> NewRoomCodeAsync(CancellationToken ct)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var code = new string(Enumerable.Range(0, 5).Select(_ => alphabet[Random.Shared.Next(alphabet.Length)]).ToArray());
        return Task.FromResult(code);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static string[] LocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address.ToString());
            }
        }
        catch { }
        list.Add("127.0.0.1");
        return list.Distinct().ToArray();
    }

    static (string, int) SplitHostPort(string s, int def)
    {
        s = s.Trim();
        if (s.StartsWith('['))
        {
            int e = s.IndexOf(']');
            if (e > 0) return (s[1..e], e + 1 < s.Length && s[e + 1] == ':' ? int.Parse(s[(e + 2)..]) : def);
        }
        int c = s.LastIndexOf(':');
        return c > 0 && int.TryParse(s[(c + 1)..], out var p) ? (s[..c], p) : (s, def);
    }
}

















