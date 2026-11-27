// Talks to the EBS. Inside Twitch the token comes from the Extension Helper (onAuthorized); on the test server, where
// the page is opened directly, a test login from /api/v1/dev/session stands in and is kept in localStorage.

declare global {
  interface Window {
    Twitch?: { ext?: { onAuthorized(cb: (auth: { token: string; channelId: string; userId: string }) => void): void } };
  }
}

/** Absolute EBS address for the Twitch build (VITE_EBS_URL), relative on the test server. */
const BASE = (import.meta.env.VITE_EBS_URL as string | undefined)?.replace(/\/$/, '') ?? '';
const DEV_KEY = 'gtastream.devToken';

export type ApiError = { error: string; message?: string; retryAfterSec?: number | null };
export class ApiFailure extends Error {
  constructor(public status: number, public body: ApiError) {
    super(body.message ?? body.error);
  }
}

let token: string | null = null;

export const inTwitch = (() => {
  try {
    return window.parent !== window;
  } catch {
    return true;
  }
})();

function storage(): Storage | null {
  try {
    return window.localStorage;
  } catch {
    return null;
  }
}

/** Resolves once a token is there. */
export function authorize(): Promise<void> {
  if (inTwitch && window.Twitch?.ext) {
    return new Promise((resolve) =>
      window.Twitch!.ext!.onAuthorized((auth) => {
        token = auth.token; // Twitch renews it on its own and calls again
        resolve();
      }),
    );
  }
  return devLogin();
}

async function devLogin(): Promise<void> {
  const params = new URLSearchParams(location.search);
  const admin = params.get('admin');
  if (admin) {
    params.delete('admin');
    history.replaceState(null, '', location.pathname + (params.size ? '?' + params : '') + location.hash);
  }
  const old = storage()?.getItem(DEV_KEY);
  const headers: Record<string, string> = {};
  if (old) headers.Authorization = `Bearer ${old}`;
  if (admin) headers['X-Admin-Token'] = admin;
  const res = await fetch(`${BASE}/api/v1/dev/session`, { method: 'POST', headers });
  if (!res.ok) throw new Error(`Test-Login fehlgeschlagen (HTTP ${res.status})`);
  const body = (await res.json()) as { token: string };
  token = body.token;
  storage()?.setItem(DEV_KEY, body.token);
}

export async function api<T>(path: string, body?: unknown): Promise<T> {
  const res = await fetch(`${BASE}/api/v1${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!res.ok) {
    let err: ApiError = { error: `http_${res.status}` };
    try {
      err = await res.json();
    } catch {
      /* not JSON */
    }
    throw new ApiFailure(res.status, err);
  }
  return res.json() as Promise<T>;
}

// ------------------------------------------------------------------ shapes of the EBS answers

export type PurchaseView = {
  id: string;
  eventId: string;
  eventName: string;
  category: Category;
  viewer: string;
  price: number;
  state: 'queued' | 'accepted' | 'executed' | 'refunded';
  reason: string | null;
  createdAt: string;
  executedAt: string | null;
  endsAt: string | null;
};

export type Category = 'harmless' | 'disruptive' | 'chaotic' | 'surreal' | 'helpful' | 'stream';

export type Me = {
  name: string;
  role: string;
  dev: boolean;
  balance: number;
  walletCap: number;
  hourlyCap: number;
  hourlyEarned: number;
  purchases: { purchase: PurchaseView; position: number | null }[];
};

export type Status = {
  studio: { mode: 'live' | 'simulation' | 'offline'; game: string | null; connectedAt: string | null; state: unknown };
  shopOpen: boolean;
  queue: PurchaseView[];
  active: PurchaseView[];
  recent: PurchaseView[];
  leaderboard: { name: string; coins: number }[];
  serverTime: string;
};

export type CatalogEvent = {
  id: string;
  name: string;
  description: string;
  category: Category;
  price: number;
  basePrice: number;
  durationSec: number;
  available: boolean;
  cooldownSec: number;
  viewerCooldownSec: number;
};

export type CatalogView = { shopOpen: boolean; events: CatalogEvent[] };

export type FinishResult = {
  hits: number;
  misses: number;
  bestLevel: number;
  earned: number;
  credited: number;
  cappedBy: 'hour' | 'wallet' | null;
  balance: number;
};

export const CATEGORY_LABEL: Record<Category, string> = {
  harmless: 'Harmlos',
  disruptive: 'Störend',
  chaotic: 'Chaotisch',
  surreal: 'Surreal',
  helpful: 'Helfend',
  stream: 'Stream',
};

/** Why an intervention was refunded, in words for viewers. */
export function reasonText(reason: string | null): string {
  switch (reason) {
    case null:
      return '';
    case 'timeout':
    case 'director_hold_timeout':
      return 'Regie hat keinen Platz gefunden, Coins zurück';
    case 'panic':
      return 'Notabschaltung, Coins zurück';
    case 'restart':
      return 'Server-Neustart, Coins zurück';
    case 'not_in_vehicle':
      return 'Kein Fahrzeug, Coins zurück';
    default:
      return 'Abgelehnt, Coins zurück';
  }
}
