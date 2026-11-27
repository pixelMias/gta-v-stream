import { describe, expect, it } from 'vitest';
import { FirewallState } from './firewall';
import { mulberry32 } from './prng';

// Same vectors as backend/GtaStream.Ebs.Tests/FirewallTests.cs: client and server must agree tap for tap.
const params = { seed: 20261127, wide: false, speedDegPerSec: [150, 190, 230, 270, 310], sectorDeg: [70, 56, 44, 34, 26], wideFactor: 1.2, minTapGapMs: 150 };
const perfect = [1310, 2310, 2550, 3050, 3910, 4150, 4450, 5270, 5480, 5690, 6710, 7640, 8400, 9330, 9520, 10000, 11100, 11860, 12110,
  12960, 13620, 14510, 15600, 16170, 16860, 17230, 17870, 18570, 19140, 19840, 20040, 20510, 21470, 22430, 23550, 23890];

describe('firewall breach', () => {
  it('prng matches reference mulberry32', () => {
    expect(mulberry32(1)()).toBe(0.6270739405881613);
  });

  it('matches the server vector', () => {
    const s = new FirewallState(params);
    for (const t of [400, 900, 1500, 2300, 3000, 3200, 4100, 5000, 5600, 7000]) s.tap(t);
    expect([s.hits, s.misses, s.bestLevel, s.roundCoins()]).toEqual([1, 9, 2, 1]);
  });

  it('perfect play hits everything and is capped', () => {
    const s = new FirewallState(params);
    for (const t of perfect) s.tap(t);
    expect([s.hits, s.misses, s.bestLevel, s.roundCoins()]).toEqual([36, 0, 5, 50]);
  });

  it('ignores taps that come too fast', () => {
    const s = new FirewallState(params);
    expect(s.tap(1000)).not.toBeNull();
    expect(s.tap(1100)).toBeNull();
    expect(s.taps).toEqual([1000]);
  });
});
