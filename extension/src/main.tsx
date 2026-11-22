import { render } from 'preact';
import { useEffect, useState } from 'preact/hooks';

type Health = { status: string; commit: string; startedAt: string; db: string };

function App() {
  const [health, setHealth] = useState<Health | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    fetch('/api/health')
      .then((r) => (r.ok ? r.json() : Promise.reject(new Error(`HTTP ${r.status}`))))
      .then(setHealth)
      .catch((e: Error) => setError(e.message));
  }, []);

  return (
    <main style={{ fontFamily: 'system-ui, sans-serif', padding: 16, maxWidth: 480, margin: '0 auto' }}>
      <h1>GTA V Stream</h1>
      <p>Gerüst der Extension. Hier entstehen Shop, Minispiele und Warteschlange.</p>
      {error && <p>EBS nicht erreichbar: {error}</p>}
      {health && (
        <p>
          EBS: {health.status}, Commit <code>{health.commit.slice(0, 7)}</code>, Datenbank {health.db}
        </p>
      )}
    </main>
  );
}

render(<App />, document.getElementById('app')!);
