using GtaStream.Studio;
using GtaStream.Studio.Voice;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace GtaStream.Studio.Tests;

public class VoiceAndEnvTests
{
    private sealed class StubTts(string engine, bool fail = false) : ITts
    {
        public int Calls;
        public Task<Speech?> Speak(string text, CancellationToken ct)
        {
            Calls++;
            if (fail) throw new HttpRequestException("down");
            return Task.FromResult<Speech?>(new Speech([1], "audio/mpeg", engine));
        }
    }

    private static FakeTimeProvider At(int hour)
    {
        var t = new FakeTimeProvider(new DateTimeOffset(2026, 11, 27, hour, 30, 0, TimeSpan.Zero));
        t.SetLocalTimeZone(TimeZoneInfo.Utc);
        return t;
    }

    [Fact]
    public async Task Premium_voice_only_in_premium_hours()
    {
        var premium = new StubTts("eleven");
        var local = new StubTts("piper");
        Assert.Equal("eleven", (await new HybridTts(premium, local, 18, 22, At(19)).Speak("x", default))!.Engine);
        Assert.Equal("piper", (await new HybridTts(premium, local, 18, 22, At(12)).Speak("x", default))!.Engine);
        Assert.Equal("piper", (await new HybridTts(premium, local, 18, 22, At(22)).Speak("x", default))!.Engine);
    }

    [Fact]
    public async Task Hours_can_wrap_midnight_and_failure_falls_back()
    {
        Assert.True(new HybridTts(null, null, 22, 2, At(1)).PremiumNow());
        Assert.False(new HybridTts(null, null, 22, 2, At(3)).PremiumNow());
        var s = await new HybridTts(new StubTts("eleven", fail: true), new StubTts("piper"), 18, 22, At(20)).Speak("x", default);
        Assert.Equal("piper", s!.Engine);
        Assert.Null(await new HybridTts(null, null, 18, 22, At(20)).Speak("x", default));
    }

    [Theory]
    [InlineData("18-22", 18, 22)]
    [InlineData(" 20 - 23 ", 20, 23)]
    [InlineData("abends", 18, 22)]
    [InlineData(null, 18, 22)]
    public void Parse_hours(string? s, int from, int to) => Assert.Equal((from, to), HybridTts.ParseHours(s));

    [Fact]
    public void Env_file_parsing()
    {
        var parsed = EnvFile.Parse([
            "# Kommentar", "", "ANTHROPIC_API_KEY=", "export A=1", "B = \"zwei drei\"", "C='4'", "kaputt", "=x", "D=e=f",
        ]).ToList();
        Assert.Equal([("A", "1"), ("B", "zwei drei"), ("C", "4"), ("D", "e=f")], parsed);
    }

    [Fact]
    public void Env_file_does_not_override_existing_variables()
    {
        var dir = Directory.CreateTempSubdirectory();
        var sub = Directory.CreateDirectory(Path.Combine(dir.FullName, "a", "b"));
        File.WriteAllLines(Path.Combine(dir.FullName, ".env"), ["GTASTREAM_TEST_X=aus-datei", "GTASTREAM_TEST_Y=aus-datei"]);
        Environment.SetEnvironmentVariable("GTASTREAM_TEST_X", "gesetzt");
        try
        {
            Assert.Equal(Path.Combine(dir.FullName, ".env"), EnvFile.Load(sub.FullName));
            Assert.Equal("gesetzt", Environment.GetEnvironmentVariable("GTASTREAM_TEST_X"));
            Assert.Equal("aus-datei", Environment.GetEnvironmentVariable("GTASTREAM_TEST_Y"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GTASTREAM_TEST_X", null);
            Environment.SetEnvironmentVariable("GTASTREAM_TEST_Y", null);
            dir.Delete(true);
        }
    }
}
