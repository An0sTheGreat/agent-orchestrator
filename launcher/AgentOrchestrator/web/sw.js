// The service worker: the app shell from the cache, so the page still opens when the PC cannot be reached. main.js registers it
// as sw.js?v=<the PC's shell: a hash of its embedded web/ files>, so each publish that changes the app installs a new worker with
// its own cache and drops the old ones. Pages are network-first (a reachable PC's signed-out page or a new index still shows),
// the shell's files cache-first; /api/* and other origins are never intercepted or cached. A module worker, so Node can
// import route(), page() and the cache names.

export const SHELL = ['/', '/index.html', '/styles.css', '/manifest.webmanifest',
  ...['agents', 'api', 'chat', 'chats', 'code', 'composer', 'dictate', 'h', 'images', 'main', 'markdown', 'pcs', 'remoteapi', 'settings', 'skills', 'speak', 'tools', 'util'].map(n => `/js/${n}.js`),
  ...['Inter-Medium', 'Inter-Regular', 'Inter-SemiBold', 'JetBrainsMono-Regular', 'Newsreader16pt-Italic', 'Newsreader16pt-Regular']
    .map(n => `/assets/fonts/${n}.ttf`),
  ...['apple-touch-icon', 'icon-192', 'icon-512', 'icon-maskable-512'].map(n => `/icons/${n}.png`)];

export const cacheName = version => 'ao-shell-' + version;
export const stale = (names, version) => names.filter(n => n.startsWith('ao-shell-') && n !== cacheName(version));

// What the worker does with a request: 'pass' (not intercepted), 'page' (the network, else the cached shell) or 'shell' (the
// cache, else the network).
export function route({ url, method, mode }, origin) {
  const u = new URL(url);
  if (method !== 'GET' || u.origin !== origin || u.pathname.startsWith('/api/')) return 'pass';
  return mode === 'navigate' ? 'page' : 'shell';
}

// A page's response: `net` (the network's) when it answers within `ms`, else `cached()` (the cached shell): a Tailscale peer
// that is off often leaves the connect hanging. With nothing cached yet, the network is still waited for.
export function page(net, cached, ms = 4000) {
  let timer;
  const late = new Promise(resolve => { timer = setTimeout(resolve, ms); });
  return Promise.race([net, late]).finally(() => clearTimeout(timer))
    .then(r => r || cached().then(c => c || net), () => cached());
}

if (typeof ServiceWorkerGlobalScope !== 'undefined') {
  const version = new URL(location.href).searchParams.get('v') || '0';
  const cache = () => caches.open(cacheName(version));
  addEventListener('install', e => e.waitUntil(cache().then(c => c.addAll(SHELL)).then(() => skipWaiting())));
  addEventListener('activate', e => e.waitUntil(caches.keys()
    .then(names => Promise.all(stale(names, version).map(n => caches.delete(n)))).then(() => clients.claim())));
  addEventListener('fetch', e => {
    const how = route(e.request, location.origin);
    if (how === 'page') e.respondWith(page(fetch(e.request), () => cache().then(c => c.match('/'))));
    else if (how === 'shell') e.respondWith(cache().then(c => c.match(e.request)).then(r => r || fetch(e.request)));
  });
}
