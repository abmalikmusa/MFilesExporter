using MFilesExporter.Domain.Exceptions;

namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Thrown by <see cref="IBinaryObjectReader"/> when the payload it transferred
/// does not match the expected byte count or checksum. Extends
/// <see cref="DomainException"/> because it represents a deterministic,
/// non-retryable data-integrity failure — retrying will not change the answer.
/// </summary>
public sealed class BinaryValidationException : DomainException
{
    public BinaryValidationException(
        string message,
        BinaryReadValidation validation)
        : base(message)
    {
        Validation = validation;
    }

    /// <summary>Structured per-check pass/fail state.</summary>
    public BinaryReadValidation Validation { get; }
}
