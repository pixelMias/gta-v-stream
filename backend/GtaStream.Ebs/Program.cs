using System.Security.Cryptography;
using System.Text;
using GtaStream.Ebs;
using GtaStream.Ebs.Api;
using GtaStream.Ebs.Auth;
using GtaStream.Ebs.Economy;
using GtaStream.Ebs.Persistence;
using GtaStream.Ebs.Studio;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSystemd();

var logBuffer = new LogBuffer();
builder.Services.AddSingleton(logBuffer);
builder.Logging.AddProvider(new LogBufferProvider(logBuffer));

builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILedgerStore>(sp =>
{
    var db = sp.GetRequiredService<IConfiguration>()["GTASTREAM_DB"];
    return string.IsNullOrEmpty(db) ? new InMemoryLedgerStore() : new PostgresLedgerStore(db);
});
builder.Services.AddSingleton(sp => ViewerTokens.FromConfig(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<Show>();
builder.Services.AddSingleton<StudioHub>();
builder.Services.AddSingleton<Simulator>();
builder.Services.AddHostedService<ShowClock>();
// The extension runs on https://<client-id>.ext-twitch.tv; the test server serves it from its own origin.
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .SetIsOriginAllowed(origin => Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
        (u.Host.EndsWith(".ext-twitch.tv", StringComparison.OrdinalIgnoreCase) || u.Host is "localhost" or "127.0.0.1"))
    .AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();
app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(10) });

// commit.txt is written next to the DLL by the release build; locally there is none.
var commitFile = Path.Combine(AppContext.BaseDirectory, "commit.txt");
var commit = File.Exists(commitFile) ? File.ReadAllText(commitFile).Trim() : "dev";
var startedAt = DateTimeOffset.UtcNow;
var dbConnection = app.Configuration["GTASTREAM_DB"];
var adminToken = app.Configuration["GTASTREAM_ADMIN_TOKEN"];

app.MapGet("/api/health", async () =>
{
    // The deploy script waits for "commit":"<sha>" here; the database state is informative only.
    var db = "off";
    if (!string.IsNullOrEmpty(dbConnection))
    {
        try
        {
            await using var conn = new NpgsqlConnection(dbConnection);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("select 1", conn);
            await cmd.ExecuteScalarAsync(cts.Token);
            db = "ok";
        }
        catch (Exception)
        {
            db = "down";
        }
    }
    return Results.Ok(new { status = "ok", commit, startedAt, db });
});

// Logs for whoever holds the admin token (header X-Admin-Token). Without a configured token the endpoint does not exist.
app.MapGet("/api/logs", (HttpRequest req, LogBuffer logs, int? limit, string? level, long? after) =>
{
    if (string.IsNullOrEmpty(adminToken)) return Results.NotFound();
    var given = req.Headers["X-Admin-Token"].ToString();
    if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(adminToken)))
        return Results.Unauthorized();
    var minLevel = Enum.TryParse<LogLevel>(level, true, out var l) ? l : LogLevel.Trace;
    return Results.Ok(logs.Read(Math.Clamp(limit ?? 200, 1, 2000), minLevel, after ?? 0));
});

app.MapViewerApi();
app.MapStudioApi();

app.Logger.LogInformation("GTA V Stream EBS gestartet, Commit {Commit}", commit);
app.Run();

public partial class Program { }
