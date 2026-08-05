namespace MFilesExporter.Application.Abstractions;

public interface IResiliencePipelineProvider
{
    ValueTask<T> ExecuteAsync<T>(
        string pipelineName,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken);

    ValueTask ExecuteAsync(
        string pipelineName,
        Func<CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken);
}

public static class ResiliencePipelineNames
{
    public const string SqlRead = "sql-read";
    public const string SqlBlobRead = "sql-blob-read";
    public const string DiskWrite = "disk-write";
    public const string StateStore = "state-store";
}
