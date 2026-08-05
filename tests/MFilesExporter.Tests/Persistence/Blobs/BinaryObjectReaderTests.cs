using System.Security.Cryptography;
using FluentAssertions;
using MFilesExporter.Persistence.MFiles.Blobs;

namespace MFilesExporter.Tests.Persistence.Blobs;

/// <summary>
/// Exercises the copy loop directly via the internal <c>ReadInternalAsync</c>
/// helper so the tests do not require a live SQL Server. The delegate
/// signature mirrors <c>SqlDataReader.GetBytes</c> exactly, so anything
/// verified here holds against the real thing.
/// </summary>
public class BinaryObjectReaderTests
{
    /// <summary>
    /// In-memory source that mimics <see cref="Microsoft.Data.SqlClient.SqlDataReader.GetBytes"/>:
    /// returns <c>0</c> at EOF, otherwise fills the buffer up to <paramref name="length"/>.
    /// </summary>
    private static BinaryObjectReader.GetBytesDelegate SourceOf(byte[] data)
    {
        return (fieldOffset, buffer, bufferOffset, length) =>
        {
            if (fieldOffset >= data.Length) return 0;
            var remaining = data.Length - fieldOffset;
            var toCopy = (int)Math.Min(length, remaining);
            Array.Copy(data, fieldOffset, buffer, bufferOffset, toCopy);
            return toCopy;
        };
    }

    [Fact]
    public async Task CopiesEveryByte_ToDestination()
    {
        var payload = new byte[10_000];
        new Random(42).NextBytes(payload);
        var destination = new MemoryStream();

        var result = await BinaryObjectReader.ReadInternalAsync(
            SourceOf(payload),
            destination,
            new BinaryReadOptions { BufferSize = 4_096 },
            progress: null,
            CancellationToken.None);

        destination.ToArray().Should().Equal(payload);
        result.BytesRead.Should().Be(payload.Length);
        result.ChunkCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task ComputesSha256_ThatMatchesReference()
    {
        var payload = "hello binary reader"u8.ToArray();
        var expected = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var result = await BinaryObjectReader.ReadInternalAsync(
            SourceOf(payload),
            new MemoryStream(),
            new BinaryReadOptions { BufferSize = 4_096, Checksum = BinaryChecksumAlgorithm.Sha256 },
            progress: null,
            CancellationToken.None);

        result.ChecksumHex.Should().Be(expected);
        result.ChecksumAlgorithm.Should().Be(BinaryChecksumAlgorithm.Sha256);
    }

    [Fact]
    public async Task Checksum_None_LeavesHashNull()
    {
        var result = await BinaryObjectReader.ReadInternalAsync(
            SourceOf(new byte[100]),
            new MemoryStream(),
            new BinaryReadOptions { BufferSize = 4_096, Checksum = BinaryChecksumAlgorithm.None },
            progress: null,
            CancellationToken.None);

        result.ChecksumHex.Should().BeNull();
        result.ChecksumAlgorithm.Should().Be(BinaryChecksumAlgorithm.None);
    }

    [Fact]
    public async Task EmitsProgress_AtIntervalAndAtEnd()
    {
        // A modestly-slow "source" so the interval-based tick has time to fire.
        var payload = new byte[64_000];
        var source = SourceOf(payload);
        BinaryObjectReader.GetBytesDelegate slowSource = (offset, buf, boff, len) =>
        {
            Thread.Sleep(10);
            return source(offset, buf, boff, len);
        };

        var samples = new List<BinaryReadProgress>();
        var progress = new SyncProgress<BinaryReadProgress>(samples.Add);

        var result = await BinaryObjectReader.ReadInternalAsync(
            slowSource,
            new MemoryStream(),
            new BinaryReadOptions
            {
                BufferSize             = 4_096,
                ProgressReportInterval = TimeSpan.FromMilliseconds(20),
                ExpectedByteCount      = payload.Length,
            },
            progress,
            CancellationToken.None);

        samples.Should().NotBeEmpty("progress must fire at least once");
        // Final sample reflects the full transfer.
        samples[^1].BytesTransferred.Should().Be(result.BytesRead);
        samples[^1].PercentComplete.Should().Be(1.0);
    }

    [Fact]
    public async Task Validation_Success_PopulatesValidationBlock()
    {
        var payload = new byte[]{ 1, 2, 3, 4, 5 };
        var expectedHash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        var result = await BinaryObjectReader.ReadInternalAsync(
            SourceOf(payload),
            new MemoryStream(),
            new BinaryReadOptions
            {
                ExpectedByteCount   = payload.Length,
                ExpectedChecksumHex = expectedHash,
            },
            progress: null,
            CancellationToken.None);

        result.Validation.Should().NotBeNull();
        result.Validation!.IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Validation_ByteCountMismatch_Throws()
    {
        var payload = new byte[100];
        Func<Task> act = async () =>
        {
            await BinaryObjectReader.ReadInternalAsync(
                SourceOf(payload),
                new MemoryStream(),
                new BinaryReadOptions { ExpectedByteCount = 200 },
                progress: null,
                CancellationToken.None);
        };
        await act.Should().ThrowAsync<BinaryValidationException>()
            .Where(e => e.Validation.ByteCountMatches == false);
    }

    [Fact]
    public async Task Validation_ChecksumMismatch_Throws()
    {
        var payload = new byte[]{ 9, 9, 9 };
        Func<Task> act = async () =>
        {
            await BinaryObjectReader.ReadInternalAsync(
                SourceOf(payload),
                new MemoryStream(),
                new BinaryReadOptions
                {
                    ExpectedByteCount   = payload.Length,
                    ExpectedChecksumHex = "0000000000000000000000000000000000000000000000000000000000000000",
                },
                progress: null,
                CancellationToken.None);
        };
        await act.Should().ThrowAsync<BinaryValidationException>()
            .Where(e => e.Validation.ChecksumMatches == false
                     && e.Validation.ByteCountMatches == true);
    }

    [Fact]
    public async Task Validation_ThrowOff_ReportsWithoutThrowing()
    {
        var payload = new byte[7];
        var result = await BinaryObjectReader.ReadInternalAsync(
            SourceOf(payload),
            new MemoryStream(),
            new BinaryReadOptions
            {
                ExpectedByteCount        = 999,
                ThrowOnValidationFailure = false,
            },
            progress: null,
            CancellationToken.None);

        result.Validation.Should().NotBeNull();
        result.Validation!.IsValid.Should().BeFalse();
        result.BytesRead.Should().Be(7);
    }

    [Fact]
    public async Task Cancellation_AbortsMidCopy()
    {
        var payload = new byte[10_000_000];       // 10 MB
        var source = SourceOf(payload);
        BinaryObjectReader.GetBytesDelegate slow = (offset, buf, boff, len) =>
        {
            Thread.Sleep(2);
            return source(offset, buf, boff, len);
        };

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(30));

        Func<Task> act = async () => await BinaryObjectReader.ReadInternalAsync(
            slow,
            new MemoryStream(),
            new BinaryReadOptions { BufferSize = 4_096 },
            progress: null,
            cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task LargeFile_SimulatesGreaterThan4GiBOffsets()
    {
        // We can't actually allocate 4 GiB in a test, but we CAN prove the loop
        // uses long offsets end-to-end by hooking the delegate and inspecting
        // the offsets it is called with.
        //
        // The delegate below claims a virtual payload of 5 GiB and returns
        // bufferSize bytes per call until the position exceeds 5 GiB.
        const long virtualSize = 5L * 1024 * 1024 * 1024;   // 5 GiB
        var maxSeenOffset = 0L;
        var chunks = 0L;
        BinaryObjectReader.GetBytesDelegate largeSource = (offset, buf, boff, len) =>
        {
            maxSeenOffset = Math.Max(maxSeenOffset, offset);
            if (offset >= virtualSize) return 0;
            chunks++;
            // Emit an arbitrary byte; contents do not matter for offset-arithmetic proof.
            Array.Clear(buf, boff, len);
            return len;
        };

        // Discard destination — avoid allocating GB of memory in the test.
        var result = await BinaryObjectReader.ReadInternalAsync(
            largeSource,
            Stream.Null,
            new BinaryReadOptions
            {
                BufferSize             = 4 * 1024 * 1024,   // 4 MB buffer keeps the loop small
                Checksum               = BinaryChecksumAlgorithm.None,
                ProgressReportInterval = TimeSpan.Zero,
            },
            progress: null,
            CancellationToken.None);

        result.BytesRead.Should().BeGreaterThan(int.MaxValue,
            "the loop must accumulate past 2 GiB — proving 64-bit safety");
        result.BytesRead.Should().Be(virtualSize);
        maxSeenOffset.Should().BeGreaterThan(int.MaxValue);
    }

    [Fact]
    public void BufferSize_TooSmall_Rejects()
    {
        var reader = new BinaryObjectReader();
        Action act = () => reader.ReadAsync(
            reader:           null!,          // never reached; validation runs first
            ordinal:          0,
            destination:      Stream.Null,
            options:          new BinaryReadOptions { BufferSize = 1024 },
            progress:         null,
            cancellationToken: CancellationToken.None);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ExpectedChecksum_WithNoAlgorithm_Rejects()
    {
        var reader = new BinaryObjectReader();
        // A synthetic SqlDataReader is not required — the guard runs before any IO.
        Action act = () => reader.ReadAsync(
            reader:           null!,
            ordinal:          0,
            destination:      Stream.Null,
            options:          new BinaryReadOptions
            {
                Checksum            = BinaryChecksumAlgorithm.None,
                ExpectedChecksumHex = "deadbeef",
            },
            progress:         null,
            cancellationToken: CancellationToken.None);
        act.Should().Throw<ArgumentException>()
            .Where(e => e.Message.Contains("ExpectedChecksumHex"));
    }

    /// <summary>
    /// <see cref="System.Progress{T}"/> hops through SynchronizationContext,
    /// which delays observation in tests. This synchronous variant delivers
    /// samples in-line for reliable assertions.
    /// </summary>
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }
}
