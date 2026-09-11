// 在一个持久 TCP 流（直连或中继）上复用多条游戏连接。
// 帧：[4B 长度][1B 类型][4B 通道id][负载]；长度含类型..负载。
// 类型：1 OPEN 2 OPEN_OK 3 DATA 4 CLOSE 5 UDP
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading.Channels;

namespace ChenLink.Engine;

public sealed class Mux
{
    public const int MaxPayload = 64 * 1024;
    const int HeaderLen = 5; // type(1) + id(4)

    public const byte Open = 1, OpenOk = 2, Data = 3, Close = 4, Udp = 5;

    readonly Stream _s;
    readonly SemaphoreSlim _writeLock = new(1, 1);
    readonly ConcurrentDictionary<int, MuxChannel> _ch = new();
    readonly ConcurrentDictionary<int, TaskCompletionSource<bool>> _pending = new();
    readonly byte[] _lenBuf = new byte[4];       // 读循环单线程使用，复用避免每帧分配
    readonly byte[] _frameBuf = new byte[9];     // 写帧头（len4+type1+id4），_writeLock 保护下复用
    int _nextId = 1;

    public long BytesUp;   // 本端 -> 对端
    public long BytesDown; // 对端 -> 本端

    public event Action<int>? Opened;          // host：收到 OPEN
    public event Action<string>? Failed;
    public event Action<byte[]>? UdpData;          // 收到一条 UDP 数据报（负载=原始报文）


    public Mux(Stream s) => _s = s;

    public int NextChannelId() => Interlocked.Increment(ref _nextId);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            
            while (!ct.IsCancellationRequested)
            {
                await ReadExactlyAsync(_s, _lenBuf, ct).ConfigureAwait(false);
                int len = ReadInt(_lenBuf, 0);
                if (len < HeaderLen || len > HeaderLen + MaxPayload) throw new InvalidDataException($"非法帧长度 {len}");
                var body = new byte[len]; // type(1) + id(4) + payload；被 Channel/事件异步持有，不池化
                await ReadExactlyAsync(_s, body, ct).ConfigureAwait(false);
                byte type = body[0];
                int id = ReadInt(body, 1);
                var payload = len == HeaderLen ? Array.Empty<byte>() : body[HeaderLen..];

                Dispatch(type, id, payload);
            }
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested)
            {
                foreach (var c in _ch.Values) c.EndRemote();
                _ch.Clear();
                foreach (var p in _pending.Values) p.TrySetResult(false);
                _pending.Clear();
                Failed?.Invoke(e.Message);
            }
        }
    }

    void Dispatch(byte type, int id, byte[] body)
    {
        switch (type)
        {
            case Open: Opened?.Invoke(id); break;
            case OpenOk: if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(true); break;
            case Data: if (_ch.TryGetValue(id, out var c)) c.Push(body); break;
            case Close:
                if (_ch.TryRemove(id, out var cc)) cc.EndRemote();
                else if (_pending.TryRemove(id, out var pt)) pt.TrySetResult(false);
                break;
            case Udp:
                Interlocked.Add(ref BytesDown, body.Length);
                UdpData?.Invoke(body);
                break;
            default: break;
        }
    }

    public Task SendAsync(byte type, int id, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => SendAsyncCore(type, id, payload, ct);

    async Task SendAsyncCore(byte type, int id, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxPayload) throw new ArgumentOutOfRangeException(nameof(payload));
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 帧头写入必须在锁内，否则多通道并发会互相覆盖 _frameBuf
            WriteInt(_frameBuf, 0, HeaderLen + payload.Length);
            _frameBuf[4] = type;
            WriteInt(_frameBuf, 5, id);
            await _s.WriteAsync(_frameBuf, ct).ConfigureAwait(false);
            if (!payload.IsEmpty) await _s.WriteAsync(payload, ct).ConfigureAwait(false);
            await _s.FlushAsync(ct).ConfigureAwait(false);

        }
        finally { _writeLock.Release(); }
    }

    /// 发送一条 UDP 数据报（与 TCP 帧共用同一条链路，通道 id 恒为 0）。
    public Task SendUdpAsync(ReadOnlyMemory<byte> payload, CancellationToken ct = default)
        => SendUdpCoreAsync(payload, ct);

    async Task SendUdpCoreAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        await SendAsyncCore(Udp, 0, payload, ct).ConfigureAwait(false);
        Interlocked.Add(ref BytesUp, payload.Length);
    }

    public async Task<bool> OpenAsync(int id, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(Open, id, default, ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        }
        catch (TimeoutException) { return false; }
        catch (OperationCanceledException) { return false; }
        finally { _pending.TryRemove(id, out _); }
    }

    public void Register(int id, MuxChannel c) => _ch[id] = c;
    public void Unregister(int id) => _ch.TryRemove(id, out _);

    static int ReadInt(byte[] b, int off) =>
        (b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3];

    static void WriteInt(byte[] b, int off, int v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    static async Task ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("对端已关闭");
            got += n;
        }
    }
}

/// 一条游戏 TCP 连接与 Mux 之间的双向管道。
public sealed class MuxChannel
{
    readonly Mux _mux;
    readonly Channel<byte[]> _in = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(128) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    int _closed;

    public int Id { get; }
    public TcpClient Local { get; }
    public event Action? Ended;

    public MuxChannel(Mux mux, int id, TcpClient local) { _mux = mux; Id = id; Local = local; }

    public void Start()
    {
        _ = Task.Run(PumpUpAsync);
        _ = Task.Run(PumpDownAsync);
    }

    async Task PumpUpAsync()
    {
        var buf = new byte[8192];
        try
        {
            var s = Local.GetStream();
            while (true)
            {
                int n = await s.ReadAsync(buf).ConfigureAwait(false);
                if (n == 0) break;
                await _mux.SendAsync(Mux.Data, Id, buf.AsMemory(0, n)).ConfigureAwait(false);
                Interlocked.Add(ref _mux.BytesUp, n);
            }
        }
        catch { }
        finally
        {
            try { await _mux.SendAsync(Mux.Close, Id, default).ConfigureAwait(false); } catch { }
            CloseLocal();
        }
    }

    async Task PumpDownAsync()
    {
        try
        {
            await foreach (var d in _in.Reader.ReadAllAsync())
            {
                await Local.GetStream().WriteAsync(d).ConfigureAwait(false);
                Interlocked.Add(ref _mux.BytesDown, d.Length);
            }
        }
        catch { }
        finally { CloseLocal(); }
    }

    internal void Push(byte[] data)
    {
        if (!_in.Writer.TryWrite(data)) CloseLocal();
    }

    internal void EndRemote()
    {
        try { _in.Writer.TryComplete(); } catch { }
    }

    void CloseLocal()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        try { Local.Close(); } catch { }
        Ended?.Invoke();
    }
}




