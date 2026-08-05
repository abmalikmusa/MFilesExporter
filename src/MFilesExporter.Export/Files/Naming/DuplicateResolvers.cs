using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Files.Naming;

/// <summary>
/// Given a *desired* full path (which may or may not already exist), yields
/// a path guaranteed to be safe to write to under the configured policy.
/// </summary>
/// <remarks>
/// The engine still uses <c>FileMode.CreateNew</c> when writing so
/// concurrent workers cannot both succeed. The resolver's job is to pick
/// a first-guess candidate that avoids race retries under normal load.
/// </remarks>
public interface IDuplicateResolver
{
    /// <summary>Resolves a candidate write path from the desired one.</summary>
    /// <returns>The resolved path.</returns>
    string Resolve(string desiredPath, DocumentDescriptor descriptor);

    /// <summary>Behavior kind, echoed to the engine so it can pick its FileMode.</summary>
    DuplicateResolutionKind Kind { get; }
}

/// <summary>
/// Deterministic collision breaker. Appends an 8-hex-char prefix of the
/// idempotency key before the extension:
/// <c>Invoice.pdf</c> → <c>Invoice_ab12cd34.pdf</c>. Only appended when
/// the desired path already exists; the check is stat-only (no reads).
/// </summary>
/// <remarks>
/// Race-safe because the hash suffix is derived from a value unique per
/// document: two workers exporting different documents into the same
/// desired path get different suffixes and never collide. Two workers
/// exporting the SAME document arrive at the SAME suffix (which is fine —
/// the sink writes bit-identical content).
/// </remarks>
public sealed class IdempotencyKeySuffixResolver : IDuplicateResolver
{
    public DuplicateResolutionKind Kind => DuplicateResolutionKind.IdempotencyKeySuffix;

    public string Resolve(string desiredPath, DocumentDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);

        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);          // includes leading dot or empty
        var hash8 = descriptor.IdempotencyKey.ToHex()[..8];
        return Path.Combine(directory, $"{stem}_{hash8}{ext}");
    }
}

/// <summary>
/// Probes the disk with an incrementing counter: <c>Invoice.pdf</c> →
/// <c>Invoice (1).pdf</c> → <c>Invoice (2).pdf</c>. Bounded so a
/// pathological collision does not loop forever.
/// </summary>
/// <remarks>
/// NOT recommended for &gt; ~100 k documents because each Resolve involves
/// N stat calls. Use <see cref="IdempotencyKeySuffixResolver"/> instead
/// for large runs.
/// </remarks>
public sealed class CounterSuffixResolver : IDuplicateResolver
{
    private const int MaxAttempts = 10_000;

    public DuplicateResolutionKind Kind => DuplicateResolutionKind.CounterSuffix;

    public string Resolve(string desiredPath, DocumentDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredPath);

        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var stem = Path.GetFileNameWithoutExtension(desiredPath);
        var ext = Path.GetExtension(desiredPath);

        for (var counter = 1; counter < MaxAttempts; counter++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({counter}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Exhausted {MaxAttempts} disambiguation attempts under {directory}. "
          + "Switch to IdempotencyKeySuffixResolver for large corpora.");
    }
}

/// <summary>Fails hard on collision — used when strict uniqueness is enforced elsewhere.</summary>
public sealed class FailOnCollisionResolver : IDuplicateResolver
{
    public DuplicateResolutionKind Kind => DuplicateResolutionKind.Fail;

    public string Resolve(string desiredPath, DocumentDescriptor descriptor)
    {
        if (File.Exists(desiredPath))
        {
            throw new IOException($"Output file already exists at '{desiredPath}'.");
        }
        return desiredPath;
    }
}

/// <summary>Overwrites the existing file. The engine opens with FileMode.Create.</summary>
public sealed class OverwriteResolver : IDuplicateResolver
{
    public DuplicateResolutionKind Kind => DuplicateResolutionKind.Overwrite;

    public string Resolve(string desiredPath, DocumentDescriptor descriptor) => desiredPath;
}

/// <summary>Factory that materializes the resolver named in <see cref="FileExportOptions.DuplicateResolution"/>.</summary>
public static class DuplicateResolverFactory
{
    public static IDuplicateResolver Create(FileExportOptions options) => options.DuplicateResolution switch
    {
        DuplicateResolutionKind.IdempotencyKeySuffix => new IdempotencyKeySuffixResolver(),
        DuplicateResolutionKind.CounterSuffix        => new CounterSuffixResolver(),
        DuplicateResolutionKind.Fail                 => new FailOnCollisionResolver(),
        DuplicateResolutionKind.Overwrite            => new OverwriteResolver(),
        _ => throw new ArgumentOutOfRangeException(
            nameof(options), options.DuplicateResolution,
            "Unknown DuplicateResolutionKind"),
    };
}
