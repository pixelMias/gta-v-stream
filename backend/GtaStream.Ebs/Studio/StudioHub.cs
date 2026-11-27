using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GtaStream.Ebs.Economy;

namespace GtaStream.Ebs.Studio;

/// <summary>
/// The studio host on the streaming PC connects outbound to wss://…/api/v1/studio (no open ports at home). Exactly one
/// host per channel; a new connection replaces the old one. Protocol: docs/studio-protokoll.md. Without a host, a
/// built-in simulator can stand in so the extension can be tried on the test server before the game bridge exists.
/// </summary>
public sealed class StudioHub
{
    private readonly Show _show;
    private readonly ILogger<StudioHub> _log;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Connection> _hosts = new();
    private readonly string? _token;
    public string ChannelId { get; }
    public bool SimulatorEnabled { get; }

    public sealed class Connection(string id, WebSocket socket)
    {
        public string Id { get; } = id;
        public WebSocket Socket { get; } = socket;
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public IReadOnlySet<string> Capabilities { get; set; } = new HashSet<string>();
        public string Game { get; set; } = "?";
        public JsonNode? State { get; set; }
        public DateTimeOffset ConnectedAt { get; init; }
    }

    public StudioHub(Show show, IConfiguration config, TimeProvider time, ILogger<StudioHub> log)
    {
        _show = show;
        _log = log;
        _time = time;
        var token = config["GTASTREAM_STUDIO_TOKEN"];
        _token = string.IsNullOrEmpty(token) ? config["GTASTREAM_ADMIN_TOKEN"] : token;
        ChannelId = config["GTASTREAM_CHANNEL_ID"] is { Length: > 0 } c ? c : "dev";
        SimulatorEnabled = !string.Equals(config["GTASTREAM_STUDIO_SIM"], "off", StringComparison.OrdinalIgnoreCase);
        show.Capabilities = CapabilitiesFor;
    }

    public static readonly IReadOnlySet<string> AllEvents = Catalog.All.Select(e => e.Id).ToHashSet();

    private IReadOnlySet<string>? CapabilitiesFor(string channel) =>
        _hosts.TryGetValue(channel, out var host) ? host.Capabilities : SimulatorEnabled ? AllEvents : null;

    public bool HasHost(string channel) => _hosts.ContainsKey(channel);

    public string Mode(string channel) => HasHost(channel) ? "live" : SimulatorEnabled ? "simulation" : "offline";

    public object Describe(string channel) => _hosts.TryGetValue(channel, out var h)
        ? new { mode = "live", game = h.Game, connectedAt = h.ConnectedAt, state = h.State }
        : new { mode = Mode(channel), game = (string?)null, connectedAt = (DateTimeOffset?)null, state = (JsonNode?)null };

    public bool Authorize(string? header) =>
        !string.IsNullOrEmpty(_token) && header is not null && header.StartsWith("Bearer ", StringComparison.Ordinal) &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()), Encoding.UTF8.GetBytes(_token));

    public async Task Run(WebSocket socket, CancellationToken stop)
    {
        var conn = new Connection(Guid.NewGuid().ToString("n")[..12], socket) { ConnectedAt = _time.GetUtcNow() };
        if (_hosts.TryRemove(ChannelId, out var previous))
        {
            _log.LogWarning("Neuer Studio-Host für {Channel}, alte Verbindung {Old} wird getrennt", ChannelId, previous.Id);
            _show.StudioGone(previous.Id);
            _ = previous.Socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "replaced", CancellationToken.None);
        }
        _hosts[ChannelId] = conn;
        _log.LogInformation("Studio-Host {Id} verbunden für Kanal {Channel}", conn.Id, ChannelId);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var pump = Pump(conn, cts.Token);
        try
        {
            await Send(conn, new JsonObject { ["type"] = "welcome", ["channelId"] = ChannelId, ["protocol"] = 1 }, cts.Token);
            await Receive(conn, cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException)
        {
            _log.LogInformation("Studio-Host {Id}: {Error}", conn.Id, e.Message);
        }
        finally
        {
            cts.Cancel();
            try { await pump; } catch (OperationCanceledException) { }
            if (_hosts.TryGetValue(ChannelId, out var current) && current == conn) _hosts.TryRemove(ChannelId, out _);
            _show.StudioGone(conn.Id);
            _log.LogInformation("Studio-Host {Id} getrennt", conn.Id);
        }
    }

    /// <summary>Hands queued purchases to the host. The director on the host decides when they run.</summary>
    private async Task Pump(Connection conn, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            foreach (var p in _show.TakeForStudio(ChannelId, conn.Id))
                await Send(conn, new JsonObject
                {
                    ["type"] = "intervention",
                    ["id"] = p.Id.ToString(),
                    ["eventId"] = p.Event.Id,
                    ["eventName"] = p.Event.Name,
                    ["category"] = Catalog.CategoryKey(p.Event.Category),
                    ["durationSec"] = p.Event.DurationSec,
                    ["viewer"] = p.ViewerName,
                    ["price"] = p.Price,
                    ["createdAt"] = p.CreatedAt,
                    ["deadline"] = p.CreatedAt + Show.QueueTimeout,
                }, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(250), _time, ct);
        }
    }

    private async Task Receive(Connection conn, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (conn.Socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await conn.Socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
                if (ms.Length > 256 * 1024) throw new WebSocketException("Nachricht zu groß");
            } while (!r.EndOfMessage);

            JsonObject msg;
            try { msg = JsonNode.Parse(ms.ToArray())!.AsObject(); }
            catch (Exception) { await Send(conn, Error("bad_json"), ct); continue; }
            await Handle(conn, msg, ct);
        }
    }

    private static JsonObject Error(string code) => new() { ["type"] = "error", ["code"] = code };

    private async Task Handle(Connection conn, JsonObject msg, CancellationToken ct)
    {
        switch ((string?)msg["type"])
        {
            case "hello":
                conn.Game = (string?)msg["game"] ?? "?";
                conn.Capabilities = (msg["capabilities"] as JsonArray)?.Select(n => (string?)n).OfType<string>().ToHashSet() ?? [];
                _log.LogInformation("Studio-Host {Id}: Spiel {Game}, {Count} Fähigkeiten", conn.Id, conn.Game, conn.Capabilities.Count);
                break;
            case "state":
                conn.State = msg["state"]?.DeepClone();
                break;
            case "ack" when Guid.TryParse((string?)msg["id"], out var id):
                _show.StudioAck(id, msg["accepted"]?.GetValue<bool>() == true, (string?)msg["reason"]);
                break;
            case "result" when Guid.TryParse((string?)msg["id"], out var id):
                _show.StudioResult(id, msg["ok"]?.GetValue<bool>() == true, (string?)msg["reason"]);
                break;
            case "ping":
                await Send(conn, new JsonObject { ["type"] = "pong" }, ct);
                break;
            default:
                await Send(conn, Error("unknown_type"), ct);
                break;
        }
    }

    public async Task<bool> SendPanic(string channel)
    {
        if (!_hosts.TryGetValue(channel, out var conn)) return false;
        await Send(conn, new JsonObject { ["type"] = "panic" }, CancellationToken.None);
        return true;
    }

    private static async Task Send(Connection conn, JsonObject msg, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
        await conn.SendLock.WaitAsync(ct);
        try { await conn.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { conn.SendLock.Release(); }
    }
}
