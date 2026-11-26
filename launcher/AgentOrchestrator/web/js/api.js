// The PC's API. `api.base` is the PC's origin ('' = this page's own). A failure throws an Error with .status (0 = unreachable).

// parseSse(chunk, state): the events this chunk completes; `state` carries a split frame to the next call. Pure.
export function parseSse(chunk, state = {}) {
  const frames = ((state.rest || '') + chunk).replace(/\r\n?/g, '\n').split('\n\n');
  state.rest = frames.pop();
  const out = [];
  for (const f of frames) {
    const data = f.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5).replace(/^ /, '')).join('\n');
    if (!data) continue; // a comment
    try { out.push(JSON.parse(data)); } catch { /* not JSON: skipped */ }
  }
  return out;
}

export const WATCHDOG = 45000;

// The next retry wait: doubles from 1 s up to 15 s.
export const nextWait = ms => Math.min(ms * 2, 15000);

// What an API error does: 'reload' for the signed-out 403 (the server then answers the page with signedout.html) unless this
// tab already reloaded for it (`reloaded`: no reload loop when a cached page keeps getting it), 'lost' when unreachable, else 'toast'.
export const failAction = (err, reloaded) =>
  err?.status === 0 ? 'lost' : err?.status === 403 && err.message === 'SignedOut' && !reloaded ? 'reload' : 'toast';

// The tailnet of a Tailscale DNS name: "home.tail0000.ts.net." -> "tail0000.ts.net".
export const tailnetOf = dnsName => (dnsName || '').replace(/\.$/, '').split('.').slice(1).join('.');

// The API base for a PC's DNS name: its https origin, or '' (this page's own origin) for the page's own PC (`here`) or none.
export const baseOf = (host, here) => {
  const h = (host || '').replace(/\.$/, '').toLowerCase();
  return !h || h === here.toLowerCase() ? '' : 'https://' + h;
};

// The base the app starts on: the remembered PC's if `probe(base)` (its /api/hello) resolves, else this page's own. A probe
// that fails with a 4xx answer (.status: the PC refused, 403/404) calls `forget`; one that is off, too slow or errors (5xx) keeps it.
export async function startBase(remembered, here, probe, forget) {
  const base = baseOf(remembered, here);
  if (!base) return '';
  try { await probe(base); return base; } catch (err) { if (err?.status >= 400 && err.status < 500) forget?.(); return ''; }
}

// Whether the remembered PC is gone: a fresh /api/pcs (`pcs`) whose own PC is on the tailnet (a dnsName) no longer lists it.
export const gone = (remembered, pcs) => !!remembered && pcs.some(pc => pc.self && pc.dnsName) &&
  !pcs.some(pc => pc.dnsName && baseOf(pc.dnsName, remembered) === '');

// `fn` wrapped so only its latest call settles: an earlier call still in flight never resolves or rejects (a late answer from
// the PC the app no longer talks to is dropped).
export function latestOnly(fn) {
  let calls = 0;
  return (...args) => {
    const mine = ++calls;
    return new Promise((resolve, reject) => fn(...args).then(
      v => { if (mine === calls) resolve(v); },
      e => { if (mine === calls) reject(e); }));
  };
}

async function call(path, init) {
  let res;
  try { res = await fetch(api.base + path, { credentials: 'omit', ...init }); }
  catch { throw Object.assign(new Error('The PC could not be reached'), { status: 0 }); }
  const body = await res.json().catch(() => null);
  if (!res.ok) throw Object.assign(new Error(body?.error || `HTTP ${res.status}`), { status: res.status });
  return body;
}

export const api = {
  base: '',
  get: path => call(path),
  post: (path, body) => call(path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body ?? {}) }),
  // Resolves to the file's saved path on the PC.
  upload: async file => (await call('/api/upload', {
    method: 'POST', body: file,
    headers: { 'Content-Type': file.type || 'application/octet-stream', 'X-File-Name': encodeURIComponent(file.name || 'photo') },
  })).path,
  // Live events for a conversation; reconnects with a 1 to 15 s backoff (each connection starts with the server's hello). Returns stop().
  // onDrop(n): the n-th failure in a row (EventSource cannot tell a 403/404 from a drop; the caller can ask the API).
  // Watchdog: a connection with no frame for WATCHDOG ms (half-open after a network switch) is dropped and reopened. EventSource
  // hides SSE comments, so the server's keepalive is a {"e":"ping"} event (every 15 s): it feeds the watchdog and goes no further.
  events(id, onEvent, onDrop) {
    let es, timer, dog, wait = 1000, stopped = false, drops = 0;
    const drop = () => {
      clearTimeout(dog);
      es.close();
      if (!stopped) { timer = setTimeout(open, wait); wait = nextWait(wait); onDrop?.(++drops); }
    };
    const fed = () => { clearTimeout(dog); dog = setTimeout(drop, WATCHDOG); };
    const open = () => {
      es = new EventSource(api.base + '/api/conversation/' + encodeURIComponent(id) + '/events');
      fed();
      es.onopen = () => { wait = 1000; drops = 0; fed(); };
      es.onmessage = m => { fed(); try { const ev = JSON.parse(m.data); if (ev?.e !== 'ping') onEvent(ev); } catch { /* not JSON: skipped */ } };
      es.onerror = drop;
    };
    open();
    return () => { stopped = true; clearTimeout(timer); clearTimeout(dog); es?.close(); };
  },
};
