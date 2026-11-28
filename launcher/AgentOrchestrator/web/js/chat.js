// The Chat screen: a conversation's messages and its live reply (GET /api/conversation/{id} and its events), Stop, the queue
// chip, the approval card and the context ring with its popover. The composer (send, the + sheet) is Part 7b's.
// applyEvent, emptyLive and ringColor are pure (Node tests them); the DOM part loads only in a page.
import { api } from './api.js';
import { h, icon } from './h.js';
import { renderMarkdown } from './markdown.js';
import { ago, longPress } from './util.js';
import { speakButton, stopSpeech, autoSpeak } from './speak.js';

/* ---------- the live reply: pure ---------- */
// parts, in order: {t:'text'|'thinking', text}, {t:'tool', name, args, result}, {t:'image', src}, {t:'stage', title}.
// The engine's deltas carry no section breaks: a text delta after a tool row (or anything not text) starts a new section.
// refetch: load the conversation again. reopen: close the stream and open it again (a fresh hello replaces the parts).
// suggestion: the last reply's suggested next message (the chip above the box), '' when none; a turn running clears it.
export const emptyLive = () => ({ busy: false, queued: 0, prompt: null, status: '', parts: [], context: null, refetch: false, reopen: false, turn: 0, epoch: 0, suggestion: '' });

const picture = path => '/api/file?path=' + encodeURIComponent(path);
// The PC path of a generated picture's URL (one in an images folder), which a long press may delete; null for the user's photos
// (pasted), screenshots or anything else. The server checks it is in the PC's own images folder.
export const deletablePicture = src => {
  if (!src.startsWith('/api/file?path=')) return null;
  const path = decodeURIComponent(src.slice('/api/file?path='.length));
  return /[\\/]images[\\/]/i.test(path) && !/[\\/](pasted|screenshots)[\\/]/i.test(path) ? path : null;
};
const SAVED = /\bSaved: ([^\r\n]+?)\. Prompt: /g; // generate_image's line (ImageGen.ChatResult) names the saved picture
const pct = p => typeof p === 'number' ? p : null; // null: no percentage yet (Loading model…)
// A picture being made (the `picture` events, or hello's `picture`): its card until `done` makes it the picture, or `end` drops it.
const card = c => ({ t: 'picture', width: c.width || 1, height: c.height || 1, text: c.text || '', progress: pct(c.progress), frame: c.frame || 0, open: true });

export function applyEvent(s, ev) {
  const last = s.parts[s.parts.length - 1];
  const tag = p => ({ ...p, turn: s.turn }); // every part belongs to a turn; `saved` closes the turn, so a closed turn's part never grows
  const append = (t, text) => last?.t === t && last.turn === s.turn
    ? { ...s, parts: [...s.parts.slice(0, -1), tag({ t, text: last.text + text })] }
    : { ...s, parts: [...s.parts, tag({ t, text })] };
  switch (ev.e) {
    case 'hello': { // refetch too: a turn saved between the first GET and the subscription is in neither, and no `saved` follows
      const parts = ev.reply?.text ? [{ t: 'text', text: ev.reply.text, turn: 0 }] : [];
      if (ev.picture) parts.push({ ...card(ev.picture), turn: 0 }); // a picture in progress: its card at once
      return { ...s, refetch: true, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null, status: ev.reply?.status || '', parts, turn: 0, epoch: s.epoch + 1,
        suggestion: ev.busy ? '' : s.suggestion };
    }
    case 'state':
      return { ...s, busy: !!ev.busy, queued: ev.queued || 0, prompt: ev.prompt || null, status: ev.status || '', suggestion: ev.busy ? '' : s.suggestion };
    case 'suggestion': // after the reply (MainWindow.Chat.cs SuggestionLine): one, until the next turn
      return { ...s, suggestion: String(ev.text || '').trim() };
    case 'delta':
      return ev.kind === 'text' || ev.kind === 'thinking' ? append(ev.kind, ev.text || '') : s; // 'tool': a call's arguments streaming
    case 'picture': {
      if (ev.state === 'start') return { ...s, parts: [...s.parts, tag(card(ev))] };
      const i = s.parts.findLastIndex(p => p.t === 'picture' && p.open), p = s.parts[i];
      if (!p) return s; // no open card (a saved load dropped it): the tool result or the saved message shows the picture
      const put = q => ({ ...s, parts: s.parts.map((x, j) => j === i ? q : x) });
      if (ev.state === 'progress') return put({ ...p, text: ev.text || '', progress: pct(ev.progress) });
      if (ev.state === 'preview') return put({ ...p, frame: ev.frame || 0 });
      if (ev.state === 'done') return put({ t: 'image', src: picture(ev.path), turn: p.turn });
      if (ev.state === 'end') return { ...s, parts: s.parts.filter((_, j) => j !== i) }; // failed or cancelled: a done card is a picture already
      return s;
    }
    case 'tool': { // a picture `done` showed already is not shown again
      const pics = [...String(ev.result || '').matchAll(SAVED)].map(m => ({ t: 'image', src: picture(m[1]) }))
        .filter(m => !s.parts.some(p => p.t === 'image' && p.src === m.src));
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
// The suggestion chip shows the suggestion while the box is empty and no turn runs (null: hidden); typing hides it, emptying
// the box shows it again.
export const suggestionShown = (s, boxText) => !s.busy && !boxText && s.suggestion ? s.suggestion : null;
// A tap on the chip fills the box (not sent), caret at the end; the input event lets the composer and the chip follow.
export function takeSuggestion(s, box) {
  const text = suggestionShown(s, box.value);
  if (!text) return;
  box.value = text;
  box.focus();
  box.setSelectionRange?.(text.length, text.length);
  box.dispatchEvent(new Event('input'));
}
// The approval card the user answered stays done across redraws, until a new prompt (new nonce) arrives.
export const approvalDone = (answered, prompt) => answered != null && answered === prompt.nonce;

// The ring's colour: blue under 50%, amber from 50%, red from 90%.
export const ringColor = f => f >= 0.9 ? 'red' : f >= 0.5 ? 'amber' : 'blue';
const RING = { blue: '#6EA8FF', amber: '#F7C873', red: '#FF6B6B' };
// The popover's rows, in bar order (ContextSnapshot.Categories); reply = what the parts leave of `used`.
const PARTS = [['system', 'System prompt', '#C88705'], ['tools', 'Tools', '#DD6122'], ['skills', 'Skills', '#8E8E8E'],
  ['messages', 'Messages', '#3085E2'], ['tool_results', 'Tool results', '#00A082'], ['reply', 'Reply', '#595959']];

/* ---------- View thinking: pure ---------- */
// Off (the ✦ toggle in the header, as on the desktop): each run of a reply's working steps folds into one WORKED row.
export const isWork = p => p.t === 'thinking' || p.t === 'tool' || p.t === 'stage';
// "2 thoughts · read 3 files · ran 1 command": a run's steps by kind (MainWindow.Thinking.cs WorkSummary).
const TOOL_KIND = { read: 'read', read_file: 'read', search_files: 'searched', grep: 'searched', find: 'searched', list_files: 'searched',
  ls: 'searched', edit: 'edited', replace_text: 'edited', write: 'wrote', write_file: 'wrote', run_command: 'ran', run_checks: 'ran',
  bash: 'ran', browser: 'browser', computer: 'computer', web_search: 'web', fetch_page: 'web' };
const N = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const SAY = { thought: n => N(n, 'thought', 'thoughts'), read: n => 'read ' + N(n, 'file', 'files'), searched: n => 'searched ' + N(n, 'time', 'times'),
  edited: n => 'edited ' + N(n, 'file', 'files'), wrote: n => 'wrote ' + N(n, 'file', 'files'), ran: n => 'ran ' + N(n, 'command', 'commands'),
  browser: n => 'used the browser ' + N(n, 'time', 'times'), computer: n => 'used the computer ' + N(n, 'time', 'times'),
  web: n => 'searched the web ' + N(n, 'time', 'times'), step: n => N(n, 'other step', 'other steps') };
export function workSummary(run) {
  const counts = new Map();
  for (const p of run) {
    const k = p.t === 'tool' ? TOOL_KIND[p.name.toLowerCase()] || 'step' : 'thought'; // a stage (a worker's turn) counts as a thought
    counts.set(k, (counts.get(k) || 0) + 1);
  }
  return [...counts].map(([k, n]) => SAY[k](n)).join(' · ') || 'working';
}

/* ---------- the chat menu: pure ---------- */
// What the ⋯ menu needs about chat `id` from GET /api/conversations: its kind, the project it is in (null: none), whether it is
// archived, the Archive item's label and the projects it may move to (the other projects of its kind; a project without a kind
// takes either kind, as on the desktop). null when the list does not have it.
export function chatMenu(list, id) {
  const { projects = [], recents = [], archived = [] } = list || {};
  const home = projects.find(p => p.items.some(c => c.id === id));
  const chat = home?.items.find(c => c.id === id) || recents.find(c => c.id === id) || archived.find(c => c.id === id);
  if (!chat) return null;
  const kind = chat.kind || 'chat', isArchived = archived.some(c => c.id === id);
  return { kind, project: home ? { id: home.id, name: home.name } : null, archived: isArchived,
    archiveLabel: isArchived ? 'Unarchive' : 'Archive',
    targets: projects.filter(p => p !== home && (!p.kind || p.kind === kind)).map(p => ({ id: p.id, name: p.name })) };
}
// The text of the last reply (not the user's) in a conversation's messages; '' when there is none.
export const lastReply = messages => [...(messages || [])].reverse().find(m => m.role !== 'user' && m.text)?.text || '';

/* ---------- the screen ---------- */
export let chatId = null; // the open chat (null: a new one), for the composer
let replaceScreen = () => {};
// A new chat's first send made it: the Chat screen's history entry becomes that chat, shown.
export const follow = id => replaceScreen('chat', { id });
// The Code screen (code.js) draws its conversation with this screen's code: showIn(its view, {id}) draws there instead, and
// tapEvents(fn) hands it every event of the open conversation's stream.
let tap = null;
export const tapEvents = fn => { tap = fn; };
export let showIn = () => {};
// The composer's send (composer.js): outgoing(id, text, paths) shows the message at once; sent(ok, queued) settles it.
export let outgoing = () => {}, sent = () => {};

if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, replace, confirmSheet, askDeleteChat, closeOverlays, askName, openSheet } = await import('./main.js');
  replaceScreen = replace;
  const $ = s => document.querySelector(s);
  const clone = id => document.getElementById(id).content.firstElementChild.cloneNode(true);
  let view = $('#view-chat'), msgs = view.querySelector('.msgs');
  const pop = $('#ctxpop'), use = v => { view = v; msgs = v.querySelector('.msgs'); };
  let outbox = null; // a message just sent from here: shown until a load's messages hold it ({message, after: messages then})
  let id = null, live = emptyLive(), saved = [], liveEls = [], context = null, stopEvents = () => {}, gen = 0, frame = 0, loadSeq = 0, applied = 0, answered = null;

  const fmt = n => n.toLocaleString('en-US');
  const deleted = new Set(); // pictures deleted from here: redraws show them deleted
  const missing = text => h('span', { class: 'pic-missing' }, text);
  const img = src => {
    if (deleted.has(src)) return missing('Picture deleted');
    const path = deletablePicture(src);
    const el = h('img', { class: path ? 'pic del' : 'pic', src: api.base + src, alt: '', loading: 'lazy', // a path on the PC the app talks to
      onerror: e => e.currentTarget.replaceWith(missing('Picture not available')) });
    if (path) longPress(el, () => confirmSheet('Delete picture',
      `Delete this picture from ${$('[data-pc-name]').textContent} for good? It can't be brought back.`, async () => {
        try { await api.post('/api/picture/delete', { path }); } catch (err) { fail(err); return; }
        deleted.add(src);
        el.replaceWith(missing('Picture deleted'));
      }));
    return el;
  };
  let viewThinking = true;
  try { viewThinking = localStorage.getItem('viewThinking') !== '0'; } catch {}
  const thinkBtns = document.querySelectorAll('[data-thinking]');
  const syncThinking = () => thinkBtns.forEach(b => b.setAttribute('aria-pressed', viewThinking));
  syncThinking();
  thinkBtns.forEach(b => b.addEventListener('click', () => {
    viewThinking = !viewThinking;
    try { localStorage.setItem('viewThinking', viewThinking ? '1' : '0'); } catch {}
    syncThinking();
    drawLive();
  }));
  // A fold's row opens on the tap's release, found again by its key: live redraws replace the reply's nodes every frame, so the
  // node a tap went down on can be gone by its click (the browser then fires the click on neither). A scroll cancels the pointer.
  const row = e => e.target.closest?.('.msgs details[data-key] > summary')?.parentElement;
  let downKey = null;
  document.addEventListener('pointerdown', e => { downKey = e.button === 0 ? row(e)?.dataset.key ?? null : null; });
  document.addEventListener('pointerup', e => {
    const d = row(e);
    if (d && downKey === d.dataset.key) d.open = !d.open;
    downKey = null;
  });
  document.addEventListener('click', e => { if (e.detail && row(e)) e.preventDefault(); }); // the release did it; a keyboard click (detail 0) toggles as usual
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
    if (!user) { const read = speakButton(m); if (read) el.querySelector('.stamp').append(' ', read); } // speak.js
    return el;
  }

  // A picture being made: dots with drifting glows, "Making a picture…", the step and the percentage, then the latest step preview
  // over the dots. Its node is kept across live redraws (a new node would refetch the no-store preview); only what changed is set.
  const cards = new Map(); // part key → card
  function pictureCard(p, key) {
    let el = cards.get(key);
    if (!el) {
      el = h('div', { class: 'pcard' }, h('div', { class: 'pcard-dots' }, h('i', { class: 'pcard-glow a' }), h('i', { class: 'pcard-glow b' })),
        h('img', { class: 'pcard-img', alt: '', hidden: true, onload: () => el.classList.add('shot') }), // shown: the dots stop under it
        h('span', { class: 'pcard-status' }, 'Making a picture', h('i', { class: 'pcard-ell' }, '...')), h('span', { class: 'pcard-step' }),
        h('span', { class: 'pcard-pct', hidden: true }));
      cards.set(key, el);
    }
    el.style.setProperty('--ar', p.height / p.width); // every time: a node kept for the same key may belong to a new picture
    const [, shot, , step, pill] = el.children, pc = p.progress == null ? '' : Math.round(Math.min(1, Math.max(0, p.progress)) * 100) + '%';
    if (step.textContent !== p.text) step.textContent = p.text;
    if (pill.textContent !== pc) { pill.textContent = pc; pill.hidden = !pc; }
    if (el.dataset.frame !== String(p.frame)) { // a new preview: the only time src is set; the old frame shows until the new one loads
      el.dataset.frame = p.frame;
      if (p.frame) shot.src = api.base + '/api/conversation/' + encodeURIComponent(id) + '/picture?frame=' + p.frame;
      else el.classList.remove('shot');
      shot.hidden = !p.frame;
    }
    return el;
  }

  function part(p, key) {
    if (p.t === 'text') return h('div', { class: 'md' }, renderMarkdown(p.text));
    if (p.t === 'image') return img(p.src);
    if (p.t === 'picture') return pictureCard(p, key);
    if (p.t === 'thinking') {
      const el = clone('tpl-thinking');
      el.dataset.key = key; // which part this fold is, so an opened fold stays open across live redraws
      el.querySelector('.fold-text').textContent = p.text.trim().split('\n')[0];
      el.querySelector('.fold-body').append(renderMarkdown(p.text));
      return el;
    }
    if (p.t === 'tool') {
      const el = clone('tpl-tool');
      el.dataset.key = key;
      el.querySelector('.tool-name').textContent = p.name;
      el.querySelector('code').textContent = p.args;
      el.querySelector('.tool-out').textContent = [p.args, p.result].filter(Boolean).join('\n\n');
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

  // With View thinking off, a run of working steps goes into one WORKED row, keyed by its first step (which a growing run keeps).
  function worked(run) {
    const el = clone('tpl-thinking'), body = el.querySelector('.fold-body');
    el.classList.add('worked');
    el.dataset.key = run[0][1] + 'w';
    el.querySelector('.fold-tag').textContent = '✦ Worked';
    el.querySelector('.fold-text').textContent = workSummary(run.map(([p]) => p));
    body.classList.remove('md');
    body.append(...run.map(([p, key]) => part(p, key)));
    return el;
  }

  function liveNodes() {
    const out = outbox ? [message(outbox.message)] : [];
    if (live.parts.length) {
      const el = clone('tpl-msg-reply');
      const n = {}; // a part's key: epoch, turn and place in its turn (landSaved drops whole turns from the front)
      const items = live.parts.map(p => [p, `${live.epoch}.${p.turn}.${n[p.turn] = (n[p.turn] ?? -1) + 1}`]), nodes = [];
      for (let i = 0; i < items.length; i++) {
        if (viewThinking || !isWork(items[i][0])) { nodes.push(part(...items[i])); continue; }
        let j = i;
        while (j < items.length && isWork(items[j][0])) j++;
        nodes.push(worked(items.slice(i, j)));
        i = j - 1;
      }
      el.querySelector('.md').replaceWith(...nodes);
      el.querySelector('.stamp').remove();
      out.push(el);
    }
    if (live.busy && live.status) out.push(h('p', { class: 'livestatus' }, live.status)); // the desktop's status line under its reply
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
    ring(view.querySelector('.ctxring'), f);
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

  function drawSuggestion() { // the Chat screen's chip (the Code screen has none)
    const chip = view.querySelector('[data-suggest]'), text = chip && suggestionShown(live, $('#chatbox').value);
    if (!chip) return;
    chip.classList.toggle('hidden', !text);
    if (text && chip.textContent !== text) chip.textContent = text;
  }

  function drawState() {
    drawSuggestion();
    view.querySelector('[data-stop]').classList.toggle('hidden', !live.busy);
    const n = live.queued, queue = view.querySelector('.queue');
    queue.classList.toggle('hidden', !n);
    queue.replaceChildren(h('span', { class: 'dot live' }), ` ${n} message${n === 1 ? '' : 's'} queued`);
  }

  // Per frame only the live reply's nodes are replaced; the saved messages' nodes are built when a load lands (`all`).
  function drawLive() {
    const open = new Set(liveEls.flatMap(el => [...el.querySelectorAll('details[open]')].map(d => d.dataset.key)));
    liveEls.forEach(el => el.remove());
    liveEls = liveNodes();
    liveEls.forEach(el => el.querySelectorAll('details').forEach(d => { if (open.has(d.dataset.key)) d.open = true; }));
    msgs.append(...liveEls);
    // A kept card still moved into the new reply node, and a moved node restarts its CSS animations: pin them to the page's clock
    // so the glow and the counting dots run on unbroken (real elements, not pseudo-elements: every engine lists their animations).
    // A card no longer drawn is let go.
    cards.forEach((el, k) => el.isConnected
      ? el.querySelectorAll('.pcard-glow, .pcard-ell').forEach(n => n.getAnimations?.().forEach(a => { a.startTime = 0; }))
      : cards.delete(k));
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
      if (outbox && saved.length > outbox.after) outbox = null; // the PC has it now
      context = r.context;
      view.querySelector('.vtitle').textContent = r.title;
      if (view.id === 'view-chat') view.querySelector('.vsub').textContent = r.sub; // the Code screen's shows the folder
    }
    if (cut) live = landSaved(live, cut); // even an outrun load: the newer one's messages hold the reply too
    draw(!refetch, changed); // a refetch keeps the scroll where it is
    if (cut && r) autoSpeak(saved.at(-1), msgs); // a finished reply: Read replies aloud (speak.js)
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
    tap?.(ev);
    if (ev.e === 'context') context = live.context;
    if (live.refetch) { live = { ...live, refetch: false }; load(mine, cutOf(live, ev), true); }
    if (live.reopen) { live = { ...live, reopen: false }; listen(mine); }
    drawSoon();
  }

  const listen = mine => { stopEvents(); stopEvents = api.events(id, ev => onEvent(mine, ev), n => dropped(mine, n)); };

  async function show(params) {
    const mine = ++gen;
    stopSpeech(); // another conversation (or the same one again)
    stopEvents();
    stopEvents = () => {};
    chatId = id = params?.id ?? null;
    live = emptyLive();
    answered = null;
    outbox = null;
    saved = [];
    context = null;
    pop.hidden = true;
    view.querySelector('.vtitle').textContent = id ? '' : 'New chat';
    view.querySelector('.vsub').textContent = '';
    draw(true, true);
    if (!id || !await load(mine)) return; // a new chat stays empty until the composer sends
    listen(mine);
  }

  outgoing = (to, text, paths) => {
    stopSpeech(); // a new send
    live = { ...live, suggestion: '' }; // sent: the box empties next, and the old suggestion stays gone
    drawSuggestion();
    if (!to || to !== id) return; // a new chat: the follow to it loads the message
    outbox = { message: { role: 'user', text, time: new Date().toISOString(), images: paths.map(picture) }, after: saved.length };
    draw(true);
  };
  sent = (ok, queued) => {
    if (!outbox) return;
    if (!ok || queued) { outbox = null; draw(false); return; } // failed (the text stays in the box) or queued (the queue chip says it)
    load(gen, null, true);
  };

  // Another PC (main.js usePc): this chat's stream and any load on its way are the old PC's.
  document.addEventListener('ao:pc', () => { gen++; stopEvents(); stopEvents = () => {}; });

  // The ⋯ menu: its state from the conversations list, fetched as it opens (the Archive label follows it).
  let menu = null;
  const post = (verb, body) => api.post('/api/conversation/' + encodeURIComponent(id) + '/' + verb, body);
  async function readMenu() {
    if (!id) return null;
    const mine = gen;
    let list;
    try { list = await api.get('/api/conversations'); } catch (err) { fail(err); return null; }
    if (mine !== gen) return null;
    menu = chatMenu(list, id);
    $('#chatarchive span').textContent = menu?.archiveLabel || 'Archive';
    return menu;
  }
  const act = fn => async () => { if (!id) { closeOverlays(); return; } await fn(); };
  const rename = act(() => { const was = view.querySelector('.vtitle').textContent; askName('RENAME CHAT', was, async title => {
    if (title === was) return; // unchanged: a derived title stays derived, as on the desktop
    try { await post('rename', { title }); } catch (err) { fail(err); return; }
    load(gen, null, true);
  }); });
  const moveTo = async (project, name) => {
    closeOverlays();
    try { await post('move', { project }); } catch (err) { fail(err); return; }
    toast(project ? 'Moved to ' + name : 'Removed from the project');
  };
  const move = act(async () => {
    const m = menu || await readMenu();
    if (!m) return;
    if (m.archived) { closeOverlays(); toast('Unarchive it first'); return; }
    const row = (text, ic, onclick) => h('button', { class: 'drawer-item', onclick }, icon(ic, 'ic sm'), ' ' + text);
    $('#moveList').replaceChildren(...m.targets.map(p => row(p.name, 'folder', () => moveTo(p.id, p.name))),
      row('New project…', 'plus', () => askName('NEW PROJECT', '', async name => {
        if (!name) return;
        let made;
        try { made = await api.post('/api/projects', { name, kind: m.kind }); } catch (err) { fail(err); return; }
        moveTo(made.id, name);
      }, 'Create and move')),
      m.project && row('Remove from ' + m.project.name, 'back', () => moveTo(null)));
    openSheet('sheet-move');
  });
  const archive = act(async () => {
    const m = menu || await readMenu();
    closeOverlays();
    if (!m) return;
    try { await post('archive', { archived: !m.archived }); } catch (err) { fail(err); return; }
    menu = null;
    toast(m.archived ? 'Unarchived' : 'Archived');
  });
  const copyReply = () => {
    closeOverlays();
    const text = lastReply(saved);
    if (!text) { toast('No reply to copy'); return; }
    navigator.clipboard.writeText(text).then(() => toast('Copied'), () => toast('Could not copy'));
  };

  // Bound once at load, not in the Chat screen's mount: the Code screen shares these buttons and may be visited first.
  document.querySelectorAll('[data-stop]').forEach(b => b.addEventListener('click', () => api.post('/api/stop', { id }).catch(fail)));
  const chatBox = $('#chatbox');
  chatBox.addEventListener('input', drawSuggestion);
  $('[data-suggest]').addEventListener('click', () => takeSuggestion(live, chatBox));
  document.querySelectorAll('.ctxring[data-ctx]').forEach(b => b.addEventListener('click', e => { e.stopPropagation(); pop.hidden = !pop.hidden; }));
  document.addEventListener('click', e => { if (!pop.hidden && !pop.contains(e.target)) pop.hidden = true; });

  registerScreen('chat', {
    mount() {
      $('#view-chat [data-sheet="sheet-chatmenu"]').addEventListener('click', () => { menu = null; readMenu(); });
      $('#chatrename').addEventListener('click', rename);
      $('#chatmove').addEventListener('click', move);
      $('#chatarchive').addEventListener('click', archive);
      $('#chatcopy').addEventListener('click', copyReply);
      $('#chatdelete').addEventListener('click', () => id ? askDeleteChat(id, view.querySelector('.vtitle').textContent) : closeOverlays());
    },
    show: params => { use($('#view-chat')); return show(params); },
  });
  showIn = (v, params) => { use(v); return show(params); };
}
