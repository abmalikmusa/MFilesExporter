namespace MFilesExporter.Domain.Errors;

/// <summary>Severity band for an <see cref="ErrorRecord"/>.</summary>
public enum ErrorSeverity
{
    /// <summary>Continuable — the pipeline recovered on its own.</summary>
    Warning = 0,

    /// <summary>A terminal failure for a single document.</summary>
    Error = 1,

    /// <summary>A failure that threatens the whole run — pipeline stops.</summary>
    Critical = 2,
}
