namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>Terminal result returned by <see cref="IBinaryObjectReader"/>.</summary>
public sealed record BinaryReadResult
{
    /// <summary>Total bytes copied. Uses <see cref="long"/> to permit &gt; 4 GiB payloads.</summary>
    public required long BytesRead { get; init; }

    /// <summary>Number of chunks pulled from <c>SqlDataReader.GetBytes</c>.</summary>
    public required long ChunkCount { get; init; }

    /// <summary>Elapsed wall-clock time.</summary>
    public required TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Hex-encoded checksum of the payload, lowercase, or <c>null</c> when
    /// <see cref="BinaryReadOptions.Checksum"/> was <see cref="BinaryChecksumAlgorithm.None"/>.
    /// </summary>
    public string? ChecksumHex { get; init; }

    /// <summary>Algorithm actually used.</summary>
    public required BinaryChecksumAlgorithm ChecksumAlgorithm { get; init; }

    /// <summary>
    /// Validation report — populated when the caller passed
    /// <see cref="BinaryReadOptions.ExpectedByteCount"/> or
    /// <see cref="BinaryReadOptions.ExpectedChecksumHex"/>. If
    /// <c>ThrowOnValidationFailure</c> is <c>true</c> a mismatch throws
    /// <see cref="BinaryValidationException"/> — this field is only populated
    /// on success paths in that mode.
    /// </summary>
    public BinaryReadValidation? Validation { get; init; }

    /// <summary>Convenience: overall MiB/s throughput.</summary>
    public double MebibytesPerSecond =>
        Elapsed.TotalSeconds > 0
            ? BytesRead / Elapsed.TotalSeconds / (1024d * 1024d)
            : 0;
}

/// <summary>Report of the pass/fail state of each validation check.</summary>
public sealed record BinaryReadValidation(
    bool ByteCountMatches,
    bool ChecksumMatches,
    long? ExpectedByteCount,
    string? ExpectedChecksumHex)
{
    /// <summary>True when every requested check passed.</summary>
    public bool IsValid => ByteCountMatches && ChecksumMatches;
}
