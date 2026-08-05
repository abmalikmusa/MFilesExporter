namespace MFilesExporter.Domain.Common;

/// <summary>
/// Discriminated result type used by domain factories that must model both
/// success and known-failure without exceptions. <c>Success</c> carries a
/// value; <c>Failure</c> carries a list of validation failures.
/// </summary>
public readonly record struct DomainResult<T>
{
    private DomainResult(bool isSuccess, T? value, IReadOnlyList<string> errors)
    {
        IsSuccess = isSuccess;
        _value = value;
        Errors = errors;
    }

    private readonly T? _value;

    /// <summary>True when the operation produced a value.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when the operation failed and carries errors.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Human-readable failure messages. Empty on success.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>Returns the value or throws if the result is a failure.</summary>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException(
                "Cannot read Value from a failed DomainResult: " + string.Join("; ", Errors));

    public static DomainResult<T> Success(T value) =>
        new(true, value, Array.Empty<string>());

    public static DomainResult<T> Failure(params string[] errors) =>
        new(false, default, errors ?? Array.Empty<string>());

    public static DomainResult<T> Failure(IReadOnlyList<string> errors) =>
        new(false, default, errors);
}
