using System.Security.Cryptography;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Export.Metadata;
using MFilesExporter.Export.Validation;

namespace MFilesExporter.Tests.Export.Validation;

/// <summary>Shared fixtures for the validation unit tests.</summary>
internal static class ValidationTestFixtures
{
    /// <summary>Writes bytes to a temp file and returns its path.</summary>
    public static string WriteTempFile(string root, string relative, byte[] bytes)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public static string Sha256Hex(byte[] payload) =>
        Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    public static DocumentDescriptor Descriptor(
        long partId = 1, long verPart = 2, string title = "Invoice", string ext = "pdf") =>
        new(new DocumentFileVersionKey(partId, verPart),
            new DataFileVersionKey(partId, 3),
            title, ext, 100, 100,
            new DateTime(2026, 8, 4, 0, 0, 0, DateTimeKind.Utc));

    public static ExportValidationContext Context(
        string outputPath,
        long expectedBytes,
        string expectedChecksum,
        string expectedExt,
        string expectedRoot,
        MetadataRecord? metadata = null) =>
        new()
        {
            Descriptor            = Descriptor(ext: expectedExt),
            OutputPath            = outputPath,
            ExpectedByteCount     = expectedBytes,
            ExpectedChecksumHex   = expectedChecksum,
            ExpectedExtension     = expectedExt,
            ExpectedRootDirectory = expectedRoot,
            MetadataRecord        = metadata,
        };

    public static MetadataRecord Metadata(
        string exportPath,
        long size,
        string checksum,
        string extension,
        string status = "Succeeded") =>
        new()
        {
            DocumentPartId   = 1,
            VersionPart      = 2,
            Title            = "Invoice",
            Extension        = extension,
            LogicalFileSize  = size,
            PhysicalFileSize = size,
            LastWriteTime    = DateTime.UtcNow,
            ExportPath       = exportPath,
            Checksum         = checksum,
            ExportStatus     = status,
            ExportDate       = DateTime.UtcNow,
            WorkerId         = 100,
            RetryCount       = 1,
        };

    public static ExportValidationOptions Options() => new()
    {
        Enabled = true,
        Mode = ValidationExecutionMode.FailFast,
        PerValidatorTimeout = TimeSpan.FromSeconds(30),
        RerunChecksumFromFile = true,
        AllowExtensionMismatch = false,
        ValidateMetadataConsistency = true,
    };
}
