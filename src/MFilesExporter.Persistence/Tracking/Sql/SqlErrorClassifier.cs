using Microsoft.Data.SqlClient;

namespace MFilesExporter.Persistence.Tracking.Sql;

/// <summary>
/// Classifies exceptions as transient (retry) vs deterministic (fail fast).
/// Keep the switch narrow — anything not explicitly transient MUST be
/// treated as terminal so we do not amplify a real problem by retrying it.
/// </summary>
public static class SqlErrorClassifier
{
    public static bool IsTransient(Exception ex) => ex switch
    {
        OperationCanceledException => false,
        SqlException sqlEx         => IsTransientSqlError(sqlEx.Number),
        System.IO.IOException      => true,
        TimeoutException           => true,
        _                          => false,
    };

    private static bool IsTransientSqlError(int number) => number switch
    {
        // Deadlock / lock timeout
        1205 or 1222                                          => true,
        // Client-side timeouts / connection loss
        -2 or 233 or 10053 or 10054 or 10060 or 121           => true,
        // Server-busy / Azure SQL scale
        40197 or 40501 or 40613 or 49918 or 49919 or 49920    => true,
        // Login rate limit
        18456 when number == 18456                            => false,
        _                                                     => false,
    };
}
