using Npgsql;

namespace GtaStream.Ebs.Persistence;

/// <summary>
/// One coin movement. The ledger is append only: a purchase is a negative row, its refund a positive row with the same
/// Ref, its execution a zero row. Balances are sums over the rows.
/// </summary>
public sealed record LedgerRow(long Id, DateTimeOffset At, string ChannelId, string Account, int Delta, string Kind, string? Ref, string? Meta);

public static class LedgerKind
{
    public const string Game = "game";
    public const string Purchase = "purchase";
    public const string Refund = "refund";
    public const string Executed = "executed";
    public const string Grant = "grant";
}

public interface ILedgerStore
{
    /// <summary>Writes the row and returns it with its id. Throws if the store is unreachable, then nothing was booked.</summary>
    LedgerRow Append(LedgerRow row);
    IReadOnlyList<LedgerRow> LoadAll();
    void SaveName(string channelId, string account, string name);
    IReadOnlyDictionary<(string Channel, string Account), string> LoadNames();
}

public sealed class InMemoryLedgerStore : ILedgerStore
{
    private readonly List<LedgerRow> _rows = [];
    private readonly Dictionary<(string, string), string> _names = [];
    private long _nextId;

    public LedgerRow Append(LedgerRow row)
    {
        lock (_rows)
        {
            var stored = row with { Id = ++_nextId };
            _rows.Add(stored);
            return stored;
        }
    }

    public IReadOnlyList<LedgerRow> LoadAll() { lock (_rows) return _rows.ToList(); }
    public void SaveName(string channelId, string account, string name) { lock (_rows) _names[(channelId, account)] = name; }
    public IReadOnlyDictionary<(string Channel, string Account), string> LoadNames() { lock (_rows) return new Dictionary<(string, string), string>(_names); }
}

/// <summary>Write-through to PostgreSQL. The schema is created on start; the EBS role owns the database.</summary>
public sealed class PostgresLedgerStore : ILedgerStore
{
    private readonly NpgsqlDataSource _db;

    public PostgresLedgerStore(string connectionString)
    {
        _db = NpgsqlDataSource.Create(connectionString);
        using var cmd = _db.CreateCommand("""
            create table if not exists ledger (
                id bigserial primary key,
                at timestamptz not null,
                channel_id text not null,
                account text not null,
                delta integer not null,
                kind text not null,
                ref text,
                meta text
            );
            create index if not exists ledger_account on ledger (channel_id, account);
            create table if not exists viewer_names (
                channel_id text not null,
                account text not null,
                name text not null,
                updated_at timestamptz not null default now(),
                primary key (channel_id, account)
            );
            """);
        cmd.ExecuteNonQuery();
    }

    public LedgerRow Append(LedgerRow row)
    {
        using var cmd = _db.CreateCommand(
            "insert into ledger (at, channel_id, account, delta, kind, ref, meta) values ($1, $2, $3, $4, $5, $6, $7) returning id");
        cmd.Parameters.AddWithValue(row.At);
        cmd.Parameters.AddWithValue(row.ChannelId);
        cmd.Parameters.AddWithValue(row.Account);
        cmd.Parameters.AddWithValue(row.Delta);
        cmd.Parameters.AddWithValue(row.Kind);
        cmd.Parameters.AddWithValue((object?)row.Ref ?? DBNull.Value);
        cmd.Parameters.AddWithValue((object?)row.Meta ?? DBNull.Value);
        return row with { Id = (long)cmd.ExecuteScalar()! };
    }

    public IReadOnlyList<LedgerRow> LoadAll()
    {
        using var cmd = _db.CreateCommand("select id, at, channel_id, account, delta, kind, ref, meta from ledger order by id");
        using var r = cmd.ExecuteReader();
        var rows = new List<LedgerRow>();
        while (r.Read())
            rows.Add(new LedgerRow(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetString(2), r.GetString(3), r.GetInt32(4),
                r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return rows;
    }

    public void SaveName(string channelId, string account, string name)
    {
        using var cmd = _db.CreateCommand("""
            insert into viewer_names (channel_id, account, name) values ($1, $2, $3)
            on conflict (channel_id, account) do update set name = excluded.name, updated_at = now()
            """);
        cmd.Parameters.AddWithValue(channelId);
        cmd.Parameters.AddWithValue(account);
        cmd.Parameters.AddWithValue(name);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyDictionary<(string Channel, string Account), string> LoadNames()
    {
        using var cmd = _db.CreateCommand("select channel_id, account, name from viewer_names");
        using var r = cmd.ExecuteReader();
        var names = new Dictionary<(string, string), string>();
        while (r.Read()) names[(r.GetString(0), r.GetString(1))] = r.GetString(2);
        return names;
    }
}
