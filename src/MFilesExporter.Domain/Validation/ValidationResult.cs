namespace MFilesExporter.Domain.Validation;

/// <summary>
/// Aggregated outcome of validating a domain object. Two shapes: <c>Valid</c>
/// (zero failures) or <c>Invalid</c> (one or more <see cref="ValidationFailure"/>).
///
/// Design notes:
/// <list type="bullet">
///   <item><description>Immutable — the set of failures is fixed at construction.</description></item>
///   <item><description>Serializable — a plain record with an <see cref="IReadOnlyList{T}"/> of records.</description></item>
///   <item><description>Additive — <see cref="Merge"/> combines results from many sub-validators.</description></item>
/// </list>
/// </summary>
public sealed record ValidationResult
{
    /// <summary>
    /// Represents the shared "no failures" sentinel. Callers must treat this
    /// as immutable and not attempt to mutate the underlying list.
    /// </summary>
    public static ValidationResult Valid { get; } = new(Array.Empty<ValidationFailure>());

    private ValidationResult(IReadOnlyList<ValidationFailure> failures)
    {
        Failures = failures ?? Array.Empty<ValidationFailure>();
    }

    /// <summary>All validation failures. Empty when <see cref="IsValid"/>.</summary>
    public IReadOnlyList<ValidationFailure> Failures { get; init; }

    /// <summary>True when no failures were reported.</summary>
    public bool IsValid => Failures.Count == 0;

    /// <summary>True when at least one failure was reported.</summary>
    public bool IsInvalid => !IsValid;

    /// <summary>Constructs a failed result from one or more failures.</summary>
    public static ValidationResult Invalid(params ValidationFailure[] failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Length == 0 ? Valid : new ValidationResult(failures);
    }

    /// <summary>Constructs a failed result from a failure list.</summary>
    public static ValidationResult Invalid(IReadOnlyList<ValidationFailure> failures)
    {
        ArgumentNullException.ThrowIfNull(failures);
        return failures.Count == 0 ? Valid : new ValidationResult(failures);
    }

    /// <summary>Convenience constructor for a single failure.</summary>
    public static ValidationResult Fail(string propertyName, string errorCode, string message) =>
        new(new[] { new ValidationFailure(propertyName, errorCode, message) });

    /// <summary>Combines two results — union of failures.</summary>
    public ValidationResult Merge(ValidationResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (IsValid) return other;
        if (other.IsValid) return this;

        var combined = new List<ValidationFailure>(Failures.Count + other.Failures.Count);
        combined.AddRange(Failures);
        combined.AddRange(other.Failures);
        return new ValidationResult(combined);
    }

    /// <summary>Throws <see cref="DomainValidationException"/> when invalid.</summary>
    public void ThrowIfInvalid()
    {
        if (IsInvalid)
        {
            throw new DomainValidationException(this);
        }
    }
}

/// <summary>
/// Thrown when an aggregate is asked to enforce validity but is invalid.
/// Callers that use <c>Validate()</c>-and-inspect never see this; only
/// <see cref="ValidationResult.ThrowIfInvalid"/> raises it.
/// </summary>
public sealed class DomainValidationException : Exception
{
    public DomainValidationException(ValidationResult result)
        : base("Domain validation failed: " + string.Join("; ",
            result.Failures.Select(f => $"{f.PropertyName}({f.ErrorCode}): {f.Message}")))
    {
        Result = result;
    }

    /// <summary>The validation result that produced this exception.</summary>
    public ValidationResult Result { get; }
}
