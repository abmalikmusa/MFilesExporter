namespace MFilesExporter.Domain.Common;

/// <summary>
/// Base type for value objects — reference-typed values whose identity is
/// determined by the equality of their components rather than by identifier.
/// Derived types override <see cref="GetEqualityComponents"/> to enumerate
/// the fields that participate in equality and hashing.
/// </summary>
/// <remarks>
/// Where a value type is small (≤ 16 bytes) and cheap to copy, prefer
/// <c>readonly record struct</c> instead of this base. This class is for
/// value objects that are compositional (aggregating several sub-values)
/// and benefit from reference semantics.
/// </remarks>
public abstract class ValueObject : IEquatable<ValueObject>
{
    /// <summary>
    /// Yields, in order, the components that determine equality. Include
    /// every field that is meaningful to identity; omit anything derived
    /// (e.g. computed properties).
    /// </summary>
    protected abstract IEnumerable<object?> GetEqualityComponents();

    /// <inheritdoc />
    public bool Equals(ValueObject? other)
    {
        if (other is null) return false;
        if (GetType() != other.GetType()) return false;
        return GetEqualityComponents().SequenceEqual(other.GetEqualityComponents());
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is ValueObject vo && Equals(vo);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var component in GetEqualityComponents())
        {
            hash.Add(component);
        }
        return hash.ToHashCode();
    }

    public static bool operator ==(ValueObject? left, ValueObject? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(ValueObject? left, ValueObject? right) => !(left == right);
}
