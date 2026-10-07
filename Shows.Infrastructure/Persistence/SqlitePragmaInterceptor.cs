using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Data.Sqlite;

namespace Shows.Infrastructure.Persistence;

/// <summary>
/// Applies the mandatory SQLite PRAGMAs on every connection open (SCHEMA.md §1): WAL, foreign keys,
/// and a busy timeout. These are not tuning — concurrent writers are expected (ADR-028), so
/// <c>foreign_keys</c> and <c>busy_timeout</c> are correctness, and they are per-connection, which is
/// why they live in a connection interceptor rather than a one-time startup command.
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(System.Data.Common.DbConnection connection, ConnectionEndEventData eventData)
        => Apply(connection);

    public override async Task ConnectionOpenedAsync(
        System.Data.Common.DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
        => await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);

    private static void Apply(System.Data.Common.DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }

    private static async Task ApplyAsync(
        System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // journal_mode=WAL is persistent per database file but re-issuing it is a cheap no-op; foreign_keys
    // and busy_timeout reset on every new connection, so all three run on open.
    private const string PragmaSql =
        "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;";
}
