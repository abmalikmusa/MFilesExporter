using System.Buffers;
using System.Globalization;
using System.Text;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Files.Naming;

/// <summary>
/// Sanitizes a TITLE + EXTENSION into a safe filename that will succeed on
/// every mainstream filesystem (NTFS, ext4, APFS, XFS). The rules are the
/// *union* of platform restrictions — the resulting name is portable.
/// </summary>
public interface IFilenameSanitizer
{
    /// <summary>Produces a filename for the given title/extension.</summary>
    /// <param name="title">Original TITLE from the source; may be empty or null.</param>
    /// <param name="extension">Original EXTENSION (no leading dot); may be empty.</param>
    /// <param name="wasSanitized">Set to <c>true</c> when the returned name differs from the naive <c>title.extension</c>.</param>
    string Sanitize(string? title, string? extension, out bool wasSanitized);
}

/// <summary>Default implementation covering:
/// <list type="bullet">
///   <item><description>Illegal characters on Windows and POSIX (replaced with underscore).</description></item>
///   <item><description>Control chars (0x00–0x1F) — stripped.</description></item>
///   <item><description>Windows reserved names (CON/PRN/AUX/NUL/COM1-9/LPT1-9) — prefixed with underscore.</description></item>
///   <item><description>Trailing dots and spaces (Windows quirk) — trimmed.</description></item>
///   <item><description>Empty title — replaced with the configured default.</description></item>
///   <item><description>Empty extension — replaced with the configured default (or kept empty if configured so).</description></item>
///   <item><description>Length limits — truncated to <see cref="FileExportOptions.MaxFilenameLength"/>.</description></item>
///   <item><description>Unicode — normalized to NFC so equivalent strings are byte-identical.</description></item>
/// </list>
/// </summary>
public sealed class FilenameSanitizer : IFilenameSanitizer
{
    /// <summary>
    /// Union of Windows and POSIX invalid characters + the ones .NET reports
    /// via <see cref="Path.GetInvalidFileNameChars"/>. Using a set for
    /// O(1) lookup and to avoid platform-dependent behaviour.
    /// </summary>
    private static readonly HashSet<char> InvalidChars = BuildInvalidCharSet();

    /// <summary>Case-insensitive set of Windows reserved device names.</summary>
    internal static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly FileExportOptions _options;

    public FilenameSanitizer(FileExportOptions options)
    {
        _options = options;
    }

    public string Sanitize(string? title, string? extension, out bool wasSanitized)
    {
        var rawTitle = title ?? string.Empty;
        var rawExt = extension ?? string.Empty;
        wasSanitized = false;

        // 1. Unicode NFC — deterministic byte-shape for callers doing hash/compare.
        var normalizedTitle = rawTitle.IsNormalized(NormalizationForm.FormC)
            ? rawTitle
            : rawTitle.Normalize(NormalizationForm.FormC);
        if (!ReferenceEquals(normalizedTitle, rawTitle) && normalizedTitle != rawTitle)
        {
            wasSanitized = true;
        }

        // 2. Replace illegal characters + strip control chars.
        var titleClean = SanitizeCharacters(normalizedTitle, ref wasSanitized);

        // 3. Trim trailing dots and spaces — Windows silently strips them
        //    at file creation time, which would cause "foo." and "foo" to
        //    collide unexpectedly.
        var trimmed = titleClean.TrimEnd(' ', '.');
        if (trimmed.Length != titleClean.Length) wasSanitized = true;

        // 4. Fallback to default when nothing survived.
        if (trimmed.Length == 0)
        {
            trimmed = _options.DefaultTitle;
            wasSanitized = true;
        }

        // 5. Reserved-name protection — Windows treats "CON.pdf" as the
        //    console device; we prefix with underscore so the name reads as
        //    plain data.
        if (ReservedWindowsNames.Contains(trimmed))
        {
            trimmed = "_" + trimmed;
            wasSanitized = true;
        }

        // 6. Extension normalisation.
        var extClean = SanitizeExtension(rawExt);
        if (extClean.Length == 0 && !string.IsNullOrEmpty(_options.DefaultExtension))
        {
            extClean = _options.DefaultExtension;
            wasSanitized = true;
        }
        if (extClean != rawExt.TrimStart('.').Trim())
        {
            wasSanitized = true;
        }

        // 7. Length ceiling. Reserve room for extension + dot.
        var extBudget = extClean.Length == 0 ? 0 : extClean.Length + 1;
        var maxTitleLen = Math.Max(1, _options.MaxFilenameLength - extBudget);
        if (trimmed.Length > maxTitleLen)
        {
            trimmed = trimmed[..maxTitleLen].TrimEnd(' ', '.');
            wasSanitized = true;
        }

        return extClean.Length == 0 ? trimmed : $"{trimmed}.{extClean}";
    }

    private static string SanitizeCharacters(string s, ref bool changed)
    {
        // Fast path — walk once looking for anything to replace.
        var needsWork = false;
        foreach (var c in s)
        {
            if (c < 0x20 || InvalidChars.Contains(c))
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork) return s;

        changed = true;
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c < 0x20)
            {
                continue;  // strip control chars entirely
            }
            sb.Append(InvalidChars.Contains(c) ? '_' : c);
        }
        return sb.ToString();
    }

    private static string SanitizeExtension(string ext)
    {
        var trimmed = ext.TrimStart('.').Trim();
        if (trimmed.Length == 0) return string.Empty;

        // Extensions must be [A-Za-z0-9]+ to be portable. Anything else is
        // usually operator-supplied garbage or a data corruption.
        Span<char> buf = trimmed.Length <= 64 ? stackalloc char[trimmed.Length] : new char[trimmed.Length];
        var len = 0;
        foreach (var c in trimmed)
        {
            if (char.IsLetterOrDigit(c))
            {
                buf[len++] = char.ToLowerInvariant(c);
            }
        }
        return len > 0 ? new string(buf[..len]) : string.Empty;
    }

    private static HashSet<char> BuildInvalidCharSet()
    {
        var set = new HashSet<char>(Path.GetInvalidFileNameChars());
        // Explicitly force the Windows-invalid list even if the current
        // platform is POSIX. The output must be portable.
        foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '\0' })
        {
            set.Add(c);
        }
        return set;
    }
}
