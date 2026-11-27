using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace GtaStream.Studio.Director;

/// <summary>
/// Writes banner and voice line with Claude (concept, section 4b: Claude Haiku 5.5 for the director). Gets only the
/// state as one line, the released intervention and already filtered viewer names, never chat text. The answer is
/// JSON with a fixed schema; <see cref="CheckedNarrator"/> checks it and falls back to templates.
/// </summary>
public sealed class ClaudeNarrator(AnthropicClient client, string model) : INarrator
{
    // Stable prefix first (persona and rules), so prompt caching can reuse it; the varying part comes last.
    private const string System = """
        Du bist die Regie einer deutschsprachigen Dauersendung, in der eine KI-Figur rund um die Uhr GTA V spielt.
        Zuschauer lösen mit erspielten Coins Eingriffe im Spiel aus. Du schreibst zu jedem Eingriff:
        - "banner": Overlay-Text, höchstens 70 Zeichen, nennt den oder die Auslöser, knapp und witzig.
        - "say": nur wenn verlangt, ein bis zwei Sätze, höchstens 180 Zeichen, gesprochen von der KI-Figur selbst.
          Die Figur nimmt alles ernst, auch das Absurde. Sie reagiert genervt, hilflos oder überraschend clever.
        Regeln: Deutsch, keine Beleidigungen, keine Links, keine Markennamen von Rockstar, keine Emojis.
        Erfinde keine Ereignisse, die nicht im Eingriff stehen. Antworte nur mit dem JSON-Objekt.
        """;

    private static readonly Dictionary<string, JsonElement> Schema = new()
    {
        ["type"] = JsonSerializer.SerializeToElement("object"),
        ["properties"] = JsonSerializer.SerializeToElement(new
        {
            banner = new { type = "string" },
            say = new { type = "string" },
        }),
        ["required"] = JsonSerializer.SerializeToElement(new[] { "banner", "say" }),
        ["additionalProperties"] = JsonSerializer.SerializeToElement(false),
    };

    public async Task<Narration?> Narrate(NarrationRequest r, CancellationToken ct)
    {
        var who = TemplateNarrator.Who(r.Viewers);
        var prompt = $"""
            Eingriff: {r.EventName} (Kategorie {r.Category}), ausgelöst von {who}.
            Lage im Spiel: {r.StateLine}
            Sprachzeile verlangt: {(r.WantSpeech ? "ja" : "nein, say leer lassen")}
            """;
        var response = await client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 1024,
            System = System,
            OutputConfig = new OutputConfig { Effort = Effort.Low, Format = new JsonOutputFormat { Schema = Schema } },
            Messages = [new UserMessageParam { Content = prompt }],
        }, ct);
        if (response.StopReason != "end_turn") return null;   // refusal, max_tokens …
        var text = string.Concat(response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        var n = CheckedNarrator.Parse(text, "llm");
        return n is null ? null : n with { Say = r.WantSpeech ? n.Say : null };
    }
}
