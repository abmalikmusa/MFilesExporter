namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Progress tick emitted by <see cref="IBinaryObjectReader"/> to any caller
/// that supplies an <see cref="IProgress{T}"/>.
/// </summary>
/// <param name="BytesTransferred">Cumulative bytes read from the source column.</param>
/// <param name="ExpectedByteCount">Optional expected total, echoed from options; enables progress-bar rendering.</param>
/// <param name="ChunksRead">Number of <c>GetBytes</c> calls issued so far.</param>
/// <param name="Elapsed">Time since the read started.</param>
public sealed record BinaryReadProgress(
    long BytesTransferred,
    long? ExpectedByteCount,
    long ChunksRead,
    TimeSpan Elapsed)
{
    /// <summary>Instantaneous throughput, averaged over the whole read.</summary>
    public double BytesPerSecond =>
        Elapsed.TotalSeconds > 0 ? BytesTransferred / Elapsed.TotalSeconds : 0;

    /// <summary>Throughput in MiB/s.</summary>
    public double MebibytesPerSecond => BytesPerSecond / (1024d * 1024d);

    /// <summary>0.0–1.0 progress ratio (or <c>null</c> if expected count is unknown).</summary>
    public double? PercentComplete =>
        ExpectedByteCount is long total && total > 0
            ? Math.Min(1.0, (double)BytesTransferred / total)
            : null;
}
