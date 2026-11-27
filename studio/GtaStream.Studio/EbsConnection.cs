using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using GtaStream.Studio.Director;

namespace GtaStream.Studio;

/// <summary>
/// Outbound connection to the EBS (docs/studio-protokoll.md). Reconnects with backoff, sends hello and the game state
/// every 2 s, and hands every intervention to the showrunner, which answers with ack right away and decides when it
/// runs; its results go back as "result".
/// </summary>
public sealed class EbsConnection(Uri url, string token, IGameAdapter game, Showrunner director)
{
    private ClientWebSocket? _socket;
    private readonly SemaphoreSlim _send = new(1, 1);

    public async Task RunForever(CancellationToken stop)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!stop.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("Authorization", $"Bearer {token}");
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
                await socket.ConnectAsync(url, stop);
                _socket = socket;
                backoff = TimeSpan.FromSeconds(1);
                Console.WriteLine($"[ebs] verbunden mit {url}");
                await Send(new JsonObject
                {
                    ["type"] = "hello",
                    ["game"] = game.Game,
                    ["capabilities"] = new JsonArray(game.Capabilities.Select(c => (JsonNode)c).ToArray()),
                }, stop);

                using var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
                director.ReportResult = (id, ok, reason) =>
                    Send(new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = ok, ["reason"] = reason }, cts.Token);
                var stateLoop = StateLoop(cts.Token);
                await ReceiveLoop(socket, cts.Token);
                cts.Cancel();
                try { await stateLoop; } catch (OperationCanceledException) { }
                if (socket.CloseStatusDescription == "replaced")
                {
                    // Two hosts would kick each other out forever; the newer one wins, this one waits.
                    Console.WriteLine("[ebs] Ein anderer Studio-Host hat übernommen. Nächster Versuch in 60 s.");
                    backoff = TimeSpan.FromSeconds(60);
                }
            }
            catch (Exception e) when (e is WebSocketException or IOException or HttpRequestException)
            {
                Console.WriteLine($"[ebs] {e.Message}");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            _socket = null;
            Console.WriteLine($"[ebs] getrennt, neuer Versuch in {backoff.TotalSeconds:0} s");
            try { await Task.Delay(backoff, stop); } catch (OperationCanceledException) { return; }
            backoff = TimeSpan.FromSeconds(Math.Min(60, backoff.TotalSeconds * 2));
        }
    }

    private async Task StateLoop(CancellationToken ct)
    {
        var n = 0;
        while (!ct.IsCancellationRequested)
        {
            // The director ticks twice a second; the state goes to the EBS every 2 s.
            await director.Tick(ct);
            if (n++ % 4 != 0) { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); continue; }
            var s = game.State;
            await Send(new JsonObject
            {
                ["type"] = "state",
                ["state"] = new JsonObject
                {
                    ["inVehicle"] = s.InVehicle, ["wanted"] = s.WantedLevel, ["cutscene"] = s.Cutscene, ["paused"] = s.Paused, ["mode"] = s.Mode,
                },
            }, ct);
            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    private async Task ReceiveLoop(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await socket.ReceiveAsync(buffer, ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, r.Count);
            } while (!r.EndOfMessage);
            var msg = JsonNode.Parse(ms.ToArray())!.AsObject();
            switch ((string?)msg["type"])
            {
                case "intervention":
                    await Handle(msg, ct);
                    break;
                case "panic":
                    await director.Panic();
                    break;
                case "welcome":
                    Console.WriteLine($"[ebs] Kanal {(string?)msg["channelId"]}");
                    break;
                case "error":
                    Console.WriteLine($"[ebs] Fehler: {(string?)msg["code"]}");
                    break;
            }
        }
    }

    private async Task Handle(JsonObject msg, CancellationToken ct)
    {
        var i = new Intervention(
            (string)msg["id"]!, (string)msg["eventId"]!, (string?)msg["eventName"] ?? (string)msg["eventId"]!,
            (string?)msg["category"] ?? "harmless", (int?)msg["durationSec"] ?? 0, (string?)msg["viewer"] ?? "?",
            (int?)msg["price"] ?? 0, msg["createdAt"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow,
            msg["deadline"]?.GetValue<DateTimeOffset>() ?? DateTimeOffset.UtcNow.AddSeconds(60));
        var decision = await director.Offer(i);
        if (decision is null) return;   // already known
        if (!decision.Accepted) Console.WriteLine($"[regie] {i.EventId} von {i.Viewer} abgelehnt: {decision.Reason}");
        await Send(new JsonObject { ["type"] = "ack", ["id"] = i.Id, ["accepted"] = decision.Accepted, ["reason"] = decision.Reason }, ct);
    }

    private async Task Send(JsonObject msg, CancellationToken ct)
    {
        var socket = _socket ?? throw new WebSocketException("nicht verbunden");
        await _send.WaitAsync(ct);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }
}
