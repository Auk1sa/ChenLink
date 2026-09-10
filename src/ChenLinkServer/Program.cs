// ChenLinkServer：信令 + 中继服务器（控制台入口）。
//   ChenLinkServer [端口]     启动服务器（默认 9100）
//   ChenLinkServer --selftest 本机端到端自测（含直连与中继两条链路）
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChenLink.Engine;
using ChenLinkServer;

if (args.Length > 0 && args[0] == "--selftest")
{
    int code = await SelfTest.RunAsync();
    Environment.Exit(code);
    return;
}

int port = args.Length > 0 && int.TryParse(args[0], out var pp) ? pp : Session.DefaultPort;
await ChenLinkServer.ChenLinkServerCore.RunAsync(port, CancellationToken.None);

static class SelfTest
{
    public static async Task<int> RunAsync()
    {
        try
        {
            int port = FreePort();
            _ = Task.Run(() => ChenLinkServerCore.RunAsync(port, CancellationToken.None));
            await Task.Delay(200);

            int gamePort = FreePort();
            using var echo = new TcpListener(IPAddress.Loopback, gamePort);
            echo.Start();
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var cl = await echo.AcceptTcpClientAsync();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using (cl)
                            {
                                var s = cl.GetStream();
                                var buf = new byte[4096];
                                int n;
                                while ((n = await s.ReadAsync(buf)) > 0) await s.WriteAsync(buf.AsMemory(0, n));
                            }
                        }
                        catch { }
                    });
                }
            });

            // UDP 回显服务器（验证 UDP 转发链路：直连与中继都要通）
            using var uecho = new UdpClient(new IPEndPoint(IPAddress.Loopback, gamePort));
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        var r = await uecho.ReceiveAsync();
                        await uecho.SendAsync(r.Buffer, r.Buffer.Length, r.RemoteEndPoint);
                    }
                }
                catch { }
            });

            Console.WriteLine("== 自测 1：局域网/同网直连 ==");
            await DirectScenarioAsync(port, gamePort, forceRelay: false, expectDirect: true);

            Console.WriteLine("== 自测 2：监听端口占用时不误报就绪 ==");
            await ListenerConflictScenarioAsync(port, gamePort);

            Console.WriteLine("== 自测 3：强制服务器中继 ==");
            await DirectScenarioAsync(port, gamePort, forceRelay: true, expectDirect: false);

            Console.WriteLine();
            Console.WriteLine("SELFTEST PASS");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("SELFTEST FAIL：" + e);
            return 1;
        }
    }

    static async Task DirectScenarioAsync(int serverPort, int gamePort, bool forceRelay, bool expectDirect)
    {
        Session.ForceRelay = forceRelay;
        int mapPort = FreePort();
        var host = new Session($"127.0.0.1:{serverPort}", "测试房主");
        var join = new Session($"127.0.0.1:{serverPort}", "测试玩家");
        host.Log += m => Console.WriteLine("  [房主] " + m);
        join.Log += m => Console.WriteLine("  [玩家] " + m);
        try
        {
            await host.CreateRoomAsync(gamePort);
            Assert(host.Room.Length == 5, "房间码应为 5 位");
            await join.JoinRoomAsync(host.Room, mapPort);

            await WaitUntil(() => host.State == SessionState.Ready && join.State == SessionState.Ready, 20_000);
            Console.WriteLine($"  链路就绪：房主={host.Mode}，玩家={join.Mode}");
            if (expectDirect)
            {
                Assert(host.Mode == "直连" && join.Mode == "直连", "同网应走直连");
            }
            else
            {
                Assert(host.Mode == "中继" && join.Mode == "中继", "强制中继应显示中继");
            }

            // 3 条顺序连接 + 2 条并发连接都要能回显
            for (int i = 0; i < 3; i++) await EchoOnceAsync(mapPort, $"hello-{i}");
            await Task.WhenAll(EchoOnceAsync(mapPort, "并行A"), EchoOnceAsync(mapPort, "并行B"));
            await WaitUntil(() => host.Channels == 0 && join.Channels == 0, 3_000);
            await EchoUdpOnceAsync(mapPort, "udp-echo");

            Assert(host.BytesUp + host.BytesDown + join.BytesUp + join.BytesDown > 0, "应有流量计数");
            Console.WriteLine("  游戏回显 5/5 通过，UDP 回显通过，流量计数正常");
        }
        finally
        {
            await host.DisconnectAsync();
            await join.DisconnectAsync();
        }
    }

    static async Task ListenerConflictScenarioAsync(int serverPort, int gamePort)
    {
        int mapPort = FreePort();
        using var blocker = new TcpListener(IPAddress.Loopback, mapPort);
        blocker.Start();
        Session.ForceRelay = false;
        var host = new Session($"127.0.0.1:{serverPort}", "测试房主");
        var join = new Session($"127.0.0.1:{serverPort}", "测试玩家");
        try
        {
            await host.CreateRoomAsync(gamePort);
            await join.JoinRoomAsync(host.Room, mapPort);
            await WaitUntil(() => join.State is SessionState.Failed or SessionState.Ready, 20_000);
            Assert(join.State == SessionState.Failed, "监听端口被占用时不应进入就绪状态");
            Console.WriteLine("  端口冲突已正确报错");
        }
        finally
        {
            await join.DisconnectAsync();
            await host.DisconnectAsync();
        }
    }

    static async Task EchoOnceAsync(int port, string payload)
    {
        using var cl = new TcpClient();
        await cl.ConnectAsync(IPAddress.Loopback, port);
        var s = cl.GetStream();
        var data = Encoding.UTF8.GetBytes(payload);
        await s.WriteAsync(data);
        var buf = new byte[256];
        int got = 0;
        while (got < data.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got));
            Assert(n > 0, "回显提前结束");
            got += n;
        }
        Assert(Encoding.UTF8.GetString(buf, 0, got) == payload, "回显内容不一致");
    }

    static async Task EchoUdpOnceAsync(int port, string payload)
    {
        using var u = new UdpClient();
        var data = Encoding.UTF8.GetBytes(payload);
        await u.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Loopback, port));
        var got = await u.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert(got.Buffer.AsSpan().SequenceEqual(data), "UDP 回显内容不一致");
    }

    static async Task WaitUntil(Func<bool> cond, int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) await Task.Delay(50);
        Assert(cond(), $"等待超时（{ms}ms）");
    }

    static void Assert(bool ok, string msg)
    {
        if (!ok) throw new Exception("断言失败：" + msg);
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








