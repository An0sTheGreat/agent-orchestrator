// The app shell: screens, the drawer, sheets, segments and switches, the landscape layout and the connection to the PC.
// Screen modules load as their own <script type="module"> after this one and call registerScreen at their top level;
// start() runs on DOMContentLoaded, after every module script has run.
import { api, nextWait, tailnetOf, failAction, baseOf, startBase, gone, latestOnly } from './api.js';
import { makeRouter, keyboardBox } from './util.js';
import { h } from './h.js';

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const IMMERSIVE = ['chat', 'code']; // no tab bar in portrait
const screens = new Map(); // name -> {mount, show, mounted}
// Every go is a browser history entry, so the phone's back gesture goes back one screen (popstate) and the Back button goes
// through history too, keeping the two in step; at Chats a back gesture stays in the app (util.js makeRouter).
const router = makeRouter(history);
router.start();

/* ---------- screens ---------- */
// mount(view) runs once, on the first visit; show(params, view) on every visit (and after a reconnect).
export function registerScreen(name, { mount, show } = {}) { screens.set(name, { mount, show, mounted: false }); }

function render() {
  const stack = router.stack, { name, params } = stack[stack.length - 1];
  const view = document.getElementById('view-' + name);
  if (!view) return;
  $$('.view').forEach(v => v.classList.toggle('on', v === view));
  $('#app').dataset.tabs = IMMERSIVE.includes(name) ? 'off' : 'on';
  const tabs = $$('.tab'), names = tabs.map(t => t.dataset.open.replace(/^view-/, ''));
  const lit = names.includes(name) ? name : stack[0].name;
  tabs.forEach((t, i) => t.classList.toggle('on', names[i] === lit));
  closeOverlays();
  showScreen(name, params, view);
}

function showScreen(name, params, view) {
  const s = screens.get(name);
  if (!s) return;
  if (!s.mounted) { s.mounted = true; s.mount?.(view); }
  s.show?.(params, view);
}

export function go(name, params = {}) { router.go({ name: name.replace(/^view-/, ''), params }); render(); }
export function back() { if (router.stack.length > 1) history.back(); else render(); }
addEventListener('popstate', e => { router.pop(e.state); render(); });
// Replaces the top screen (its history entry too) and shows it.
export function replace(name, params = {}) { router.reset([...router.stack.slice(0, -1), { name, params }]); render(); }
function tabTo(name) { router.reset([{ name: name.replace(/^view-/, ''), params: {} }]); render(); }

// iOS Safari ignores user-scalable=no: a pinch is cancelled here, so the app never zooms (touch-action covers double-tap).
document.addEventListener('gesturestart', e => e.preventDefault());

document.addEventListener('click', e => {
  const el = e.target.closest('[data-open],[data-go],[data-back],[data-drawer],[data-sheet],[data-close],[data-newchat],[data-retry]');
  if (!el) return;
  if (el.dataset.go) go(el.dataset.go);
  else if (el.classList.contains('tab')) tabTo(el.dataset.open);
  else if (el.dataset.open) go(el.dataset.open);
  else if (el.hasAttribute('data-back')) back();
  else if (el.hasAttribute('data-drawer')) toggleDrawer();
  else if (el.hasAttribute('data-newchat')) { router.reset([{ name: 'chats', params: {} }, { name: 'chat', params: { id: null } }]); render(); }
  else if (el.dataset.sheet) openSheet(el.dataset.sheet);
  else if (el.hasAttribute('data-close')) closeOverlays();
  else if (el.hasAttribute('data-retry')) connect();
});

/* ---------- overlays ---------- */
function toggleDrawer() {
  const open = !$('#drawer').classList.contains('open');
  closeOverlays();
  if (open) { $('#drawer').classList.add('open'); $('#drawer').setAttribute('aria-hidden', 'false'); $('#scrim').classList.add('on'); }
}
export function openSheet(id) {
  closeOverlays();
  $('#' + id).classList.add('open');
  $('#scrim').classList.add('on');
  const field = $('#' + id).querySelector('input');
  if (field) setTimeout(() => field.focus({ preventScroll: true }), 120);
}
export function closeOverlays() {
  $('#drawer').classList.remove('open');
  $('#drawer').setAttribute('aria-hidden', 'true');
  $$('.sheet').forEach(s => s.classList.remove('open'));
  $('#scrim').classList.remove('on');
}
$('#scrim').addEventListener('click', closeOverlays);

// The confirm sheet: a title, its text, a red button (calls onYes; "Delete" with the bin, or `yes` without it) and Cancel.
let onConfirm = null;
export function confirmSheet(title, text, onYes, yes = 'Delete') {
  $('#sheet-confirm .confirm-title').textContent = title;
  $('#sheet-confirm .confirm-text').textContent = text;
  $('#confirmyes span').textContent = yes;
  $('#confirmyes svg').style.display = yes === 'Delete' ? '' : 'none';
  onConfirm = onYes;
  openSheet('sheet-confirm');
}
$('#confirmyes').addEventListener('click', () => { const yes = onConfirm; onConfirm = null; closeOverlays(); yes?.(); });

// Deletes a chat on the PC once confirmed, then shows Chats again (its list reloads); a refusal (409) is the server's toast.
export function askDeleteChat(id, title) {
  confirmSheet('Delete chat', `Delete "${title}"? This can't be undone.`, async () => {
    try { await api.post('/api/conversation/' + encodeURIComponent(id) + '/delete'); } catch (err) { fail(err); return; }
    tabTo('chats');
  });
}

// The name sheet: a title, a text field holding `value` and a button (`yes`); onSave(the trimmed text) runs once it is pressed
// (or Enter). Used for renaming chats and projects and naming a new project.
let onName = null;
export function askName(title, value, onSave, yes = 'Save') {
  $('#sheet-name .secthead').textContent = title;
  $('#sheet-name input').value = value || '';
  $('#sheet-name button').textContent = yes;
  onName = onSave;
  openSheet('sheet-name');
}
$('#sheet-name').addEventListener('submit', e => {
  e.preventDefault();
  const save = onName, text = $('#sheet-name input').value.trim();
  onName = null;
  closeOverlays();
  save?.(text);
});

let toastTimer;
export function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.add('on');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('on'), 2000);
}

/* ---------- switches, segments, radios, chips, disclosure (visuals only) ---------- */
// A segment bar records its choice in data-value and fires a bubbling 'change' event for the screen that owns it.
document.addEventListener('click', e => {
  const sw = e.target.closest('[data-switch]');
  if (sw) { sw.classList.toggle('on'); return; }

  const segBtn = e.target.closest('.seg > button');
  if (segBtn) {
    const seg = segBtn.parentElement;
    $$('button', seg).forEach(b => b.classList.toggle('on', b === segBtn));
    seg.dataset.value = segBtn.dataset.segOn ?? segBtn.textContent.trim();
    seg.dispatchEvent(new Event('change', { bubbles: true }));
    return;
  }

  const radio = e.target.closest('[data-radio]');
  if (radio) { $$('[data-radio]', radio.closest('[data-radio-group]') || document).forEach(r => r.classList.toggle('on', r === radio)); return; }

  const tick = e.target.closest('[data-tick]');
  if (tick) { tick.classList.toggle('on'); return; }

  const chip = e.target.closest('.chip[data-toggle]');
  if (chip) { chip.classList.toggle('on'); return; }

  const unfold = e.target.closest('[data-disclose]');
  if (unfold) { unfold.classList.toggle('open'); $('#' + unfold.dataset.disclose).classList.toggle('hidden'); }
});

/* ---------- composer: a resting pill until tapped, grows with its text (composer.js: send, the + sheet, / completion) ---------- */
$$('.composer').forEach(footer => {
  const box = footer.querySelector('textarea'), wrap = footer.querySelector('.inputwrap');
  if (!box || !wrap) return;
  const collapse = () => { if (document.activeElement !== box && !box.value.trim()) footer.classList.add('collapsed'); };
  footer.classList.add('collapsed');
  wrap.addEventListener('click', () => { if (footer.classList.contains('collapsed')) { footer.classList.remove('collapsed'); box.focus(); } });
  box.addEventListener('focus', () => footer.classList.remove('collapsed'));
  box.addEventListener('blur', () => setTimeout(collapse, 120));
  box.addEventListener('input', () => {
    box.style.height = 'auto';
    box.style.height = Math.min(96, box.scrollHeight) + 'px';
  });
});

/* ---------- iOS keyboard: the shell fits the visible part above it, so the header stays and no gap opens over the keyboard ---------- */
const vv = window.visualViewport;
if (vv) {
  const fitShell = () => {
    const box = keyboardBox(vv, document.documentElement.clientHeight), style = $('.shell').style;
    style.height = box ? box.height + 'px' : ''; // '' restores the stylesheet's 100dvh / 100lvh
    style.transform = box ? `translateY(${box.top}px)` : '';
  };
  vv.addEventListener('resize', fitShell);
  vv.addEventListener('scroll', fitShell);
}

/* ---------- landscape: a phone on its side (short and wide) gets the rail layout ---------- */
const landscape = matchMedia('(orientation: landscape) and (max-height: 560px)');
const applyLandscape = () => document.body.classList.toggle('landscape', landscape.matches);
landscape.addEventListener('change', applyLandscape);
applyLandscape();

/* ---------- the PC: /api/hello, the banners, retry ---------- */
let retryWait = 1000, retryTimer = 0, connected = false;
// A switch, retry or Retry during a connect supersedes it: the earlier PC's late hello (or failure) is dropped.
const hello = latestOnly(() => api.get('/api/hello'));

// For every module's API errors: the signed-out 403 reloads once, so the server answers with the signed-out page (the flag,
// cleared by a good /api/hello, stops a loop); unreachable shows the connection-lost banner and retries; anything else is a toast.
const RELOADED = 'ao.signedOutReload';
export function fail(err) {
  const action = failAction(err, sessionStorage.getItem(RELOADED) !== null);
  if (action === 'reload') { sessionStorage.setItem(RELOADED, '1'); location.reload(); }
  else if (action === 'lost') lost();
  else if (err?.status === 409 && err.message === 'DialogOpen') toast('The PC is waiting on a dialog');
  else toast(err?.message === 'SignedOut' ? 'Signed out of Tailscale' : err?.message || String(err));
}

function lost() {
  $('#bannerLost').classList.add('on');
  drawOtherPcs();
  if (retryTimer) return;
  retryTimer = setTimeout(connect, retryWait);
  retryWait = nextWait(retryWait);
}

async function connect() {
  clearTimeout(retryTimer);
  retryTimer = 0;
  let me;
  try { me = await hello(); } catch (err) { fail(err); return; }
  sessionStorage.removeItem(RELOADED);
  clearTimeout(retryTimer);
  retryTimer = 0;
  retryWait = 1000;
  $('#bannerLost').classList.remove('on');
  $$('[data-pc-subtitle], [data-pc-name]').forEach(el => { el.textContent = me.pc; });
  $('#meName').textContent = me.user || 'Tailscale';
  $('#meAvatar').textContent = (me.user || '?')[0].toUpperCase();
  $('#meTailnet').textContent = tailnetOf(me.dnsName);
  api.get('/api/pcs').then(rememberPcs, () => {});
  document.dispatchEvent(new Event('ao:hello')); // connected (again): settings.js applies the PC's accent
  if (!connected) { connected = true; render(); return; }
  const { name, params } = router.stack[router.stack.length - 1]; // back after a loss: the screen reloads its data
  showScreen(name, params, document.getElementById('view-' + name));
}

const applyOnline = () => {
  $('#bannerOffline').classList.toggle('on', !navigator.onLine);
  if (navigator.onLine && retryTimer) connect();
};
addEventListener('online', applyOnline);
addEventListener('offline', applyOnline);

/* ---------- the PCs: api.base is the PC the app talks to ('' = this page's own) ---------- */
const LAST_PC = 'ao.lastPc', PCS = 'ao.pcs';
// The owner's PCs from the last good /api/pcs, for the connection-lost banner when the PC the app talks to stops answering.
// A remembered PC the list no longer has is forgotten.
export const rememberPcs = pcs => {
  localStorage.setItem(PCS, JSON.stringify(pcs));
  if (gone(localStorage.getItem(LAST_PC), pcs)) localStorage.removeItem(LAST_PC);
  return pcs;
};

// Talks to another PC in place, no navigation: its API, then Chats, the titles and the drawer's ticks come from it; an open
// chat's stream closes (chat.js, on 'ao:pc'). Remembered for the next start.
export function usePc(dnsName, name) {
  localStorage.setItem(LAST_PC, dnsName.replace(/\.$/, ''));
  api.base = baseOf(dnsName, location.host);
  document.dispatchEvent(new Event('ao:pc'));
  $$('[data-pc-subtitle], [data-pc-name]').forEach(el => { el.textContent = name; });
  router.reset([{ name: 'chats', params: {} }]);
  render();
  connect();
}

// The connection-lost banner's other PCs (any but the one not answering), each a tap away.
function drawOtherPcs() {
  let pcs;
  try { pcs = JSON.parse(localStorage.getItem(PCS)) || []; } catch { pcs = []; }
  const others = pcs.filter(pc => pc.dnsName && baseOf(pc.dnsName, location.host) !== api.base);
  $('#lostPcs').replaceChildren(...(others.length ? [h('span', {}, 'Switch to'),
    ...others.map(pc => h('button', { class: 'ghosttiny', onclick: () => usePc(pc.dnsName, pc.pc) }, pc.pc))] : []));
}

// The service worker (sw.js) comes from this page's own PC, so its shell (a hash of the PC's web/ files) is that PC's, whichever
// PC the app talks to. A new
// one takes over at once; the page then reloads, so the index (network-first) and its scripts come from the same publish.
function registerWorker() {
  const sw = navigator.serviceWorker;
  if (!sw) return;
  const had = !!sw.controller;
  sw.addEventListener('controllerchange', () => { if (had) location.reload(); });
  fetch('/api/hello').then(r => r.ok ? r.json() : Promise.reject())
    .then(me => sw.register('sw.js?v=' + encodeURIComponent(me.shell), { type: 'module' }))
    .catch(err => console.warn('Service worker not registered:', err));
}

// On start: the remembered PC if it answers within 5 s, else this one; one that refuses (an HTTP answer) is forgotten.
document.addEventListener('DOMContentLoaded', async () => {
  applyOnline();
  const probe = base => fetch(base + '/api/hello', { credentials: 'omit', signal: AbortSignal.timeout(5000) })
    .then(r => r.ok || Promise.reject({ status: r.status }));
  api.base = await startBase(localStorage.getItem(LAST_PC), location.host, probe, () => localStorage.removeItem(LAST_PC));
  connect();
  registerWorker();
});
