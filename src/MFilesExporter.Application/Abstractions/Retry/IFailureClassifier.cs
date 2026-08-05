namespace MFilesExporter.Application.Abstractions.Retry;

/// <summary>
/// Maps a raw <see cref="Exception"/> onto a <see cref="FailureCategory"/>.
/// The classifier is the single source of truth for what the retry engine
/// considers transient — callers should NOT decide retryability locally.
/// </summary>
public interface IFailureClassifier
{
    /// <summary>Classify the given exception. Never throws; unknowns return <see cref="FailureCategory.Unknown"/>.</summary>
    FailureCategory Classify(Exception exception);
}
