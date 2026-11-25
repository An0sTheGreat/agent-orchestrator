// The PC's API. `api.base` is the PC's origin ('' = this page's own). A failure throws an Error with .status (0 = unreachable).

// parseSse(chunk, state): the events this chunk completes; `state` carries a split frame to the next call. Pure.
export function parseSse(chunk, state = {}) {
  const frames = ((state.rest || '') + chunk).replace(/\r\n?/g, '\n').split('\n\n');
  state.rest = frames.pop();
  const out = [];
  for (const f of frames) {
    const data = f.split('\n').filter(l => l.startsWith('data:')).map(l => l.slice(5).replace(/^ /, '')).join('\n');
    if (!data) continue; // a comment (keepalive)
    try { out.push(JSON.parse(data)); } catch { /* not JSON: skipped */ }
  }
  return out;
}

// The next retry wait: doubles from 1 s up to 15 s.
export const nextWait = ms => Math.min(ms * 2, 15000);

// The tailnet of a Tailscale DNS name: "home.tail0000.ts.net." -> "tail0000.ts.net".
export const tailnetOf = dnsName => (dnsName || '').replace(/\.$/, '').split('.').slice(1).join('.');

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
  // Live events for a conversation; reconnects with a 1 to 15 s backoff and sends {e:'resync'} after each reconnect. Returns stop().
  events(id, onEvent) {
    let es, timer, wait = 1000, stopped = false, dropped = false;
    const open = () => {
      es = new EventSource(api.base + '/api/conversation/' + encodeURIComponent(id) + '/events');
      es.onopen = () => { wait = 1000; if (dropped) { dropped = false; onEvent({ e: 'resync' }); } };
      es.onmessage = m => { try { onEvent(JSON.parse(m.data)); } catch { /* not JSON: skipped */ } };
      es.onerror = () => {
        es.close();
        dropped = true;
        if (!stopped) { timer = setTimeout(open, wait); wait = nextWait(wait); }
      };
    };
    open();
    return () => { stopped = true; clearTimeout(timer); es?.close(); };
  },
};
