using FluentValidation;
using Microsoft.Extensions.Options;

namespace MFilesExporter.Configuration.Validation;

/// <summary>
/// Adapts a FluentValidation <see cref="IValidator{T}"/> to the Options
/// pattern so <c>ValidateOnStart()</c> exercises it at host build time.
/// </summary>
public sealed class FluentValidateOptions<TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly string? _name;
    private readonly IValidator<TOptions> _validator;

    public FluentValidateOptions(string? name, IValidator<TOptions> validator)
    {
        _name = name;
        _validator = validator;
    }

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (_name is not null && _name != name)
        {
            return ValidateOptionsResult.Skip;
        }

        ArgumentNullException.ThrowIfNull(options);

        var result = _validator.Validate(options);
        if (result.IsValid) return ValidateOptionsResult.Success;

        var errors = result.Errors.Select(e => $"{e.PropertyName}: {e.ErrorMessage}").ToArray();
        return ValidateOptionsResult.Fail(errors);
    }
}
