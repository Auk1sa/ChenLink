// ChenLink 信令协议：控制连接与中继握手都是 "\n" 结尾的 JSON 行。
// 设计对应 OpenP2P 的简化：房间码替代账号/控制台，TCP 打洞直连 + 服务器中继
// 替代其 QUIC + 共享节点中继。本文件只依赖 BCL，供客户端与服务端/自测共享。
using System.Text;
using System.Text.Json;

namespace ChenLink.Engine;

public static class Msg
{
    public const string Hello = "hello";  // client -> server：登记进房间
    public const string You   = "you";    // server -> client：你的公网 IP（判断是否同 NAT/本机）
    public const string Peer  = "peer";   // server -> client：对方信息
    public const string Go    = "go";     // server -> client：房间已满，开始打通
    public const string Bye   = "bye";    // 任意方向：离开/对方离开
    public const string Relay = "relay";  // 中继数据连接握手
    public const string Ping  = "ping";   // client -> server：心跳（保活，防 NAT 空闲断开）
    public const string Pong  = "pong";   // server -> client：心跳应答
    public const string Err   = "err";
}

public sealed class HelloMsg
{
    public string T { get; set; } = Msg.Hello;
    public string Room { get; set; } = "";
    public string Role { get; set; } = "";            // host | join
    public string Name { get; set; } = "";
    public int Port { get; set; }                     // 本机用于直连的本地端口
    public string[] Lan { get; set; } = Array.Empty<string>();
    public string Secret { get; set; } = "";          // 房间口令（可选；为空表示无口令房间）
}

public sealed class YouMsg { public string T { get; set; } = Msg.You; public string Ip { get; set; } = ""; }

public sealed class PeerMsg { public string T { get; set; } = Msg.Peer; public PeerInfo Peer { get; set; } = new(); }

public sealed class PeerInfo
{
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Ip { get; set; } = "";              // 对方公网 IP
    public int Port { get; set; }                     // 对方本地直连端口
    public string[] Lan { get; set; } = Array.Empty<string>();
}

public sealed class GoMsg { public string T { get; set; } = Msg.Go; public string Room { get; set; } = ""; }
public sealed class ByeMsg { public string T { get; set; } = Msg.Bye; public string Why { get; set; } = ""; }
public sealed class RelayMsg { public string T { get; set; } = Msg.Relay; public string Room { get; set; } = ""; public string Role { get; set; } = ""; }
public sealed class ErrMsg { public string T { get; set; } = Msg.Err; public string Text { get; set; } = ""; }
public sealed class PingMsg { public string T { get; set; } = Msg.Ping; }
public sealed class PongMsg { public string T { get; set; } = Msg.Pong; }

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Line<T>(T obj) => JsonSerializer.Serialize(obj, Json) + "\n";

    public static T? Parse<T>(string s) => JsonSerializer.Deserialize<T>(s, Json);

    /// 从原始流读一行（握手用，读取字节直到 \n），EOF 返回 null，超长抛异常。
    public static async Task<string?> ReadLineRawAsync(Stream s, CancellationToken ct = default)
    {
        var buf = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, ct).ConfigureAwait(false);
            if (n == 0) return buf.Length == 0 ? null : throw new EndOfStreamException("连接在行中间断开");
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(buf.ToArray()).TrimStart('\uFEFF').TrimEnd('\r');
            if (buf.Length > 64 * 1024) throw new InvalidDataException("协议行过长");
            buf.WriteByte(one[0]);
        }
    }

    public static async Task WriteLineAsync(Stream s, string line, CancellationToken ct = default)
    {
        var b = Encoding.UTF8.GetBytes(line);
        await s.WriteAsync(b, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }
}



