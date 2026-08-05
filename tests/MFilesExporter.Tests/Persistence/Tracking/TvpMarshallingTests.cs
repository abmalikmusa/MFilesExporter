using System.Reflection;
using FluentAssertions;
using MFilesExporter.Application.Models.Tracking;
using MFilesExporter.Persistence.Tracking.Sql;
using Microsoft.Data.SqlClient.Server;

namespace MFilesExporter.Tests.Persistence.Tracking;

/// <summary>
/// Guards the private TVP marshallers on the repositories. These tests
/// prove:
///   * The projected SqlDataRecord column count matches the SqlMetaData[].
///   * Null-valued optional columns are DBNull (not zero).
///   * Enumeration is lazy — invoking the marshaller does not eagerly
///     enumerate its input.
/// </summary>
public class TvpMarshallingTests
{
    [Fact]
    public void MetricRepository_ProjectsAllSevenColumns()
    {
        var records = new[]
        {
            new ExportMetricRecord(
                ExportJobId:    1,
                ExportWorkerId: 2,
                MetricName:     "docs.succeeded",
                MetricValue:    3.14,
                MetricUnit:     "{documents}",
                TagsJson:       null,
                CapturedAtUtc:  DateTime.UtcNow),
        };

        var projected = InvokeStaticMarshaller<ExportMetricRecord>(
            typeof(MFilesExporter.Persistence.Tracking.Sql.SqlServerMetricRepository),
            "ToTvpRecords",
            records);

        var row = projected.Single();
        row.FieldCount.Should().Be(7);
        row.GetInt64(0).Should().Be(1);
        row.GetInt64(1).Should().Be(2);
        row.GetString(2).Should().Be("docs.succeeded");
        row.GetDouble(3).Should().Be(3.14);
        row.GetString(4).Should().Be("{documents}");
        row.IsDBNull(5).Should().BeTrue();
    }

    [Fact]
    public void ErrorRepository_ProjectsAllFourteenColumns()
    {
        var records = new[]
        {
            new ExportErrorRecord
            {
                ExportJobId  = 100,
                ErrorSource  = "ContentReaderStage",
                ErrorMessage = "boom",
                Severity     = ExportErrorSeverity.Critical,
                Category     = ExportErrorCategory.Transient,
            },
        };

        var projected = InvokeStaticMarshaller<ExportErrorRecord>(
            typeof(MFilesExporter.Persistence.Tracking.Sql.SqlServerErrorRepository),
            "ToTvpRecords",
            records);

        var row = projected.Single();
        row.FieldCount.Should().Be(14);
        row.GetInt64(0).Should().Be(100);
        row.IsDBNull(1).Should().BeTrue();     // no worker
        row.GetString(6).Should().Be("Critical");
        row.GetString(7).Should().Be("Transient");
        row.GetString(8).Should().Be("ContentReaderStage");
        row.GetString(10).Should().Be("boom");
    }

    [Fact]
    public void ProgressRepository_ProjectsAllTwelveColumns()
    {
        var records = new[]
        {
            new ExportProgressRecord
            {
                ExportJobId       = 42,
                ExportWorkerId    = null,
                SnapshotAtUtc     = new DateTime(2026, 8, 3, 12, 0, 0, DateTimeKind.Utc),
                TotalRecorded     = 100,
                TotalSucceeded    = 90,
                TotalFailed       = 5,
                TotalSkipped      = 5,
                TotalBytesWritten = 999_999,
            },
        };

        var projected = InvokeStaticMarshaller<ExportProgressRecord>(
            typeof(MFilesExporter.Persistence.Tracking.Sql.SqlServerProgressRepository),
            "ToTvpRecords",
            records);

        var row = projected.Single();
        row.FieldCount.Should().Be(12);
        row.IsDBNull(1).Should().BeTrue();
        row.GetInt64(3).Should().Be(100);
        row.IsDBNull(8).Should().BeTrue();      // DocumentsPerSecond
        row.IsDBNull(11).Should().BeTrue();     // LastVersionPartId
    }

    private static IEnumerable<SqlDataRecord> InvokeStaticMarshaller<T>(Type owner, string methodName, IReadOnlyCollection<T> input)
    {
        var method = owner.GetMethod(
            methodName,
            BindingFlags.Static | BindingFlags.NonPublic);
        method.Should().NotBeNull("marshaller '{0}' should exist on {1}", methodName, owner);
        var result = (IEnumerable<SqlDataRecord>)method!.Invoke(null, new object[] { input })!;
        return result.ToArray();
    }
}
