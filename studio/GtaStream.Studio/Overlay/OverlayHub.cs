using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using GtaStream.Studio.Director;
using GtaStream.Studio.Voice;

namespace GtaStream.Studio.Overlay;

/// <summary>
/// Overlay for OBS (browser source on http://127.0.0.1:7780/): banners, subtitles, mode badge, queue and the voice.
/// The voice plays in the browser source, so OBS mixes it like any other source. Implements the director's output.
/// </summary>
public sealed class OverlayHub(ITts tts) : IShowOutput
{
    private readonly ConcurrentDictionary<Guid, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, Speech> _audio = new();
    private readonly ConcurrentQueue<string> _audioOrder = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _send = new(1, 1);   // a WebSocket allows one send at a time

    public int Clients => _clients.Count;

    public static string Page()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("overlay.html")!;
        return new StreamReader(s).ReadToEnd();
    }

    public async Task Banner(string text, string category)
    {
        Console.WriteLine($"[banner/{category}] {text}");
        await Broadcast(new { type = "banner", text, category });
    }

    public async Task Say(string text)
    {
        Console.WriteLine($"[stimme] {text}");
        string? url = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            if (await tts.Speak(text, cts.Token) is { } speech)
            {
                var id = Guid.NewGuid().ToString("n");
                _audio[id] = speech;
                _audioOrder.Enqueue(id);
                while (_audioOrder.Count > 20 && _audioOrder.TryDequeue(out var old)) _audio.TryRemove(old, out _);
                url = $"/audio/{id}";
            }
        }
        catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"[stimme] keine Audioausgabe: {e.Message}");
        }
        await Broadcast(new { type = "say", text, audio = url });
    }

    public Speech? Audio(string id) => _audio.GetValueOrDefault(id);

    public Task Status(object status) => Broadcast(new { type = "status", status });

    public async Task Run(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _clients[id] = socket;
        try
        {
            var buf = new byte[1024];
            while (socket.State == WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        finally { _clients.TryRemove(id, out _); }
    }

    private async Task Broadcast(object msg)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, Json));
        await _send.WaitAsync();
        try
        {
            foreach (var (id, s) in _clients)
            {
                try { await s.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
                catch (Exception e) when (e is WebSocketException or ObjectDisposedException or IOException) { _clients.TryRemove(id, out _); }
            }
        }
        finally { _send.Release(); }
    }
}
