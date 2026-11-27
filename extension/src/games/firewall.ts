import { mulberry32 } from './prng';

/** Round parameters as POST /api/v1/games/firewall/start returns them. */
export type FirewallRound = {
  roundId: string;
  seed: number;
  wide: boolean;
  durationMs: number;
  minTapGapMs: number;
  speedDegPerSec: number[];
  sectorDeg: number[];
  wideFactor: number;
  hourlyLeft: number;
};

export const TOLERANCE_DEG = 3;
export const MAX_ROUND_COINS = 50;

export function distance(a: number, b: number): number {
  return Math.abs(((((a - b) % 360) + 540) % 360) - 180);
}

/**
 * Same rules as FirewallBreach.Score on the server: the client uses them to animate and to show the score at once,
 * the server replays the taps and alone decides the coins.
 */
export class FirewallState {
  private rng: () => number;
  angleAtLast: number;
  center: number;
  level = 1;
  lastT = 0;
  hits = 0;
  misses = 0;
  bestLevel = 1;
  coins = 0;
  taps: number[] = [];

  constructor(private round: Pick<FirewallRound, 'seed' | 'wide' | 'speedDegPerSec' | 'sectorDeg' | 'wideFactor' | 'minTapGapMs'>) {
    this.rng = mulberry32(round.seed);
    this.angleAtLast = this.rng() * 360;
    this.center = this.rng() * 360;
  }

  speed(): number {
    return this.round.speedDegPerSec[this.level - 1];
  }

  sectorWidth(): number {
    return this.round.sectorDeg[this.level - 1] * (this.round.wide ? this.round.wideFactor : 1);
  }

  angleAt(t: number): number {
    return (this.angleAtLast + (this.speed() * (t - this.lastT)) / 1000) % 360;
  }

  /** Returns true for a hit, false for a miss, null if the tap came too soon after the last one (ignored). */
  tap(t: number): boolean | null {
    t = Math.round(t);
    if (this.taps.length > 0 && t - this.lastT < this.round.minTapGapMs) return null;
    const angle = this.angleAt(t);
    this.angleAtLast = angle;
    this.lastT = t;
    this.taps.push(t);
    const half = this.sectorWidth() / 2 + TOLERANCE_DEG;
    const hit = distance(angle, this.center) <= half;
    if (hit) {
      this.hits++;
      this.coins += this.level;
      this.level = Math.min(5, this.level + 1);
      this.bestLevel = Math.max(this.bestLevel, this.level);
    } else {
      this.misses++;
      this.level = 1;
    }
    this.center = this.rng() * 360;
    return hit;
  }

  roundCoins(): number {
    return Math.min(this.coins, MAX_ROUND_COINS);
  }
}
