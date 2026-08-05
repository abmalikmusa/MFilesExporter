using FluentAssertions;
using MFilesExporter.Application.Abstractions.Tracking;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.UseCases.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MFilesExporter.Tests.Application;

public class StartExportJobHandlerTests
{
    [Fact]
    public async Task Success_ReturnsAssignedId()
    {
        var repo = Substitute.For<IExportJobRepository>();
        repo.StartAsync("j", "s", "d", "p", 1000L, Arg.Any<CancellationToken>())
            .Returns(42L);

        var sut = new StartExportJobHandler(repo, NullLogger<StartExportJobHandler>.Instance);
        var result = await sut.HandleAsync(new StartExportJobCommand
        {
            JobName = "j",
            SourceServer = "s",
            SourceDatabase = "d",
            PartitionKey = "p",
            TotalDocumentsExpected = 1000,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42L);
    }

    [Fact]
    public async Task MissingRequiredFields_ProducesValidationErrors()
    {
        var repo = Substitute.For<IExportJobRepository>();
        var sut = new StartExportJobHandler(repo, NullLogger<StartExportJobHandler>.Instance);

        var result = await sut.HandleAsync(new StartExportJobCommand
        {
            JobName = "",
            SourceServer = " ",
            SourceDatabase = "",
            PartitionKey = "",
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Errors.Should().OnlyContain(e => e.Kind == ApplicationErrorKind.Validation);
        result.Errors.Should().HaveCountGreaterThan(1);
        await repo.DidNotReceive().StartAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RepositoryException_MappedToUnexpectedError()
    {
        var repo = Substitute.For<IExportJobRepository>();
        repo.StartAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns<Task<long>>(_ => throw new InvalidOperationException("wire down"));

        var sut = new StartExportJobHandler(repo, NullLogger<StartExportJobHandler>.Instance);
        var result = await sut.HandleAsync(new StartExportJobCommand
        {
            JobName = "j", SourceServer = "s", SourceDatabase = "d", PartitionKey = "p",
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.PrimaryError!.Kind.Should().Be(ApplicationErrorKind.Unexpected);
        result.PrimaryError.Code.Should().Be("JOB_START_FAILED");
    }
}
