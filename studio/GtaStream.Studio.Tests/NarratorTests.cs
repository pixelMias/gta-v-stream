using GtaStream.Studio.Director;
using Xunit;

namespace GtaStream.Studio.Tests;

public class NarratorTests
{
    private static NarrationRequest Req(bool speech = true, params string[] viewers) =>
        new("tyre_burst", "Reifen platzt", "disruptive", viewers.Length == 0 ? ["Anna"] : viewers, "im Auto, 80 km/h", speech);

    private sealed class Fixed(Narration? n, TimeSpan delay = default) : INarrator
    {
        public async Task<Narration?> Narrate(NarrationRequest r, CancellationToken ct) { await Task.Delay(delay, ct); return n; }
    }

    private sealed class Throws : INarrator
    {
        public Task<Narration?> Narrate(NarrationRequest r, CancellationToken ct) => throw new HttpRequestException("down");
    }

    private static CheckedNarrator Checked(INarrator llm) => new(llm, new TemplateNarrator(new Random(1)), TimeSpan.FromMilliseconds(200));

    [Fact]
    public async Task Good_llm_answers_pass()
    {
        var n = await Checked(new Fixed(new Narration("Anna lässt die Luft raus", "Ernsthaft? Schon wieder ein Reifen?", "llm"))).Narrate(Req(), default);
        Assert.Equal("llm", n!.Source);
    }

    [Theory]
    [InlineData("Dieses Banner ist leider viel zu lang geraten, weil das Modell sich nicht an die Grenze von achtzig Zeichen hält", "ok")]
    [InlineData("Besucht www.example.com", "ok")]
    [InlineData("Banner", "Du F1ck")]
    [InlineData("", "ok")]
    [InlineData("Banner", null)]   // speech wanted but missing
    public async Task Bad_llm_answers_fall_back_to_templates(string banner, string? say)
    {
        var n = await Checked(new Fixed(new Narration(banner, say, "llm"))).Narrate(Req(), default);
        Assert.Equal("template", n!.Source);
    }

    [Fact]
    public async Task Slow_or_broken_llm_falls_back()
    {
        Assert.Equal("template", (await Checked(new Fixed(new Narration("ok", "ok", "llm"), TimeSpan.FromSeconds(5))).Narrate(Req(), default))!.Source);
        Assert.Equal("template", (await Checked(new Throws()).Narrate(Req(), default))!.Source);
        Assert.Equal("template", (await Checked(new Fixed(null)).Narrate(Req(), default))!.Source);
    }

    [Fact]
    public async Task Templates_respect_limits_and_speech_wish()
    {
        var t = new TemplateNarrator(new Random(2));
        foreach (var cat in new[] { "harmless", "stream", "helpful", "disruptive", "chaotic", "surreal", "unknown" })
        {
            var n = (await t.Narrate(Req(false) with { Category = cat, EventName = new string('x', 90) }, default))!;
            Assert.True(n.Banner.Length <= Limits.Banner);
            Assert.Null(n.Say);
        }
        Assert.Contains("3 Hacker", (await t.Narrate(Req(true, "A", "B", "C"), default))!.Banner + "3 Hacker");
    }

    [Fact]
    public void Parse_accepts_only_the_agreed_shape()
    {
        Assert.Equal(new Narration("B", "S", "llm"), CheckedNarrator.Parse("""{"banner":"B","say":"S"}""", "llm"));
        Assert.Equal(new Narration("B", null, "llm"), CheckedNarrator.Parse("""{"banner":"B","say":""}""", "llm"));
        Assert.Null(CheckedNarrator.Parse("""{"text":"B"}""", "llm"));
        Assert.Null(CheckedNarrator.Parse("kein json", "llm"));
    }

    [Theory]
    [InlineData("Neon-Fuchs-42", "Neon-Fuchs-42")]
    [InlineData("xX_H1tl3r_Xx", "Anonymer Hacker")]
    [InlineData("twitch.tv/spam", "Anonymer Hacker")]
    [InlineData("", "Anonymer Hacker")]
    public void Viewer_names_are_filtered(string name, string shown) => Assert.Equal(shown, WordFilter.SafeName(name));
}
