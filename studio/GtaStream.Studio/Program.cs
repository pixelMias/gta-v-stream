using Anthropic;
using GtaStream.Studio;
using GtaStream.Studio.Director;

// Studio host: connects to the EBS, the showrunner decides when interventions run, the game side is still a fake.
//   GTASTREAM_EBS=wss://2-29-164-176.sslip.io/api/v1/studio GTASTREAM_STUDIO_TOKEN=… dotnet run --project studio/GtaStream.Studio
// Without GTASTREAM_EBS it connects to a local EBS (ws://127.0.0.1:5090/api/v1/studio).
// With ANTHROPIC_API_KEY the director writes banners and lines with Claude (GTASTREAM_LLM_MODEL, default
// claude-haiku-5-5), otherwise it uses templates. A bad or slow LLM answer always falls back to the templates.
var url = new Uri(Environment.GetEnvironmentVariable("GTASTREAM_EBS") ?? "ws://127.0.0.1:5090/api/v1/studio");
var token = Environment.GetEnvironmentVariable("GTASTREAM_STUDIO_TOKEN");
if (string.IsNullOrEmpty(token))
{
    Console.Error.WriteLine("GTASTREAM_STUDIO_TOKEN fehlt (auf dem Testserver: das Admin-Token).");
    return 1;
}

INarrator narrator = new TemplateNarrator();
if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")))
{
    var model = Environment.GetEnvironmentVariable("GTASTREAM_LLM_MODEL") is { Length: > 0 } m ? m : "claude-haiku-5-5";
    narrator = new CheckedNarrator(new ClaudeNarrator(new AnthropicClient(), model), narrator, TimeSpan.FromSeconds(4));
    Console.WriteLine($"[regie] Texte mit {model}, Vorlagen als Ersatz");
}
else Console.WriteLine("[regie] Kein ANTHROPIC_API_KEY: Texte aus Vorlagen");

var game = new FakeAdapter();
var director = new Showrunner(game, narrator, new ConsoleShowOutput(), TimeProvider.System);

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
await new EbsConnection(url, token, game, director).RunForever(stop.Token);
return 0;
