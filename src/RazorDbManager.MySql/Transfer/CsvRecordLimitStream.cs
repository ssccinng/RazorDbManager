using RazorDbManager.Core;

namespace RazorDbManager.MySql.Transfer;

internal sealed class CsvRecordLimitStream(
    Stream inner,
    long maximumRecordBytes,
    bool leaveOpen = true) : Stream
{
    private long _recordBytes;
    private bool _inQuotes;
    private bool _pendingQuote;
    private bool _pendingCarriageReturn;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => inner.CanSeek ? inner.Position : throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Inspect(buffer.AsSpan(offset, read));
        return read;
    }

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Inspect(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        Inspect(buffer.Span[..read]);
        return read;
    }

    private void Inspect(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) Inspect(value);
    }

    private void Inspect(byte value)
    {
        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            if (value == (byte)'\n')
            {
                AddByte();
                EndRecord();
                return;
            }

            EndRecord();
        }

        if (_pendingQuote)
        {
            _pendingQuote = false;
            if (value == (byte)'"')
            {
                AddByte();
                return;
            }

            _inQuotes = false;
        }

        AddByte();
        if (value == (byte)'"')
        {
            if (_inQuotes) _pendingQuote = true;
            else _inQuotes = true;
            return;
        }

        if (_inQuotes) return;
        if (value == (byte)'\r') _pendingCarriageReturn = true;
        else if (value == (byte)'\n') EndRecord();
    }

    private void AddByte()
    {
        _recordBytes++;
        if (_recordBytes > maximumRecordBytes)
        {
            throw new RazorDbException(
                RazorDbErrorCode.LimitExceeded,
                $"A CSV record exceeds the {maximumRecordBytes} byte limit.");
        }
    }

    private void EndRecord() => _recordBytes = 0;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen) inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen) await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
