namespace MFilesExporter.Shared.Collections;

public static class CollectionExtensions
{
    /// <summary>
    /// Chunks a source enumerable into buffers of a fixed size. The last chunk
    /// may be smaller. Materializes each chunk fully before yielding.
    /// </summary>
    public static IEnumerable<IReadOnlyList<T>> ChunkBy<T>(this IEnumerable<T> source, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        var buffer = new List<T>(size);
        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count == size)
            {
                yield return buffer;
                buffer = new List<T>(size);
            }
        }
        if (buffer.Count > 0)
        {
            yield return buffer;
        }
    }
}
