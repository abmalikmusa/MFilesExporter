using System.Globalization;

namespace MFilesExporter.Domain.Jobs;

/// <summary>
/// Strongly-typed identifier for an <see cref="ExportJob"/>. Backed by
/// <c>long</c> so it matches the underlying tracking-DB BIGINT identity.
/// The value <c>0</c> is reserved as an "unassigned" sentinel for entities
/// that have not yet been persisted.
/// </summary>
public readonly record struct ExportJobId(long Value)
{
    /// <summary>Sentinel used for entities that have not been persisted.</summary>
    public static ExportJobId Unassigned { get; } = new(0);

    /// <summary>True once the DB has assigned an identity.</summary>
    public bool IsAssigned => Value > 0;

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
