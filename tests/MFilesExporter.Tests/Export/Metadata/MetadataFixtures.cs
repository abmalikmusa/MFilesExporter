using MFilesExporter.Export.Metadata;

namespace MFilesExporter.Tests.Export.Metadata;

/// <summary>Reusable sample records for the metadata tests.</summary>
internal static class MetadataFixtures
{
    public static MetadataRecord Sample(
        long partId = 1,
        long verPart = 2,
        string title = "Invoice",
        string extension = "pdf",
        string exportStatus = "Succeeded",
        long workerId = 100,
        int retryCount = 1) =>
        new()
        {
            DocumentPartId    = partId,
            VersionPart       = verPart,
            Title             = title,
            Extension         = extension,
            LogicalFileSize   = 1024,
            PhysicalFileSize  = 900,
            LastWriteTime     = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
            ExportPath        = "/data/documents/ab/12/Invoice.pdf",
            Checksum          = "deadbeefcafef00d",
            ExportStatus      = exportStatus,
            ExportDate        = new DateTime(2026, 8, 3, 13, 0, 0, DateTimeKind.Utc),
            WorkerId          = workerId,
            RetryCount        = retryCount,
            IdempotencyKey    = "abcdef012345",
            DataFileVersionId = 999,
        };
}
