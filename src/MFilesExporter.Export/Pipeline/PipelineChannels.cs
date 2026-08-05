using System.Threading.Channels;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Domain.Documents;

namespace MFilesExporter.Export.Pipeline;

public sealed class PipelineChannels
{
    public PipelineChannels(PipelineOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Enumeration = Channel.CreateBounded<DocumentDescriptor>(new BoundedChannelOptions(options.EnumerationChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
            AllowSynchronousContinuations = false,
        });

        Content = Channel.CreateBounded<PreparedDocument>(new BoundedChannelOptions(options.ContentChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        Outcomes = Channel.CreateBounded<ExportOutcome>(new BoundedChannelOptions(Math.Max(options.OutcomeBatchSize * 4, 512))
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public Channel<DocumentDescriptor> Enumeration { get; }
    public Channel<PreparedDocument> Content { get; }
    public Channel<ExportOutcome> Outcomes { get; }
}
