using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shows.Infrastructure.Persistence;
using Xunit;

namespace Shows.Tests.Integration;

/// <summary>
/// Step 1.1 acceptance: the SQLite setup and the id convention. The reflection check is the load-bearing
/// one — it is the model, not the migration, that decides whether EF tries to read a generated value
/// back (SCHEMA.md §1a). A migration grep alone would pass even with the convention missing.
/// </summary>
public sealed class ShowsDbContextTests : IDisposable
{
    private readonly string _dbPath;

    public ShowsDbContextTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"shows-1-1-{Guid.NewGuid():N}.db");
    }

    private ShowsDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<ShowsDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString())
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;
        return new ShowsDbContext(options);
    }

    [Fact]
    public void Opening_and_closing_creates_the_file_with_wal_journal_mode()
    {
        using (var context = CreateContext())
        {
            context.Database.EnsureCreated();
        }

        Assert.True(File.Exists(_dbPath));

        // Read journal_mode back on a fresh connection — WAL is persistent per file, so this proves the
        // PRAGMA actually ran, not that the interceptor was registered.
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        var mode = command.ExecuteScalar()?.ToString();

        Assert.Equal("wal", mode);
    }

    [Fact]
    public void Generated_ddl_contains_no_autoincrement()
    {
        using var context = CreateContext();

        // The shipped DDL, generated from the model — the same text EF would emit. Grepping this is
        // stronger than grepping the migration file: it reflects the current model, not a snapshot.
        var script = context.Database.GenerateCreateScript();

        Assert.DoesNotContain("AUTOINCREMENT", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_property_in_the_model_is_store_generated()
    {
        using var context = CreateContext();

        // SCHEMA.md §1a: the SQLite provider defaults an INTEGER PK to OnAdd. The convention must beat
        // it on EVERY property, so this set must be empty. This is the check that actually matters.
        var generated = context.Model.GetEntityTypes()
            .SelectMany(t => t.GetProperties())
            .Where(p => p.ValueGenerated != ValueGenerated.Never)
            .Select(p => $"{p.DeclaringType.ShortName()}.{p.Name}={p.ValueGenerated}")
            .ToList();

        Assert.Empty(generated);
    }

    [Fact]
    public void Foreign_keys_are_enabled_on_an_open_connection()
    {
        using var context = CreateContext();
        context.Database.OpenConnection();

        using var command = context.Database.GetDbConnection().CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        var value = Convert.ToInt64(command.ExecuteScalar());

        // foreign_keys is per-connection and defaults OFF; a non-zero read proves the interceptor ran.
        Assert.NotEqual(0, value);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
