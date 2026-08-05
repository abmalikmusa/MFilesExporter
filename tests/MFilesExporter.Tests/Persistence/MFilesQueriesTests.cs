using FluentAssertions;
using MFilesExporter.Configuration.Options;
using MFilesExporter.Persistence.MFiles;

namespace MFilesExporter.Tests.Persistence;

/// <summary>
/// Pins the canonical-query invariants. Any refactor that widens the WHERE
/// clause or drops a join fails the build here.
/// </summary>
public class MFilesQueriesTests
{
    private static readonly MFilesTables Tables = new();

    [Fact]
    public void Enumeration_PreservesUploadCommittedFilter()
    {
        MFilesQueries.EnumerationQuery(Tables, false).Should().Contain("d.UPLOADCOMMITTED = 1");
    }

    [Fact]
    public void Enumeration_JoinsBothMetadataTables()
    {
        var sql = MFilesQueries.EnumerationQuery(Tables, false);
        sql.Should().Contain("DOCUMENTFILEVERSION");
        sql.Should().Contain("DATAFILEVERSION");
        sql.Should().NotContain("DATAFILEVERSION_BYTES");
    }

    [Fact]
    public void Content_JoinsBytesToDataFileVersion_AndFiltersCommitted()
    {
        var sql = MFilesQueries.ContentQuery(Tables);
        sql.Should().Contain("DATAFILEVERSION_BYTES");
        sql.Should().Contain("DATAFILEVERSION");
        sql.Should().Contain("d.UPLOADCOMMITTED = 1");
    }

    [Fact]
    public void Enumeration_KeysetPaginates()
    {
        var sql = MFilesQueries.EnumerationQuery(Tables, false);
        sql.Should().Contain("@LastDocumentFilePartId");
        sql.Should().Contain("@LastVersionPartId");
        sql.Should().Contain("ORDER BY dfv.ID_DOCUMENTFILEPART ASC, dfv.ID_VERSIONPART ASC");
    }
}
