// The Images screen: the desktop's Images page on the phone (GET /api/images and its status, generate, the queue, the live preview,
// the gallery, the viewer, delete). The form <-> settings mapping, which fields a model shows and the stage's state are pure (Node
// tests them); the DOM part loads only in a page.
import { api } from './api.js';
import { h } from './h.js';
import { longPress } from './util.js';

/* ---------- pure ---------- */
export const SOURCES = [['local', 'This PC'], ['shared', 'Primary PC'], ['remote', 'OpenRouter']];
// The model picker's groups, in SOURCES order; an empty group is left out.
export const groupModels = models => SOURCES.map(([src, label]) => [label, models.filter(m => m.source === src)]).filter(([, ms]) => ms.length);
// Which fields a model shows: the negative prompt when it takes one; steps, guidance, width/height and seed only for a model run on a
// PC (OpenRouter is sent the prompt and the nearest aspect ratio, as the desktop's _imgLocalOnly).
export const fieldsFor = model => ({ negative: !!model?.negative, local: !!model && model.source !== 'remote' });
// A locked size: the height for this width at the aspect ratio ("3:2"), a multiple of the model's step; null for Custom.
export function lockedHeight(aspect, width, multiple = 64) {
  const m = /^(\d+):(\d+)$/.exec(aspect || ''), step = multiple > 0 ? multiple : 1;
  return m && width > 0 ? Math.max(step, Math.round(width * m[2] / m[1] / step) * step) : null;
}

// The form's values (strings and booleans, as the inputs hold them) from the PC's settings; a random seed (-1) is an empty box.
export const settingsToForm = s => ({
  model: s.model || '', prompt: s.prompt || '', negative: s.negative || '', steps: String(s.steps ?? ''), cfg: String(s.cfg ?? ''),
  aspect: s.aspect || '', width: String(s.width || ''), height: String(s.height || ''), unlocked: !!s.unlocked,
  count: String(s.count || 1), seed: s.seed >= 0 ? String(s.seed) : '', lowVram: !!s.lowVram });

// The settings to send from the form: numbers, the count kept to 1..maxBatch, an empty or bad seed is random (-1, as ReadSeed),
// no negative for a model without one.
export function formToSettings(f, model, maxBatch = 20) {
  const int = v => parseInt(v, 10), seed = /^\s*\d+\s*$/.test(f.seed) ? int(f.seed) : -1;
  return {
    model: f.model, prompt: f.prompt.trim(), negative: fieldsFor(model).negative ? f.negative.trim() : '',
    steps: int(f.steps) || 0, cfg: parseFloat(f.cfg) || 0, aspect: f.aspect, width: int(f.width) || 0, height: int(f.height) || 0,
    unlocked: !!f.unlocked, count: Math.min(maxBatch, Math.max(1, int(f.count) || 1)), seed, lowVram: !!f.lowVram };
}

export const pictureUrl = path => '/api/file?path=' + encodeURIComponent(path);
// The stage card from /api/images/status: 'busy' (text, the percentage pill, the preview frame), 'done' (the newest picture, once
// a job seen running here has ended) or 'none'.
export function stageView(st, sawBusy) {
  if (st?.busy) {
    const p = typeof st.progress === 'number' ? Math.round(Math.min(1, Math.max(0, st.progress)) * 100) + '%' : '';
    return { mode: 'busy', text: st.text || '', pct: p, frame: st.frame || 0 };
  }
  return sawBusy && st?.latest ? { mode: 'done', src: pictureUrl(st.latest) } : { mode: 'none' };
}
// The next status poll: every second while a job runs, every 5 s while idle.
export const pollDelay = busy => busy ? 1000 : 5000;

/* ---------- the screen ---------- */
if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, confirmSheet } = await import('./main.js');
  const $ = s => document.querySelector(s);
  const view = $('#view-images'), form = $('#imgform'), card = $('#imgstage'), shot = card.querySelector('.pcard-img');
  const [, , status, step, pill] = card.children;
  let models = [], maxBatch = 20, pictures = [], gen = 0, timer = 0, sawBusy = false, viewing = -1;
  const field = name => form.elements[name];
  const model = () => models.find(m => m.id === field('model').value);
  const lock = $('#imglock'), lowVram = $('#imglowvram');

  function showFields() {
    const f = fieldsFor(model());
    form.querySelectorAll('[data-neg]').forEach(el => { el.hidden = !f.negative; });
    form.querySelectorAll('[data-local]').forEach(el => { el.hidden = !f.local; });
  }
  function relock() {
    const height = lock.classList.contains('on') ? null : lockedHeight(field('aspect').value, +field('width').value, model()?.multiple);
    if (height) field('height').value = height;
  }
  function fill(s) {
    const f = settingsToForm(s);
    for (const k of ['model', 'prompt', 'negative', 'steps', 'cfg', 'aspect', 'width', 'height', 'count', 'seed']) field(k).value = f[k];
    lock.classList.toggle('on', f.unlocked); // on: unlocked, any size as typed
    lowVram.classList.toggle('on', f.lowVram);
    showFields();
  }
  const read = () => formToSettings({ ...Object.fromEntries(new FormData(form)), unlocked: lock.classList.contains('on'),
    lowVram: lowVram.classList.contains('on') }, model(), maxBatch);

  function drawGallery() {
    $('#imggallery').replaceChildren(...pictures.map((p, i) => {
      const el = h('img', { class: 'imgthumb', src: api.base + p.url, alt: '', loading: 'lazy', onclick: () => openViewer(i) });
      longPress(el, () => askDelete(p.path));
      return el;
    }));
    $('#imgempty').hidden = pictures.length > 0;
  }

  async function load(mine) {
    let r;
    try { r = await api.get('/api/images'); } catch (err) { if (mine === gen) fail(err); return; }
    if (mine !== gen) return;
    models = r.models || [];
    maxBatch = r.maxBatch || 20;
    pictures = r.pictures || [];
    field('model').replaceChildren(...groupModels(models).map(([label, ms]) =>
      h('optgroup', { label }, ms.map(m => h('option', { value: m.id }, m.name + (m.note ? ` (${m.note})` : ''))))));
    field('aspect').replaceChildren(...(r.aspects || []).map(a => h('option', { value: a }, a)));
    field('count').replaceChildren(...Array.from({ length: maxBatch }, (_, i) => h('option', { value: i + 1 }, i + 1)));
    fill(r.settings || {});
    drawGallery();
  }

  function drawStage(st) {
    const v = stageView(st, sawBusy);
    card.hidden = v.mode === 'none';
    status.hidden = v.mode !== 'busy';
    if (v.mode === 'done') {
      if (card.dataset.src !== v.src) { card.dataset.src = v.src; card.dataset.frame = ''; shot.src = api.base + v.src; shot.hidden = false; }
      step.textContent = ''; pill.hidden = true;
      return;
    }
    if (v.mode !== 'busy') return;
    delete card.dataset.src;
    if (step.textContent !== v.text) step.textContent = v.text;
    if (pill.textContent !== v.pct) { pill.textContent = v.pct; pill.hidden = !v.pct; }
    if (card.dataset.frame !== String(v.frame)) { // a new preview: the only time src is set (the route is no-store)
      card.dataset.frame = v.frame;
      if (v.frame) shot.src = api.base + '/api/images/preview?frame=' + v.frame;
      else card.classList.remove('shot');
      shot.hidden = !v.frame;
    }
  }

  function drawQueue(queue) {
    const n = queue.length;
    $('#imgqueued').hidden = !n;
    $('#imgqueued').textContent = `${n} queued`;
    if (!n) $('#imgqueue').hidden = true;
    $('#imgqueue .imgqlist').replaceChildren(...queue.map(q => h('div', { class: 'rowline' },
      h('div', {}, h('b', {}, q.prompt || 'Picture'), h('small', {}, q.seed >= 0 ? `Seed ${q.seed}` : 'Random seed')),
      h('button', { class: 'iconbtn sm', 'aria-label': 'Remove from the queue', onclick: () =>
        api.post('/api/images/unqueue', { index: q.index }).then(() => poll(gen), fail) }, '✕'))));
  }

  // Polls while the Images screen is shown (a hidden view, a new show or a PC switch ends this loop).
  async function poll(mine) {
    clearTimeout(timer);
    if (mine !== gen || !view.classList.contains('on')) return;
    let st;
    try { st = await api.get('/api/images/status'); } catch (err) { if (mine === gen) fail(err); return; }
    if (mine !== gen) return;
    if (st.busy) sawBusy = true;
    $('#imgcancel').hidden = !st.busy;
    drawStage(st);
    drawQueue(st.queue || []);
    if (st.latest && st.latest !== pictures[0]?.path) load(mine); // a new picture: the gallery's newest
    timer = setTimeout(() => poll(mine), pollDelay(st.busy));
  }

  /* the viewer: swipe or arrow keys between the gallery's pictures */
  const viewer = $('#imgviewer'), big = viewer.querySelector('img');
  function openViewer(i) {
    if (!pictures.length) { closeViewer(); return; }
    viewing = Math.max(0, Math.min(pictures.length - 1, i));
    big.src = api.base + pictures[viewing].url;
    viewer.hidden = false;
  }
  const closeViewer = () => { viewer.hidden = true; viewing = -1; };
  let downX = null;
  viewer.addEventListener('pointerdown', e => { downX = e.clientX; });
  viewer.addEventListener('pointerup', e => {
    const dx = downX == null ? 0 : e.clientX - downX;
    downX = null;
    if (Math.abs(dx) > 50) openViewer(viewing + (dx < 0 ? 1 : -1));
  });
  document.addEventListener('keydown', e => {
    if (viewer.hidden) return;
    if (e.key === 'ArrowRight') openViewer(viewing + 1);
    else if (e.key === 'ArrowLeft') openViewer(viewing - 1);
    else if (e.key === 'Escape') closeViewer();
  });

  function askDelete(path) {
    confirmSheet('Delete picture', `Delete this picture from ${$('[data-pc-name]').textContent} for good? It can't be brought back.`, async () => {
      try { await api.post('/api/picture/delete', { path }); } catch (err) { fail(err); return; }
      const i = pictures.findIndex(p => p.path === path);
      if (i >= 0) pictures.splice(i, 1);
      drawGallery();
      if (viewing >= 0) openViewer(Math.min(viewing, pictures.length - 1));
      toast('Picture deleted');
    });
  }

  function show() {
    const mine = ++gen;
    sawBusy = false;
    card.hidden = true;
    closeViewer();
    load(mine).then(() => poll(mine));
  }

  // Another PC (main.js usePc): the loop and any load on its way are the old PC's.
  document.addEventListener('ao:pc', () => { gen++; clearTimeout(timer); });

  registerScreen('images', {
    mount() {
      field('model').addEventListener('change', () => {
        const m = model();
        if (m?.steps) field('steps').value = m.steps; // a new model brings its own steps and guidance, as on the PC
        if (m?.cfg != null) field('cfg').value = m.cfg;
        showFields();
        relock();
      });
      field('aspect').addEventListener('change', relock);
      field('width').addEventListener('input', relock);
      lock.addEventListener('click', () => { lock.classList.toggle('on'); relock(); });
      lowVram.addEventListener('click', () => lowVram.classList.toggle('on'));
      form.addEventListener('submit', async e => {
        e.preventDefault();
        const s = read();
        card.style.setProperty('--ar', (s.height || 1) / (s.width || 1));
        let r;
        try { r = await api.post('/api/images/generate', { settings: s }); } catch (err) { fail(err); return; }
        toast(r?.queued > 1 ? `${r.queued} pictures queued` : 'Picture queued');
        poll(gen);
      });
      $('#imgqueued').addEventListener('click', () => { $('#imgqueue').hidden = !$('#imgqueue').hidden; });
      $('#imgcancel').addEventListener('click', () => api.post('/api/images/cancel').then(() => poll(gen), fail));
      shot.addEventListener('load', () => card.classList.add('shot'));
      viewer.querySelector('[data-viewer-close]').addEventListener('click', closeViewer);
      viewer.querySelector('[data-viewer-delete]').addEventListener('click', () => askDelete(pictures[viewing].path));
      viewer.querySelector('[data-viewer-use]').addEventListener('click', async () => {
        let s;
        try { s = await api.get('/api/images/picture?path=' + encodeURIComponent(pictures[viewing].path)); }
        catch (err) { if (err.status === 404) toast('No settings were saved with this picture'); else fail(err); return; }
        fill(s);
        closeViewer();
        view.querySelector('.vbody').scrollTop = 0;
      });
    },
    show,
  });
}
