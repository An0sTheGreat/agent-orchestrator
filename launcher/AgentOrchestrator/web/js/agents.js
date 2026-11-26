// Settings > Agents' worker list (GET /api/workers, under settings.js's global rows) and a worker's page (#view-agent): its model
// (the local picker, the primary PC's models or the Remote API page), the options form from GET /api/worker/{role}, the GPU
// estimate, presets, Revert and Save. The form model, the fields sent, the Bool mapping, the GPU bar and the preset groups are
// pure (Node tests them); the DOM part loads only in a page.
import { api, latestOnly } from './api.js';
import { h, icon } from './h.js';

/* ---------- pure ---------- */
export const SOURCES = { local: 'Local', primary: 'Primary PC', api: 'Remote API' };

// A Bool option's seg: Default (the key removed) / On / Off; boolValue reads any stored form as one of them.
export const BOOL = [['', 'Default'], ['on', 'On'], ['off', 'Off']];
export const boolValue = v => v === true || v === 'on' || v === 'true' ? 'on' : v === false || v === 'off' || v === 'false' ? 'off' : '';

const text = v => v == null ? '' : String(v);
// The form's starting values (key -> string; a Bool as '' / 'on' / 'off').
export const initialValues = (options = []) =>
  Object.fromEntries(options.map(o => [o.key, o.kind === 'Bool' ? boolValue(o.value) : text(o.value)]));

// The options as the form draws them: max_seq_length apart (the Context card), the rest in groups in the server's order, a group
// advanced when all its options are; apiOnly options only for an API worker.
export function formModel(options = [], isApi = false) {
  const groups = [];
  let context = null;
  for (const o of options) {
    if (o.apiOnly && !isApi) continue;
    if (o.key === 'max_seq_length') { context = o; continue; }
    let g = groups.find(x => x.name === o.group);
    if (!g) groups.push(g = { name: o.group, options: [] });
    g.options.push(o);
  }
  for (const g of groups) g.advanced = g.options.every(o => o.advanced);
  return { context, groups };
}

// Save's fields: only those that differ from what the server sent ('' = back to the default: the key removed).
export const changedFields = (options = [], values = {}) => Object.fromEntries(Object.entries(initialValues(options))
  .filter(([k, v]) => k in values && text(values[k]).trim() !== v.trim()).map(([k]) => [k, text(values[k]).trim()]));
// The GPU estimate's and a preset's fields: the non-empty ones (empty = the default).
export const filledFields = (values = {}) =>
  Object.fromEntries(Object.entries(values).map(([k, v]) => [k, text(v).trim()]).filter(([, v]) => v !== ''));

export const gb = bytes => (bytes / 2 ** 30).toFixed(1) + ' GB';

// POST .../vram's answer as the bar: each known part's width in percent of the GPU (or of the total when it is bigger or the GPU
// is unknown, so the bar never overflows) and the fit text, null when the GPU size or the estimate is unknown.
export function vramView(r) {
  const g = r?.gpu || {}, cap = r?.gpuTotal || 0, total = g.total || 0, scale = Math.max(cap, total) || 1;
  const parts = [['weights', 'vw', 'Weights'], ['kv', 'vk', 'KV cache'], ['compute', 'vc', 'Compute'], ['vision', 'vv', 'Vision']]
    .filter(([k]) => g[k] > 0).map(([k, cls, label]) => ({ cls, label, bytes: g[k], pct: Math.round(g[k] / scale * 1000) / 10 }));
  const fits = cap && total ? Math.max(total, r.whole || 0) <= cap : null;
  return { parts, total, gpuTotal: cap || null, fits, text: fits == null ? '' : fits ? 'fits' : 'spills to RAM' };
}

// The presets sheet's groups (empty ones left out), each item marked active when it is the worker's current preset.
export const presetGroups = (presets = {}) => (presets.groups || []).filter(g => g.items?.length)
  .map(g => ({ name: g.name, items: g.items.map(i => ({ ...i, active: i.name === presets.active })) }));
export const presetExists = (presets, name) => (presets?.groups || []).some(g => (g.items || []).some(i => i.name === name));

/* ---------- the screens ---------- */
if (typeof document !== 'undefined') {
  const { registerScreen, go, back, fail, toast, openSheet, closeOverlays, confirmSheet, askName } = await import('./main.js');
  const { longPress } = await import('./util.js');
  const $ = s => document.querySelector(s);
  const at = role => '/api/worker/' + encodeURIComponent(role);
  const sizeText = s => typeof s === 'number' ? gb(s) : s || '';

  /* ----- Settings > Agents: the workers ----- */
  async function loadWorkers() {
    let r;
    try { r = await api.get('/api/workers'); } catch (err) { fail(err); return; }
    $('#workerlist').replaceChildren(h('p', { class: 'secthead' }, 'WORKERS'),
      h('div', { class: 'list' }, (r.workers || []).map(w => h('div', { class: 'rowline skillrow' },
        h('button', { class: 'item-text', onclick: () => go('agent', { role: w.role }) }, h('b', {}, w.title),
          h('small', {}, [w.model || 'No model', SOURCES[w.source]].filter(Boolean).join(' · ')), w.problem && h('small', { class: 'danger' }, w.problem)),
        h('button', { class: 'switch' + (w.shown ? ' on' : ''), 'aria-label': 'Show ' + w.title + ' in Chat', onclick: async e => {
          const el = e.currentTarget, shown = !el.classList.contains('on');
          el.classList.toggle('on', shown);
          try { await api.post(at(w.role) + '/shown', { shown }); } catch (err) { el.classList.toggle('on', !shown); fail(err); }
        } })))),
      h('p', { class: 'hintline' }, 'The switch shows a worker in Chat.'),
      h('div', { class: 'pad' }, h('button', { class: 'btn ghost', onclick: addWorker }, 'Add worker')));
  }
  async function addWorker() {
    let r;
    try { r = await api.post('/api/workers/add'); } catch (err) { fail(err); return; }
    go('agent', { role: r.role });
  }
  document.addEventListener('ao:agents', loadWorkers); // settings.js shows the Agents page

  /* ----- a worker's page ----- */
  let role = null, w = null, values = {}, advanced = false, primary = null, models = null, errorEls = {};
  const isApi = () => w?.source === 'api';

  async function loadWorker() {
    try { w = await api.get(at(role)); } catch (err) { fail(err); return; }
    values = initialValues(w.options);
    draw();
  }

  function draw() {
    const view = $('#view-agent');
    view.querySelector('.vtitle').textContent = w.title;
    view.querySelector('.vsub').textContent = w.custom ? 'Custom worker · tap the name to rename' : 'Worker options';
    $('#agentmore').hidden = !w.custom;
    errorEls = {};
    const { context, groups } = formModel(w.options, isApi());
    view.querySelector('.vbody').replaceChildren(modelCard(), context ? contextCard(context) : null, vramCard(),
      ...groups.map(g => h('div', { class: 'optgroup' + (g.advanced ? ' adv' : ''), hidden: g.advanced && !advanced },
        h('p', { class: 'secthead' }, g.name), h('div', { class: 'fields' }, g.options.map(field)))),
      w.custom && customCard(),
      h('div', { class: 'pad two' }, h('button', { class: 'btn ghost', 'data-sheet': 'sheet-mdlpresets', onclick: drawPresets }, 'Presets'),
        h('button', { class: 'btn ghost', onclick: () => { loadWorker(); toast('Reverted'); } }, 'Revert'),
        h('button', { class: 'btn', onclick: save }, 'Save')));
    askVram();
  }

  function modelCard() {
    const src = w.source || 'local', name = src === 'local' ? w.model?.label || w.model?.name : w.remote?.model || w.model?.name;
    const seg = h('div', { class: 'seg', onchange: e => pickSource(e.currentTarget.dataset.value, e.currentTarget) },
      Object.entries(SOURCES).filter(([k]) => k !== 'primary' || primary || src === 'primary')
        .map(([k, label]) => h('button', { class: k === src ? 'on' : null, 'data-seg-on': k }, label)));
    return h('div', { class: 'card' }, h('p', { class: 'secthead inline' }, 'MODEL'),
      h('button', { class: 'selectrow', onclick: () => src === 'api' ? remoteApi() : src === 'primary' ? openPrimary() : openModels() },
        h('span', {}, name || 'Choose a model'), icon('down', 'ic sm')),
      src === 'local' && h('p', { class: 'muted' }, 'Quantization ', h('b', {}, w.model?.quant || '—'), ' · installed models only'),
      src === 'api' && h('p', { class: 'muted' }, [w.remote?.service, w.remote?.hasKey === false && 'no key set on the PC'].filter(Boolean).join(' · ')),
      h('p', { class: 'secthead inline' }, 'MODEL SOURCE'), seg,
      h('p', { class: 'muted' }, src === 'api' ? "Remote API sends this worker's requests to an OpenAI-compatible endpoint."
        : src === 'primary' ? 'Runs on your primary PC.' : 'Runs on this PC.'));
  }

  // The seg is put back at once; the switch draws the new source when the PC has it.
  async function pickSource(to, seg) {
    [...seg.children].forEach(b => b.classList.toggle('on', b.dataset.segOn === w.source));
    if (to === 'api') return remoteApi();
    if (to === 'primary') return openPrimary();
    if (to === 'local' && w.source !== 'local') {
      try { await api.post(at(role) + '/local'); } catch (err) { fail(err); return; }
      loadWorker();
    }
  }
  const remoteApi = async () => (await import('./remoteapi.js')).openRemoteApi(role);

  function contextCard(o) {
    const v = () => +values[o.key] || +o.default || 0, max = Math.max(131072, v());
    const fmt = n => n ? n.toLocaleString('en-US') : 'Auto';
    const pill = h('span', { class: 'pill' }, fmt(v()));
    const slider = h('input', { class: 'slider', type: 'range', min: 4096, max, step: 2048, value: v(), 'aria-label': 'Context length' });
    const box = h('input', { type: 'number', min: o.min, max: o.max, step: 1, value: values[o.key], placeholder: text(o.default), name: o.key });
    slider.addEventListener('input', () => { values[o.key] = box.value = slider.value; pill.textContent = fmt(+slider.value); askVramSoon(); });
    box.addEventListener('input', () => { values[o.key] = box.value; slider.value = v(); pill.textContent = fmt(v()); askVramSoon(); });
    const adv = h('button', { class: 'switch' + (advanced ? ' on' : ''), 'aria-label': 'Advanced settings', onclick: e => {
      advanced = !advanced;
      e.currentTarget.classList.toggle('on', advanced);
      document.querySelectorAll('#view-agent .optgroup.adv').forEach(g => { g.hidden = !advanced; });
    } });
    return h('div', { class: 'card' }, h('div', { class: 'rowline' }, h('div', {}, h('b', {}, o.label || 'Context length')), pill), slider,
      h('p', { class: 'muted between' }, h('span', {}, '4,096'), h('span', {}, max.toLocaleString('en-US'))),
      h('div', { class: 'fields' }, h('label', { class: 'span' }, 'Tokens', box, errorEl(o.key))),
      h('div', { class: 'rowline' }, h('div', {}, h('b', {}, 'Advanced settings'), h('small', {}, 'KV cache, batch, GPU layers, extra args')), adv));
  }

  function errorEl(key) { return errorEls[key] = h('small', { class: 'danger', hidden: true }); }

  // One option's input by kind; empty = the default (its placeholder).
  function field(o) {
    const set = v => { values[o.key] = v; askVramSoon(); };
    const tip = o.help || null;
    if (o.kind === 'Bool') return h('div', { class: 'rowline span', title: tip }, h('div', {}, h('b', {}, o.label)),
      h('div', { class: 'seg small', onchange: e => set(e.currentTarget.dataset.value) },
        BOOL.map(([v, label]) => h('button', { class: v === values[o.key] ? 'on' : null, 'data-seg-on': v }, label))), errorEl(o.key));
    let input;
    if (o.kind === 'Multiline') input = h('textarea', { rows: 5, spellcheck: 'false', placeholder: text(o.default) }, values[o.key]);
    else if (o.kind === 'Choice') {
      input = h('select', {}, h('option', { value: '' }, o.default ? `Default (${o.default})` : 'Default'),
        (o.choices || []).map(c => h('option', { value: c }, c)));
      input.value = values[o.key];
    } else {
      const num = o.kind === 'Int' || o.kind === 'Number';
      input = h('input', { type: num ? 'number' : 'text', min: num ? o.min : null, max: num ? o.max : null,
        step: num ? (o.kind === 'Int' ? 1 : 'any') : null, value: values[o.key], placeholder: text(o.default), spellcheck: 'false' });
    }
    input.addEventListener(o.kind === 'Choice' ? 'change' : 'input', () => set(input.value));
    const wide = o.kind === 'Multiline' || /List$/.test(o.kind) || o.kind === 'Text';
    return h('label', { class: wide ? 'span field' : null, title: tip }, o.label, input, errorEl(o.key));
  }

  let custom = null; // the custom worker card's values
  function customCard() {
    custom = { title: w.title, kind: w.custom.kind || 'specialist', description: w.custom.description || '', systemPrompt: w.custom.systemPrompt || '' };
    const desc = h('input', { value: custom.description, oninput: e => { custom.description = e.target.value; } });
    const prompt = h('textarea', { rows: 8, spellcheck: 'false', oninput: e => { custom.systemPrompt = e.target.value; } }, custom.systemPrompt);
    return h('div', { class: 'card' }, h('p', { class: 'secthead inline' }, 'CUSTOM WORKER'),
      h('div', { class: 'list', 'data-radio-group': true }, [['specialist', 'Specialist', 'Does the work it is sent'], ['reviewer', 'Reviewer', 'Checks finished work']]
        .map(([k, label, hint]) => h('button', { class: 'radiorow' + (k === custom.kind ? ' on' : ''), 'data-radio': true, onclick: () => { custom.kind = k; } },
          h('span', { class: 'item-text' }, h('b', {}, label), h('small', {}, hint)), h('span', { class: 'tick' })))),
      h('label', { class: 'field' }, h('span', {}, 'Description'), desc),
      h('label', { class: 'field' }, h('span', {}, 'System prompt'), prompt));
  }

  async function save() {
    Object.values(errorEls).forEach(el => { el.hidden = true; });
    let r;
    try { r = await api.post(at(role) + '/save', { fields: changedFields(w.options, values), ...(w.custom && custom ? { custom } : {}) }); }
    catch (err) {
      const errors = err.status === 400 && err.body?.errors;
      if (!errors) { fail(err); return; }
      for (const [k, msg] of Object.entries(errors)) if (errorEls[k]) Object.assign(errorEls[k], { hidden: false, textContent: msg });
      toast(errors[''] || 'Check the highlighted fields');
      return;
    }
    toast(r.note || 'Saved');
    if (r.worker) { w = r.worker; values = initialValues(w.options); draw(); } else loadWorker();
  }

  /* ----- the GPU estimate, re-asked 400 ms after the last edit ----- */
  let vramTimer = 0;
  const vram = latestOnly(fields => api.post(at(role) + '/vram', { fields }));
  function vramCard() {
    return h('div', { class: 'card vramcard', hidden: w.source !== 'local' },
      h('div', { class: 'vram-head' }, h('span', { class: 'vram-label' }, 'GPU estimate'), h('span', { class: 'vram-total' }, '…')),
      h('p', { class: 'vram-sub' }), h('div', { class: 'vrambar' }), h('div', { class: 'vramlegend' }));
  }
  function askVramSoon() { clearTimeout(vramTimer); vramTimer = setTimeout(askVram, 400); }
  async function askVram() {
    clearTimeout(vramTimer);
    const card = $('#view-agent .vramcard');
    if (!card || card.hidden) return;
    let r;
    try { r = await vram(filledFields(values)); } catch { card.querySelector('.vram-sub').textContent = 'No estimate for this model'; return; }
    const v = vramView(r);
    card.querySelector('.vram-total').textContent = v.total ? gb(v.total) : '—';
    card.querySelector('.vram-total').classList.toggle('over', v.fits === false);
    card.querySelector('.vram-sub').textContent = v.gpuTotal ? `of ${gb(v.gpuTotal)} · ${v.text}` : 'GPU size unknown';
    card.querySelector('.vrambar').replaceChildren(...v.parts.map(p => { const i = h('i', { class: p.cls }); i.style.width = p.pct + '%'; return i; }));
    card.querySelector('.vramlegend').replaceChildren(...v.parts.map(p => h('span', {}, h('i', { class: p.cls }), p.label, ' ', h('b', {}, gb(p.bytes)))));
  }

  /* ----- the local picker and favorites ----- */
  const GROUPS = [['text', 'TEXT ONLY'], ['vision', 'VISION CAPABLE'], ['image', 'IMAGE GENERATION']];
  async function openModels() {
    openSheet('sheet-models');
    if (!models) {
      $('#mdlList').replaceChildren(h('p', { class: 'hintline' }, 'Loading…'));
      try { models = (await api.get('/api/models/local')).models || []; } catch (err) { fail(err); return; }
    }
    drawModels();
  }
  function mdlRow(m) {
    const star = h('button', { class: 'star' + (m.favorite ? ' on' : ''), 'aria-label': 'Favorite', onclick: async () => {
      const on = !m.favorite;
      try { await api.post('/api/favorites', { kind: 'local', id: m.label, on }); } catch (err) { fail(err); return; }
      m.favorite = on;
      drawModels();
    } }, '★');
    return h('div', { class: 'mdlrow' + (m.label === w.model?.label ? ' on' : '') },
      h('button', { class: 'mdlmain', disabled: !m.pickable, onclick: () => pickModel(m) },
        h('b', {}, m.display || m.label), h('small', {}, [sizeText(m.size), m.quant, !m.pickable && 'not pickable'].filter(Boolean).join(' · '))), star);
  }
  function drawModels() {
    const list = models || [];
    $('#mdlList').replaceChildren(...(list.length ? GROUPS.flatMap(([g, title]) => {
      const rows = list.filter(m => m.group === g);
      return rows.length ? [h('p', { class: 'secthead' }, title), ...rows.map(mdlRow)] : [];
    }) : [h('p', { class: 'hintline' }, 'No models in the models folder on the PC.')]));
    const favs = list.filter(m => m.favorite);
    $('#mdlFavList').replaceChildren(...(favs.length ? favs.map(mdlRow) : [h('p', { class: 'hintline' }, 'No favorites yet. Star a model.')]));
  }
  async function pickModel(m) {
    try { await api.post(at(role) + '/model', { label: m.label }); } catch (err) { fail(err); return; }
    closeOverlays();
    toast('Takes effect from the next load');
    loadWorker();
  }

  /* ----- the primary PC's models ----- */
  async function openPrimary() {
    let r;
    try { r = await api.get('/api/primary/models'); } catch (err) { fail(err); return; }
    $('#sheet-primary .secthead').textContent = 'MODELS ON ' + (r.host || 'THE PRIMARY PC').toUpperCase();
    $('#primList').replaceChildren(...(r.models || []).map(m => h('div', { class: 'mdlrow' + (w.source === 'primary' && m.id === w.remote?.model ? ' on' : '') },
      h('button', { class: 'mdlmain', onclick: async () => {
        try { await api.post(at(role) + '/primary', { model: m.id }); } catch (err) { fail(err); return; }
        closeOverlays();
        loadWorker();
      } }, h('b', {}, m.id)))));
    openSheet('sheet-primary');
  }

  /* ----- presets ----- */
  function drawPresets() {
    const groups = presetGroups(w.presets);
    $('#presetList').replaceChildren(...(groups.length ? groups.flatMap(g => [h('p', { class: 'secthead' }, g.name), ...g.items.map(i => {
      const row = h('button', { class: 'drawer-item', onclick: () => applyPreset(i.name) }, icon('orb', 'ic sm'), i.name,
        i.active && h('small', { class: 'count' }, 'current'));
      longPress(row, () => confirmSheet('Delete preset', `Delete "${i.name}"?`, () => presetCall('delete', { name: i.name }, 'Preset deleted')));
      return row;
    })]) : [h('p', { class: 'hintline' }, 'No presets yet.')]));
  }
  async function applyPreset(name) {
    let r;
    try { r = await api.post(at(role) + '/preset/apply', { name }); } catch (err) { fail(err); return; }
    closeOverlays();
    Object.assign(values, Object.fromEntries(Object.entries(r.fields || {}).map(([k, v]) => [k, w.options.find(o => o.key === k)?.kind === 'Bool' ? boolValue(v) : text(v)])));
    w.presets = { ...w.presets, active: name };
    draw();
    toast(`"${name}" applied · Save to keep it`);
  }
  async function presetCall(what, body, done) {
    let r;
    try { r = await api.post(at(role) + '/preset/' + what, body); } catch (err) { fail(err); return; }
    if (r.presets) w.presets = r.presets;
    toast(done);
  }
  function savePreset() {
    askName('SAVE PRESET AS', w.presets?.active || '', name => {
      if (!name) return;
      const store = () => presetCall('save', { name, fields: filledFields(values) }, 'Preset saved');
      if (presetExists(w.presets, name)) confirmSheet('Replace preset', `Replace "${name}" with these settings?`, store, 'Replace');
      else store();
    });
  }

  registerScreen('agent', {
    mount() {
      $('#agentpresets').addEventListener('click', drawPresets);
      $('#presetsave').addEventListener('click', savePreset);
      $('#view-agent .vtitle').addEventListener('click', () => {
        if (w?.custom) askName('RENAME WORKER', w.title, async title => {
          try { await api.post(at(role) + '/rename', { title }); } catch (err) { fail(err); return; }
          loadWorker();
        });
      });
      $('#workerremove').addEventListener('click', () => confirmSheet('Remove worker', `Remove "${w.title}"? Its settings go with it.`, async () => {
        try { await api.post(at(role) + '/remove'); } catch (err) { fail(err); return; }
        back();
      }, 'Remove'));
    },
    show(params) {
      if (params.role) role = params.role;
      models = null;
      api.get('/api/primary/models').then(() => true, () => false).then(ok => { if (primary !== ok) { primary = ok; if (w) draw(); } });
      loadWorker();
    },
  });
}
