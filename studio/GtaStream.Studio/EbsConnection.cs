using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace GtaStream.Studio;

/// <summary>
/// Outbound connection to the EBS (docs/studio-protokoll.md). Reconnects with backoff, sends hello and the game state
/// every 2 s, and answers every intervention with ack and result. The director (pacing, bundling) will sit between
/// receiving and running; for now each intervention runs after a short fixed pause.
/// </summary>
public sealed class EbsConnection(Uri url, string token, IGameAdapter game)
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
        while (!ct.IsCancellationRequested)
        {
            var s = game.State;
            await Send(new JsonObject
            {
                ["type"] = "state",
                ["state"] = new JsonObject
                {
                    ["inVehicle"] = s.InVehicle, ["wanted"] = s.WantedLevel, ["cutscene"] = s.Cutscene, ["paused"] = s.Paused, ["mode"] = s.Mode,
                },
            }, ct);
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
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
                    _ = Handle(msg, ct);
                    break;
                case "panic":
                    await game.StopAll();
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
        var id = (string)msg["id"]!;
        var effect = (string)msg["eventId"]!;
        var viewer = (string?)msg["viewer"] ?? "?";
        var reason = game.Check(effect);
        await Send(new JsonObject { ["type"] = "ack", ["id"] = id, ["accepted"] = reason is null, ["reason"] = reason }, ct);
        if (reason is not null) return;
        await Task.Delay(TimeSpan.FromSeconds(2), ct);   // stand-in for the director's timing
        var outcome = await game.Run(effect, (int?)msg["durationSec"] ?? 0, viewer, ct);
        await Send(new JsonObject { ["type"] = "result", ["id"] = id, ["ok"] = outcome.Ok, ["reason"] = outcome.Reason }, ct);
    }

    private async Task Send(JsonObject msg, CancellationToken ct)
    {
        var socket = _socket ?? throw new WebSocketException("nicht verbunden");
        await _send.WaitAsync(ct);
        try { await socket.SendAsync(Encoding.UTF8.GetBytes(msg.ToJsonString()), WebSocketMessageType.Text, true, ct); }
        finally { _send.Release(); }
    }
}
