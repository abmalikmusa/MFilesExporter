namespace MFilesExporter.Application.Common;

/// <summary>
/// Non-generic result — success carries no payload, failure carries one or
/// more <see cref="ApplicationError"/>s. Value type to keep the happy-path
/// allocation-free.
/// </summary>
public readonly record struct ApplicationResult
{
    private ApplicationResult(bool ok, IReadOnlyList<ApplicationError> errors)
    {
        IsSuccess = ok;
        Errors = errors;
    }

    /// <summary>True when the use case completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when at least one error was reported.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Errors reported by the use case. Empty on success.</summary>
    public IReadOnlyList<ApplicationError> Errors { get; }

    /// <summary>First error, or <c>null</c> on success.</summary>
    public ApplicationError? PrimaryError => Errors.Count > 0 ? Errors[0] : null;

    public static ApplicationResult Success() => new(true, Array.Empty<ApplicationError>());
    public static ApplicationResult Failure(ApplicationError error) => new(false, new[] { error });
    public static ApplicationResult Failure(params ApplicationError[] errors) => new(false, errors);
    public static ApplicationResult Failure(IReadOnlyList<ApplicationError> errors) => new(false, errors);
}

/// <summary>
/// Generic result — success carries a payload of type <typeparamref name="T"/>.
/// </summary>
public readonly record struct ApplicationResult<T>
{
    private readonly T? _value;

    private ApplicationResult(bool ok, T? value, IReadOnlyList<ApplicationError> errors)
    {
        IsSuccess = ok;
        _value = value;
        Errors = errors;
    }

    /// <summary>True when the use case returned a value.</summary>
    public bool IsSuccess { get; }

    /// <summary>True when at least one error was reported.</summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Value produced by the use case.</summary>
    /// <exception cref="InvalidOperationException">Thrown when read from a failed result.</exception>
    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException(
                "Cannot access Value on failed result: "
                + string.Join("; ", Errors.Select(e => $"{e.Code}: {e.Message}")));

    /// <summary>Errors reported by the use case. Empty on success.</summary>
    public IReadOnlyList<ApplicationError> Errors { get; }

    /// <summary>First error, or <c>null</c> on success.</summary>
    public ApplicationError? PrimaryError => Errors.Count > 0 ? Errors[0] : null;

    public static ApplicationResult<T> Success(T value) =>
        new(true, value, Array.Empty<ApplicationError>());

    public static ApplicationResult<T> Failure(ApplicationError error) =>
        new(false, default, new[] { error });

    public static ApplicationResult<T> Failure(params ApplicationError[] errors) =>
        new(false, default, errors);

    public static ApplicationResult<T> Failure(IReadOnlyList<ApplicationError> errors) =>
        new(false, default, errors);

    /// <summary>Adapts <see cref="ApplicationResult"/> to <see cref="ApplicationResult{T}"/> by lifting a value.</summary>
    public static ApplicationResult<T> From(ApplicationResult inner, T value) =>
        inner.IsSuccess ? Success(value) : Failure(inner.Errors);

    /// <summary>Drops the payload, keeping only success/failure.</summary>
    public ApplicationResult AsNonGeneric() =>
        IsSuccess ? ApplicationResult.Success() : ApplicationResult.Failure(Errors);
}
