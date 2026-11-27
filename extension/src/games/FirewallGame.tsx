import { useEffect, useRef, useState } from 'preact/hooks';
import { api, ApiFailure, type FinishResult } from '../api';
import { FirewallState, type FirewallRound } from './firewall';

const PREVIEW = { seed: 7, wide: false, speedDegPerSec: [150], sectorDeg: [70], wideFactor: 1, minTapGapMs: 150 };

type Phase =
  | { kind: 'idle' }
  | { kind: 'loading' }
  | { kind: 'playing'; round: FirewallRound }
  | { kind: 'sending' }
  | { kind: 'done'; result: FinishResult }
  | { kind: 'error'; message: string };

const coarse = () => window.matchMedia?.('(pointer: coarse)').matches ?? false;

/**
 * Firewall Breach (concept, section 2): tap when the pointer is in the green sector. Plays against the local
 * animation, never against the video, because the stream lags 2–15 s behind. The server replays the taps.
 */
export function FirewallGame({ onCoins }: { onCoins: (balance: number) => void }) {
  const [phase, setPhase] = useState<Phase>({ kind: 'idle' });
  const [hud, setHud] = useState({ level: 1, hits: 0, coins: 0, left: 0 });
  const canvas = useRef<HTMLCanvasElement>(null);
  const game = useRef<{ state: FirewallState; t0: number; flash: { at: number; hit: boolean } | null } | null>(null);

  async function start() {
    setPhase({ kind: 'loading' });
    try {
      const round = await api<FirewallRound>('/games/firewall/start', { wide: coarse() });
      game.current = { state: new FirewallState(round), t0: performance.now(), flash: null };
      setPhase({ kind: 'playing', round });
    } catch (e) {
      const msg = e instanceof ApiFailure ? e.message : 'Server nicht erreichbar.';
      setPhase({ kind: 'error', message: msg });
    }
  }

  async function finish(round: FirewallRound) {
    const g = game.current;
    game.current = null;
    if (!g) return;
    setPhase({ kind: 'sending' });
    try {
      const result = await api<FinishResult>('/games/firewall/finish', { roundId: round.roundId, taps: g.state.taps });
      onCoins(result.balance);
      setPhase({ kind: 'done', result });
    } catch (e) {
      setPhase({ kind: 'error', message: e instanceof ApiFailure ? e.message : 'Ergebnis kam nicht an.' });
    }
  }

  function tap() {
    const g = game.current;
    if (!g || phase.kind !== 'playing') return;
    const hit = g.state.tap(performance.now() - g.t0);
    if (hit === null) return;
    g.flash = { at: performance.now(), hit };
    navigator.vibrate?.(hit ? 15 : [30, 30, 30]);
  }

  // Still picture behind the start screen, redrawn when the panel changes size.
  useEffect(() => {
    if (phase.kind === 'playing' || !canvas.current) return;
    const el = canvas.current;
    const paint = () => draw(el, new FirewallState(PREVIEW), 600, null);
    paint();
    const ro = new ResizeObserver(paint);
    ro.observe(el);
    return () => ro.disconnect();
  }, [phase]);

  // Animation loop while a round runs.
  useEffect(() => {
    if (phase.kind !== 'playing') return;
    const round = phase.round;
    let raf = 0;
    let lastHud = 0;
    const loop = () => {
      const g = game.current;
      const el = canvas.current;
      if (!g || !el) return;
      const t = performance.now() - g.t0;
      if (t >= round.durationMs) {
        finish(round);
        return;
      }
      draw(el, g.state, t, g.flash);
      if (performance.now() - lastHud > 100) {
        lastHud = performance.now();
        setHud({ level: g.state.level, hits: g.state.hits, coins: g.state.roundCoins(), left: Math.ceil((round.durationMs - t) / 1000) });
      }
      raf = requestAnimationFrame(loop);
    };
    raf = requestAnimationFrame(loop);
    return () => cancelAnimationFrame(raf);
  }, [phase]);

  return (
    <section class="game">
      <div class="game-head">
        <h2>Firewall Breach</h2>
        {phase.kind === 'playing' && (
          <span class="game-hud">
            Stufe {hud.level} · {hud.hits} Treffer · <b>{hud.coins} HC</b> · {hud.left}s
          </span>
        )}
      </div>

      <div class="game-stage">
        <canvas
          ref={canvas}
          class={phase.kind === 'playing' ? 'ring live' : 'ring'}
          onPointerDown={(e) => {
            e.preventDefault();
            tap();
          }}
          aria-label="Spielfeld: tippen, wenn der Zeiger im grünen Bereich steht"
        />
        {phase.kind !== 'playing' && (
          <div class="game-overlay">
            {phase.kind === 'idle' && <p>Tippe, wenn der Zeiger im grünen Sektor steht. Jeder Treffer macht es schneller und enger.</p>}
            {phase.kind === 'loading' && <p>Verbinde …</p>}
            {phase.kind === 'sending' && <p>Prüfe Einbruch …</p>}
            {phase.kind === 'done' && <Result r={phase.result} />}
            {phase.kind === 'error' && <p class="bad">{phase.message}</p>}
            {phase.kind !== 'loading' && phase.kind !== 'sending' && (
              <button class="primary big" onClick={start}>
                {phase.kind === 'idle' ? 'Einbruch starten' : 'Nochmal'}
              </button>
            )}
          </div>
        )}
      </div>

      {phase.kind === 'playing' && (
        <button class="primary big breach" onPointerDown={(e) => { e.preventDefault(); tap(); }}>
          BREACH
        </button>
      )}
    </section>
  );
}

function Result({ r }: { r: FinishResult }) {
  return (
    <div class="result">
      <p class="result-coins">+{r.credited} HC</p>
      <p>
        {r.hits} Treffer, {r.misses} daneben, beste Stufe {r.bestLevel}
      </p>
      {r.cappedBy === 'hour' && <p class="muted">Stundengrenze erreicht: {r.earned - r.credited} HC verfallen.</p>}
      {r.cappedBy === 'wallet' && <p class="muted">Wallet voll: {r.earned - r.credited} HC verfallen.</p>}
    </div>
  );
}

function draw(el: HTMLCanvasElement, s: FirewallState, t: number, flash: { at: number; hit: boolean } | null) {
  const dpr = window.devicePixelRatio || 1;
  const size = el.clientWidth;
  if (el.width !== Math.round(size * dpr)) {
    el.width = Math.round(size * dpr);
    el.height = Math.round(size * dpr);
  }
  const ctx = el.getContext('2d')!;
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  ctx.clearRect(0, 0, size, size);
  const c = size / 2;
  const r = size * 0.4;
  const rad = (deg: number) => ((deg - 90) * Math.PI) / 180; // 0° at the top, clockwise
  const css = getComputedStyle(el);
  const ok = css.getPropertyValue('--ok').trim() || '#3ef08a';
  const line = css.getPropertyValue('--line').trim() || '#25403a';
  const fg = css.getPropertyValue('--fg').trim() || '#d6ffe9';
  const bad = css.getPropertyValue('--bad').trim() || '#ff4d6d';

  ctx.lineWidth = size * 0.07;
  ctx.strokeStyle = line;
  ctx.beginPath();
  ctx.arc(c, c, r, 0, Math.PI * 2);
  ctx.stroke();

  const half = s.sectorWidth() / 2;
  ctx.strokeStyle = ok;
  ctx.beginPath();
  ctx.arc(c, c, r, rad(s.center - half), rad(s.center + half));
  ctx.stroke();

  const since = flash ? performance.now() - flash.at : Infinity;
  if (flash && since < 250) {
    ctx.lineWidth = size * 0.015;
    ctx.strokeStyle = flash.hit ? ok : bad;
    ctx.globalAlpha = 1 - since / 250;
    ctx.beginPath();
    ctx.arc(c, c, r + size * 0.06, 0, Math.PI * 2);
    ctx.stroke();
    ctx.globalAlpha = 1;
  }

  const a = rad(s.angleAt(t));
  ctx.lineWidth = size * 0.02;
  ctx.lineCap = 'round';
  ctx.strokeStyle = fg;
  ctx.beginPath();
  ctx.moveTo(c + Math.cos(a) * r * 0.2, c + Math.sin(a) * r * 0.2);
  ctx.lineTo(c + Math.cos(a) * (r + size * 0.05), c + Math.sin(a) * (r + size * 0.05));
  ctx.stroke();

  // Combo pips in the middle.
  for (let i = 0; i < 5; i++) {
    ctx.fillStyle = i < s.level ? ok : line;
    ctx.beginPath();
    ctx.arc(c + (i - 2) * size * 0.05, c, size * 0.016, 0, Math.PI * 2);
    ctx.fill();
  }
}
