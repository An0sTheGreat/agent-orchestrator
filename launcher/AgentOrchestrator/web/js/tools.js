// The Code screen's tools: the PC's terminal (the card in Files) and the model's browser pane (the Browser pane). Each polls only
// while it is on screen (an IntersectionObserver: a hidden pane, another screen or the card scrolled away all stop it) and the
// phone is in front; a PC switch ('ao:pc') stops both and forgets the terminal's output.
// The pure helpers below are Node-tested; the DOM part loads only in a page (code.js imports this module).
import { api } from './api.js';
import { h } from './h.js';

/* ---------- pure ---------- */
export const TERM_LINES = 2000;

// The terminal as text: finished `lines`, the cursor's `line` and column, and `rest` (an escape cut off at a chunk's end).
export const termEmpty = () => ({ lines: [], line: '', col: 0, rest: '' });
export const termText = t => [...t.lines, t.line].join('\n');

// A chunk of PowerShell output fed into the terminal: escape codes dropped (CSI K erases to the line's end, C / D move the
// cursor, the rest are ignored), \r back to the line's start, backspace one left, \n a new line; the last TERM_LINES kept.
const TOKEN = /([^\x00-\x08\x0a-\x1f\x7f]+)|(\r)|(\n)|(\x08)|\x1b\[([0-?]*)[ -/]*([@-~])|\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)|\x1b[ -/]*[0-~]|[\x00-\x1f\x7f]/g;
const CUT = /\x1b(?:\[[0-?]*[ -/]*|\][^\x07\x1b]*\x1b?|[ -/]*)$/;
export function termFeed(t, chunk) {
  let s = t.rest + String(chunk ?? '');
  const cut = s.match(CUT);
  const rest = cut ? cut[0] : '';
  if (cut) s = s.slice(0, cut.index);
  let lines = t.lines.slice(), line = t.line, col = t.col;
  for (const m of s.matchAll(TOKEN)) {
    const [, text, cr, lf, bs, args, op] = m;
    if (text) { line = line.slice(0, col).padEnd(col) + text + line.slice(col + text.length); col += text.length; }
    else if (cr) col = 0;
    else if (lf) { lines.push(line); line = ''; col = 0; }
    else if (bs) col = Math.max(0, col - 1);
    else if (op === 'K' && !args.replace('0', '')) line = line.slice(0, col);
    else if (op === 'C') col += parseInt(args, 10) || 1;
    else if (op === 'D') col = Math.max(0, col - (parseInt(args, 10) || 1));
  }
  if (lines.length > TERM_LINES - 1) lines = lines.slice(-(TERM_LINES - 1));
  return { lines, line, col, rest };
}

// A GET /api/terminal?since= answer {seq, text} merged into the terminal read up to `since`: a seq behind it means a fresh
// buffer on the PC (its output starts over); the next poll asks from the answer's seq.
export function termMerge(t, since, r) {
  const seq = Number(r?.seq) || 0;
  return { t: termFeed(seq < since ? termEmpty() : t, r?.text || ''), since: seq };
}

// A tap at (clientX, clientY) on the screenshot drawn in `rect` ({left, top, width, height}) → that point in the shot's own
// pixels (w × h, the image's natural size), inside it.
export function shotPoint(clientX, clientY, rect, w, h) {
  const at = (v, from, size, n) => Math.min(n - 1, Math.max(0, Math.round((v - from) * n / (size || 1))));
  return { x: at(clientX, rect.left, rect.width, w), y: at(clientY, rect.top, rect.height, h) };
}

// The URL bar: a tab's address shown without https:// (and a bare host's trailing /); what the user types goes to the PC as
// typed (trimmed), which decides between a URL and a search.
export const barUrl = url => String(url || '').replace(/^https:\/\//i, '').replace(/^((?:[a-z][a-z0-9+.-]*:\/\/)?[^/?#]+)\/$/i, '$1');
export const typedUrl = text => String(text || '').trim();

export const TERM_KEYS = [['Ctrl+C', '\x03'], ['Tab', '\t'], ['↑', '\x1b[A'], ['↓', '\x1b[B'], ['Esc', '\x1b']];
export const PAGE_KEYS = [['Enter', 'Enter'], ['⌫', 'Backspace'], ['Tab', 'Tab'], ['Esc', 'Escape'],
  ['←', 'ArrowLeft'], ['↑', 'ArrowUp'], ['↓', 'ArrowDown'], ['→', 'ArrowRight']];

/* ---------- the DOM part ---------- */
if (typeof document !== 'undefined') {
  const { fail, confirmSheet } = await import('./main.js');
  const $ = s => document.querySelector(s);
  let pc = 0; // bumped on a PC switch: answers from the earlier PC are dropped
  document.addEventListener('ao:pc', () => { pc++; term.stop(); term.reset(); page.stop(); });

  // A poller: `fn` now and every `ms` while `el` is on screen and the phone shows the page.
  function poller(el, ms, fn) {
    let timer = 0, seen = false;
    const run = () => { clearTimeout(timer); if (!seen) return; if (!document.hidden) fn(); timer = setTimeout(run, ms); };
    const io = new IntersectionObserver(es => { seen = es[es.length - 1].isIntersecting; if (seen) run(); else clearTimeout(timer); });
    io.observe(el);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && seen) run(); });
    return { now: run, stop: () => { clearTimeout(timer); seen = false; io.unobserve(el); io.observe(el); } }; // observing again reports the element's state: still on screen restarts the poll
  }

  /* the terminal card */
  const term = (() => {
    const out = $('#termtext'), input = $('#terminput');
    let t = termEmpty(), since = 0, running = false, busy = false;
    async function pull() {
      if (busy) return;
      busy = true;
      const mine = pc;
      let r;
      try { r = await api.get('/api/terminal?since=' + since); } catch (err) { busy = false; if (mine === pc && err.status !== 404) fail(err); return; }
      busy = false;
      if (mine !== pc) return;
      const before = termText(t);
      ({ t, since } = termMerge(t, since, r));
      running = !!r.running;
      $('#termcwd').textContent = running && r.cwd ? r.cwd + '>' : '';
      $('#termhead').textContent = 'TERMINAL · PowerShell' + (r.cwd ? ' in ' + r.cwd.replace(/[\\/]+$/, '').split(/[\\/]/).pop() : '');
      $('#termnew').textContent = running ? 'New' : 'Start';
      $('#termnote').hidden = running;
      input.disabled = !running;
      const text = termText(t);
      if (text === before) return;
      const stick = out.scrollHeight - out.scrollTop - out.clientHeight < 24;
      out.textContent = text;
      if (stick) out.scrollTop = out.scrollHeight;
    }
    const poll = poller($('#termcard'), 700, pull);
    async function send(data) {
      try { await api.post('/api/terminal/input', { data }); } catch (err) { fail(err); return; }
      poll.now();
    }
    input.addEventListener('keydown', e => {
      if (e.key !== 'Enter' || e.isComposing) return;
      e.preventDefault();
      const data = input.value + '\r';
      input.value = '';
      send(data);
    });
    // Tab sends what is typed first, so PowerShell completes it.
    $('#termkeys').replaceChildren(...TERM_KEYS.map(([label, key]) => h('button', { class: 'chip', onclick: () => {
      const data = (key === '\t' ? input.value : '') + key;
      if (key === '\t') input.value = '';
      send(data);
    } }, label)));
    const start = async restart => {
      try { await api.post('/api/terminal/new', restart ? { restart: true } : {}); } catch (err) { fail(err); return; }
      poll.now();
    };
    $('#termnew').addEventListener('click', () => running
      ? confirmSheet('New terminal', 'End the running PowerShell on the PC and start a new one?', () => start(true), 'New')
      : start(false));
    return { stop: poll.stop, reset() { t = termEmpty(); since = 0; out.textContent = ''; } };
  })();

  /* the browser pane */
  const page = (() => {
    const img = $('#bshot'), bar = $('#burl'), ring = $('#bpage .clickring');
    let state = null, shotUrl = '', loading = false, ringTimer = 0;
    async function shot() {
      if (loading) return;
      loading = true;
      const mine = pc;
      let blob = null;
      try {
        const res = await fetch(api.base + '/api/browser/shot', { credentials: 'omit', cache: 'no-store' });
        if (res.ok) blob = await res.blob();
      } catch { /* the state poll reports a lost PC */ }
      loading = false;
      if (mine !== pc) return;
      if (shotUrl) URL.revokeObjectURL(shotUrl);
      shotUrl = blob ? URL.createObjectURL(blob) : '';
      img.hidden = !blob;
      if (blob) img.src = shotUrl; else img.removeAttribute('src');
      $('#bnone').hidden = !!blob;
    }
    function draw() {
      const tabs = state.tabs || [], on = tabs.find(t => t.shown);
      $('#btabs').replaceChildren(...tabs.map(t => h('span', { class: t.shown ? 'btab on' : 'btab' },
        h('button', { onclick: () => act('tab', { index: t.index }) }, t.title || barUrl(t.url) || 'New tab'),
        h('button', { class: 'btabx', 'aria-label': 'Close tab', onclick: () => act('close', { index: t.index }) }, '✕'))),
        h('button', { class: 'iconbtn sm', 'aria-label': 'New tab', onclick: () => act('newtab') }, '+'));
      if (document.activeElement !== bar) bar.value = barUrl(on?.url);
      $('#bnone').textContent = state.open ? 'No page to show yet.' : 'The browser pane is closed on the PC. + opens a tab.';
      $('#bfollow').classList.toggle('on', !!state.follow);
      $('#binput').classList.toggle('on', !!state.input);
    }
    async function pull() {
      const mine = pc;
      let s;
      try { s = await api.get('/api/browser'); } catch (err) { if (mine === pc) fail(err); return; }
      if (mine !== pc) return;
      state = s;
      draw();
      shot();
    }
    const poll = poller($('#bpage'), 3000, pull);
    async function act(verb, body) {
      try { await api.post('/api/browser/' + verb, body); } catch (err) { fail(err); return; }
      poll.now();
    }
    bar.addEventListener('focus', () => { bar.value = state?.tabs?.find(t => t.shown)?.url || bar.value; bar.select(); });
    bar.addEventListener('blur', () => { if (state) draw(); });
    bar.addEventListener('keydown', e => {
      if (e.key !== 'Enter' || e.isComposing) return;
      e.preventDefault();
      const url = typedUrl(bar.value);
      if (url) { bar.blur(); act('navigate', { url }); }
    });
    $('#bback').addEventListener('click', () => act('back'));
    $('#bfwd').addEventListener('click', () => act('forward'));
    $('#breload').addEventListener('click', () => act('reload'));
    img.addEventListener('click', e => {
      const r = img.getBoundingClientRect(), box = $('#bpage').getBoundingClientRect();
      ring.style.left = (e.clientX - box.left) + 'px';
      ring.style.top = (e.clientY - box.top) + 'px';
      ring.hidden = false;
      clearTimeout(ringTimer);
      ringTimer = setTimeout(() => { ring.hidden = true; }, 1200);
      act('tap', shotPoint(e.clientX, e.clientY, r, img.naturalWidth, img.naturalHeight));
    });
    const type = () => { const text = $('#btype').value; if (!text) return; $('#btype').value = ''; act('type', { text }); };
    $('#btypesend').addEventListener('click', type);
    $('#btype').addEventListener('keydown', e => { if (e.key === 'Enter' && !e.isComposing) { e.preventDefault(); type(); } });
    $('#bkeys').replaceChildren(...PAGE_KEYS.map(([label, key]) => h('button', { class: 'chip', onclick: () => act('key', { key }) }, label)));
    // main.js has flipped the switch already; a refusal flips it back.
    for (const [sel, name] of [['#bfollow', 'follow'], ['#binput', 'input']]) {
      $(sel).addEventListener('click', async e => {
        const sw = e.currentTarget, on = sw.classList.contains('on');
        try { await api.post('/api/browser/options', { [name]: on }); } catch (err) { sw.classList.toggle('on', !on); fail(err); }
      });
    }
    return { stop: poll.stop };
  })();
}
