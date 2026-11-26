// The service worker: the app shell from the cache, so the page still opens when the PC cannot be reached. main.js registers it
// as sw.js?v=<the PC's version>, so each publish installs a new worker with its own cache and drops the old ones. Pages are
// network-first (a reachable PC's signed-out page or a new index still shows), the shell's files cache-first; /api/* and
// other origins are never intercepted or cached. A module worker, so Node can import route() and the cache names.

export const SHELL = ['/', '/index.html', '/styles.css', '/manifest.webmanifest',
  ...['api', 'chat', 'chats', 'composer', 'h', 'main', 'markdown', 'pcs', 'util'].map(n => `/js/${n}.js`),
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

if (typeof ServiceWorkerGlobalScope !== 'undefined') {
  const version = new URL(location.href).searchParams.get('v') || '0';
  const cache = () => caches.open(cacheName(version));
  addEventListener('install', e => e.waitUntil(cache().then(c => c.addAll(SHELL)).then(() => skipWaiting())));
  addEventListener('activate', e => e.waitUntil(caches.keys()
    .then(names => Promise.all(stale(names, version).map(n => caches.delete(n)))).then(() => clients.claim())));
  addEventListener('fetch', e => {
    const how = route(e.request, location.origin);
    if (how === 'page') e.respondWith(fetch(e.request).catch(() => cache().then(c => c.match('/'))));
    else if (how === 'shell') e.respondWith(cache().then(c => c.match(e.request)).then(r => r || fetch(e.request)));
  });
}
