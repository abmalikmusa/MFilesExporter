using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Streaming reader for a <c>VARBINARY(MAX)</c> column exposed by a
/// <see cref="SqlDataReader"/> opened under
/// <see cref="System.Data.CommandBehavior.SequentialAccess"/>. Copies the
/// column to a destination <see cref="Stream"/>, computing a checksum and
/// reporting progress as bytes flow through.
/// </summary>
/// <remarks>
/// Never buffers the payload. Uses <see cref="SqlDataReader.GetBytes"/> in
/// bounded chunks and supports single columns larger than 4 GiB
/// (<see cref="long"/> byte offsets throughout).
/// </remarks>
public interface IBinaryObjectReader
{
    /// <summary>
    /// Copies the varbinary column at <paramref name="ordinal"/> to
    /// <paramref name="destination"/>. The reader MUST already be positioned
    /// on the row (i.e. <c>ReadAsync</c> returned <c>true</c>).
    /// </summary>
    /// <param name="reader">
    /// SQL data reader positioned on the row. Not disposed by this method;
    /// the caller owns its lifetime.
    /// </param>
    /// <param name="ordinal">Column ordinal of the <c>VARBINARY(MAX)</c>.</param>
    /// <param name="destination">Where the payload is written. Not flushed or closed by this method.</param>
    /// <param name="options">Buffer size, checksum algorithm, validation targets.</param>
    /// <param name="progress">Optional progress sink. Called on
    /// <see cref="BinaryReadOptions.ProgressReportInterval"/>.</param>
    /// <param name="cancellationToken">Cancels the copy. On cancellation,
    /// bytes already written to <paramref name="destination"/> remain there;
    /// the caller is responsible for cleanup.</param>
    Task<BinaryReadResult> ReadAsync(
        SqlDataReader reader,
        int ordinal,
        Stream destination,
        BinaryReadOptions options,
        IProgress<BinaryReadProgress>? progress,
        CancellationToken cancellationToken);
}
