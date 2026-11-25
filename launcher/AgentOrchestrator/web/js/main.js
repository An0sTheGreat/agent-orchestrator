// The app shell: screens, the drawer, sheets, segments and switches, the landscape layout and the connection to the PC.
// Screen modules load as their own <script type="module"> after this one and call registerScreen at their top level;
// start() runs on DOMContentLoaded, after every module script has run.
import { api, nextWait, tailnetOf, failAction } from './api.js';
import { screensToPop } from './util.js';

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const IMMERSIVE = ['chat']; // no tab bar in portrait
const screens = new Map(); // name -> {mount, show, mounted}
let stack = [{ name: 'chats', params: {} }];

/* ---------- screens ---------- */
// mount(view) runs once, on the first visit; show(params, view) on every visit (and after a reconnect).
export function registerScreen(name, { mount, show } = {}) { screens.set(name, { mount, show, mounted: false }); }

function render() {
  const { name, params } = stack[stack.length - 1];
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

// Every go is a browser history entry, so the phone's back gesture goes back one screen (popstate) and the Back
// button goes through history too, keeping the two in step. At the root a back gesture pops nothing.
let depth = history.state?.depth ?? 0; // our place in the tab's history (kept across a reload)
export function go(name, params = {}) {
  stack.push({ name: name.replace(/^view-/, ''), params });
  history.pushState({ depth: ++depth }, '');
  render();
}
export function back() { if (stack.length > 1) history.back(); else render(); }
addEventListener('popstate', e => {
  const to = e.state?.depth ?? 0, n = screensToPop(stack.length, depth, to);
  depth = to;
  if (n) { stack.splice(-n); render(); } else closeOverlays();
});
function tabTo(name) { stack = [{ name: name.replace(/^view-/, ''), params: {} }]; render(); }

document.addEventListener('click', e => {
  const el = e.target.closest('[data-open],[data-go],[data-back],[data-drawer],[data-sheet],[data-close],[data-newchat],[data-retry]');
  if (!el) return;
  if (el.dataset.go) go(el.dataset.go);
  else if (el.classList.contains('tab')) tabTo(el.dataset.open);
  else if (el.dataset.open) go(el.dataset.open);
  else if (el.hasAttribute('data-back')) back();
  else if (el.hasAttribute('data-drawer')) toggleDrawer();
  else if (el.hasAttribute('data-newchat')) { stack = [{ name: 'chats', params: {} }]; go('chat', { id: null }); }
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

let toastTimer;
export function toast(msg) {
  const t = $('#toast');
  t.textContent = msg;
  t.classList.add('on');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('on'), 2000);
}

/* ---------- switches, segments, radios, chips, accents, disclosure (visuals only) ---------- */
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

  const acc = e.target.closest('[data-accent]');
  if (acc) {
    const [a, light, on, name] = acc.dataset.accent.split('|');
    const root = document.documentElement;
    root.style.setProperty('--accent', a);
    root.style.setProperty('--accent-light', light);
    root.style.setProperty('--on-accent', on);
    $$('.acc').forEach(x => x.classList.toggle('on', x === acc));
    const label = $('#accentname');
    if (label) label.textContent = name;
    return;
  }

  const unfold = e.target.closest('[data-disclose]');
  if (unfold) { unfold.classList.toggle('open'); $('#' + unfold.dataset.disclose).classList.toggle('hidden'); }
});

/* ---------- composer: a resting pill until tapped, grows with its text, / or $ opens the skill list ---------- */
$$('.composer').forEach(footer => {
  const box = footer.querySelector('textarea'), wrap = footer.querySelector('.inputwrap'), pop = footer.querySelector('.skillpop');
  if (!box || !wrap) return;
  const collapse = () => { if (document.activeElement !== box && !box.value.trim()) footer.classList.add('collapsed'); };
  footer.classList.add('collapsed');
  wrap.addEventListener('click', () => { if (footer.classList.contains('collapsed')) { footer.classList.remove('collapsed'); box.focus(); } });
  box.addEventListener('focus', () => footer.classList.remove('collapsed'));
  box.addEventListener('blur', () => setTimeout(collapse, 120));
  box.addEventListener('input', () => {
    box.style.height = 'auto';
    box.style.height = Math.min(96, box.scrollHeight) + 'px';
    pop?.classList.toggle('hidden', !/^[/$]$/.test(box.value));
  });
  footer.addEventListener('click', e => {
    const pick = e.target.closest('[data-complete]');
    if (!pick) return;
    box.value = pick.dataset.complete + ' ';
    box.focus();
    pop?.classList.add('hidden');
  });
});

/* ---------- landscape: a phone on its side (short and wide) gets the rail layout ---------- */
const landscape = matchMedia('(orientation: landscape) and (max-height: 560px)');
const applyLandscape = () => document.body.classList.toggle('landscape', landscape.matches);
landscape.addEventListener('change', applyLandscape);
applyLandscape();

/* ---------- the PC: /api/hello, the banners, retry ---------- */
let retryWait = 1000, retryTimer = 0, connected = false;

// For every module's API errors: the signed-out 403 reloads once, so the server answers with the signed-out page (the flag,
// cleared by a good /api/hello, stops a loop); unreachable shows the connection-lost banner and retries; anything else is a toast.
const RELOADED = 'ao.signedOutReload';
export function fail(err) {
  const action = failAction(err, sessionStorage.getItem(RELOADED) !== null);
  if (action === 'reload') { sessionStorage.setItem(RELOADED, '1'); location.reload(); }
  else if (action === 'lost') lost();
  else toast(err?.message === 'SignedOut' ? 'Signed out of Tailscale' : err?.message || String(err));
}

function lost() {
  $('#bannerLost').classList.add('on');
  if (retryTimer) return;
  retryTimer = setTimeout(connect, retryWait);
  retryWait = nextWait(retryWait);
}

async function connect() {
  clearTimeout(retryTimer);
  retryTimer = 0;
  let me;
  try { me = await api.get('/api/hello'); } catch (err) { fail(err); return; }
  sessionStorage.removeItem(RELOADED);
  clearTimeout(retryTimer);
  retryTimer = 0;
  retryWait = 1000;
  $('#bannerLost').classList.remove('on');
  $$('[data-pc-subtitle], [data-pc-name]').forEach(el => { el.textContent = me.pc; });
  $('#meName').textContent = me.user || 'Tailscale';
  $('#meAvatar').textContent = (me.user || '?')[0].toUpperCase();
  $('#meTailnet').textContent = tailnetOf(me.dnsName);
  if (!connected) { connected = true; render(); return; }
  const { name, params } = stack[stack.length - 1]; // back after a loss: the screen reloads its data
  showScreen(name, params, document.getElementById('view-' + name));
}

const applyOnline = () => {
  $('#bannerOffline').classList.toggle('on', !navigator.onLine);
  if (navigator.onLine && retryTimer) connect();
};
addEventListener('online', applyOnline);
addEventListener('offline', applyOnline);

document.addEventListener('DOMContentLoaded', () => { applyOnline(); connect(); });
