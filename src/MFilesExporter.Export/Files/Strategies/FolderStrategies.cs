using System.Globalization;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Files.Strategies;

/// <summary>
/// Produces the directory path (relative to the export root) that a
/// particular document should live in. Strategies are pure functions —
/// no filesystem probing, no state — so they parallelize trivially.
/// </summary>
public interface IFolderStrategy
{
    /// <summary>The relative folder segment (may be empty for flat layout).</summary>
    string BuildRelativeFolder(FileExportContext context);

    /// <summary>Strategy kind for logging + diagnostics.</summary>
    FolderStrategyKind Kind { get; }
}

/// <summary>
/// Flat layout. Example: <c>Output/Invoice.pdf</c>. Suitable only for
/// small corpora (&lt; ~10 000). At millions of files, a single directory
/// destroys performance on every mainstream filesystem.
/// </summary>
public sealed class FlatFolderStrategy : IFolderStrategy
{
    public FolderStrategyKind Kind => FolderStrategyKind.Flat;
    public string BuildRelativeFolder(FileExportContext context) => string.Empty;
}

/// <summary>
/// Hash-sharded layout using the first N bytes of the idempotency key.
/// Example (depth = 2): <c>Output/ab/12/Invoice.pdf</c>. Uniform
/// distribution because SHA-256 is uniform.
/// </summary>
public sealed class HashShardedFolderStrategy : IFolderStrategy
{
    private readonly int _depth;

    public HashShardedFolderStrategy(int depth)
    {
        if (depth is < 1 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(depth), depth, "ShardDepth must be 1–4.");
        }
        _depth = depth;
    }

    public FolderStrategyKind Kind => FolderStrategyKind.HashSharded;

    public string BuildRelativeFolder(FileExportContext context)
    {
        var hex = context.Descriptor.IdempotencyKey.ToHex();
        var segments = new string[_depth];
        for (var i = 0; i < _depth; i++)
        {
            segments[i] = hex.Substring(i * 2, 2);
        }
        return Path.Combine(segments);
    }
}

/// <summary>
/// Numeric-shard layout using <c>ID_DOCUMENTFILEPART mod bucketCount</c>.
/// Example (bucketCount = 1000): <c>Output/535/Invoice.pdf</c>. Uniform
/// only if part IDs are dense integers.
/// </summary>
public sealed class NumericShardFolderStrategy : IFolderStrategy
{
    private readonly int _bucketCount;

    public NumericShardFolderStrategy(int bucketCount)
    {
        if (bucketCount is < 1 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(bucketCount), bucketCount, "BucketCount must be 1–100 000.");
        }
        _bucketCount = bucketCount;
    }

    public FolderStrategyKind Kind => FolderStrategyKind.NumericShard;

    public string BuildRelativeFolder(FileExportContext context)
    {
        var partId = context.Descriptor.DocumentFileVersionKey.DocumentFilePartId;
        var bucket = (int)(((partId % _bucketCount) + _bucketCount) % _bucketCount);
        return bucket.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>
/// Date-based layout using the document's <c>LastWriteTimeUtc</c>.
/// Configurable via <see cref="FileExportOptions.DateFolderPattern"/> —
/// the pattern is a <see cref="DateTime.ToString(string)"/> format string,
/// so <c>yyyy/MM</c> yields <c>Output/2026/08/Invoice.pdf</c>.
/// </summary>
public sealed class DateFolderStrategy : IFolderStrategy
{
    private readonly string _pattern;

    public DateFolderStrategy(string pattern)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        _pattern = pattern;
    }

    public FolderStrategyKind Kind => FolderStrategyKind.Date;

    public string BuildRelativeFolder(FileExportContext context) =>
        context.Descriptor.LastWriteTimeUtc.ToString(_pattern, CultureInfo.InvariantCulture)
            .Replace('/', Path.DirectorySeparatorChar);
}

/// <summary>
/// Category-based layout. Uses <see cref="FileExportContext.Category"/> if
/// provided, else falls back to the file extension. Missing/blank category
/// falls back to "misc".
/// </summary>
public sealed class CategoryFolderStrategy : IFolderStrategy
{
    public FolderStrategyKind Kind => FolderStrategyKind.Category;

    public string BuildRelativeFolder(FileExportContext context)
    {
        var category = context.Category
                    ?? context.Descriptor.Extension?.ToLowerInvariant();
        var isFallback = string.IsNullOrWhiteSpace(category);
        if (isFallback) category = "misc";

        // Pluralize real categories only — the "misc" sentinel is already plural-neutral.
        var withSuffix = isFallback || category!.EndsWith('s')
            ? category!
            : category + "s";

        return SafeSegment(withSuffix);
    }

    private static string SafeSegment(string s)
    {
        Span<char> buf = stackalloc char[s.Length];
        var len = 0;
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                buf[len++] = char.ToLowerInvariant(c);
            }
        }
        return len == 0 ? "misc" : new string(buf[..len]);
    }
}

/// <summary>
/// Recommended production layout: shard × date. Combines hash-sharded
/// fan-out (uniform distribution) with a date suffix (temporal locality
/// for browsing / retention policies). Example:
/// <c>Output/ab/12/2026/08/Invoice.pdf</c>.
/// </summary>
public sealed class ShardedByDateFolderStrategy : IFolderStrategy
{
    private readonly HashShardedFolderStrategy _shard;
    private readonly DateFolderStrategy _date;

    public ShardedByDateFolderStrategy(int shardDepth, string datePattern)
    {
        _shard = new HashShardedFolderStrategy(shardDepth);
        _date = new DateFolderStrategy(datePattern);
    }

    public FolderStrategyKind Kind => FolderStrategyKind.ShardedByDate;

    public string BuildRelativeFolder(FileExportContext context) =>
        Path.Combine(
            _shard.BuildRelativeFolder(context),
            _date.BuildRelativeFolder(context));
}

/// <summary>Materializes the strategy named in <see cref="FileExportOptions.FolderStrategy"/>.</summary>
public static class FolderStrategyFactory
{
    public static IFolderStrategy Create(FileExportOptions options) => options.FolderStrategy switch
    {
        FolderStrategyKind.Flat          => new FlatFolderStrategy(),
        FolderStrategyKind.HashSharded   => new HashShardedFolderStrategy(options.ShardDepth),
        FolderStrategyKind.NumericShard  => new NumericShardFolderStrategy(options.NumericBucketCount),
        FolderStrategyKind.Date          => new DateFolderStrategy(options.DateFolderPattern),
        FolderStrategyKind.Category      => new CategoryFolderStrategy(),
        FolderStrategyKind.ShardedByDate => new ShardedByDateFolderStrategy(options.ShardDepth, options.DateFolderPattern),
        _ => throw new ArgumentOutOfRangeException(
            nameof(options), options.FolderStrategy,
            "Unknown FolderStrategyKind."),
    };
}
