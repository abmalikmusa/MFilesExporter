using System.Globalization;
using System.Text.Json;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Emits <c>manifest.json</c> — a single-file run summary written once at
/// the end of the export. Small (a few KB), so we take the simple path:
/// build the object with <see cref="Utf8JsonWriter"/> and write it out in
/// one shot.
/// </summary>
public sealed class ManifestJsonWriter
{
    private readonly MetadataOptions _options;

    public ManifestJsonWriter(MetadataOptions options)
    {
        _options = options;
    }

    /// <summary>Writes the manifest to disk. Returns the final path.</summary>
    public async Task<string> WriteAsync(ManifestSummary summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(summary);

        Directory.CreateDirectory(_options.OutputDirectory);
        var path = Path.Combine(_options.OutputDirectory, _options.ManifestFileName);

        var tempPath = path + ".partial";

        await using (var stream = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.Read,
            bufferSize: 4_096,
            FileOptions.Asynchronous))
        await using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            SkipValidation = false,
        }))
        {
            writer.WriteStartObject();

            writer.WriteString("schemaVersion", MetadataSchema.Version);
            writer.WriteString("schemaId",      MetadataSchema.SchemaId);
            writer.WriteString("generator",     MetadataSchema.GeneratorName);
            writer.WriteString("generatedAt",   FormatDate(DateTime.UtcNow));

            /* ---- job block ---- */
            writer.WritePropertyName("job");
            writer.WriteStartObject();
            writer.WriteNumber("id",            summary.JobId);
            writer.WriteString("name",          summary.JobName);
            writer.WriteString("partitionKey",  summary.PartitionKey);
            writer.WriteString("sourceServer",  summary.SourceServer);
            writer.WriteString("sourceDatabase",summary.SourceDatabase);
            writer.WriteString("startedAtUtc",  FormatDate(summary.StartedAtUtc));
            if (summary.CompletedAtUtc is DateTime completed)
            {
                writer.WriteString("completedAtUtc", FormatDate(completed));
            }
            else
            {
                writer.WriteNull("completedAtUtc");
            }
            writer.WriteEndObject();

            /* ---- totals block ---- */
            writer.WritePropertyName("totals");
            writer.WriteStartObject();
            writer.WriteNumber("documentsExpected",  summary.Totals.DocumentsExpected);
            writer.WriteNumber("documentsRecorded",  summary.Totals.DocumentsRecorded);
            writer.WriteNumber("succeeded",          summary.Totals.Succeeded);
            writer.WriteNumber("failed",             summary.Totals.Failed);
            writer.WriteNumber("skipped",            summary.Totals.Skipped);
            writer.WriteNumber("totalBytesWritten",  summary.Totals.TotalBytesWritten);
            writer.WriteEndObject();

            /* ---- artifacts block ---- */
            writer.WritePropertyName("artifacts");
            writer.WriteStartArray();
            foreach (var artifact in summary.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("relativePath", artifact.RelativePath);
                writer.WriteString("format",       artifact.Format);
                writer.WriteNumber("recordCount",  artifact.RecordCount);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        // Atomic swap so a partially-written manifest never appears at the final path.
        File.Move(tempPath, path, overwrite: true);
        return path;
    }

    private static string FormatDate(DateTime d) =>
        DateTime.SpecifyKind(d, DateTimeKind.Utc)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}
