namespace MFilesExporter.Application.Common;

/// <summary>
/// Opaque correlation identifier attached to a use-case invocation so log
/// lines from the dispatcher, handler, and downstream ports can be joined.
/// Backed by <see cref="Guid"/>; formatted as compact 32-char hex.
/// </summary>
public readonly record struct CorrelationId
{
    /// <param name="value">Underlying Guid. Use <see cref="New"/> in production.</param>
    public CorrelationId(Guid value)
    {
        Value = value;
    }

    /// <summary>Underlying value.</summary>
    public Guid Value { get; }

    /// <summary>Fresh correlation ID from a cryptographic RNG.</summary>
    public static CorrelationId New() => new(Guid.NewGuid());

    /// <summary>Explicit sentinel for callers that opt out of correlation.</summary>
    public static CorrelationId None { get; } = new(Guid.Empty);

    /// <summary>Compact hex form suitable for log correlation.</summary>
    public override string ToString() => Value.ToString("N");
}
