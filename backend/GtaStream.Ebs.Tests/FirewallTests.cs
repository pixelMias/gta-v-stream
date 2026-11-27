using GtaStream.Ebs.Games;
using Xunit;

namespace GtaStream.Ebs.Tests;

public class FirewallTests
{
    // Same vectors as extension/src/games/firewall.test.ts: client and server must agree tap for tap.
    public static readonly int[] PerfectTaps =
        [1310, 2310, 2550, 3050, 3910, 4150, 4450, 5270, 5480, 5690, 6710, 7640, 8400, 9330, 9520, 10000, 11100, 11860, 12110,
         12960, 13620, 14510, 15600, 16170, 16860, 17230, 17870, 18570, 19140, 19840, 20040, 20510, 21470, 22430, 23550, 23890];

    [Fact]
    public void Matches_the_client_vector()
    {
        var r = FirewallBreach.Score(20261127, false, [400, 900, 1500, 2300, 3000, 3200, 4100, 5000, 5600, 7000]);
        Assert.True(r.Valid);
        Assert.Equal((1, 9, 2, 1), (r.Hits, r.Misses, r.BestLevel, r.Coins));
    }

    [Fact]
    public void Perfect_play_hits_everything_and_is_capped()
    {
        var r = FirewallBreach.Score(20261127, false, PerfectTaps);
        Assert.Equal((36, 0, 5), (r.Hits, r.Misses, r.BestLevel));
        Assert.Equal(FirewallBreach.MaxRoundCoins, r.Coins);
    }

    [Fact]
    public void Inhuman_or_out_of_round_taps_invalidate_the_round()
    {
        Assert.Equal("taps_too_fast", FirewallBreach.Score(1, false, [1000, 1100]).Reason);
        Assert.Equal("taps_too_fast", FirewallBreach.Score(1, false, [2000, 1000]).Reason);
        Assert.Equal("tap_out_of_round", FirewallBreach.Score(1, false, [FirewallBreach.DurationMs + 1]).Reason);
        Assert.True(FirewallBreach.Score(1, false, []).Valid);
    }

    [Fact]
    public void Wide_mode_is_more_forgiving()
    {
        var narrow = Enumerable.Range(0, 200).Sum(s => FirewallBreach.Score((uint)s + 1, false, [500, 1200, 2000]).Hits);
        var wide = Enumerable.Range(0, 200).Sum(s => FirewallBreach.Score((uint)s + 1, true, [500, 1200, 2000]).Hits);
        Assert.True(wide > narrow, $"wide {wide} <= narrow {narrow}");
    }

    [Fact]
    public void Prng_matches_reference_mulberry32()
    {
        var r = new Mulberry32(1);
        Assert.Equal(0.6270739405881613, r.Next(), 15);
    }
}
