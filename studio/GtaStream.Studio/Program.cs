using Anthropic;
using GtaStream.Studio;
using GtaStream.Studio.Director;
using GtaStream.Studio.Overlay;
using GtaStream.Studio.Voice;

// Studio host on the streaming PC: connects to the EBS, the showrunner decides when interventions run, the overlay
// for OBS shows banners and plays the voice. The game side is still a fake. Settings: .env (see .env_example).
//   dotnet run --project studio/GtaStream.Studio
var envFile = EnvFile.Load();
Console.WriteLine(envFile is null ? "[start] keine .env gefunden, nur Umgebungsvariablen" : $"[start] Einstellungen aus {envFile}");
string? Env(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } v ? v : null;

var url = new Uri(Env("GTASTREAM_EBS") ?? "ws://127.0.0.1:5090/api/v1/studio");
var token = Env("GTASTREAM_STUDIO_TOKEN");
if (token is null)
{
    Console.Error.WriteLine("GTASTREAM_STUDIO_TOKEN fehlt (in .env eintragen, auf dem Testserver: das Admin-Token).");
    return 1;
}

// Texts: Claude with checks, templates as fallback.
INarrator narrator = new TemplateNarrator();
if (Env("ANTHROPIC_API_KEY") is not null)
{
    var model = Env("GTASTREAM_LLM_MODEL") ?? "claude-haiku-5-5";
    narrator = new CheckedNarrator(new ClaudeNarrator(new AnthropicClient(), model), narrator, TimeSpan.FromSeconds(4));
    Console.WriteLine($"[regie] Texte mit {model}, Vorlagen als Ersatz");
}
else Console.WriteLine("[regie] Kein ANTHROPIC_API_KEY: Texte aus Vorlagen");

// Voice: ElevenLabs in the premium hours, Piper otherwise, silent if neither is set up.
var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
ITts? premium = Env("ELEVENLABS_API_KEY") is { } elKey && Env("ELEVENLABS_VOICE_ID") is { } voice
    ? new ElevenLabsTts(http, elKey, voice, Env("ELEVENLABS_MODEL") ?? "eleven_flash_v2_5") : null;
ITts? local = Env("PIPER_PATH") is { } piper && Env("PIPER_VOICE") is { } piperVoice ? new PiperTts(piper, piperVoice) : null;
var (from, to) = HybridTts.ParseHours(Env("GTASTREAM_TTS_PREMIUM_HOURS"));
ITts tts = premium is null && local is null ? new NoTts() : new HybridTts(premium, local, from, to, TimeProvider.System);
Console.WriteLine($"[stimme] ElevenLabs {(premium is null ? "aus" : $"{from}–{to} Uhr")}, Piper {(local is null ? "aus" : "an")}");

var overlay = new OverlayHub(tts);
var game = new FakeAdapter();
var director = new Showrunner(game, narrator, overlay, TimeProvider.System);
var ebs = new EbsConnection(url, token, game, director);

// Overlay server, only on this machine.
var overlayUrl = Env("GTASTREAM_OVERLAY_URL") ?? "http://127.0.0.1:7780";
var builder = WebApplication.CreateSlimBuilder();
builder.WebHost.UseUrls(overlayUrl);
builder.Logging.SetMinimumLevel(LogLevel.Warning);
var app = builder.Build();
app.UseWebSockets();
var page = OverlayHub.Page();
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));
app.MapGet("/audio/{id}", (string id) => overlay.Audio(id) is { } a ? Results.File(a.Audio, a.ContentType) : Results.NotFound());
app.Map("/ws", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) return Results.BadRequest();
    using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
    await overlay.Run(ws, ctx.RequestAborted);
    return Results.Empty;
});

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await app.StartAsync(stop.Token);
Console.WriteLine($"[overlay] OBS-Browser-Quelle: {overlayUrl}/  (Test im Browser: {overlayUrl}/?debug)");

var statusLoop = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        var (tension, target, phase) = director.Pulse();
        await overlay.Status(new { mode = game.State.Mode, ebsConnected = ebs.IsConnected, tension, target, phase, queue = director.QueueLength });
        try { await Task.Delay(1000, stop.Token); } catch (OperationCanceledException) { }
    }
});

await ebs.RunForever(stop.Token);
await statusLoop;
await app.StopAsync();
return 0;
