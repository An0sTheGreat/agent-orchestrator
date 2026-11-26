// The Chat screen: a conversation's messages and its live reply (GET /api/conversation/{id} and its events), Stop, the queue
// chip, the approval card and the context ring with its popover. The composer (send, the + sheet) is Part 7b's.
// applyEvent, emptyLive and ringColor are pure (Node tests them); the DOM part loads only in a page.
import { api } from './api.js';
import { h } from './h.js';
import { renderMarkdown } from './markdown.js';
import { ago } from './util.js';

/* ---------- the live reply: pure ---------- */
// parts, in order: {t:'text'|'thinking', text}, {t:'tool', name, args, result}, {t:'image', src}, {t:'stage', title}.
// The engine's deltas carry no section breaks: a text delta after a tool row (or anything not text) starts a new section.
// refetch: load the conversation again. reopen: close the stream and open it again (a fresh hello replaces the parts).
export const emptyLive = () => ({ busy: false, queued: 0, prompt: null, parts: [], context: null, refetch: false, reopen: false, turn: 0, epoch: 0 });

const picture = path => '/api/file?path=' + encodeURIComponent(path);
const SAVED = /\bSaved: ([^\r\n]+?)\. Prompt: /g; // generate_image's line (ImageGen.ChatResult) names the saved picture

export function applyEvent(s, ev) {
  const last = s.parts[s.parts.length - 1];
  const tag = p => ({ ...p, turn: s.turn }); // every part belongs to a turn; `saved` closes the turn, so a closed turn's part never grows
  const append = (t, text) => last?.t === t && last.turn === s.turn
    ? { ...s, parts: [...s.parts.slice(0, -1), tag({ t, text: last.text + text })] }
    : { ...s, parts: [...s.parts, tag({ t, text })] };
  switch (ev.e) {
    case 'hello': // refetch too: a turn saved between the first GET and the subscription is in neither, and no `saved` follows
      return { ...s, refetch: true, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null, parts: ev.reply?.text ? [{ t: 'text', text: ev.reply.text, turn: 0 }] : [], turn: 0, epoch: s.epoch + 1 };
    case 'state':
      return { ...s, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null };
    case 'delta':
      return ev.kind === 'text' || ev.kind === 'thinking' ? append(ev.kind, ev.text || '') : s; // 'tool': a call's arguments streaming
    case 'tool': {
      const pics = [...String(ev.result || '').matchAll(SAVED)].map(m => ({ t: 'image', src: picture(m[1]) }));
      const args = typeof ev.args === 'string' ? ev.args : JSON.stringify(ev.args ?? '');
      return { ...s, parts: [...s.parts, tag({ t: 'tool', name: ev.name || '', args, result: ev.result || '' }), ...pics.map(tag)] };
    }
    case 'chat_stage':
      return ev.stage === 'delegate' ? { ...s, parts: [...s.parts, tag({ t: 'stage', title: ev.title || ev.role || '' })] } : s;
    case 'context': {
      const nCtx = ev.n_ctx > 0 ? ev.n_ctx : null, used = (ev.prompt_tokens || 0) + (ev.completion_tokens || 0);
      return { ...s, context: { nCtx, used, fraction: nCtx ? Math.min(1, used / nCtx) : 0, parts: ev.parts || {} } };
    }
    case 'saved':
      return { ...s, refetch: true, turn: s.turn + 1 };
    case 'resync': // the server dropped events (the reply has a hole): reopen, so the new connection's hello replaces the parts
      return { ...s, refetch: true, reopen: true };
    default:
      return s;
  }
}

// A `saved` event makes a cut: the turn it closed, in the connection (epoch) it happened in. The load it triggers lands it with landSaved.
export const cutOf = (s, ev) => ev.e === 'saved' ? { turn: s.turn - 1, epoch: s.epoch } : null;
// The loaded messages hold every closed turn's reply: drop those parts. A cut from before a reconnect (hello) drops nothing.
export const landSaved = (s, cut) => cut.epoch !== s.epoch ? s : { ...s, parts: s.parts.filter(p => p.turn > cut.turn) };
// Loads can land out of order: only a response newer than the last applied one may replace the messages.
export const freshLoad = (applied, seq) => seq > applied;
// What a landed load changes (null: it was outrun, so it writes nothing, title included).
export const landLoad = (applied, seq, conv, context) => !freshLoad(applied, seq) ? null : {
  messages: conv.messages, context: conv.context ?? context, title: conv.title || 'Chat',
  sub: conv.worker ? `${conv.kind === 'code' ? 'Code' : 'Chat'} · to ${conv.worker}` : '' };
// The approval card the user answered stays done across redraws, until a new prompt (new nonce) arrives.
export const approvalDone = (answered, prompt) => answered != null && answered === prompt.nonce;

// The ring's colour: blue under 50%, amber from 50%, red from 90%.
export const ringColor = f => f >= 0.9 ? 'red' : f >= 0.5 ? 'amber' : 'blue';
const RING = { blue: '#6EA8FF', amber: '#F7C873', red: '#FF6B6B' };
// The popover's rows, in bar order (ContextSnapshot.Categories); reply = what the parts leave of `used`.
const PARTS = [['system', 'System prompt', '#C88705'], ['tools', 'Tools', '#DD6122'], ['skills', 'Skills', '#8E8E8E'],
  ['messages', 'Messages', '#3085E2'], ['tool_results', 'Tool results', '#00A082'], ['reply', 'Reply', '#595959']];

/* ---------- the screen ---------- */
export let chatId = null; // the open chat (null: a new one), for the composer
let replaceScreen = () => {};
// A new chat's first send made it: the Chat screen's history entry becomes that chat, shown.
export const follow = id => replaceScreen('chat', { id });

if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, replace } = await import('./main.js');
  replaceScreen = replace;
  const $ = s => document.querySelector(s);
  const clone = id => document.getElementById(id).content.firstElementChild.cloneNode(true);
  const view = $('#view-chat'), msgs = $('#view-chat .msgs'), pop = $('#ctxpop');
  let id = null, live = emptyLive(), saved = [], liveEls = [], context = null, stopEvents = () => {}, gen = 0, frame = 0, loadSeq = 0, applied = 0, answered = null;

  const fmt = n => n.toLocaleString('en-US');
  const img = src => h('img', { class: 'pic', src: api.base + src, alt: '', loading: 'lazy', // a path on the PC the app talks to
    onerror: e => e.currentTarget.replaceWith(h('span', { class: 'pic-missing' }, 'Picture not available')) });
  const copyBtn = (el, text) => el.querySelector('[data-copy]').addEventListener('click', () =>
    navigator.clipboard.writeText(text).then(() => toast('Copied'), () => toast('Could not copy')));

  function message(m) {
    const user = m.role === 'user', el = clone(user ? 'tpl-msg-user' : 'tpl-msg-reply');
    const body = el.querySelector(user ? '.bubble' : '.md');
    if (user) body.textContent = m.text; else body.append(renderMarkdown(m.text || ''));
    body.after(...(m.images || []).map(img));
    if (!user) { const who = el.querySelector('.who'); who.textContent = (m.worker || '').toUpperCase(); who.hidden = !m.worker; }
    el.querySelector('.when').textContent = [m.time && ago(m.time), m.stats].filter(Boolean).join(' · ');
    copyBtn(el, m.text || '');
    return el;
  }

  function part(p, key) {
    if (p.t === 'text') return h('div', { class: 'md' }, renderMarkdown(p.text));
    if (p.t === 'image') return img(p.src);
    if (p.t === 'thinking') {
      const el = clone('tpl-thinking');
      el.dataset.key = key; // which part this fold is, so an opened fold stays open across live redraws
      el.querySelector('.fold-text').textContent = p.text.trim().split('\n')[0];
      el.querySelector('.fold-body').append(renderMarkdown(p.text));
      return el;
    }
    if (p.t === 'tool') {
      const el = clone('tpl-tool');
      el.querySelector('.tool-name').textContent = p.name;
      el.querySelector('code').textContent = p.args;
      el.querySelector('.tool-ok').hidden = p.result.startsWith('ERROR');
      return el;
    }
    const el = clone('tpl-delegate'); // stage: a worker's turn
    el.querySelector('.who').textContent = p.title;
    return el;
  }

  function approval(p) {
    const el = clone('tpl-approval');
    el.classList.toggle('done', approvalDone(answered, p));
    el.querySelector('.approval-title').textContent = p.title;
    el.querySelector('p').textContent = p.message;
    el.querySelector('.approval-actions').replaceChildren(...p.choices.map((c, i) =>
      h('button', { class: i === p.choices.length - 1 ? 'btn' : 'btn ghost', onclick: async () => {
        el.classList.add('done');
        answered = p.nonce;
        try { await api.post('/api/answer', { id, nonce: p.nonce, choice: c.value }); } catch (err) { answered = null; el.classList.remove('done'); fail(err); }
      } }, c.label)));
    return el;
  }

  function liveNodes() {
    const out = [];
    if (live.parts.length) {
      const el = clone('tpl-msg-reply');
      const n = {}; // a part's key: epoch, turn and place in its turn (landSaved drops whole turns from the front)
      el.querySelector('.md').replaceWith(...live.parts.map(p => part(p, `${live.epoch}.${p.turn}.${n[p.turn] = (n[p.turn] ?? -1) + 1}`)));
      el.querySelector('.stamp').remove();
      out.push(el);
    }
    if (live.prompt) out.push(approval(live.prompt));
    return out;
  }

  function ring(el, f) {
    const pct = Math.round(f * 100);
    el.style.setProperty('--ctx', pct);
    el.style.setProperty('--ctxc', RING[ringColor(f)]);
    el.querySelector('span').textContent = pct + '%';
  }

  function drawContext() {
    const c = context, f = c?.fraction || 0;
    ring($('#ctxchat'), f);
    ring($('#ctxpopring'), f);
    $('#ctxpopused').textContent = c ? (c.nCtx ? `${fmt(c.used)} / ${fmt(c.nCtx)}` : `${fmt(c.used)} tokens (context size unknown)`) : 'Nothing measured yet';
    $('#ctxpoppct2').textContent = c?.nCtx ? `${Math.round(f * 100)}% used` : '';
    const sum = Object.values(c?.parts || {}).reduce((a, b) => a + b, 0);
    const rows = c ? PARTS.map(([k, name, color]) => [name, color, k === 'reply' ? Math.max(0, c.used - sum) : c.parts[k] || 0]).filter(r => r[2] > 0) : [];
    if (c?.nCtx) rows.push(['Free space', '', Math.max(0, c.nCtx - c.used)]);
    const share = n => c?.nCtx ? Math.round(n / c.nCtx * 100) + '%' : '';
    const swatch = color => { const i = h('i'); if (color) i.style.background = color; return i; };
    $('#ctxpop .ctxbar').replaceChildren(...rows.filter(r => r[1] && c.nCtx).map(([, color, n]) => {
      const i = swatch(color); i.style.width = n / c.nCtx * 100 + '%'; return i;
    }));
    $('#ctxpop .ctxrows').replaceChildren(...rows.map(([name, color, n]) =>
      h('li', { class: color ? null : 'free' }, swatch(color), h('span', null, name), h('b', null, fmt(n)), h('small', null, share(n)))));
  }

  function drawState() {
    $('#stopbtn').classList.toggle('hidden', !live.busy);
    const n = live.queued;
    $('#queue').classList.toggle('hidden', !n);
    $('#queue').replaceChildren(h('span', { class: 'dot live' }), ` ${n} message${n === 1 ? '' : 's'} queued`);
  }

  // Per frame only the live reply's nodes are replaced; the saved messages' nodes are built when a load lands (`all`).
  function drawLive() {
    const open = new Set(liveEls.flatMap(el => [...el.querySelectorAll('details[open]')].map(d => d.dataset.key)));
    liveEls.forEach(el => el.remove());
    liveEls = liveNodes();
    liveEls.forEach(el => el.querySelectorAll('details').forEach(d => { if (open.has(d.dataset.key)) d.open = true; }));
    msgs.append(...liveEls);
  }

  function draw(toEnd, all = false) {
    cancelAnimationFrame(frame);
    const stick = toEnd || msgs.scrollHeight - msgs.scrollTop - msgs.clientHeight < 80;
    if (all) { msgs.replaceChildren(...saved.map(message)); liveEls = []; }
    drawLive();
    drawState();
    drawContext();
    if (stick) msgs.scrollTop = msgs.scrollHeight;
  }
  const drawSoon = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => draw(false)); };

  const notFound = () => { stopEvents(); msgs.replaceChildren(h('div', { class: 'card empty' }, 'This chat is not on this PC any more.')); };

  // The conversation's messages, title and context; after `saved` the live reply is in the messages, so it goes.
  async function load(mine, cut = null, refetch = false) {
    const seq = ++loadSeq;
    let conv;
    try { conv = await api.get('/api/conversation/' + encodeURIComponent(id)); }
    catch (err) { if (mine === gen) { if (err.status === 404) notFound(); else fail(err); } return false; }
    if (mine !== gen) return false;
    const r = landLoad(applied, seq, conv, context);
    const changed = !!r && JSON.stringify(r.messages) !== JSON.stringify(saved); // hello refetches on every connection
    if (r) {
      applied = seq;
      saved = r.messages;
      context = r.context;
      view.querySelector('.vtitle').textContent = r.title;
      view.querySelector('.vsub').textContent = r.sub;
    }
    if (cut) live = landSaved(live, cut); // even an outrun load: the newer one's messages hold the reply too
    draw(!refetch, changed); // a refetch keeps the scroll where it is
    return true;
  }

  // EventSource cannot tell a refusal from a drop: after a few failures in a row, ask the API; a 403 or 404 ends the retries.
  async function dropped(mine, n) {
    if (n < 3 || mine !== gen) return;
    try { await api.get('/api/conversation/' + encodeURIComponent(id)); }
    catch (err) {
      if (mine !== gen) return;
      if (err.status === 404) notFound();
      else if (err.status === 403) { stopEvents(); fail(err); }
    }
  }

  function onEvent(mine, ev) {
    if (mine !== gen) return;
    live = applyEvent(live, ev);
    if (ev.e === 'context') context = live.context;
    if (live.refetch) { live = { ...live, refetch: false }; load(mine, cutOf(live, ev), true); }
    if (live.reopen) { live = { ...live, reopen: false }; listen(mine); }
    drawSoon();
  }

  const listen = mine => { stopEvents(); stopEvents = api.events(id, ev => onEvent(mine, ev), n => dropped(mine, n)); };

  async function show(params) {
    const mine = ++gen;
    stopEvents();
    stopEvents = () => {};
    chatId = id = params?.id ?? null;
    live = emptyLive();
    answered = null;
    saved = [];
    context = null;
    pop.hidden = true;
    view.querySelector('.vtitle').textContent = id ? '' : 'New chat';
    view.querySelector('.vsub').textContent = '';
    draw(true, true);
    if (!id || !await load(mine)) return; // a new chat stays empty until the composer sends
    listen(mine);
  }

  // Another PC (main.js usePc): this chat's stream and any load on its way are the old PC's.
  document.addEventListener('ao:pc', () => { gen++; stopEvents(); stopEvents = () => {}; });

  registerScreen('chat', {
    mount() {
      $('#stopbtn').addEventListener('click', () => api.post('/api/stop', { id }).catch(fail));
      $('#ctxchat').addEventListener('click', e => { e.stopPropagation(); pop.hidden = !pop.hidden; });
      document.addEventListener('click', e => { if (!pop.hidden && !pop.contains(e.target)) pop.hidden = true; });
    },
    show,
  });
}
