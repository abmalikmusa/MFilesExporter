using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;

namespace MFilesExporter.Domain.Documents;

/// <summary>
/// Deterministic SHA-256 fingerprint over the tuple that uniquely identifies a
/// committed BLOB: (ID_DOCUMENTFILEPART, ID_VERSIONPART, ID_DATAFILEVERSION).
/// Stable across processes and reruns. Uniform distribution makes it a good
/// shard root for on-disk fan-out.
/// </summary>
public readonly record struct IdempotencyKey
{
    private readonly byte[] _bytes;

    private IdempotencyKey(byte[] bytes) => _bytes = bytes;

    public static IdempotencyKey For(long documentFilePartId, long versionPartId, long dataFileVersionId)
    {
        Span<byte> buffer = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(buffer[..8], documentFilePartId);
        BinaryPrimitives.WriteInt64BigEndian(buffer.Slice(8, 8), versionPartId);
        BinaryPrimitives.WriteInt64BigEndian(buffer.Slice(16, 8), dataFileVersionId);

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer, hash);
        return new IdempotencyKey(hash.ToArray());
    }

    public static IdempotencyKey Parse(string hex)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hex);
        if (hex.Length != SHA256.HashSizeInBytes * 2)
        {
            throw new FormatException($"Expected {SHA256.HashSizeInBytes * 2}-character hex string.");
        }
        return new IdempotencyKey(Convert.FromHexString(hex));
    }

    public ReadOnlySpan<byte> AsSpan() => _bytes;

    public byte[] ToArray() => (byte[])_bytes.Clone();

    public string ToHex() => Convert.ToHexString(_bytes).ToLowerInvariant();

    public string ShardPrefix1 => _bytes[0].ToString("x2", CultureInfo.InvariantCulture);

    public string ShardPrefix2 => _bytes[1].ToString("x2", CultureInfo.InvariantCulture);

    public bool Equals(IdempotencyKey other) => _bytes.AsSpan().SequenceEqual(other._bytes);

    public override int GetHashCode() =>
        _bytes.Length >= 4 ? BinaryPrimitives.ReadInt32BigEndian(_bytes.AsSpan(0, 4)) : 0;

    public override string ToString() => ToHex();
}
