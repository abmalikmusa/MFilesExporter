using System.Diagnostics;

namespace MFilesExporter.Export.Validation.Validators;

/// <summary>
/// Confirms the file can be opened for read + one byte read (or EOF for a
/// zero-length file). Detects broken permissions, locked files, and
/// low-level FS corruption without incurring the full checksum cost.
/// </summary>
public sealed class ReadableValidator : IExportValidator
{
    public string Name => nameof(ReadableValidator);
    public int Order => 40;

    public async Task<ValidationCheckResult> ValidateAsync(
        ExportValidationContext context,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        try
        {
            await using var fs = new FileStream(
                context.OutputPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4_096,
                FileOptions.Asynchronous);

            if (fs.Length > 0)
            {
                var buffer = new byte[1];
                var read = await fs.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return ValidationCheckResult.Failed(
                        Name, sw.Elapsed,
                        "File reports non-zero length but ReadAsync returned 0.",
                        isRetryable: true);
                }
            }

            return ValidationCheckResult.Passed(Name, sw.Elapsed);
        }
        catch (UnauthorizedAccessException ex)
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed, $"Permission denied: {ex.Message}", isRetryable: false);
        }
        catch (FileNotFoundException ex)
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed, ex.Message, isRetryable: true);
        }
        catch (IOException ex)
        {
            return ValidationCheckResult.Failed(
                Name, sw.Elapsed, $"IO failure opening file: {ex.Message}", isRetryable: true);
        }
    }
}
