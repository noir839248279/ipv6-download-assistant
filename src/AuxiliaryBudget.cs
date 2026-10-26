namespace IPv6Lens;

// The allowance is shared by concurrent auxiliary tunnels and survives counter resets.
internal sealed class AuxiliaryBudget
{
    internal const long Limit = 2 * 1024 * 1024;
    long used, capacity = Limit;
    internal long Used => Interlocked.Read(ref used);
    internal long Capacity => Interlocked.Read(ref capacity);
    internal void Grant() => Interlocked.Add(ref capacity, Limit);
    internal int Reserve(int requested)
    {
        while (true)
        {
            long before = Used;
            int count = (int)Math.Min(requested, Capacity - before);
            if (count <= 0) throw new AuxiliaryBudgetException();
            if (Interlocked.CompareExchange(ref used, before + count, before) == before) return count;
        }
    }
    internal void Refund(int count) => Interlocked.Add(ref used, -count);
}

internal sealed class AuxiliaryStream(Stream inner, AuxiliaryBudget budget) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => inner.Flush();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.Length == 0) return 0;
        int reserved = budget.Reserve(Math.Min(buffer.Length, 16384)), read = 0;
        try { read = await inner.ReadAsync(buffer[..reserved], cancellationToken); return read; }
        finally { budget.Refund(reserved - read); }
    }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        while (!buffer.IsEmpty)
        {
            int reserved = budget.Reserve(Math.Min(buffer.Length, 16384));
            // A failed send may already have emitted bytes: conservatively keep its reservation.
            await inner.WriteAsync(buffer[..reserved], cancellationToken);
            buffer = buffer[reserved..];
        }
    }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
