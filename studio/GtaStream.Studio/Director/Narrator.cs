using System.Text.Json;
using System.Text.RegularExpressions;

namespace GtaStream.Studio.Director;

/// <summary>What the director wants told about a released intervention.</summary>
public sealed record NarrationRequest(
    string EventId, string EventName, string Category, IReadOnlyList<string> Viewers, string StateLine, bool WantSpeech);

/// <summary>Banner for the overlay (at most 80 characters) and, if wanted, one line for the voice (at most 200).</summary>
public sealed record Narration(string Banner, string? Say, string Source);

public interface INarrator
{
    Task<Narration?> Narrate(NarrationRequest request, CancellationToken ct);
}

public static class Limits
{
    public const int Banner = 80;
    public const int Say = 200;
}

/// <summary>
/// Words that must never reach the overlay or the voice, also as part of viewer names. Deliberately short; the
/// moderation word list of the channel can replace it later.
/// </summary>
public static partial class WordFilter
{
    private static readonly string[] Blocked =
    [
        "nazi", "hitler", "heil", "fotze", "hure", "schlampe", "nigger", "neger", "kanake", "schwuchtel", "spast", "missgeburt",
        "fick", "fuck", "cunt", "whore", "slut", "faggot", "retard", "kys", "suizid", "selbstmord",
    ];

    [GeneratedRegex(@"https?://|www\.|\.(com|de|net|tv|gg)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Link();

    public static bool IsClean(string text)
    {
        var lower = text.ToLowerInvariant().Replace('0', 'o').Replace('1', 'i').Replace('3', 'e').Replace('4', 'a').Replace('$', 's');
        var squashed = new string(lower.Where(char.IsLetter).ToArray());
        return !Blocked.Any(w => squashed.Contains(w)) && !Link().IsMatch(text);
    }

    /// <summary>Concept: names that fail the filter appear as "Anonymer Hacker".</summary>
    public static string SafeName(string name) =>
        string.IsNullOrWhiteSpace(name) || name.Length > 40 || !IsClean(name) ? "Anonymer Hacker" : name.Trim();
}

/// <summary>Fixed texts as fallback and for everything the LLM does not need to do.</summary>
public sealed class TemplateNarrator(Random? random = null) : INarrator
{
    private readonly Random _random = random ?? Random.Shared;

    private static readonly Dictionary<string, string[]> Banners = new()
    {
        ["harmless"] = ["Lagewechsel von {who}: {event}", "{event}, mit freundlichen Grüßen von {who}", "{who} spielt am Regler: {event}"],
        ["stream"] = ["{who} übernimmt kurz das Bild", "Bildstörung, verursacht von {who}"],
        ["helpful"] = ["{who} hilft: {event}", "Rettung von {who}: {event}", "{who} hat ein Herz: {event}"],
        ["disruptive"] = ["{who} sabotiert: {event}", "Hack erfolgreich: {event} von {who}", "{who} macht Ärger: {event}"],
        ["chaotic"] = ["CHAOS: {event} von {who}", "{who} drückt den roten Knopf: {event}", "Alarm! {event}, ausgelöst von {who}"],
        ["surreal"] = ["Die Realität bricht: {event} von {who}", "{who} hackt die Physik: {event}", "Fieber-Traum: {event} von {who}"],
    };

    private static readonly Dictionary<string, string[]> Lines = new()
    {
        ["harmless"] = ["Hm. {event}. Na gut, ich hab schon Schlimmeres gesehen.", "Wer spielt da an den Einstellungen herum?"],
        ["stream"] = ["Irgendwas stimmt mit meinen Augen nicht.", "Hallo? Ist das Bild kaputt oder ich?"],
        ["helpful"] = ["Oh, danke! Endlich mal jemand auf meiner Seite.", "Na also, geht doch. Danke, {who}."],
        ["disruptive"] = ["Ernsthaft? {event}? Ihr seid unmöglich.", "Ich merke mir das, {who}.", "Super. Genau das hab ich jetzt gebraucht."],
        ["chaotic"] = ["Was zum ... nein, nein, NEIN!", "Okay. Okay. Ich bleibe ruhig. Ich bleibe ganz ruhig.", "Das war Absicht, oder?"],
        ["surreal"] = ["Ich glaube, die Welt hat einen Bug.", "Das ist nicht normal. Das ist nicht normal!", "Egal. Ich mache einfach weiter, als wäre nichts."],
    };

    public Task<Narration?> Narrate(NarrationRequest r, CancellationToken ct)
    {
        var who = Who(r.Viewers);
        string Fill(string t) => t.Replace("{who}", who).Replace("{event}", r.EventName);
        var banner = Fill(Pick(Banners, r.Category));
        banner = char.ToUpperInvariant(banner[0]) + banner[1..];
        if (banner.Length > Limits.Banner) banner = banner[..(Limits.Banner - 1)] + "…";
        var say = r.WantSpeech ? Fill(Pick(Lines, r.Category)) : null;
        return Task.FromResult<Narration?>(new Narration(banner, say, "template"));
    }

    public static string Who(IReadOnlyList<string> viewers) => viewers.Count switch
    {
        0 => "Unbekannt",
        1 => viewers[0],
        // Singular on purpose, so every template reads right: "Team Anna & Ben sabotiert".
        2 => $"Team {viewers[0]} & {viewers[1]}",
        _ => $"ein Team aus {viewers.Count} Hackern",
    };

    private string Pick(Dictionary<string, string[]> table, string category)
    {
        var list = table.GetValueOrDefault(category) ?? table["harmless"];
        return list[_random.Next(list.Length)];
    }
}

/// <summary>
/// Concept, section 4: code decides whether and when, the LLM only how it is told. Its answer is checked (schema,
/// length, word filter); anything wrong, slow or missing falls back to the templates.
/// </summary>
public sealed class CheckedNarrator(INarrator llm, INarrator fallback, TimeSpan timeout) : INarrator
{
    public async Task<Narration?> Narrate(NarrationRequest r, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var n = await llm.Narrate(r, cts.Token);
            if (n is not null && Valid(n, r)) return n;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.WriteLine($"[regie] LLM nicht nutzbar ({e.GetType().Name}), Vorlage statt LLM");
        }
        return await fallback.Narrate(r, ct);
    }

    public static bool Valid(Narration n, NarrationRequest r) =>
        !string.IsNullOrWhiteSpace(n.Banner) && n.Banner.Length <= Limits.Banner && WordFilter.IsClean(n.Banner) &&
        (n.Say is null || (n.Say.Length <= Limits.Say && WordFilter.IsClean(n.Say))) &&
        (r.WantSpeech ? !string.IsNullOrWhiteSpace(n.Say) : true);

    /// <summary>Parses the LLM's JSON answer; null if it is not the agreed shape.</summary>
    public static Narration? Parse(string json, string source)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("banner", out var b) || b.ValueKind != JsonValueKind.String) return null;
            string? say = root.TryGetProperty("say", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return new Narration(b.GetString()!.Trim(), string.IsNullOrWhiteSpace(say) ? null : say.Trim(), source);
        }
        catch (JsonException) { return null; }
    }
}
