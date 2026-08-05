using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Export.Validation;
using MFilesExporter.Export.Validation.Reporting;
using Microsoft.Extensions.Logging.Abstractions;

namespace MFilesExporter.Tests.Export.Validation;

public class ExportValidationPipelineTests : IDisposable
{
    private readonly string _root;
    private readonly List<ExportValidationReport> _reported = new();

    public ExportValidationPipelineTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mfx-pipe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    /// <summary>Test validator that returns a preset result.</summary>
    private sealed class ScriptedValidator : IExportValidator
    {
        public required string ScriptedName { get; init; }
        public required int ScriptedOrder { get; init; }
        public required ValidationCheckResult ScriptedResult { get; init; }
        public int InvocationCount { get; private set; }

        public string Name => ScriptedName;
        public int Order => ScriptedOrder;

        public Task<ValidationCheckResult> ValidateAsync(ExportValidationContext ctx, CancellationToken ct)
        {
            InvocationCount++;
            return Task.FromResult(ScriptedResult);
        }
    }

    private sealed class ThrowingValidator : IExportValidator
    {
        public string Name => nameof(ThrowingValidator);
        public int Order => 5;
        public Task<ValidationCheckResult> ValidateAsync(ExportValidationContext ctx, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class RecordingReporter : IValidationReporter
    {
        public List<ExportValidationReport> Reports { get; } = new();
        public Task ReportAsync(ExportValidationContext ctx, ExportValidationReport report, CancellationToken ct)
        {
            Reports.Add(report);
            return Task.CompletedTask;
        }
    }

    private static ExportValidationContext MinimalContext(string root) =>
        ValidationTestFixtures.Context(
            Path.Combine(root, "a.pdf"), 0, "", "pdf", root);

    [Fact]
    public async Task RunsValidators_InOrder()
    {
        var order = new List<string>();
        IExportValidator make(string name, int ord) => new ScriptedValidator
        {
            ScriptedName = name, ScriptedOrder = ord,
            ScriptedResult = ValidationCheckResult.Passed(name, TimeSpan.Zero),
        };

        var validators = new[] { make("C", 30), make("A", 10), make("B", 20) };
        var pipeline = new ExportValidationPipeline(
            validators, Array.Empty<IValidationReporter>(),
            ValidationTestFixtures.Options(),
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);
        report.Checks.Select(c => c.ValidatorName).Should().Equal("A", "B", "C");
    }

    [Fact]
    public async Task FailFast_StopsAtFirstFailure()
    {
        var v1 = new ScriptedValidator
        {
            ScriptedName = "V1", ScriptedOrder = 1,
            ScriptedResult = ValidationCheckResult.Passed("V1", TimeSpan.Zero),
        };
        var v2 = new ScriptedValidator
        {
            ScriptedName = "V2", ScriptedOrder = 2,
            ScriptedResult = ValidationCheckResult.Failed("V2", TimeSpan.Zero, "no", isRetryable: false),
        };
        var v3 = new ScriptedValidator
        {
            ScriptedName = "V3", ScriptedOrder = 3,
            ScriptedResult = ValidationCheckResult.Passed("V3", TimeSpan.Zero),
        };

        var opts = ValidationTestFixtures.Options(); opts.Mode = ValidationExecutionMode.FailFast;
        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { v1, v2, v3 },
            Array.Empty<IValidationReporter>(),
            opts,
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);

        report.Checks.Should().HaveCount(2, "V3 should not have been invoked");
        v3.InvocationCount.Should().Be(0);
        report.HasFailures.Should().BeTrue();
    }

    [Fact]
    public async Task RunAll_ExecutesEveryValidator_EvenAfterFailure()
    {
        var v1 = new ScriptedValidator
        {
            ScriptedName = "V1", ScriptedOrder = 1,
            ScriptedResult = ValidationCheckResult.Failed("V1", TimeSpan.Zero, "x", isRetryable: false),
        };
        var v2 = new ScriptedValidator
        {
            ScriptedName = "V2", ScriptedOrder = 2,
            ScriptedResult = ValidationCheckResult.Passed("V2", TimeSpan.Zero),
        };
        var v3 = new ScriptedValidator
        {
            ScriptedName = "V3", ScriptedOrder = 3,
            ScriptedResult = ValidationCheckResult.Failed("V3", TimeSpan.Zero, "y", isRetryable: true),
        };

        var opts = ValidationTestFixtures.Options(); opts.Mode = ValidationExecutionMode.RunAll;
        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { v1, v2, v3 },
            Array.Empty<IValidationReporter>(),
            opts,
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);

        report.Checks.Should().HaveCount(3);
        report.HasFailures.Should().BeTrue();
        report.AllFailuresRetryable.Should().BeFalse();     // V1 non-retryable dominates
    }

    [Fact]
    public async Task Retryable_ReportedCorrectly_WhenAllFailuresAreRetryable()
    {
        var v = new ScriptedValidator
        {
            ScriptedName = "T", ScriptedOrder = 1,
            ScriptedResult = ValidationCheckResult.Failed("T", TimeSpan.Zero, "transient", isRetryable: true),
        };

        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { v }, Array.Empty<IValidationReporter>(),
            ValidationTestFixtures.Options(),
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);

        report.HasFailures.Should().BeTrue();
        report.AllFailuresRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task ValidatorException_BecomesNonRetryableFailure()
    {
        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { new ThrowingValidator() },
            Array.Empty<IValidationReporter>(),
            ValidationTestFixtures.Options(),
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);

        report.HasFailures.Should().BeTrue();
        report.AllFailuresRetryable.Should().BeFalse();
        report.Failures.Single().FailureReason.Should().Contain("boom");
    }

    [Fact]
    public async Task PipelineDisabled_ReturnsEmptyReport_Immediately()
    {
        var opts = ValidationTestFixtures.Options(); opts.Enabled = false;
        var v = new ScriptedValidator
        {
            ScriptedName = "V", ScriptedOrder = 1,
            ScriptedResult = ValidationCheckResult.Passed("V", TimeSpan.Zero),
        };

        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { v }, Array.Empty<IValidationReporter>(),
            opts,
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);
        report.Checks.Should().BeEmpty();
        report.IsValid.Should().BeTrue();
        v.InvocationCount.Should().Be(0);
    }

    [Fact]
    public async Task EnabledValidatorAllowList_Filters()
    {
        var vA = new ScriptedValidator
        {
            ScriptedName = "A", ScriptedOrder = 1,
            ScriptedResult = ValidationCheckResult.Passed("A", TimeSpan.Zero),
        };
        var vB = new ScriptedValidator
        {
            ScriptedName = "B", ScriptedOrder = 2,
            ScriptedResult = ValidationCheckResult.Passed("B", TimeSpan.Zero),
        };

        var opts = ValidationTestFixtures.Options();
        opts.EnabledValidators.Add("B");

        var pipeline = new ExportValidationPipeline(
            new IExportValidator[] { vA, vB }, Array.Empty<IValidationReporter>(),
            opts,
            NullLogger<ExportValidationPipeline>.Instance);

        var report = await pipeline.ValidateAsync(MinimalContext(_root), default);
        report.Checks.Should().HaveCount(1);
        report.Checks[0].ValidatorName.Should().Be("B");
    }

    [Fact]
    public async Task Reporters_ReceiveFinalReport()
    {
        var reporter = new RecordingReporter();
        var pipeline = new ExportValidationPipeline(
            Array.Empty<IExportValidator>(), new[] { reporter },
            ValidationTestFixtures.Options(),
            NullLogger<ExportValidationPipeline>.Instance);

        _ = await pipeline.ValidateAsync(MinimalContext(_root), default);
        reporter.Reports.Should().HaveCount(1);
    }
}
