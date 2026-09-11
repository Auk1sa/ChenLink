// 端到端加密流：用 AES-GCM 把底层 Stream 包装成加密字节流，对 Mux 完全透明。
// 写入时每次 Write 加密为一帧 [4B 密文总长][12B nonce][密文][16B tag]；
// 读取时按帧解密并内部缓冲，按调用方请求的字节数返回。
// 仅当房间口令非空时启用；双方口令一致则密钥一致（SHA-256 派生）。
using System.Security.Cryptography;

namespace ChenLink.Engine;

public sealed class AesGcmStream : Stream
{
    const int NonceSize = 12;
    const int TagSize = 16;
    const int MaxCipherFrame = 4 + NonceSize + Mux.MaxPayload + TagSize;

    readonly Stream _inner;
    readonly AesGcm _aes;
    long _sendCounter;

    // 读缓冲：已解密的明文，供 ReadAsync 分次消费
    byte[] _recvBuf = Array.Empty<byte>();
    int _recvPos;
    int _recvLen;

    public AesGcmStream(Stream inner, byte[] key)
    {
        _inner = inner;
        _aes = new AesGcm(key, TagSize);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (count == 0) return;
        var nonce = new byte[NonceSize];
        var ctr = Interlocked.Increment(ref _sendCounter);
        BitConverter.TryWriteBytes(nonce, ctr); // 低 8 字节为计数器，高 4 字节为 0

        var cipher = new byte[count + TagSize];
        _aes.Encrypt(nonce, buffer.AsSpan(offset, count), cipher.AsSpan(0, count), cipher.AsSpan(count));

        var header = new byte[4];
        BitConverter.TryWriteBytes(header, NonceSize + count + TagSize);
        await _inner.WriteAsync(header, ct).ConfigureAwait(false);
        await _inner.WriteAsync(nonce, ct).ConfigureAwait(false);
        await _inner.WriteAsync(cipher, ct).ConfigureAwait(false);
        await _inner.FlushAsync(ct).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_recvPos >= _recvLen)
        {
            // 缓冲已空，读取下一加密帧并解密
            var header = new byte[4];
            await ReadExactlyAsync(_inner, header, ct).ConfigureAwait(false);
            int total = BitConverter.ToInt32(header);
            if (total < NonceSize + TagSize || total > MaxCipherFrame)
                throw new InvalidDataException($"加密帧长度非法 {total}");

            var nonce = new byte[NonceSize];
            await ReadExactlyAsync(_inner, nonce, ct).ConfigureAwait(false);
            var cipher = new byte[total - NonceSize];
            await ReadExactlyAsync(_inner, cipher, ct).ConfigureAwait(false);

            int plainLen = cipher.Length - TagSize;
            var plain = new byte[plainLen];
            _aes.Decrypt(nonce, cipher.AsSpan(0, plainLen), cipher.AsSpan(plainLen), plain);

            _recvBuf = plain;
            _recvPos = 0;
            _recvLen = plainLen;
        }

        int toCopy = Math.Min(count, _recvLen - _recvPos);
        Array.Copy(_recvBuf, _recvPos, buffer, offset, toCopy);
        _recvPos += toCopy;
        return toCopy;
    }

    static async Task ReadExactlyAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int got = 0;
        while (got < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(got), ct).ConfigureAwait(false);
            if (n == 0) throw new EndOfStreamException("加密流对端已关闭");
            got += n;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _aes.Dispose();
        base.Dispose(disposing);
    }
}
