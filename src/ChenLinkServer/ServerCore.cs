// ChenLinkServerCore：信令 + 中继服务器核心（纯 BCL）。GUI 可内嵌，控制台也可独立运行。
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChenLink.Engine;

namespace ChenLinkServer;
public static class ChenLinkServerCore
{
    sealed class Ctrl
    {
        public TcpClient C = null!;
        public StreamReader R = null!;
        public StreamWriter W = null!;
        public string Room = "";
        public string Role = "";      // host | join
        public string Name = "";
        public int Port;
        public string[] Lan = Array.Empty<string>();
    }

    sealed class RoomState
    {
        public Ctrl? Host;
        public Ctrl? Join;
        public TcpClient? HostRelay; public TaskCompletionSource? HostRelayTcs;
        public TcpClient? JoinRelay; public TaskCompletionSource? JoinRelayTcs;
    }

    static readonly ConcurrentDictionary<string, RoomState> Rooms = new();
    const string RoomAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    static bool ValidRoom(string? room) =>
        room is { Length: 5 } && room.All(c => RoomAlphabet.Contains(c));

    static bool ValidRole(string? role) => role is "host" or "join";

    static string CleanName(string? name)
    {
        var cleaned = new string((name ?? "").Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
        return cleaned.Length == 0 ? "未命名" : cleaned;
    }

    public static async Task RunAsync(int port, CancellationToken ct = default)
    {
        var l = new TcpListener(IPAddress.Any, port);
        l.Start();
        Console.WriteLine($"[ChenLink 服务器] 0.0.0.0:{port}（信令 + 中继）");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var c = await l.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleAsync(c));
            }
        }
        catch (OperationCanceledException) { }
        finally { try { l.Stop(); } catch { } }
    }

    static async Task HandleAsync(TcpClient c)
    {
        try
        {
            var s = c.GetStream();
            var first = await Wire.ReadLineRawAsync(s);
            if (first is null) return;
            var root = Wire.Parse<Dictionary<string, object?>>(first);
            var t = root?.GetValueOrDefault("t")?.ToString();
            if (t == Msg.Relay)
            {
                if (Wire.Parse<RelayMsg>(first) is { } m) await HandleRelayAsync(c, m);
                else c.Close();
                return;
            }
            if (t == Msg.Hello && Wire.Parse<HelloMsg>(first) is { } h)
                await HandleControlAsync(c, s, h);
            else c.Close();
        }
        catch (Exception e) { Console.WriteLine("连接异常：" + e.Message); try { c.Close(); } catch { } }
    }

    // ---------- 控制连接：登记房间 + 配对 ----------

    static async Task HandleControlAsync(TcpClient c, Stream s, HelloMsg h)
    {
        h.Room = h.Room?.Trim().ToUpperInvariant() ?? "";
        h.Name = CleanName(h.Name);
        h.Lan = (h.Lan ?? Array.Empty<string>())
            .Where(x => IPAddress.TryParse(x, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            .Distinct(StringComparer.Ordinal)
            .Take(16)
            .ToArray();
        if (!ValidRole(h.Role) || !ValidRoom(h.Room) || h.Port is < 1 or > 65535)
        {
            try
            {
                await using var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true };
                await w.WriteAsync(Wire.Line(new ErrMsg { Text = "无效的房间、角色或端口" }));
            }
            catch { }
            return;
        }

        var ctrl = new Ctrl
        {
            C = c,
            R = new StreamReader(s, new UTF8Encoding(false)),
            W = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true },
            Room = h.Room,
            Role = h.Role,
            Name = h.Name,
            Port = h.Port,
            Lan = h.Lan,
        };
        string myIp = RemoteIp(c);
        string err = "";
        Ctrl? host = null, join = null;

        lock (Rooms)
        {
            if (h.Role == "host")
            {
                if (Rooms.ContainsKey(h.Room)) err = "房间码已被占用，请重新创建房间";
                else Rooms[h.Room] = new RoomState { Host = ctrl };
            }
            else
            {
                if (!Rooms.TryGetValue(h.Room, out var room) || room.Host is null)
                {
                    err = "房间不存在或房主已离线";
                    Console.WriteLine($"[join] 房间 {h.Room} 不存在(hasKey={Rooms.ContainsKey(h.Room)})");
                }
                else if (room.Join is not null) err = "房间已满";
                else { room.Join = ctrl; host = room.Host; join = ctrl; }
            }
        }

        if (err != "")
        {
            try { await ctrl.W.WriteAsync(Wire.Line(new ErrMsg { Text = err })); } catch { }
            try { c.Close(); } catch { }
            return;
        }

        try
        {
            await ctrl.W.WriteAsync(Wire.Line(new YouMsg { Ip = myIp }));
            if (h.Role == "host")
            {
                Console.WriteLine($"[{h.Room}] 房主 {h.Name} 创建房间");
            }
            else
            {
                await host!.W.WriteLineAsync(Wire.Line(new PeerMsg { Peer = ToPeer(join!) }));
                await host.W.WriteLineAsync(Wire.Line(new GoMsg { Room = h.Room }));
                await join!.W.WriteLineAsync(Wire.Line(new PeerMsg { Peer = ToPeer(host) }));
                await join.W.WriteLineAsync(Wire.Line(new GoMsg { Room = h.Room }));
                Console.WriteLine($"[{h.Room}] 玩家 {h.Name} 加入，双方开始打通");
            }

            while (true)
            {
                var line = await ctrl.R.ReadLineAsync();
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                var r = Wire.Parse<Dictionary<string, object?>>(line);
                if (r?.GetValueOrDefault("t")?.ToString() == Msg.Bye) break;
            }
        }
        catch { /* 断开即清理 */ }

        await RemoveAsync(ctrl);
        try { c.Close(); } catch { }
    }

    static async Task RemoveAsync(Ctrl ctrl)
    {
        Ctrl? peer = null;
        List<TcpClient> close = new();
        lock (Rooms)
        {
            if (Rooms.TryGetValue(ctrl.Room, out var room))
            {
                if (ctrl.Role == "host")
                {
                    peer = room.Join;
                    room.HostRelayTcs?.TrySetResult();
                    room.JoinRelayTcs?.TrySetResult();
                    if (room.HostRelay is not null) close.Add(room.HostRelay);
                    if (room.JoinRelay is not null) close.Add(room.JoinRelay);
                    Rooms.TryRemove(ctrl.Room, out _);
                }
                else
                {
                    peer = room.Host;
                    room.JoinRelayTcs?.TrySetResult();
                    if (room.JoinRelay is not null) close.Add(room.JoinRelay);
                    room.Join = null;
                    room.JoinRelay = null;
                    room.JoinRelayTcs = null;
                }
            }
        }
        foreach (var cc in close) try { cc.Close(); } catch { }
        if (peer is not null)
        {
            try
            {
                await peer.W.WriteAsync(Wire.Line(new ByeMsg { Why = ctrl.Role == "host" ? "房主已离开" : "玩家已离开" }));
            }
            catch { }
            Console.WriteLine($"[{ctrl.Room}] {ctrl.Name} 离开");
        }
    }

    // ---------- 中继连接：同一房间的 host/join 各来一条即双向桥接 ----------

    static async Task HandleRelayAsync(TcpClient c, RelayMsg m)
    {
        if (!ValidRoom(m.Room) || !ValidRole(m.Role)) { c.Close(); return; }

        RoomState? room;
        lock (Rooms)
        {
            if (!Rooms.TryGetValue(m.Room, out room) || room is null || room.Host is null ||
                (m.Role == "join" && room.Join is null)) { c.Close(); return; }
        }
        var rm = room!;

        // 注册自己（每角色只允许一条中继）
        bool claimed;
        lock (Rooms)
        {
            if (m.Role == "host")
            {
                if (rm.HostRelay is null) { rm.HostRelay = c; rm.HostRelayTcs = new TaskCompletionSource(); claimed = true; }
                else claimed = false;
            }
            else
            {
                if (rm.JoinRelay is null) { rm.JoinRelay = c; rm.JoinRelayTcs = new TaskCompletionSource(); claimed = true; }
                else claimed = false;
            }
        }
        if (!claimed) { c.Close(); return; }

        // 尝试取走对方那条并桥接；取不到就等对方（30s 超时）
        TcpClient? other;
        lock (Rooms)
        {
            bool both = rm.HostRelay is not null && rm.JoinRelay is not null;
            if (both)
            {
                other = m.Role == "host" ? rm.JoinRelay : rm.HostRelay;
                rm.HostRelayTcs?.TrySetResult();
                rm.JoinRelayTcs?.TrySetResult();
                rm.HostRelay = null; rm.JoinRelay = null;
            }
            else other = null;
        }

        if (other is not null)
        {
            await BridgeAsync(c, other);      // 本方负责桥接，结束后关闭双方
            return;
        }

        var wait = m.Role == "host" ? rm.HostRelayTcs : rm.JoinRelayTcs;
        await Task.WhenAny(wait!.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        bool stillMine;
        lock (Rooms)
        {
            stillMine = (m.Role == "host" ? rm.HostRelay : rm.JoinRelay) == c;
            if (stillMine)
            {
                if (m.Role == "host") { rm.HostRelay = null; rm.HostRelayTcs = null; }
                else { rm.JoinRelay = null; rm.JoinRelayTcs = null; }
            }
        }
        if (stillMine) c.Close();             // 超时没人来：关闭并清掉自己
        // 否则已被对方取走并桥接，对方负责关闭，这里直接返回
    }

    static async Task BridgeAsync(TcpClient a, TcpClient b)
    {
        try
        {
            using (a)
            using (b)
            {
                var s1 = a.GetStream();
                var s2 = b.GetStream();
                await Task.WhenAll(CopyAsync(s1, s2), CopyAsync(s2, s1));
            }
        }
        catch { }
    }

    static async Task CopyAsync(Stream from, Stream to)
    {
        try { await from.CopyToAsync(to); } catch { }
        try { to.Close(); } catch { }
    }

    static PeerInfo ToPeer(Ctrl ctrl) => new()
    {
        Name = ctrl.Name,
        Role = ctrl.Role,
        Ip = RemoteIp(ctrl.C),
        Port = ctrl.Port,
        Lan = ctrl.Lan,
    };

    static string RemoteIp(TcpClient c) => ((IPEndPoint)c.Client.RemoteEndPoint!).Address.ToString();
}




