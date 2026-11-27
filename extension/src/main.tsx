import { render } from 'preact';
import { useEffect, useMemo, useState } from 'preact/hooks';
import {
  api,
  ApiFailure,
  authorize,
  CATEGORY_LABEL,
  reasonText,
  type CatalogEvent,
  type CatalogView,
  type Category,
  type Me,
  type PurchaseView,
  type Status,
} from './api';
import { FirewallGame } from './games/FirewallGame';
import './styles.css';

type Tab = 'hack' | 'shop' | 'live';

function usePoll<T>(path: string | null, everyMs: number): [T | null, () => void] {
  const [data, setData] = useState<T | null>(null);
  const [nonce, setNonce] = useState(0);
  useEffect(() => {
    if (!path) return;
    let alive = true;
    const load = () =>
      api<T>(path)
        .then((d) => alive && setData(d))
        .catch(() => {});
    load();
    const id = setInterval(() => !document.hidden && load(), everyMs);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, [path, everyMs, nonce]);
  return [data, () => setNonce((n) => n + 1)];
}

/** Seconds ticking down locally between polls. */
function useNow(everyMs = 1000) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const id = setInterval(() => setNow(Date.now()), everyMs);
    return () => clearInterval(id);
  }, [everyMs]);
  return now;
}

function App() {
  const [ready, setReady] = useState(false);
  const [authError, setAuthError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('hack');
  const [toast, setToast] = useState<{ text: string; bad?: boolean } | null>(null);

  useEffect(() => {
    authorize()
      .then(() => setReady(true))
      .catch((e: Error) => setAuthError(e.message));
  }, []);

  const [me, reloadMe] = usePoll<Me>(ready ? '/me' : null, 5000);
  const [status, reloadStatus] = usePoll<Status>(ready ? '/status' : null, 2000);
  const [catalog, reloadCatalog] = usePoll<CatalogView>(ready && tab === 'shop' ? '/catalog' : null, 5000);
  const [balance, setBalance] = useState<number | null>(null);
  useEffect(() => setBalance(me?.balance ?? null), [me]);

  useEffect(() => {
    if (!toast) return;
    const id = setTimeout(() => setToast(null), 3500);
    return () => clearTimeout(id);
  }, [toast]);

  async function buy(e: CatalogEvent) {
    try {
      const res = await api<{ position: number; balance: number }>('/purchase', { eventId: e.id });
      setBalance(res.balance);
      setToast({ text: `${e.name} in der Warteschlange, Platz ${res.position}` });
      reloadMe();
      reloadStatus();
      reloadCatalog();
    } catch (err) {
      const f = err instanceof ApiFailure ? err : null;
      const wait = f?.body.retryAfterSec ? ` (noch ${f.body.retryAfterSec} s)` : '';
      setToast({ text: (f?.message ?? 'Kauf fehlgeschlagen.') + wait, bad: true });
    }
  }

  async function panic() {
    try {
      const r = await api<{ refunded: number }>('/director/panic', {});
      setToast({ text: `Notabschaltung: ${r.refunded} Eingriffe erstattet` });
      reloadStatus();
    } catch {
      setToast({ text: 'Notabschaltung fehlgeschlagen.', bad: true });
    }
  }

  if (authError) return <main class="app"><p class="bad pad">Anmeldung fehlgeschlagen: {authError}</p></main>;
  if (!ready) return <main class="app"><p class="muted pad">Verbinde mit dem Netz …</p></main>;

  return (
    <main class="app">
      <header class="top">
        <div class="brand">
          <span class="logo">▚</span> GTA V Stream
        </div>
        <StudioBadge status={status} />
        <div class="wallet" title={me ? `${me.hourlyEarned}/${me.hourlyCap} HC aus Spielen in der letzten Stunde` : ''}>
          <b>{balance ?? '…'}</b> HC
        </div>
      </header>

      <nav class="tabs" role="tablist">
        {(['hack', 'shop', 'live'] as Tab[]).map((t) => (
          <button role="tab" aria-selected={tab === t} class={tab === t ? 'tab on' : 'tab'} onClick={() => setTab(t)}>
            {t === 'hack' ? 'Hacken' : t === 'shop' ? 'Eingriffe' : `Live${status?.queue.length ? ` (${status.queue.length})` : ''}`}
          </button>
        ))}
      </nav>

      <div class="body">
        <div class={tab === 'live' ? 'pane main hide-narrow' : 'pane main'}>
          {tab === 'shop' ? (
            <Shop catalog={catalog} balance={balance ?? 0} onBuy={buy} />
          ) : (
            <FirewallGame onCoins={(b) => { setBalance(b); reloadMe(); }} />
          )}
          {me && tab === 'hack' && (
            <p class="muted small">
              Stundengrenze: {me.hourlyEarned}/{me.hourlyCap} HC · Wallet max. {me.walletCap} HC · Du bist <b>{me.name}</b>
            </p>
          )}
        </div>
        <aside class={tab === 'live' ? 'pane side' : 'pane side hide-narrow'}>
          <Live status={status} me={me} />
          {me?.role === 'broadcaster' || me?.role === 'moderator' ? (
            <section class="card director">
              <h3>Regie</h3>
              <button class="danger big" onClick={panic}>Notabschaltung</button>
              <p class="muted small">Stoppt alle Effekte, leert die Warteschlange und erstattet alles.</p>
            </section>
          ) : null}
        </aside>
      </div>

      {toast && <div class={toast.bad ? 'toast bad' : 'toast'} role="status">{toast.text}</div>}
    </main>
  );
}

function StudioBadge({ status }: { status: Status | null }) {
  const mode = status?.studio.mode;
  const label = mode === 'live' ? 'KI am Steuer' : mode === 'simulation' ? 'Simulation' : mode === 'offline' ? 'Signal verloren' : '…';
  return <span class={`badge ${mode ?? ''}`} title={mode === 'simulation' ? 'Noch kein Spiel verbunden: Eingriffe laufen in einer Simulation.' : ''}>{label}</span>;
}

const ORDER: Category[] = ['harmless', 'stream', 'helpful', 'disruptive', 'chaotic', 'surreal'];

function Shop({ catalog, balance, onBuy }: { catalog: CatalogView | null; balance: number; onBuy: (e: CatalogEvent) => void }) {
  const [cat, setCat] = useState<Category>('harmless');
  const [loadedAt, setLoadedAt] = useState(Date.now());
  useEffect(() => setLoadedAt(Date.now()), [catalog]);
  const now = useNow();
  const elapsed = Math.floor((now - loadedAt) / 1000);
  if (!catalog) return <p class="muted pad">Lade Eingriffe …</p>;
  const events = catalog.events.filter((e) => e.category === cat);
  return (
    <section class="shop">
      {!catalog.shopOpen && <p class="bad">Signal verloren: Das Spiel ist nicht verbunden, Eingriffe sind pausiert.</p>}
      <div class="chips">
        {ORDER.map((c) => (
          <button class={c === cat ? `chip on ${c}` : `chip ${c}`} onClick={() => setCat(c)}>
            {CATEGORY_LABEL[c]}
          </button>
        ))}
      </div>
      <ul class="events">
        {events.map((e) => {
          const join = e.joinable && elapsed < 15 && e.viewerCooldownSec - elapsed <= 0;
          const wait = join ? 0 : Math.max(e.cooldownSec, e.viewerCooldownSec) - elapsed;
          const disabled = !e.available || wait > 0 || balance < e.price;
          return (
            <li class={`event ${e.category}`}>
              <div class="event-text">
                <b>{e.name}</b>
                <span class="muted small">{e.description}</span>
              </div>
              <button class="buy" disabled={disabled} onClick={() => onBuy(e)}>
                {!e.available ? 'n. v.' : wait > 0 ? fmt(wait) : join ? `Dabei! ${e.price}` : `${e.price} HC`}
              </button>
            </li>
          );
        })}
      </ul>
      <p class="muted small">Beliebte Eingriffe werden für 10 Minuten teurer. Wer binnen 20 s dasselbe kauft, macht mit: die Regie bündelt es zu einem stärkeren Eingriff. Was sie nicht unterbringt, wird erstattet.</p>
    </section>
  );
}

function Live({ status, me }: { status: Status | null; me: Me | null }) {
  const now = useNow();
  const mine = useMemo(() => new Set(me?.purchases.map((p) => p.purchase.id)), [me]);
  if (!status) return <p class="muted pad">Lade …</p>;
  // Server clock, so countdowns work even if the viewer's clock is off.
  const skew = Date.parse(status.serverTime) - Date.now();
  const left = (p: PurchaseView) => (p.endsAt ? Math.max(0, Math.ceil((Date.parse(p.endsAt) - now - skew) / 1000)) : 0);
  return (
    <>
      <section class="card">
        <h3>Warteschlange</h3>
        {status.queue.length === 0 ? (
          <p class="muted small">Leer. Die Regie wartet auf Ideen.</p>
        ) : (
          <ol class="list">
            {status.queue.map((p, i) => (
              <li class={mine.has(p.id) ? 'mine' : ''}>
                <span class="pos">{i + 1}</span> <b>{p.eventName}</b> <span class="muted">von {p.viewer}</span>
              </li>
            ))}
          </ol>
        )}
      </section>
      {status.active.filter((p) => left(p) > 0).length > 0 && (
        <section class="card">
          <h3>Läuft gerade</h3>
          <ul class="list">
            {status.active.filter((p) => left(p) > 0).map((p) => (
              <li>
                <b>{p.eventName}</b> <span class="muted">noch {fmt(left(p))}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
      <section class="card">
        <h3>Zuletzt</h3>
        {status.recent.length === 0 ? (
          <p class="muted small">Noch nichts passiert.</p>
        ) : (
          <ul class="list">
            {status.recent.map((p) => (
              <li class={mine.has(p.id) ? 'mine' : ''}>
                {p.state === 'executed' ? '✓' : '↺'} <b>{p.eventName}</b> <span class="muted">von {p.viewer}</span>
                {p.state === 'refunded' && <span class="muted small block">{reasonText(p.reason)}</span>}
              </li>
            ))}
          </ul>
        )}
      </section>
      <section class="card">
        <h3>Top-Hacker heute</h3>
        {status.leaderboard.length === 0 ? (
          <p class="muted small">Noch niemand. Sei der Erste.</p>
        ) : (
          <ol class="list">
            {status.leaderboard.map((l, i) => (
              <li class={l.name === me?.name ? 'mine' : ''}>
                <span class="pos">{i + 1}</span> {l.name} <span class="muted">{l.coins} HC</span>
              </li>
            ))}
          </ol>
        )}
      </section>
    </>
  );
}

function fmt(sec: number): string {
  return sec >= 60 ? `${Math.floor(sec / 60)}:${String(sec % 60).padStart(2, '0')}` : `${sec}s`;
}

render(<App />, document.getElementById('app')!);
