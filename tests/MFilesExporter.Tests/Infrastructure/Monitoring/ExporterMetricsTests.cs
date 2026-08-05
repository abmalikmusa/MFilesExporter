using System.Diagnostics.Metrics;
using FluentAssertions;
using MFilesExporter.Application.Abstractions.Monitoring;
using MFilesExporter.Infrastructure.Monitoring;

namespace MFilesExporter.Tests.Infrastructure.Monitoring;

public class ExporterMetricsTests
{
    [Fact]
    public void RecordOutcome_Increments_Correct_Counter_And_Bytes()
    {
        using var metrics = new ExporterMetrics();
        var collected = new List<(string Name, long Value, IReadOnlyDictionary<string, object?> Tags)>();

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ExporterMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            collected.Add((inst.Name, value, d));
        });
        listener.Start();

        metrics.RecordOutcome(DocumentOutcome.Succeeded, bytesWritten: 2048, elapsed: TimeSpan.FromMilliseconds(50));
        metrics.RecordOutcome(DocumentOutcome.Failed,    bytesWritten: 0,    elapsed: TimeSpan.FromMilliseconds(80));
        metrics.RecordOutcome(DocumentOutcome.Skipped,   bytesWritten: 0,    elapsed: TimeSpan.FromMilliseconds(5));

        collected.Should().Contain(e => e.Name == "mfilesexporter.documents.succeeded" && e.Value == 1);
        collected.Should().Contain(e => e.Name == "mfilesexporter.documents.failed"    && e.Value == 1);
        collected.Should().Contain(e => e.Name == "mfilesexporter.documents.skipped"   && e.Value == 1);
        collected.Should().Contain(e => e.Name == "mfilesexporter.bytes.written"       && e.Value == 2048);
    }

    [Fact]
    public void RecordRetry_Attaches_Category_And_Operation_Tags()
    {
        using var metrics = new ExporterMetrics();
        (string, long, string?, string?)? seen = null;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "mfilesexporter.retries.total") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            string? op = null, cat = null;
            foreach (var t in tags)
            {
                if (t.Key == "operation") op = t.Value?.ToString();
                if (t.Key == "category")  cat = t.Value?.ToString();
            }
            seen = (inst.Name, value, op, cat);
        });
        listener.Start();

        metrics.RecordRetry("sql-blob-read", "SqlDeadlock");

        seen.Should().NotBeNull();
        seen!.Value.Item2.Should().Be(1);
        seen.Value.Item3.Should().Be("sql-blob-read");
        seen.Value.Item4.Should().Be("SqlDeadlock");
    }

    [Fact]
    public void RecordSqlLatency_Emits_To_Histogram()
    {
        using var metrics = new ExporterMetrics();
        double? recorded = null;

        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "mfilesexporter.sql.latency") l.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>((_, value, _, _) => recorded = value);
        listener.Start();

        metrics.RecordSqlLatency("sql.enumerate", TimeSpan.FromMilliseconds(42.5), succeeded: true);

        recorded.Should().Be(42.5);
    }
}
