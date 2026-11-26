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
export const emptyLive = () => ({ busy: false, queued: 0, prompt: null, parts: [], context: null, refetch: false });

const picture = path => '/api/file?path=' + encodeURIComponent(path);
const SAVED = /\bSaved: ([^\r\n]+?)\. Prompt: /g; // generate_image's line (ImageGen.ChatResult) names the saved picture

export function applyEvent(s, ev) {
  const last = s.parts[s.parts.length - 1];
  const append = (t, text) => last?.t === t
    ? { ...s, parts: [...s.parts.slice(0, -1), { t, text: last.text + text }] }
    : { ...s, parts: [...s.parts, { t, text }] };
  switch (ev.e) {
    case 'hello':
      return { ...s, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null, parts: ev.reply?.text ? [{ t: 'text', text: ev.reply.text }] : [] };
    case 'state':
      return { ...s, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null };
    case 'delta':
      return ev.kind === 'text' || ev.kind === 'thinking' ? append(ev.kind, ev.text || '') : s; // 'tool': a call's arguments streaming
    case 'tool': {
      const pics = [...String(ev.result || '').matchAll(SAVED)].map(m => ({ t: 'image', src: picture(m[1]) }));
      const args = typeof ev.args === 'string' ? ev.args : JSON.stringify(ev.args ?? '');
      return { ...s, parts: [...s.parts, { t: 'tool', name: ev.name || '', args, result: ev.result || '' }, ...pics] };
    }
    case 'chat_stage':
      return ev.stage === 'delegate' ? { ...s, parts: [...s.parts, { t: 'stage', title: ev.title || ev.role || '' }] } : s;
    case 'context': {
      const nCtx = ev.n_ctx > 0 ? ev.n_ctx : null, used = (ev.prompt_tokens || 0) + (ev.completion_tokens || 0);
      return { ...s, context: { nCtx, used, fraction: nCtx ? Math.min(1, used / nCtx) : 0, parts: ev.parts || {} } };
    }
    case 'saved':
    case 'resync':
      return { ...s, refetch: true };
    default:
      return s;
  }
}

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
  let id = null, live = emptyLive(), saved = [], context = null, stopEvents = () => {}, gen = 0, frame = 0;

  const fmt = n => n.toLocaleString('en-US');
  const img = src => h('img', { class: 'pic', src, alt: '', loading: 'lazy',
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

  function part(p) {
    if (p.t === 'text') return h('div', { class: 'md' }, renderMarkdown(p.text));
    if (p.t === 'image') return img(p.src);
    if (p.t === 'thinking') {
      const el = clone('tpl-thinking');
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
    el.querySelector('.approval-title').textContent = p.title;
    el.querySelector('p').textContent = p.message;
    el.querySelector('.approval-actions').replaceChildren(...p.choices.map((c, i) =>
      h('button', { class: i === p.choices.length - 1 ? 'btn' : 'btn ghost', onclick: async () => {
        el.classList.add('done');
        try { await api.post('/api/answer', { id, nonce: p.nonce, choice: c.value }); } catch (err) { el.classList.remove('done'); fail(err); }
      } }, c.label)));
    return el;
  }

  function liveNodes() {
    const out = [];
    if (live.parts.length) {
      const el = clone('tpl-msg-reply');
      el.querySelector('.md').replaceWith(...live.parts.map(part));
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

  function draw(toEnd) {
    cancelAnimationFrame(frame);
    const stick = toEnd || msgs.scrollHeight - msgs.scrollTop - msgs.clientHeight < 80;
    msgs.replaceChildren(...saved.map(message), ...liveNodes());
    drawState();
    drawContext();
    if (stick) msgs.scrollTop = msgs.scrollHeight;
  }
  const drawSoon = () => { cancelAnimationFrame(frame); frame = requestAnimationFrame(() => draw(false)); };

  const notFound = () => { stopEvents(); msgs.replaceChildren(h('div', { class: 'card empty' }, 'This chat is not on this PC any more.')); };

  // The conversation's messages, title and context; after `saved` the live reply is in the messages, so it goes.
  async function load(mine, after) {
    let conv;
    try { conv = await api.get('/api/conversation/' + encodeURIComponent(id)); }
    catch (err) { if (mine === gen) { if (err.status === 404) notFound(); else fail(err); } return false; }
    if (mine !== gen) return false;
    view.querySelector('.vtitle').textContent = conv.title || 'Chat';
    view.querySelector('.vsub').textContent = conv.worker ? `${conv.kind === 'code' ? 'Code' : 'Chat'} · to ${conv.worker}` : '';
    saved = conv.messages;
    context = conv.context ?? context;
    if (after === 'saved') live = { ...live, parts: [] };
    draw(!after);
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
    if (live.refetch) { live = { ...live, refetch: false }; load(mine, ev.e); }
    drawSoon();
  }

  async function show(params) {
    const mine = ++gen;
    stopEvents();
    stopEvents = () => {};
    chatId = id = params?.id ?? null;
    live = emptyLive();
    saved = [];
    context = null;
    pop.hidden = true;
    view.querySelector('.vtitle').textContent = id ? '' : 'New chat';
    view.querySelector('.vsub').textContent = '';
    draw(true);
    if (!id || !await load(mine)) return; // a new chat stays empty until the composer sends
    stopEvents = api.events(id, ev => onEvent(mine, ev), n => dropped(mine, n));
  }

  registerScreen('chat', {
    mount() {
      $('#stopbtn').addEventListener('click', () => api.post('/api/stop', { id }).catch(fail));
      $('#ctxchat').addEventListener('click', e => { e.stopPropagation(); pop.hidden = !pop.hidden; });
      document.addEventListener('click', e => { if (!pop.hidden && !pop.contains(e.target)) pop.hidden = true; });
    },
    show,
  });
}
