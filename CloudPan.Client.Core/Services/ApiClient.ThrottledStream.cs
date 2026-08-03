namespace CloudPan.Client.Core.Services;

/// <summary>限速读取流——控制每秒读取字节数（上传/下载共用，ApiClient 内部使用）。</summary>
internal sealed class ThrottledStream : Stream
{
    private readonly Stream _inner;
    private readonly double _bytesPerTick;
    private long _bytesThisTick;
    private long _tickStartTicks;

    private const long TicksPerSecond = 10_000_000; // 1 tick = 100ns

    internal ThrottledStream(Stream inner, long bytesPerSecond)
    {
        _inner = inner;
        _bytesPerTick = bytesPerSecond / (double)TicksPerSecond;
        _tickStartTicks = DateTime.UtcNow.Ticks;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_bytesPerTick <= 0)
        {
            return _inner.Read(buffer, offset, count);
        }

        while (true)
        {
            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _tickStartTicks;

            // 每秒重置一次计数器
            if (elapsed > TicksPerSecond)
            {
                _tickStartTicks = now;
                _bytesThisTick = 0;
            }

            long maxBytes = (long)(_bytesPerTick * elapsed);
            int allowed = (int)Math.Min(count, maxBytes - _bytesThisTick);
            if (allowed <= 0)
            {
                // T-073：配额耗尽（含首次 elapsed 极小时）→ 等待后重试，绝不返回 0（0 仅底层流 EOF 语义）
                Thread.Sleep(10);
                continue;
            }

            int read = _inner.Read(buffer, offset, allowed);
            if (read <= 0)
            {
                // 底层流真正结束 → 向 CopyToAsync 传播 EOF
                return 0;
            }

            _bytesThisTick += read;
            return read;
        }
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        if (_bytesPerTick <= 0)
        {
            return await _inner.ReadAsync(buffer, offset, count, ct);
        }

        while (true)
        {
            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _tickStartTicks;

            if (elapsed > TicksPerSecond)
            {
                _tickStartTicks = now;
                _bytesThisTick = 0;
            }

            long maxBytes = (long)(_bytesPerTick * elapsed);
            int allowed = (int)Math.Min(count, maxBytes - _bytesThisTick);
            if (allowed <= 0)
            {
                // T-073：配额耗尽（含首次 elapsed 极小时）→ 等待后重试，绝不返回 0（0 仅底层流 EOF 语义）
                await Task.Delay(10, ct);
                continue;
            }

            int read = await _inner.ReadAsync(buffer, offset, allowed, ct);
            if (read <= 0)
            {
                // 底层流真正结束 → 向 CopyToAsync 传播 EOF
                return 0;
            }

            _bytesThisTick += read;
            return read;
        }
    }

    public override async Task CopyToAsync(Stream destination, int bufferSize, CancellationToken ct)
    {
        if (_bytesPerTick <= 0)
        {
            await _inner.CopyToAsync(destination, bufferSize, ct);
            return;
        }

        byte[] buffer = new byte[bufferSize];
        int bytesRead;
        while ((bytesRead = await ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
        {
            await destination.WriteAsync(buffer, 0, bytesRead, ct);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => _inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
