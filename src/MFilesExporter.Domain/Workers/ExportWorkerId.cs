using System.Globalization;

namespace MFilesExporter.Domain.Workers;

/// <summary>Strongly-typed identifier for an <see cref="ExportWorker"/>.</summary>
public readonly record struct ExportWorkerId(long Value)
{
    public static ExportWorkerId Unassigned { get; } = new(0);
    public bool IsAssigned => Value > 0;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
