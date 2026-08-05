namespace MFilesExporter.Export.Validation;

/// <summary>
/// One post-export check. Implementations MUST be pure functions of the
/// context and the on-disk state — no shared mutable state, no external
/// I/O beyond what the check itself demands.
/// </summary>
public interface IExportValidator
{
    /// <summary>Canonical name — matches the entries in <c>EnabledValidators</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Lower runs first. By convention:
    /// <list type="bullet">
    ///   <item><description>0–9 — sanity checks (file exists).</description></item>
    ///   <item><description>10–29 — cheap structural checks (folder, extension).</description></item>
    ///   <item><description>30–49 — moderate cost (file size, readable).</description></item>
    ///   <item><description>50+ — expensive (re-hash the file).</description></item>
    /// </list>
    /// </summary>
    int Order { get; }

    Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken);
}
