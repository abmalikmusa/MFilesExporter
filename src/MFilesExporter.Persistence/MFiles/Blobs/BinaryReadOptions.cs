namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Configuration for one invocation of <see cref="IBinaryObjectReader"/>.
/// Immutable record so callers can safely share options across parallel reads.
/// </summary>
public sealed record BinaryReadOptions
{
    /// <summary>
    /// Bytes per chunk in the copy loop. Matches the process-wide sink write
    /// buffer default of 80 KiB. Larger buffers reduce TDS packet count for
    /// very large BLOBs at the cost of temporarily-larger managed
    /// working-set; smaller buffers reduce peak memory at the cost of
    /// more <c>GetBytes</c> calls.
    /// </summary>
    public int BufferSize { get; init; } = 81_920;

    /// <summary>Checksum algorithm computed during transfer.</summary>
    public BinaryChecksumAlgorithm Checksum { get; init; } = BinaryChecksumAlgorithm.Sha256;

    /// <summary>
    /// Optional expected byte count. When set and validation is enabled,
    /// the reader throws <see cref="BinaryValidationException"/> on mismatch.
    /// Uses <see cref="long"/> so files above 4 GiB (past <see cref="int"/> range) validate correctly.
    /// </summary>
    public long? ExpectedByteCount { get; init; }

    /// <summary>
    /// Optional expected checksum (lowercase hex). Compared case-insensitively.
    /// Combined with <see cref="Checksum"/> — a value here with
    /// <see cref="BinaryChecksumAlgorithm.None"/> is a configuration error.
    /// </summary>
    public string? ExpectedChecksumHex { get; init; }

    /// <summary>
    /// When <c>true</c>, mismatch on either <see cref="ExpectedByteCount"/> or
    /// <see cref="ExpectedChecksumHex"/> throws. When <c>false</c>, the
    /// mismatch is reported in <see cref="BinaryReadResult.Validation"/>.
    /// </summary>
    public bool ThrowOnValidationFailure { get; init; } = true;

    /// <summary>Minimum time between progress ticks. Set to <see cref="TimeSpan.Zero"/> to disable throttling.</summary>
    public TimeSpan ProgressReportInterval { get; init; } = TimeSpan.FromSeconds(2);
}
