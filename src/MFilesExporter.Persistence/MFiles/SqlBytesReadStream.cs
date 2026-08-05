using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.MFiles;

/// <summary>
/// Forward-only, non-seekable <see cref="Stream"/> that pulls a
/// <c>varbinary(max)</c> column out of a <see cref="SqlDataReader"/> using
/// chunked <see cref="SqlDataReader.GetBytes(int, long, byte[], int, int)"/>
/// calls. This is the explicit, memory-bounded alternative to
/// <c>SqlDataReader.GetStream(int)</c> — no BLOB is ever loaded whole.
///
/// The stream owns nothing but its column position; the caller retains
/// ownership of the reader/command/connection lifetime.
/// </summary>
public sealed class SqlBytesReadStream : Stream
{
    private readonly SqlDataReader _reader;
    private readonly int _ordinal;
    private long _position;
    private bool _eof;

    public SqlBytesReadStream(SqlDataReader reader, int ordinal)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        if (ordinal < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }
        _ordinal = ordinal;
    }

    public override bool CanRead  => !_eof;
    public override bool CanSeek  => false;
    public override bool CanWrite => false;

    public override long Length =>
        throw new NotSupportedException("Length unknown; use SqlDataReader in SequentialAccess mode.");

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset + count > buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }
        if (_eof || count == 0) return 0;

        // GetBytes returns the number of bytes actually copied into the
        // target buffer starting at dataOffset. When the return value is
        // less than requested it signals end-of-BLOB.
        long copied = _reader.GetBytes(_ordinal, _position, buffer, offset, count);

        if (copied <= 0)
        {
            _eof = true;
            return 0;
        }

        _position += copied;
        return (int)copied;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_eof || buffer.IsEmpty) return 0;

        // GetBytes has no async form — under SequentialAccess it does not
        // block waiting for the whole row, so a synchronous call here just
        // pulls the next chunk from the underlying TDS stream. We schedule
        // the call on the thread pool if the buffer is large enough that
        // synchronous execution would monopolize the caller's stage.
        if (buffer.Length >= 32 * 1024)
        {
            return await Task.Run(() => ReadCore(buffer.Span), cancellationToken).ConfigureAwait(false);
        }
        return ReadCore(buffer.Span);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        return ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    private int ReadCore(Span<byte> destination)
    {
        // We must rent an intermediate array because GetBytes' overload takes
        // byte[], not Span<byte>. Use ArrayPool to avoid allocations.
        var pool = System.Buffers.ArrayPool<byte>.Shared;
        var rented = pool.Rent(destination.Length);
        try
        {
            var copied = _reader.GetBytes(_ordinal, _position, rented, 0, destination.Length);
            if (copied <= 0)
            {
                _eof = true;
                return 0;
            }
            new ReadOnlySpan<byte>(rented, 0, (int)copied).CopyTo(destination);
            _position += copied;
            return (int)copied;
        }
        finally
        {
            pool.Return(rented);
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
