using System.Threading.Channels;
using MFilesExporter.Application.Abstractions.Monitoring;

namespace MFilesExporter.Infrastructure.Monitoring;

/// <summary>
/// Adapter that turns a <see cref="Channel{T}"/> into an
/// <see cref="IQueueDepthProvider"/> so its buffered depth shows up on the
/// <c>mfilesexporter.queue.depth</c> gauge.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Channel{T}"/> exposes an approximate count via
/// <see cref="Channel{T}.Reader"/>'s <c>CanCount</c> / <c>Count</c> properties
/// (bounded channels expose it; unbounded do not). When counting is not
/// supported the depth is reported as <c>0</c> so the gauge does not lie.
/// </para>
/// </remarks>
public sealed class ChannelQueueDepthProvider<T> : IQueueDepthProvider
{
    private readonly ChannelReader<T> _reader;

    public ChannelQueueDepthProvider(string name, Channel<T> channel, int? capacity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(channel);

        Name = name;
        Capacity = capacity;
        _reader = channel.Reader;
    }

    public string Name { get; }

    public int? Capacity { get; }

    public int Depth => _reader.CanCount ? _reader.Count : 0;
}
