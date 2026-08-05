using FluentAssertions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.UseCases.Progress;
using NSubstitute;

namespace MFilesExporter.Tests.Application;

public class SaveCheckpointHandlerTests
{
    [Fact]
    public async Task ReturnsAdvancedFlag_FromRepository()
    {
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.SaveAsync(1, "p", 10, 20, 100, Arg.Any<CancellationToken>()).Returns(true);

        var sut = new SaveCheckpointHandler(repo);
        var result = await sut.HandleAsync(new SaveCheckpointCommand
        {
            ExportJobId = 1,
            PartitionKey = "p",
            LastDocumentFilePartId = 10,
            LastVersionPartId = 20,
            DocumentsProcessedInPartition = 100,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task NotAdvanced_IsStillSuccess()
    {
        var repo = Substitute.For<IExportCheckpointRepository>();
        repo.SaveAsync(1, "p", 1, 1, null, Arg.Any<CancellationToken>()).Returns(false);

        var sut = new SaveCheckpointHandler(repo);
        var result = await sut.HandleAsync(new SaveCheckpointCommand
        {
            ExportJobId = 1,
            PartitionKey = "p",
            LastDocumentFilePartId = 1,
            LastVersionPartId = 1,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task InvalidInput_ProducesValidationError()
    {
        var repo = Substitute.For<IExportCheckpointRepository>();
        var sut = new SaveCheckpointHandler(repo);
        var result = await sut.HandleAsync(new SaveCheckpointCommand
        {
            ExportJobId = 0,
            PartitionKey = "",
            LastDocumentFilePartId = 1,
            LastVersionPartId = 1,
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().HaveCount(2);
        await repo.DidNotReceive().SaveAsync(
            Arg.Any<long>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }
}
