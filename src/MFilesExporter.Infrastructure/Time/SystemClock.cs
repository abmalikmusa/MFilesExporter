using MFilesExporter.Application.Abstractions;

namespace MFilesExporter.Infrastructure.Time;

internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
