using System.Globalization;

namespace MFilesExporter.Domain.Batches;

/// <summary>Strongly-typed identifier for an <see cref="ExportBatch"/>. Backed by <c>long</c>.</summary>
public readonly record struct ExportBatchId(long Value)
{
    public static ExportBatchId Unassigned { get; } = new(0);
    public bool IsAssigned => Value > 0;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
