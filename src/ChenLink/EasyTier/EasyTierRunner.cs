// EasyTier 子进程驱动：把随程序内置的官方二进制解包到本地，以管理员身份运行
// easytier-core 加入公共共享节点组网；再通过 easytier-cli(RPC, JSON) 轮询
// 本机虚拟 IP 与对端列表，并探测对端的游戏端口(如 MC 25565)。
// EasyTier(LGPL-3.0) 只作为独立进程调用、不链接，详见 THIRD_PARTY_NOTICES.md。
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Principal;
using System.Text.Json;

namespace ChenLink.EasyTier;

public sealed class EasyTierPeerInfo
{
    public string Hostname { get; set; } = "";
    public string Ipv4 { get; set; } = "";
    public string Id { get; set; } = "";
}

/// 一个可用公共节点及其 TCP 连接延迟（毫秒）
public sealed class EasyTierNodePing
{
    public string Node { get; init; } = "";
    public int Ms { get; init; }
}

public sealed class EasyTierRunner : IDisposable
{
    /// 默认公共共享节点（已验证可达的社区节点 + 官方节点；可在 UI 里改）
    public static readonly string[] DefaultNodes =
    {
        "tcp://38.147.105.178:11010", // 已验证可达的社区公共节点；想更多可用上方“自动获取”
    };

    static readonly string[] NativeFiles = { "easytier-core.exe", "easytier-cli.exe", "wintun.dll", "Packet.dll", "WinDivert64.sys" };

    /// 内置候选公共节点池（官方 + 社区）。点“自动获取可用公共节点”时会逐个 TCP 探测，
    /// 只保留当前可达的；同时尝试拉取官方/社区聚合列表补充候选。
    public static readonly string[] CandidateNodes =
    {
        "public.easytier.top", "public.easytier.cn", "easytier.public.kkrainbow.top",
        "38.147.105.178", "103.218.44.24", "101.34.126.42",
        "45.91.92.220", "154.12.36.17", "154.12.28.56",
    };

    /// 聚合/公告源（JSON 或网页都行，只抽取里面的 tcp/udp://host:port 与 ip:port）
    static readonly string[] NodeListUrls =
    {
        "https://ruixuan.online/uptime/status/easytier", // 社区监控源（带 * 的打码节点会被正则自动跳过）
        "https://uptime.easytier.cn/api/nodes?page=1&per_page=100",
        "https://easytier.gd.nkbpal.cn/status/easytier",
    };

    /// 自动探测“当前可达”的公共节点，返回 tcp://host:port 列表（TCP 11010 通则视为可用）。
    public static async Task<EasyTierNodePing[]> AutoFetchNodesAsync(int perTimeoutMs = 1500, int urlTimeoutMs = 8000)
    {
        var pool = new List<string>(CandidateNodes);
        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(urlTimeoutMs) };
        foreach (var u in NodeListUrls)
        {
            try
            {
                var body = await http.GetStringAsync(u);
                pool.AddRange(ExtractHosts(body));
            }
            catch { /* 该源连不上就跳过 */ }
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tasks = pool.Where(x => !string.IsNullOrWhiteSpace(x))
                       .Select(h => h.Trim())
                       .Where(h => seen.Add(ExtractHostPort(h).host))
                       .Select(async h => await IsHostReachableAsync(h, perTimeoutMs));
        var results = await Task.WhenAll(tasks);
        return results.Where(r => r.ok)
                      .Select(r => new EasyTierNodePing { Node = "tcp://" + r.endpoint, Ms = r.ms })
                      .DistinctBy(n => n.Node)
                      .OrderBy(n => n.Ms)
                      .ToArray();
    }

    static async Task<(bool ok, string endpoint, int ms)> IsHostReachableAsync(string hostPort, int timeoutMs)
    {
        var (host, port) = ExtractHostPort(hostPort);
        var sw = System.Diagnostics.Stopwatch.StartNew(); // TCP 建连耗时≈节点往返延迟
        try
        {
            using var c = new TcpClient();
            using var t = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(host, port, t.Token);
            return (true, $"{host}:{port}", (int)sw.ElapsedMilliseconds);
        }
        catch { return (false, $"{host}:{port}", 0); }
    }

    static (string host, int port) ExtractHostPort(string s)
    {
        s = s.Trim();
        if (s.Contains("://")) s = s[(s.IndexOf("://") + 3)..];
        int slash = s.IndexOf('/'); if (slash >= 0) s = s[..slash];
        int colon = s.LastIndexOf(':');
        if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p is > 0 and < 65536) return (s[..colon], p);
        return (s, 11010);
    }

    /// 从网页/JSON 文本里抠 host:port 候选（限端口 1024-65535，避开无关 URL）
    static IEnumerable<string> ExtractHosts(string body)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"(?:tcp|udp|ws|wss)://([A-Za-z0-9._\-]+|\[[0-9a-fA-F:]+\]):(\d{2,5})"))
        {
            if (int.TryParse(m.Groups[2].Value, out int p) && p is >= 11010 and <= 11013) yield return m.Groups[1].Value + ":" + p;
        }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(body, @"\b((?:\d{1,3}\.){3}\d{1,3}):(\d{2,5})\b"))
        {
            if (int.TryParse(m.Groups[2].Value, out int p) && p is >= 11010 and <= 11013) yield return m.Groups[1].Value + ":" + p;
        }
    }

    public event Action<string>? Log;
    public event Action? Changed;

    public string OwnIp { get; private set; } = "";
    public string SelfHostname { get; private set; } = "";
    public string Network { get; private set; } = "";
    public bool Ready { get; private set; }
    public IReadOnlyList<EasyTierPeerInfo> Peers { get; private set; } = Array.Empty<EasyTierPeerInfo>();
    public string StatusText { get; private set; } = "未启动";

    public static bool IsAdministrator =>
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    readonly string _toolsDir;
    readonly string _cli;
    readonly int _rpcPort;
    readonly string _logDir;
    Process? _core;
    CancellationTokenSource? _cts;
    Task? _poll;

    public EasyTierRunner()
    {
        _toolsDir = EnsureTools();
        _cli = Path.Combine(_toolsDir, "easytier-cli.exe");
        _logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChenLink", "logs");
        _rpcPort = FreePort();
    }

    /// 启动组网：network 即“房间名”，secret 即口令；nodes 为公共节点列表。
    public async Task StartAsync(string network, string secret, string[] nodes, string nick, string tag)
    {
        Network = network;
        if (_core is { HasExited: false }) { Log?.Invoke("已在运行，先停止旧的"); await StopAsync(); }

        var hostname = string.IsNullOrWhiteSpace(nick) ? Environment.MachineName : nick.Trim();
        hostname = new string(hostname.Where(char.IsLetterOrDigit).Take(16).ToArray());
        if (hostname.Length == 0) hostname = "node";
        hostname += "-" + (tag == "host" ? "房主" : "玩家");
        SelfHostname = hostname;

        var args = new List<string>
        {
            "--network-name", network,
            "--network-secret", secret,
            "--hostname", hostname,
            "--instance-name", "chenlink-" + Random.Shared.Next(1000, 9999),
            "-d", "true",
            "-r", $"127.0.0.1:{_rpcPort}",
            "--file-log-dir", _logDir,
            "--file-log-level", "info",
        };
        foreach (var n in nodes)
            if (!string.IsNullOrWhiteSpace(n)) { args.Add("-p"); args.Add(n.Trim()); }

        Directory.CreateDirectory(_logDir);
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(_toolsDir, "easytier-core.exe"),
            WorkingDirectory = _toolsDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        _cts = new CancellationTokenSource();
        _core = Process.Start(psi);
        if (_core is null) throw new Exception("无法启动 easytier-core");

        Log?.Invoke($"正在通过公共节点组网（房间 {network}）…");
        StatusText = "正在组网…";
        _poll = Task.Run(() => PollLoopAsync(_cts.Token));
        await Task.CompletedTask;
    }

    async Task PollLoopAsync(CancellationToken ct)
    {
        string last = "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_core is { HasExited: true })
                {
                    StatusText = "easytier-core 已退出，请重试";
                    Log?.Invoke("⚠️ easytier-core 意外退出：多为端口被旧进程占用，下次启动会自动清理并重试");
                    Changed?.Invoke();
                    break;
                }
                var (ip, host) = await QueryNodeAsync();
                var peers = await QueryPeersAsync();
                OwnIp = ip; Peers = peers;
                if (!Ready && !string.IsNullOrEmpty(OwnIp))
                {
                    Ready = true;
                    StatusText = "组网就绪";
                    Log?.Invoke($"✅ 已加入虚拟网络，本机虚拟 IP：{OwnIp}");
                }
                else if (!Ready) StatusText = "正在申请虚拟 IP…";
                else StatusText = "已就绪";
                var snap = $"{OwnIp}|{host}|{string.Join(',', peers.Select(x => x.Hostname + "=" + x.Ipv4))}|{Ready}";
                if (snap != last) { last = snap; Changed?.Invoke(); }
            }
            catch { /* RPC 未就绪属正常 */ }
            try { await Task.Delay(3000, ct); } catch { break; }
        }
    }

    async Task<(string, string)> QueryNodeAsync()
    {
        var s = await RunCliAsync("node");
        using var doc = JsonDocument.Parse(s);
        var r = doc.RootElement;
        var raw = r.TryGetProperty("ipv4_addr", out var ip) ? ip.GetString() ?? "" : ""; // 形如 10.126.126.1/24
        var slash = raw.IndexOf('/');
        return (slash > 0 ? raw[..slash] : raw,
                r.TryGetProperty("hostname", out var hn) ? hn.GetString() ?? "" : "");
    }

    async Task<List<EasyTierPeerInfo>> QueryPeersAsync()
    {
        var list = new List<EasyTierPeerInfo>();
        var s = await RunCliAsync("peer");
        using var doc = JsonDocument.Parse(s);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var it in doc.RootElement.EnumerateArray())
        {
            var p = new EasyTierPeerInfo
            {
                Hostname = it.TryGetProperty("hostname", out var h) ? h.GetString() ?? "" : "",
                Ipv4 = it.TryGetProperty("ipv4", out var i) ? i.GetString() ?? "" : "",
                Id = it.TryGetProperty("id", out var d) ? d.GetString() ?? "" : "",
            };
            if (!string.IsNullOrEmpty(p.Hostname)) list.Add(p);
        }
        return list;
    }

    async Task<string> RunCliAsync(string sub)
    {
        var psi = new ProcessStartInfo
        {
            FileName = _cli,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _toolsDir,
        };
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add($"127.0.0.1:{_rpcPort}");
        psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("json");
        psi.ArgumentList.Add(sub);
        using var proc = Process.Start(psi) ?? throw new Exception("无法启动 easytier-cli");
        // 先启动异步读取，再等退出，避免 stdout 缓冲区满导致死锁
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        try
        {
            await proc.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
        }
        catch (TimeoutException) { try { proc.Kill(); } catch { } }
        var stdout = await stdoutTask;
        _ = await stderrTask;
        if (!proc.HasExited || proc.ExitCode != 0) throw new Exception("cli " + sub + " 失败");
        return stdout;
    }

    /// 探测某个 IP 的游戏端口是否开放（TCP）
    public static async Task<bool> IsPortOpenAsync(string ip, int port, int timeoutMs = 700)
    {
        if (!IPAddress.TryParse(ip, out _)) return false;
        try
        {
            using var c = new TcpClient();
            using var t = new CancellationTokenSource(timeoutMs);
            await c.ConnectAsync(IPAddress.Parse(ip), port, t.Token);
            return true;
        }
        catch { return false; }
    }

    public async Task StopAsync()
    {
        _cts?.Cancel();
        if (_core is not null)
        {
            try { if (!_core.HasExited) { _core.Kill(); _core.WaitForExit(3000); } } catch { }
            _core = null;
        }
        Ready = false;
        OwnIp = "";
        Peers = Array.Empty<EasyTierPeerInfo>();
        StatusText = "已停止";
        if (_poll is not null) { try { await _poll; } catch { } _poll = null; }
        Log?.Invoke("虚拟网络已断开");
    }

    public void Dispose() { try { _cts?.Cancel(); } catch { } }

    /// 清掉可能残留的 easytier-core（本工具独立使用 easytier，同机不会再跑别的实例）
    static void KillLeftoverCores()
    {
        try
        {
            var psi = new ProcessStartInfo("taskkill") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("/F"); psi.ArgumentList.Add("/IM"); psi.ArgumentList.Add("easytier-core.exe");
            using var p = Process.Start(psi);
            p?.WaitForExit(2000);
        }
        catch { }
    }
    // ---------- 解包内置二进制 ----------

    static string EnsureTools()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChenLink", "easytier");
        Directory.CreateDirectory(dir);
        var asm = typeof(EasyTierRunner).Assembly;
        foreach (var name in NativeFiles)
        {
            var dest = Path.Combine(dir, name);
            bool need = !File.Exists(dest);
            if (!need)
            {
                try
                {
                    using var res = asm.GetManifestResourceStream("native." + name);
                    if (res is not null) need = new FileInfo(dest).Length != res.Length;
                }
                catch { need = true; }
            }
            if (need)
            {
                using var src = asm.GetManifestResourceStream("native." + name)
                    ?? throw new FileNotFoundException("缺少内置组件 native." + name);
                using var fs = File.Create(dest);
                src.CopyTo(fs);
            }
        }
        return dir;
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}







