using FluentAssertions;
using MFilesExporter.Application.Abstractions;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Persistence.MFiles.Streaming;

namespace MFilesExporter.Tests.Persistence.Streaming;

public class StreamedDocumentDescriptorTests
{
    [Fact]
    public async Task OpenContentStreamAsync_DelegatesToFactory_OnDemand()
    {
        var descriptor = new DocumentDescriptor(
            new DocumentFileVersionKey(1, 2),
            new DataFileVersionKey(1, 3),
            "t", "pdf", 10, 10, DateTime.UtcNow);

        var opened = 0;
        var factory = new Func<CancellationToken, Task<DocumentContentStream>>(_ =>
        {
            opened++;
            var stream = new MemoryStream(new byte[] { 1, 2, 3 });
            return Task.FromResult(new DocumentContentStream(stream, 3, () =>
            {
                stream.Dispose();
                return ValueTask.CompletedTask;
            }));
        });

        // Construct via reflection since the ctor is internal.
        var ctor = typeof(StreamedDocumentDescriptor).GetConstructor(
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            binder: null,
            new[] { typeof(DocumentDescriptor), typeof(Func<CancellationToken, Task<DocumentContentStream>>) },
            modifiers: null)!;
        var streamed = (StreamedDocumentDescriptor)ctor.Invoke(new object[] { descriptor, factory });

        // The factory must not fire at construction time.
        opened.Should().Be(0);

        await using var content = await streamed.OpenContentStreamAsync(CancellationToken.None);
        opened.Should().Be(1);
        content.Length.Should().Be(3);
    }
}
