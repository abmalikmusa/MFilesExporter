namespace MFilesExporter.Application.Dispatching;

/// <summary>Marker for commands that produce no payload beyond success/failure.</summary>
public interface ICommand
{
}

/// <summary>Marker for commands that produce a typed payload on success.</summary>
public interface ICommand<TResult>
{
}
