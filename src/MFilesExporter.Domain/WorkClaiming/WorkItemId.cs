using System.Globalization;

namespace MFilesExporter.Domain.WorkClaiming;

/// <summary>Strongly-typed identifier for a <see cref="WorkItem"/>.</summary>
public readonly record struct WorkItemId(long Value)
{
    public static WorkItemId Unassigned { get; } = new(0);
    public bool IsAssigned => Value > 0;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
