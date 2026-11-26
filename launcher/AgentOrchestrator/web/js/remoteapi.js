// The Remote API page (#view-remoteapi): run a worker on a service. Service chips with the key status (keys stay on the PC:
// the phone only sees set / not set), the model id with the PC's model list (search, favorites first), OpenRouter's providers in
// a tapped order, its price controls and Apply to the worker. The list order, the provider order, :floor and the apply body are
// pure (Node tests them); the DOM part loads only in a page.
import { api, latestOnly } from './api.js';
import { h } from './h.js';

/* ---------- pure ---------- */
export const fav = (serviceId, modelId) => serviceId + '|' + modelId;

// The models list: those matching `search` (id or name, any case), favorites first, the server's order otherwise.
export function modelList(models = [], favorites = [], serviceId = '', search = '') {
  const q = search.trim().toLowerCase(), favs = new Set(favorites);
  const hits = models.filter(m => !q || m.id.toLowerCase().includes(q) || (m.name || '').toLowerCase().includes(q))
    .map(m => ({ ...m, favorite: favs.has(fav(serviceId, m.id)) }));
  return [...hits.filter(m => m.favorite), ...hits.filter(m => !m.favorite)];
}

// A provider tapped: added at the end of the order, or taken out (the rest move up). `provider` is the order joined as the desktop.
export const toggleOrder = (order, name) => order.includes(name) ? order.filter(n => n !== name) : [...order, name];
export const providerList = order => order.join(', ');
export const parseOrder = text => (text || '').split(',').map(s => s.trim()).filter(Boolean);

// ":floor" on a model id: on adds it to a base id (an id with a variant of its own, ":free" etc., gets none), off takes it off.
const FLOOR = ':floor';
export const hasFloor = id => id.endsWith(FLOOR) && !id.slice(0, -FLOOR.length).includes(':');
export const baseId = id => hasFloor(id) ? id.slice(0, -FLOOR.length) : id;
export const floorable = id => !baseId(id.trim()).includes(':');
export const withFloor = (id, on) => { const b = baseId(id.trim()); return on && b && !b.includes(':') ? b + FLOOR : b; };

// POST /api/worker/{role}/remote's body from the form ({service, baseUrl, model, order, sortPrice, maxInput, maxOutput, floor}):
// the price settings and providers are OpenRouter's only (as the desktop, others get the bare id); the address is the service's
// own unless it is Custom URL.
export function applyBody(service, form) {
  const custom = !service.baseUrl, model = (form.model || '').trim();
  const base = { service: service.id, baseUrl: (custom ? form.baseUrl || '' : service.baseUrl).trim().replace(/\/+$/, '') };
  return service.hasProviders
    ? { ...base, model: withFloor(model, form.floor), provider: providerList(form.order || []), sortPrice: !!form.sortPrice,
      maxInput: (form.maxInput || '').trim(), maxOutput: (form.maxOutput || '').trim() }
    : { ...base, model, provider: '', sortPrice: false, maxInput: '', maxOutput: '' };
}

// The service a worker's `remote` uses: by id (or name), else by its address, else Custom URL (the one with no address).
export const serviceFor = (services, remote) => services.find(s => s.id === remote?.service || s.name === remote?.service)
  || services.find(s => s.baseUrl && s.baseUrl === (remote?.baseUrl || '').replace(/\/+$/, '')) || services.find(s => !s.baseUrl) || services[0];

export const price = p => p == null ? '—' : p === 0 ? 'free' : '$' + (+p).toFixed(p < 0.1 ? 3 : 2);
export const ctxText = n => !n ? '' : n >= 1e6 ? +(n / 1e6).toFixed(1) + 'M ctx' : Math.round(n / 1024) + 'K ctx';

/* ---------- the screen ---------- */
// Opens the page for a worker (its page imports this module, so the screen is registered by then).
export async function openRemoteApi(role) {
  const { go } = await import('./main.js');
  go('remoteapi', { role });
}

if (typeof document !== 'undefined') {
  const { registerScreen, back, fail, toast } = await import('./main.js');
  const $ = s => document.querySelector(s);
  const at = role => '/api/worker/' + encodeURIComponent(role);
  // The signed-out 403 and an unreachable PC go to the app's handler; the rest is shown in `el`.
  const inline = (err, el) => {
    if (err?.status === 0 || err?.message === 'SignedOut') { fail(err); return; }
    Object.assign(el, { hidden: false, textContent: err?.message || String(err) });
  };

  let role = null, worker = null, services = [], favorites = [], svc = null, models = null, providers = [], order = [];
  const modelId = () => $('#ramodel').value.trim();

  async function load() {
    let w, s;
    try { [w, s] = await Promise.all([api.get(at(role)), api.get('/api/remote/services')]); } catch (err) { fail(err); return; }
    worker = w;
    services = s.services || [];
    favorites = s.favorites || [];
    $('#raapply').textContent = 'Apply to ' + worker.title;
    $('#raerror').hidden = true;
    const r = worker.source === 'api' ? worker.remote : null;
    pickService(r ? serviceFor(services, r) : services[0], r);
  }

  // A service chosen: its key status, address field, an empty list; `r` (the worker's remote) prefills the rest.
  function pickService(s, r = null) {
    svc = s;
    if (!svc) return;
    $('#rasvc').replaceChildren(...services.map(x => h('button', { class: 'chip' + (x === svc ? ' on' : ''), onclick: () => x !== svc && pickService(x) }, x.name)));
    $('#rakey').textContent = svc.hasKey ? 'Key set' + (svc.keyEnv ? ' · ' + svc.keyEnv : '') : 'No key — add one on the PC';
    $('#raurlrow').hidden = !!svc.baseUrl;
    $('#raurl').value = !svc.baseUrl ? r?.baseUrl || '' : '';
    $('#ramodel').value = baseId(r?.model || '');
    $('#rafloor').classList.toggle('on', !!r && hasFloor(r.model || ''));
    $('#rasort').classList.toggle('on', !!r?.sortPrice);
    $('#ramaxin').value = r?.maxInput || '';
    $('#ramaxout').value = r?.maxOutput || '';
    $('#raprice').hidden = !svc.hasProviders;
    models = null;
    $('#ramodels').hidden = true;
    $('#ramdlnote').hidden = true;
    $('#rasearch').value = '';
    modelChanged(parseOrder(r?.provider));
  }

  // The model id changed (typed, tapped or prefilled): the providers reload and their order starts over (`keep` for a prefill).
  function modelChanged(keep = []) {
    order = keep;
    floorShown();
    if (models) drawModels();
    loadProviders();
  }

  function floorShown() {
    const id = modelId(), ok = floorable(id), sw = $('#rafloor');
    sw.disabled = !ok;
    if (!ok) sw.classList.remove('on');
    const saved = withFloor(id, sw.classList.contains('on'));
    $('#rafloornote').textContent = !ok ? 'This id already has a variant' : saved !== id && id ? 'Saved as ' + saved : 'Saves the model id with the suffix';
  }

  async function loadModels() {
    const btn = $('#raload'), note = $('#ramdlnote');
    note.hidden = true;
    btn.disabled = true;
    btn.replaceChildren(h('span', { class: 'spin' }), 'Loading models…');
    const q = new URLSearchParams({ service: svc.id });
    if (!svc.baseUrl) q.set('baseUrl', $('#raurl').value.trim());
    const asked = svc;
    try { models = (await api.get('/api/remote/models?' + q)).models || []; }
    catch (err) { if (asked === svc) inline(err, note); return; }
    finally { btn.disabled = false; btn.textContent = 'Load models'; }
    if (asked !== svc) return;
    $('#ramodels').hidden = false;
    drawModels();
  }

  function drawModels() {
    const list = modelList(models, favorites, svc.id, $('#rasearch').value), id = modelId();
    $('#ramdllist').replaceChildren(...(list.length ? list.map(m => h('div', { class: 'mdlrow' + (m.id === id ? ' on' : '') },
      h('button', { class: 'mdlmain', onclick: () => { $('#ramodel').value = m.id; modelChanged(); } },
        h('b', {}, m.name || m.id), h('small', {}, [m.name && m.name !== m.id && m.id, `${price(m.priceIn)} / ${price(m.priceOut)}`, ctxText(m.context)].filter(Boolean).join(' · '))),
      m.vision && h('span', { class: 'badge ok' }, 'Vision'),
      h('button', { class: 'star' + (m.favorite ? ' on' : ''), 'aria-label': 'Favorite', onclick: () => star(m) }, '★')))
      : [h('p', { class: 'hintline' }, models.length ? 'No model matches.' : 'The service listed no models.')]));
  }

  async function star(m) {
    const id = fav(svc.id, m.id), on = !m.favorite;
    try { await api.post('/api/favorites', { kind: 'remote', id, on }); } catch (err) { fail(err); return; }
    favorites = on ? [...favorites, id] : favorites.filter(f => f !== id);
    drawModels();
  }

  const fetchProviders = latestOnly(id => api.get('/api/remote/providers?model=' + encodeURIComponent(id)));
  async function loadProviders() {
    const box = $('#raprov'), id = baseId(modelId());
    providers = [];
    box.hidden = !svc.hasProviders || !id;
    if (box.hidden) return;
    $('#raprovlist').replaceChildren(h('p', { class: 'hintline' }, h('span', { class: 'spin' }), 'Loading providers…'));
    try { providers = (await fetchProviders(id)).providers || []; }
    catch (err) { $('#raprovlist').replaceChildren(); inline(err, $('#raorder')); return; }
    drawProviders();
  }

  function drawProviders() {
    const figure = (v, unit, digits) => v == null ? null : (+v).toFixed(digits) + unit;
    $('#raprovlist').replaceChildren(...(providers.length ? providers.map(p => {
      const n = order.indexOf(p.name) + 1;
      return h('div', { class: 'prov' },
        h('button', { class: 'tickbox' + (n ? ' on' : ''), dataset: n ? { n } : {}, 'aria-label': n ? `Provider ${n}: tap to remove` : 'Use provider',
          onclick: () => { order = toggleOrder(order, p.name); drawProviders(); } }),
        h('span', { class: 'prov-name' }, p.name, p.vision && h('span', { class: 'badge ok' }, 'Vision')),
        h('span', {}, `${price(p.priceIn)} / ${price(p.priceOut)}`),
        h('small', {}, [figure(p.latency, 's', 1), figure(p.tps, ' tok/s', 0), figure(p.uptime, '%', 1), p.quant && p.quant !== 'unknown' && p.quant]
          .filter(Boolean).join(' · ')));
    }) : [h('p', { class: 'hintline' }, 'No providers listed for this model.')]));
    Object.assign($('#raorder'), { hidden: false, textContent: order.length ? 'Only, in this order: ' + providerList(order)
      : 'Nothing ticked: OpenRouter picks any provider. Tap providers to use only those, in the order tapped.' });
  }

  async function apply() {
    const err = $('#raerror');
    err.hidden = true;
    const body = applyBody(svc, { baseUrl: $('#raurl').value, model: modelId(), order, floor: $('#rafloor').classList.contains('on'),
      sortPrice: $('#rasort').classList.contains('on'), maxInput: $('#ramaxin').value, maxOutput: $('#ramaxout').value });
    try { await api.post(at(role) + '/remote', body); } catch (e) { inline(e, err); return; }
    toast('Applied to ' + worker.title);
    back();
  }

  registerScreen('remoteapi', {
    mount() {
      $('#raload').addEventListener('click', loadModels);
      $('#rasearch').addEventListener('input', () => models && drawModels());
      $('#ramodel').addEventListener('change', () => modelChanged());
      $('#ramodel').addEventListener('input', floorShown);
      $('#rafloor').addEventListener('click', e => { e.currentTarget.classList.toggle('on'); floorShown(); });
      $('#raapply').addEventListener('click', apply);
    },
    show(params) {
      if (params.role) role = params.role;
      load();
    },
  });
}
