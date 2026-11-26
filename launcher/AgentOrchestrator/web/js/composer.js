// The Chat screen's composer: send (photos from the phone are uploaded as they are picked), the + sheet bound to
// GET/POST /api/composer, and / completion of the composer's skills. main.js keeps the pill's expand and fold.
// sizeBody, sizeBoxes, slashMatches and sendPayload are pure (Node tests them); the DOM part loads only in a page.
import { api } from './api.js';
import { h, icon } from './h.js';

/* ---------- pure ---------- */
// The app's commands the / list offers besides the skills (the PC takes /compact from the phone).
export const COMMANDS = [{ name: 'compact', help: 'Summarise this conversation now', insert: '/compact ' }];

// The / pop-up's rows for the box's text: a "/" word alone at the start (a space closes it), prefix-matched; null = closed.
// A skill is listed as /name but inserts $name (the desktop's other skill syntax): the PC refuses /name from the phone.
export function slashMatches(text, skills) {
  const m = /^\/([\w.-]*)$/.exec(text);
  if (!m) return null;
  const q = m[1].toLowerCase();
  return [...skills.map(s => ({ name: s.name, help: s.description || (s.on ? 'Skill, on for this turn' : 'Skill'), insert: '$' + s.name + ' ' })), ...COMMANDS]
    .filter(r => r.name.toLowerCase().startsWith(q));
}

// An edited size box posts that field alone (empty = 0): the server keeps the other box, so a height edit is never lost
// to an echoed effective width.
export function sizeBody(field, value) {
  const n = parseInt(value, 10);
  return { imageGen: { [field]: n > 0 ? n : 0 } };
}

// The boxes show the stored box values (0 = empty); the effective size ("1280×720") is only their placeholder.
export function sizeBoxes(g) {
  const [hintWidth = '', hintHeight = ''] = g.size ? g.size.split('×') : [];
  return { width: g.width > 0 ? String(g.width) : '', height: g.height > 0 ? String(g.height) : '', hintWidth, hintHeight };
}

// POST /api/send's body: a new chat (id null) sends id ""; null when there is nothing to send.
export function sendPayload(id, text, attachments) {
  const t = text.trim();
  return t || attachments.length ? { id: id || '', text: t, attachments } : null;
}

const EFFORT = { off: 'Off', minimal: 'Minimal', low: 'Low', medium: 'Medium', high: 'High', xhigh: 'Extra high', max: 'Max' };

/* ---------- the composer ---------- */
if (typeof document !== 'undefined') {
  const { fail, toast, closeOverlays } = await import('./main.js');
  const chat = await import('./chat.js');
  const $ = s => document.querySelector(s);
  const box = $('#chatbox'), pop = $('#skillpop'), picker = $('#photoinput'), strip = $('#attachrow'), sendBtn = $('#sendbtn');
  let opts = null, photos = [], sending = false; // photos: {name, path} (path null while uploading)

  const refresh = () => api.get('/api/composer').then(draw, () => {}); // quiet: the screens show the PC's own errors
  const set = body => api.post('/api/composer', body).then(draw, err => { fail(err); if (opts) draw(opts); });

  function options(sel, list, value, empty) {
    if (!list.some(([v]) => v === value)) list = [[value, value || empty], ...list];
    sel.replaceChildren(...list.map(([v, label]) => h('option', { value: v }, label)));
    sel.value = value;
  }

  function draw(o) {
    opts = o;
    document.querySelectorAll('[data-opt]').forEach(sw => sw.classList.toggle('on', !!o[sw.dataset.opt]));
    $('#visionchip').classList.toggle('on', o.vision);
    $('#effortchip span').textContent = 'Effort: ' + (EFFORT[o.effort] || 'Default');
    $('#effortseg').replaceChildren(...['', ...o.efforts].map(v =>
      h('button', { class: v === o.effort ? 'on' : null, dataset: { segOn: v } }, EFFORT[v] || 'Default')));
    $('#workerchips').replaceChildren(...o.workers.map(w =>
      h('button', { class: w.role === o.worker ? 'chip on' : 'chip', onclick: () => set({ worker: w.role }) }, ...(w.role === o.worker ? [icon('orb', 'ic sm'), ' '] : []), w.name)));
    $('#skillchips').replaceChildren(...o.skills.map(s =>
      h('button', { class: s.on ? 'chip on' : 'chip', onclick: () => set({ skills: [{ name: s.name, on: !s.on }] }) }, icon('layers', 'ic sm'), ' ' + s.name)));
    const g = o.imageGen, b = sizeBoxes(g);
    $('#imgchip span').textContent = g.name || 'Image Gen';
    options($('#imgmodel'), g.models.map(m => [m.id, m.note ? `${m.name} · ${m.note}` : m.name]), g.model, 'Pick a model');
    options($('#imgaspect'), g.aspects.map(a => [a.ratio, a.ratio === 'Custom' ? 'Custom' : `${a.name} ${a.ratio}`]), g.aspect, 'Square 1:1');
    for (const k of ['width', 'height']) {
      const el = $('#img' + k);
      el.placeholder = k === 'width' ? b.hintWidth : b.hintHeight;
      if (document.activeElement !== el) el.value = b[k];
    }
    if (document.activeElement !== $('#imgsteps')) $('#imgsteps').value = g.steps > 0 ? g.steps : '';
    $('#imglock').classList.toggle('on', !g.unlocked);
    $('#imgsize').textContent = g.size ? 'Makes ' + g.size : 'No image model ready';
  }

  function drawStrip() {
    strip.classList.toggle('hidden', !photos.length);
    strip.replaceChildren(...photos.map(p => h('span', { class: 'chip' }, icon('image', 'ic sm'), p.path ? ' ' + p.name : ` Uploading ${p.name}…`,
      h('button', { class: 'x', 'aria-label': 'Remove ' + p.name, onclick: () => { photos = photos.filter(x => x !== p); drawStrip(); } }, '×'))));
  }

  function upload(files) {
    for (const f of files) {
      if (f.size > 25 * 1024 * 1024) { toast('That file is over 25 MB'); continue; }
      const p = { name: f.name || 'photo', path: null };
      photos.push(p);
      api.upload(f).then(path => { p.path = path; }, err => { photos = photos.filter(x => x !== p); fail(err); }).finally(drawStrip);
    }
    drawStrip();
  }

  function drawPop() {
    const rows = opts && slashMatches(box.value, opts.skills);
    pop.classList.toggle('hidden', !rows?.length);
    pop.replaceChildren(h('p', { class: 'skillpop-head' }, 'Skills'), ...(rows || []).map(r =>
      h('button', { onclick: () => { box.value = r.insert; box.focus(); box.dispatchEvent(new Event('input')); } }, h('b', null, '/' + r.name), h('small', null, r.help))));
  }

  async function send() {
    if (sending) return;
    if (photos.some(p => !p.path)) { toast('The photos are still uploading'); return; }
    const body = sendPayload(chat.chatId, box.value, photos.map(p => p.path));
    if (!body) return;
    sending = sendBtn.disabled = true;
    try {
      const r = await api.post('/api/send', body);
      box.value = '';
      box.dispatchEvent(new Event('input'));
      photos = [];
      drawStrip();
      if (!body.id && r?.id) chat.follow(r.id);
    } catch (err) { fail(err); } finally { sending = sendBtn.disabled = false; }
  }

  box.addEventListener('input', drawPop);
  box.addEventListener('focus', () => { if (!opts) refresh(); });
  box.addEventListener('keydown', e => {
    if (e.key !== 'Enter' || e.shiftKey || e.isComposing) return;
    e.preventDefault();
    const first = pop.classList.contains('hidden') ? null : pop.querySelector('button');
    if (first) first.click(); else send();
  });
  sendBtn.addEventListener('click', send);
  document.addEventListener('click', e => { if (e.target.closest('[data-sheet="sheet-tools"]')) refresh(); });
  document.querySelectorAll('[data-opt]').forEach(sw => sw.addEventListener('click', () => opts && set({ [sw.dataset.opt]: !opts[sw.dataset.opt] })));
  $('#visionchip').addEventListener('click', () => opts && set({ vision: !opts.vision }));
  $('#effortseg').addEventListener('change', e => set({ effort: e.currentTarget.dataset.value }));
  $('#imgmodel').addEventListener('change', e => set({ imageGen: { model: e.target.value } }));
  $('#imgaspect').addEventListener('change', e => set({ imageGen: { aspect: e.target.value } }));
  $('#imgwidth').addEventListener('change', e => set(sizeBody('width', e.target.value)));
  $('#imgheight').addEventListener('change', e => set(sizeBody('height', e.target.value)));
  $('#imgsteps').addEventListener('change', e => set({ imageGen: { steps: parseInt(e.target.value, 10) || 0 } }));
  $('#imglock').addEventListener('click', () => opts && set({ imageGen: { unlocked: !opts.imageGen.unlocked } }));
  $('#photobtn').addEventListener('click', () => { closeOverlays(); picker.click(); });
  picker.addEventListener('change', () => { upload([...picker.files]); picker.value = ''; });
  // A PC switch (main.js usePc): the options and the uploaded photos belong to the old PC.
  document.addEventListener('ao:pc', () => { opts = null; photos = []; drawStrip(); });
  refresh();
}
