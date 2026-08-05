using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Storage;

/// <summary>
/// Deterministically maps a DocumentDescriptor onto a filesystem path.
/// Path shape (ShardDepth = 2): {rootPath}/{ab}/{cd}/{hash}[__{title}].{ext}
/// </summary>
internal sealed class PathBuilder
{
    private static readonly HashSet<char> InvalidFileChars = new(Path.GetInvalidFileNameChars());
    private static readonly char[] ReservedPunctuation = { ':', '*', '?', '"', '<', '>', '|', '/', '\\', '\0' };
    private const int MaxTitleCharsForFilename = 96;

    private readonly StorageOptions _options;

    public PathBuilder(StorageOptions options)
    {
        _options = options;
    }

    public string BuildOutputPath(DocumentDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var hashHex = descriptor.IdempotencyKey.ToHex();
        var shardSegments = new string[_options.ShardDepth];
        for (var i = 0; i < _options.ShardDepth; i++)
        {
            shardSegments[i] = hashHex.Substring(i * 2, 2);
        }

        var segments = new List<string>(_options.ShardDepth + 1) { _options.RootPath };
        segments.AddRange(shardSegments);
        var directory = Path.Combine(segments.ToArray());
        var filename = BuildFilename(descriptor, hashHex);
        return Path.Combine(directory, filename);
    }

    public string BuildTempPath(string finalPath)
    {
        var directory = Path.GetDirectoryName(finalPath) ?? _options.RootPath;
        var name = Path.GetFileName(finalPath);
        return Path.Combine(directory, "." + name + ".partial");
    }

    private string BuildFilename(DocumentDescriptor descriptor, string hashHex)
    {
        var ext = SanitizeExtension(descriptor.Extension);
        if (!_options.PreserveOriginalFilename || string.IsNullOrWhiteSpace(descriptor.Title))
        {
            return string.IsNullOrEmpty(ext) ? hashHex : $"{hashHex}.{ext}";
        }
        var sanitizedTitle = SanitizeTitleForFilename(descriptor.Title);
        return string.IsNullOrEmpty(ext)
            ? $"{hashHex}__{sanitizedTitle}"
            : $"{hashHex}__{sanitizedTitle}.{ext}";
    }

    internal static string SanitizeTitleForFilename(string title)
    {
        Span<char> buffer = title.Length <= 512 ? stackalloc char[title.Length] : new char[title.Length];
        var len = 0;
        foreach (var ch in title)
        {
            if (ch < 0x20) continue;
            if (InvalidFileChars.Contains(ch) || Array.IndexOf(ReservedPunctuation, ch) >= 0)
            {
                buffer[len++] = '_';
            }
            else
            {
                buffer[len++] = ch;
            }
        }
        var trimmed = new string(buffer[..len]).Trim().Trim('.').Trim();
        if (trimmed.Length == 0) return "untitled";
        return trimmed.Length > MaxTitleCharsForFilename ? trimmed[..MaxTitleCharsForFilename] : trimmed;
    }

    internal static string SanitizeExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
        var ext = extension.TrimStart('.').Trim();
        if (ext.Length == 0) return string.Empty;

        Span<char> buffer = ext.Length <= 64 ? stackalloc char[ext.Length] : new char[ext.Length];
        var len = 0;
        foreach (var ch in ext)
        {
            if (char.IsLetterOrDigit(ch))
            {
                buffer[len++] = char.ToLowerInvariant(ch);
            }
        }
        return len > 0 ? new string(buffer[..len]) : string.Empty;
    }
}
