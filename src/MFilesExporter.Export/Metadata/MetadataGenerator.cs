using MFilesExporter.Configuration.Options;
using Microsoft.Extensions.Logging;

namespace MFilesExporter.Export.Metadata;

/// <summary>
/// Default <see cref="IMetadataGenerator"/> — constructs the enabled
/// writers up-front and drives them in lockstep. Errors from individual
/// writers are surfaced as they occur so a bad disk does not silently
/// produce a truncated artifact.
/// </summary>
public sealed class MetadataGenerator : IMetadataGenerator
{
    private readonly MetadataOptions _options;
    private readonly ManifestJsonWriter _manifestWriter;
    private readonly ILogger<MetadataGenerator> _logger;
    private readonly List<IMetadataWriter> _writers = new();
    private bool _initialized;
    private bool _finalized;

    public MetadataGenerator(
        MetadataOptions options,
        ManifestJsonWriter manifestWriter,
        ILogger<MetadataGenerator> logger)
    {
        _options = options;
        _manifestWriter = manifestWriter;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        if (_options.WriteCsv)  _writers.Add(new CsvMetadataWriter(_options));
        if (_options.WriteJson) _writers.Add(new JsonMetadataWriter(_options));

        foreach (var w in _writers)
        {
            await w.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Metadata generator initialized with {Count} writer(s): {Formats}",
            _writers.Count,
            string.Join(", ", _writers.Select(w => w.Format)));

        _initialized = true;
    }

    public async Task AppendAsync(MetadataRecord record, CancellationToken cancellationToken)
    {
        if (!_initialized) throw new InvalidOperationException("InitializeAsync was not called.");
        ArgumentNullException.ThrowIfNull(record);

        // Fan-out concurrently — writers are independent.
        if (_writers.Count == 1)
        {
            await _writers[0].AppendAsync(record, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tasks = new Task[_writers.Count];
        for (var i = 0; i < _writers.Count; i++)
        {
            tasks[i] = _writers[i].AppendAsync(record, cancellationToken);
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ManifestArtifactReference>> FinalizeAsync(
        ManifestSummary summaryWithoutArtifacts,
        CancellationToken cancellationToken)
    {
        if (_finalized) throw new InvalidOperationException("FinalizeAsync already invoked.");
        _finalized = true;

        // 1. Close each writer (writes trailers).
        foreach (var w in _writers)
        {
            await w.FinalizeAsync(cancellationToken).ConfigureAwait(false);
        }

        // 2. Compose artifact references — record counts come from the writers themselves.
        var references = _writers
            .Select(w => new ManifestArtifactReference(
                RelativePath: Path.GetFileName(w.OutputPath),
                Format:       w.Format,
                RecordCount:  w.RecordCount))
            .ToList();

        // 3. Emit manifest.json with the artifact list attached.
        if (_options.WriteManifest)
        {
            var manifest = summaryWithoutArtifacts with { Artifacts = references };
            var path = await _manifestWriter.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Manifest written to {Path}", path);
        }

        // 4. Dispose writers (closes underlying streams).
        foreach (var w in _writers)
        {
            await w.DisposeAsync().ConfigureAwait(false);
        }

        return references;
    }
}
