using FluentAssertions;
using MFilesExporter.Application.Abstractions.WorkClaiming;
using MFilesExporter.Application.Common;
using MFilesExporter.Application.UseCases.WorkClaiming;
using MFilesExporter.Domain.Documents;
using MFilesExporter.Domain.Jobs;
using MFilesExporter.Domain.WorkClaiming;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace MFilesExporter.Tests.Application;

public class WorkClaimingHandlersTests
{
    [Fact]
    public async Task Claim_ForwardsToStore_AndReturnsResult()
    {
        var claimed = new List<ClaimedWorkItem>
        {
            new()
            {
                WorkItemId = new WorkItemId(1),
                JobId = new ExportJobId(10),
                IdempotencyKey = IdempotencyKey.For(1, 2, 3),
                DocumentFileVersionKey = new DocumentFileVersionKey(1, 2),
                DataFileVersionKey = new DataFileVersionKey(1, 3),
                ClaimToken = new ClaimToken(Guid.NewGuid()),
                LeaseExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
                AttemptNumber = 1,
                MaxAttempts = 5,
            },
        };
        var store = Substitute.For<IWorkClaimStore>();
        store.ClaimAsync(10, 20, 100, TimeSpan.FromMinutes(5), Arg.Any<CancellationToken>())
            .Returns(claimed);

        var sut = new ClaimWorkBatchHandler(store);
        var result = await sut.HandleAsync(new ClaimWorkBatchCommand
        {
            ExportJobId = 10, WorkerId = 20, BatchSize = 100,
            LeaseDuration = TimeSpan.FromMinutes(5),
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeEquivalentTo(claimed);
    }

    [Fact]
    public async Task Claim_InvalidBatchSize_Fails()
    {
        var store = Substitute.For<IWorkClaimStore>();
        var sut = new ClaimWorkBatchHandler(store);
        var result = await sut.HandleAsync(new ClaimWorkBatchCommand
        {
            ExportJobId = 1, WorkerId = 1, BatchSize = 0,
        }, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.PrimaryError!.Kind.Should().Be(ApplicationErrorKind.Validation);
        await store.DidNotReceive().ClaimAsync(
            Arg.Any<long>(), Arg.Any<long>(), Arg.Any<int>(),
            Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_ReturnsTrue_WhenTokenOwnsClaim()
    {
        var store = Substitute.For<IWorkClaimStore>();
        var token = new ClaimToken(Guid.NewGuid());
        store.CompleteAsync(
                new WorkItemId(1), token, "/out", "abc", 100, Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new CompleteWorkItemHandler(store);
        var result = await sut.HandleAsync(new CompleteWorkItemCommand
        {
            WorkItemId = new WorkItemId(1),
            ClaimToken = token,
            OutputPath = "/out",
            Checksum = "abc",
            BytesWritten = 100,
        }, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeTrue();
    }

    [Fact]
    public async Task Complete_ReturnsFalse_WhenTokenNoLongerOwnsClaim()
    {
        var store = Substitute.For<IWorkClaimStore>();
        var token = new ClaimToken(Guid.NewGuid());
        store.CompleteAsync(
                Arg.Any<WorkItemId>(), Arg.Any<ClaimToken>(),
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = new CompleteWorkItemHandler(store);
        var result = await sut.HandleAsync(new CompleteWorkItemCommand
        {
            WorkItemId = new WorkItemId(1),
            ClaimToken = token,
            OutputPath = "/out",
            Checksum = "abc",
            BytesWritten = 100,
        }, CancellationToken.None);

        // The handler must treat this as SUCCESS with value=false (not a validation error).
        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeFalse();
    }

    [Fact]
    public async Task Fail_TransientVsPermanent_ForwardedCorrectly()
    {
        var store = Substitute.For<IWorkClaimStore>();
        store.FailAsync(
                Arg.Any<WorkItemId>(), Arg.Any<ClaimToken>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var sut = new FailWorkItemHandler(store);

        _ = await sut.HandleAsync(new FailWorkItemCommand
        {
            WorkItemId = new WorkItemId(1),
            ClaimToken = new ClaimToken(Guid.NewGuid()),
            Reason = "transient",
            IsPermanent = false,
        }, CancellationToken.None);

        _ = await sut.HandleAsync(new FailWorkItemCommand
        {
            WorkItemId = new WorkItemId(2),
            ClaimToken = new ClaimToken(Guid.NewGuid()),
            Reason = "permanent",
            IsPermanent = true,
        }, CancellationToken.None);

        await store.Received(1).FailAsync(
            new WorkItemId(1), Arg.Any<ClaimToken>(), "transient", false, Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await store.Received(1).FailAsync(
            new WorkItemId(2), Arg.Any<ClaimToken>(), "permanent", true, Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Renew_ReturnsNewExpiryOrNull()
    {
        var next = DateTimeOffset.UtcNow.AddMinutes(5);
        var store = Substitute.For<IWorkClaimStore>();
        store.RenewAsync(
                Arg.Any<WorkItemId>(), Arg.Any<ClaimToken>(), Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(next, (DateTimeOffset?)null);

        var sut = new RenewLeaseHandler(store);
        var firstToken = new ClaimToken(Guid.NewGuid());

        var r1 = await sut.HandleAsync(new RenewLeaseCommand
        {
            WorkItemId = new WorkItemId(1), ClaimToken = firstToken,
        }, CancellationToken.None);
        var r2 = await sut.HandleAsync(new RenewLeaseCommand
        {
            WorkItemId = new WorkItemId(1), ClaimToken = firstToken,
        }, CancellationToken.None);

        r1.Value.Should().Be(next);
        r2.Value.Should().BeNull();
    }

    [Fact]
    public async Task Reclaim_ReturnsCount()
    {
        var store = Substitute.For<IWorkClaimStore>();
        store.ReclaimExpiredAsync(Arg.Any<TimeSpan>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var sut = new ReclaimExpiredHandler(store, NullLogger<ReclaimExpiredHandler>.Instance);
        var result = await sut.HandleAsync(new ReclaimExpiredCommand(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }
}
