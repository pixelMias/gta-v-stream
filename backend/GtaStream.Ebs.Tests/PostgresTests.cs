using GtaStream.Ebs.Persistence;
using Npgsql;
using Xunit;

namespace GtaStream.Ebs.Tests;

/// <summary>Runs only with GTASTREAM_TEST_DB set (CI starts a PostgreSQL service); each run uses its own schema.</summary>
public class PostgresTests
{
    private static string? Db => Environment.GetEnvironmentVariable("GTASTREAM_TEST_DB");

    [Fact]
    public void Ledger_round_trips()
    {
        if (string.IsNullOrEmpty(Db)) return;
        var schema = "t" + Guid.NewGuid().ToString("n")[..10];
        using (var conn = new NpgsqlConnection(Db)) { conn.Open(); using var c = new NpgsqlCommand($"create schema {schema}", conn); c.ExecuteNonQuery(); }
        var cs = new NpgsqlConnectionStringBuilder(Db) { SearchPath = schema }.ConnectionString;
        try
        {
            var store = new PostgresLedgerStore(cs);
            var at = new DateTimeOffset(2026, 11, 27, 18, 0, 0, TimeSpan.Zero);
            var a = store.Append(new LedgerRow(0, at, "dev", "U1", 100, LedgerKind.Grant, null, "start"));
            var b = store.Append(new LedgerRow(0, at, "dev", "U1", -50, LedgerKind.Purchase, "p1", "weather_storm"));
            Assert.True(b.Id > a.Id);
            store.SaveName("dev", "U1", "Anna");
            store.SaveName("dev", "U1", "Anna B.");

            var again = new PostgresLedgerStore(cs);   // schema creation is idempotent
            var rows = again.LoadAll();
            Assert.Equal(new[] { 100, -50 }, rows.Select(r => r.Delta));
            Assert.Equal("p1", rows[1].Ref);
            Assert.Null(rows[0].Ref);
            Assert.Equal(at, rows[0].At);
            Assert.Equal("Anna B.", again.LoadNames()[("dev", "U1")]);
        }
        finally
        {
            using var conn = new NpgsqlConnection(Db); conn.Open();
            using var c = new NpgsqlCommand($"drop schema {schema} cascade", conn); c.ExecuteNonQuery();
        }
    }
}
