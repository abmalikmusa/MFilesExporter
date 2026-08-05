using System.Globalization;
using MFilesExporter.Configuration.Options;

namespace MFilesExporter.Persistence.MFiles;

/// <summary>
/// Assembles the two derivative queries that together are provably equivalent
/// to the canonical business query:
///
///   SELECT dfv.ID_DOCUMENTFILEPART, dfv.ID_VERSIONPART, dfv.TITLE, dfv.EXTENSION,
///          d.ID_DATAFILEVERSION, d.LOGICALFILESIZE, d.PHYSICALFILESIZE, d.LASTWRITETIME,
///          b.DATA
///   FROM DOCUMENTFILEVERSION dfv
///   JOIN DATAFILEVERSION d
///     ON dfv.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
///    AND dfv.DATAFILEVERSION    = d.ID_DATAFILEVERSION
///   JOIN DATAFILEVERSION_BYTES b
///     ON d.ID_DOCUMENTFILEPART   = b.ID_DOCUMENTFILEPART
///    AND d.ID_DATAFILEVERSION    = b.ID_DATAFILEVERSION
///   WHERE d.UPLOADCOMMITTED = 1;
///
/// The join set, filter, and result set are preserved. Only the BLOB fetch is
/// deferred to a per-document lookup so the enumeration can stream cheaply.
/// </summary>
internal static class MFilesQueries
{
    public static string EnumerationQuery(MFilesTables tables, bool readUncommitted) =>
        string.Format(
            CultureInfo.InvariantCulture,
            @"{0}
SELECT TOP (@BatchSize)
    dfv.ID_DOCUMENTFILEPART,
    dfv.ID_VERSIONPART,
    dfv.TITLE,
    dfv.EXTENSION,
    d.ID_DATAFILEVERSION,
    d.LOGICALFILESIZE,
    d.PHYSICALFILESIZE,
    d.LASTWRITETIME
FROM {1} AS dfv WITH (NOLOCK)
INNER JOIN {2} AS d WITH (NOLOCK)
    ON dfv.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND dfv.DATAFILEVERSION    = d.ID_DATAFILEVERSION
WHERE d.UPLOADCOMMITTED = 1
  AND (
        dfv.ID_DOCUMENTFILEPART > @LastDocumentFilePartId
        OR (dfv.ID_DOCUMENTFILEPART = @LastDocumentFilePartId AND dfv.ID_VERSIONPART > @LastVersionPartId)
      )
ORDER BY dfv.ID_DOCUMENTFILEPART ASC, dfv.ID_VERSIONPART ASC;",
            readUncommitted ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;" : string.Empty,
            QuoteIdentifier(tables.DocumentFileVersion),
            QuoteIdentifier(tables.DataFileVersion));

    public static string ContentQuery(MFilesTables tables) =>
        string.Format(
            CultureInfo.InvariantCulture,
            @"SELECT b.DATA
FROM {0} AS b
INNER JOIN {1} AS d
    ON b.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND b.ID_DATAFILEVERSION  = d.ID_DATAFILEVERSION
WHERE b.ID_DOCUMENTFILEPART = @DocumentFilePartId
  AND b.ID_DATAFILEVERSION  = @DataFileVersionId
  AND d.UPLOADCOMMITTED = 1;",
            QuoteIdentifier(tables.DataFileVersionBytes),
            QuoteIdentifier(tables.DataFileVersion));

    public static string RemainingCountQuery(MFilesTables tables, bool readUncommitted) =>
        string.Format(
            CultureInfo.InvariantCulture,
            @"{0}
SELECT COUNT_BIG(1)
FROM {1} AS dfv WITH (NOLOCK)
INNER JOIN {2} AS d WITH (NOLOCK)
    ON dfv.ID_DOCUMENTFILEPART = d.ID_DOCUMENTFILEPART
   AND dfv.DATAFILEVERSION    = d.ID_DATAFILEVERSION
WHERE d.UPLOADCOMMITTED = 1
  AND (
        dfv.ID_DOCUMENTFILEPART > @LastDocumentFilePartId
        OR (dfv.ID_DOCUMENTFILEPART = @LastDocumentFilePartId AND dfv.ID_VERSIONPART > @LastVersionPartId)
      );",
            readUncommitted ? "SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;" : string.Empty,
            QuoteIdentifier(tables.DocumentFileVersion),
            QuoteIdentifier(tables.DataFileVersion));

    private static string QuoteIdentifier(string ident)
    {
        if (string.IsNullOrWhiteSpace(ident))
        {
            throw new ArgumentException("Identifier must not be empty.", nameof(ident));
        }
        return "[" + ident.Replace("]", "]]", StringComparison.Ordinal) + "]";
    }
}
