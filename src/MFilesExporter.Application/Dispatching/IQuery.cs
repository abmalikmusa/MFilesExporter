namespace MFilesExporter.Application.Dispatching;

/// <summary>
/// Marker for queries. A query never mutates state and always returns a
/// payload. Split from <see cref="ICommand{TResult}"/> so pipeline behaviors
/// can treat reads and writes differently (e.g., only writes get audit
/// logging).
/// </summary>
public interface IQuery<TResult>
{
}
