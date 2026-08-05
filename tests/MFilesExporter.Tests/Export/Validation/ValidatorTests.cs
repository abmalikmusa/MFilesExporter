using FluentAssertions;
using MFilesExporter.Export.Validation;
using MFilesExporter.Export.Validation.Validators;

namespace MFilesExporter.Tests.Export.Validation;

/// <summary>One test class per validator, sharing a scratch directory.</summary>
public class ValidatorTests : IDisposable
{
    private readonly string _root;

    public ValidatorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mfx-val-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    /* ----------------------- FileExistsValidator ----------------------- */

    [Fact]
    public async Task FileExists_Passes_WhenFilePresent()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, ValidationTestFixtures.Sha256Hex(new byte[]{1}), "pdf", _root);

        var r = await new FileExistsValidator().ValidateAsync(ctx, default);

        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task FileExists_Fails_Retryable_WhenAbsent()
    {
        var ctx = ValidationTestFixtures.Context(
            Path.Combine(_root, "missing.pdf"), 0, "", "pdf", _root);
        var r = await new FileExistsValidator().ValidateAsync(ctx, default);

        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeTrue();
    }

    /* ----------------------- OutputFolderValidator ----------------------- */

    [Fact]
    public async Task OutputFolder_Passes_WhenUnderRoot()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "ab/12/a.pdf", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root);
        var r = await new OutputFolderValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task OutputFolder_Fails_NotRetryable_WhenOutsideRoot()
    {
        var otherRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(otherRoot);
        try
        {
            var path = ValidationTestFixtures.WriteTempFile(otherRoot, "a.pdf", new byte[]{1});
            var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root);
            var r = await new OutputFolderValidator().ValidateAsync(ctx, default);

            r.Status.Should().Be(ValidationCheckStatus.Failed);
            r.IsRetryable.Should().BeFalse();
        }
        finally
        {
            Directory.Delete(otherRoot, true);
        }
    }

    /* ----------------------- ExtensionValidator ----------------------- */

    [Fact]
    public async Task Extension_Passes_WhenMatch_CaseInsensitive()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.PDF", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root);
        var r = await new ExtensionValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task Extension_Fails_WhenMismatch_AndNotAllowed()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.docx", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root);
        var r = await new ExtensionValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Extension_Warns_WhenMismatch_AndAllowed()
    {
        var opts = ValidationTestFixtures.Options(); opts.AllowExtensionMismatch = true;
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.docx", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root);
        var r = await new ExtensionValidator(opts).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Warning);
    }

    /* ----------------------- FileSizeValidator ----------------------- */

    [Fact]
    public async Task FileSize_Passes_OnMatch()
    {
        var payload = new byte[]{1,2,3};
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", payload);
        var ctx = ValidationTestFixtures.Context(path, 3, "", "pdf", _root);
        var r = await new FileSizeValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task FileSize_Fails_NotRetryable_OnMismatch()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", new byte[]{1,2,3});
        var ctx = ValidationTestFixtures.Context(path, 999, "", "pdf", _root);
        var r = await new FileSizeValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeFalse();
    }

    /* ----------------------- ReadableValidator ----------------------- */

    [Fact]
    public async Task Readable_Passes_ForOrdinaryFile()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", new byte[]{1,2,3});
        var ctx = ValidationTestFixtures.Context(path, 3, "", "pdf", _root);
        var r = await new ReadableValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task Readable_Passes_ForZeroLengthFile()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "empty.pdf", Array.Empty<byte>());
        var ctx = ValidationTestFixtures.Context(path, 0, "", "pdf", _root);
        var r = await new ReadableValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task Readable_Fails_Retryable_WhenMissing()
    {
        var ctx = ValidationTestFixtures.Context(
            Path.Combine(_root, "nope.pdf"), 1, "", "pdf", _root);
        var r = await new ReadableValidator().ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeTrue();
    }

    /* ----------------------- ChecksumValidator ----------------------- */

    [Fact]
    public async Task Checksum_Passes_OnMatch()
    {
        var payload = new byte[]{4, 5, 6, 7};
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", payload);
        var ctx = ValidationTestFixtures.Context(path, payload.Length, ValidationTestFixtures.Sha256Hex(payload), "pdf", _root);
        var r = await new ChecksumValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task Checksum_Fails_NotRetryable_OnMismatch()
    {
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", new byte[]{1,2,3});
        var ctx = ValidationTestFixtures.Context(path, 3,
            expectedChecksum: new string('0', 64), "pdf", _root);
        var r = await new ChecksumValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Checksum_Skipped_WhenDisabled()
    {
        var opts = ValidationTestFixtures.Options(); opts.RerunChecksumFromFile = false;
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", new byte[]{1});
        var ctx = ValidationTestFixtures.Context(path, 1, "wontmatch", "pdf", _root);
        var r = await new ChecksumValidator(opts).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Skipped);
    }

    /* ----------------------- MetadataConsistencyValidator ----------------------- */

    [Fact]
    public async Task Metadata_Passes_WhenConsistent()
    {
        var payload = new byte[]{9, 8, 7};
        var checksum = ValidationTestFixtures.Sha256Hex(payload);
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", payload);
        var metadata = ValidationTestFixtures.Metadata(path, payload.Length, checksum, "pdf");
        var ctx = ValidationTestFixtures.Context(path, payload.Length, checksum, "pdf", _root, metadata);

        var r = await new MetadataConsistencyValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Passed);
    }

    [Fact]
    public async Task Metadata_Fails_NotRetryable_OnAnyMismatch()
    {
        var payload = new byte[]{9, 8, 7};
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", payload);
        var metadata = ValidationTestFixtures.Metadata(path, 999, "wrongsum", "docx");
        var ctx = ValidationTestFixtures.Context(path, payload.Length,
            ValidationTestFixtures.Sha256Hex(payload), "pdf", _root, metadata);

        var r = await new MetadataConsistencyValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Failed);
        r.IsRetryable.Should().BeFalse();
        r.FailureReason.Should().Contain("LogicalFileSize");
        r.FailureReason.Should().Contain("Checksum");
        r.FailureReason.Should().Contain("Extension");
    }

    [Fact]
    public async Task Metadata_Skipped_WhenAbsent()
    {
        var payload = new byte[]{1};
        var path = ValidationTestFixtures.WriteTempFile(_root, "a.pdf", payload);
        var ctx = ValidationTestFixtures.Context(path, 1, "", "pdf", _root, metadata: null);

        var r = await new MetadataConsistencyValidator(ValidationTestFixtures.Options()).ValidateAsync(ctx, default);
        r.Status.Should().Be(ValidationCheckStatus.Skipped);
    }
}
