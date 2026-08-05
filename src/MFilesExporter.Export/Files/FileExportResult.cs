namespace MFilesExporter.Export.Files;

/// <summary>Return value of a successful <see cref="IFileExportEngine.ExportAsync"/>.</summary>
public sealed record FileExportResult
{
    /// <summary>Absolute path of the file as written.</summary>
    public required string OutputPath { get; init; }

    /// <summary>Directory containing <see cref="OutputPath"/>.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Final filename component with extension.</summary>
    public required string FinalFilename { get; init; }

    /// <summary>Total bytes written to disk.</summary>
    public required long BytesWritten { get; init; }

    /// <summary>True when a duplicate was detected and disambiguation kicked in.</summary>
    public required bool DisambiguatedFromDuplicate { get; init; }

    /// <summary>True when the sanitizer had to modify TITLE (illegal chars, reserved name, truncation).</summary>
    public required bool TitleWasSanitized { get; init; }

    /// <summary>True when the final path required the Windows long-path prefix (<c>\\?\</c>) to open.</summary>
    public required bool RequiredLongPathPrefix { get; init; }
}
