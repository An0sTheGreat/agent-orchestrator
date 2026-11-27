// The Settings tab: the desktop's Settings (General, Appearance, Models, Agents, Plugins, About) from GET /api/settings; each control
// writes POST /api/settings {key, value} at once and draws the settings the PC answers with (a refusal redraws the last ones). The
// GET shape -> rows, the normalising and the accent -> tokens mapping are pure (Node tests them); the DOM part loads only in a page.
import { api } from './api.js';
import { h, icon } from './h.js';

/* ---------- pure ---------- */
// The GET shape with every section and list present; followTabs 3..11 (0, missing or 11+ is Unlimited = 11, as the desktop's slider).
export function normalize(s = {}) {
  const g = s.general || {}, a = s.appearance || {}, m = s.models || {}, tabs = Number(g.followTabs);
  return {
    general: { ...g, linkOpen: g.linkOpen || '', followTabs: !tabs || tabs >= 11 ? 11 : Math.max(3, Math.round(tabs)),
      computerApps: g.computerApps || [], stats: g.stats || '', phoneApp: g.phoneApp || {} },
    appearance: { ...a, themes: a.themes || [], accents: a.accents || [] },
    models: { ...m, share: m.share || {} },
    agents: { ...s.agents, codeEngine: s.agents?.codeEngine || '' },
    plugins: { ponytail: s.plugins?.ponytail || '' },
    about: s.about || {},
  };
}

export const tabsText = n => n >= 11 ? 'Unlimited' : `${n} tabs`;
// An approved application's name from its .exe path.
export const appName = path => String(path).split(/[\\/]/).pop();
export const currentAccent = s => s.appearance.accents.find(x => x.key === s.appearance.accent);

// The phone's accent tokens for a colour ("#RRGGBB", or WPF's "#AARRGGBB"): the colour, a lighter one (40% toward white) and the
// text on it (dark on a light accent, as the concept's swatches); null for anything else.
export function accentTokens(color) {
  const m = /^#?(?:[0-9a-f]{2})?([0-9a-f]{6})$/i.exec(color || '');
  if (!m) return null;
  const rgb = [0, 2, 4].map(i => parseInt(m[1].slice(i, i + 2), 16));
  const hex = cs => '#' + cs.map(c => c.toString(16).padStart(2, '0')).join('').toUpperCase();
  const lin = c => (c /= 255) <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  const lum = 0.2126 * lin(rgb[0]) + 0.7152 * lin(rgb[1]) + 0.0722 * lin(rgb[2]);
  return { '--accent': hex(rgb), '--accent-light': hex(rgb.map(c => Math.round(c + (255 - c) * 0.4))),
    '--on-accent': lum > 0.3 ? '#141414' : '#FFFFFF' };
}

// The Usage page's bars from GET /api/usage's `days` ([{day: "yyyy-MM-dd", tokens}], oldest first): each one's height in percent of
// the busiest day (0 when every day is 0), its weekday letter and its tokens.
export function usageBars(days = []) {
  const max = Math.max(0, ...days.map(d => d.tokens || 0));
  return days.map(({ day, tokens = 0 }) => {
    const [y, m, d] = String(day).split('-').map(Number);
    return { letter: 'SMTWTFS'[new Date(y, m - 1, d).getDay()] || '', h: max ? Math.round(tokens / max * 100) : 0, tokens };
  });
}
// A task's length in seconds as "1 h 05 min", "12 min 30 s" or "45 s".
export function duration(sec) {
  const t = Math.max(0, Math.round(sec || 0)), hh = Math.floor(t / 3600), mm = Math.floor(t % 3600 / 60), ss = t % 60;
  return hh ? `${hh} h ${String(mm).padStart(2, '0')} min` : mm ? `${mm} min ${ss} s` : `${ss} s`;
}

// The phone asks before these go on (the desktop's own switches do not ask; the phone is a step further from the PC).
const CONFIRMS = {
  fullAccess: { title: 'Turn on Full access', yes: 'Turn on', text: "This turns off the command sandbox's isolation: commands run " +
    "as you, with your rights, anywhere on this PC and with the network, and the model's file tools read and write every folder " +
    'except Windows\\System32, in Code and in normal chats.' },
  resetStats: { title: 'Reset usage stats', yes: 'Reset', text: "Set lifetime tokens, peak tokens, longest task and streaks back to zero? This can't be undone." },
};
export const confirmFor = (key, value) => value === true ? CONFIRMS[key] || null : null;

// A page's rows, top to bottom: {type: head | note | info | switch | seg | radio | slider | accents | button, ...}. Writable rows carry
// the POST key and the current value; seg and radio options are [value, label, hint?].
export function rows(page, s) {
  const { general: g, appearance: a, models: m, agents: ag, plugins: p, about } = s;
  const head = text => ({ type: 'head', text }), note = text => ({ type: 'note', text });
  const sw = (key, value, label, hint) => ({ type: 'switch', key, value: !!value, label, hint });
  const seg = (key, value, label, options, wide = false) => ({ type: 'seg', key, value, label, options, wide });
  const radio = (key, value, options) => ({ type: 'radio', key, value, options });
  const info = (label, text, hint) => ({ type: 'info', label, text, hint });
  switch (page) {
    case 'general': return [
      head('Startup'), sw('startWithWindows', g.startWithWindows, 'Start with Windows', 'Opens in the system tray when you sign in to Windows.'),
      head('Notifications'), sw('notifyWhenDone', g.notifyWhenDone, 'Notify when done', 'A desktop notification when a reply or run finishes, while the app is not in front.'),
      head('Links'), seg('linkOpen', g.linkOpen, 'Open links in', [['', 'Ask me'], ['app', 'In-app browser'], ['default', 'Default browser']], true),
      { type: 'slider', key: 'followTabs', value: g.followTabs, min: 3, max: 11, label: 'Tabs the model may open' },
      head('Computer use'), sw('computerUse', g.computerUse, 'Let the model use applications', 'One window per reply, your real mouse; Esc stops it.'),
      info('Approved applications', g.computerApps.length ? g.computerApps.map(appName).join(', ') : 'None approved.', 'Add or remove them on the PC.'),
      head('Usage stats'), { type: 'nav', open: 'view-usage', label: 'Usage & tokens', text: g.stats || '—' },
      head('Phone app'), info('Phone app over Tailscale', g.phoneApp.on ? 'On' + (g.phoneApp.address ? ' · ' + g.phoneApp.address : '') : 'Off',
        'It can only be turned off at the PC.')];
    case 'appearance': return [
      head('Theme'), radio('theme', a.theme, a.themes.map(t => [t.key, t.name])),
      head('Accent · ' + (currentAccent(s)?.name || a.accent || '')), { type: 'accents', key: 'accent', value: a.accent, options: a.accents },
      note('Each theme remembers its own accent colour. The phone takes the accent and keeps its dark look.'),
      head('Conversation'), sw('centerChat', a.centerChat, 'Center the conversation', 'A readable width in the middle of the column'),
      seg('readingSize', a.readingSize, 'Reading size', [['compact', 'Compact'], ['comfortable', 'Comfortable'], ['large', 'Large']], true),
      seg('emoji', a.emoji, 'Emoji in replies', [['none', 'None'], ['less', 'Less'], ['standard', 'Standard'], ['more', 'More']], true),
      sw('contextPercent', a.contextPercent, 'Show the context percentage')];
    case 'models': return [
      head('Loading'), sw('keepLoaded', m.keepLoaded, 'Keep models loaded', 'Replies start faster; off frees GPU memory after each reply.'),
      head('Several models at once'), radio('multiModel', m.multiModel, [['never', 'Never', 'One model at a time'],
        ['fit', 'If they fit the GPU', 'Side by side in free GPU memory (NVIDIA)'], ['always', 'Load anyway', 'What does not fit runs slower from RAM']]),
      head('Share models with my other PCs'), sw('share.on', m.share.on, "Share this PC's models", 'For the app on your other PCs'),
      note('The model runtime, models folders, connection codes and keys stay on the PC.')];
    case 'agents': return [
      head('Workflow'), radio('workflow', ag.workflow, [['dual', 'Lead + Reviewer', 'The reviewer checks every finished item'],
        ['tri', 'Lead + Implementer + Reviewer', 'The lead plans, the implementer writes'], ['solo', 'Lead only', 'No independent check']]),
      head('Code engine'), radio('codeEngine', ag.codeEngine, [['', 'Built-in', "The app's own agent loop and tools"], ['pi', 'Pi', 'pi.dev agent harness']]),
      head('Command sandbox'),
      sw('fullAccess', ag.fullAccess, 'Full access', "Commands run outside the sandbox, as you, with the network; the model's file tools reach every folder except System32."),
      sw('sandboxNetwork', ag.sandboxNetwork, 'Commands may use the network without asking'),
      note('Installing Pi and setting up sandbox access stay on the PC.')];
    case 'plugins': return [
      head('Ponytail'), seg('ponytail', p.ponytail, '', [['', 'Off'], ['lite', 'Lite'], ['full', 'Full'], ['ultra', 'Ultra']], true),
      note("Adds the Ponytail prompt after every model's system prompt, in all chats: the smallest change that fully works. Lite names " +
        'the smaller option; Ultra also pushes back on the request. About 1,000 tokens a message.')];
    case 'about': return [info('Version', about.version || '—'), info('PC', about.pc || '—')];
    default: return [];
  }
}

/* ---------- the screen ---------- */
if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, confirmSheet } = await import('./main.js');
  const $ = s => document.querySelector(s);
  const PAGES = ['general', 'appearance', 'models', 'agents', 'plugins', 'about'];
  let state = null, page = 'settings';

  // The accent of the PC's current theme on the phone's own tokens (on every GET and write).
  const applyAccent = () => {
    const tokens = accentTokens(currentAccent(state)?.color);
    for (const [k, v] of Object.entries(tokens || {})) document.documentElement.style.setProperty(k, v);
  };
  const take = s => { state = normalize(s); applyAccent(); draw(); };
  const load = () => api.get('/api/settings').then(take, fail);

  // A write; the PC's answer is drawn (it may normalise), a refusal (400/409: its message) redraws the settings from before.
  async function write(key, value) {
    let r;
    try { r = await api.post('/api/settings', { key, value }); } catch (err) { fail(err); draw(); return; }
    take(r.settings);
  }
  function change(key, value) {
    const ask = confirmFor(key, value);
    if (ask) confirmSheet(ask.title, ask.text, () => write(key, value), ask.yes);
    else write(key, value);
  }
  function resetStats() {
    const ask = confirmFor('resetStats', true);
    confirmSheet(ask.title, ask.text, async () => {
      try { await api.post('/api/settings/reset-stats'); } catch (err) { fail(err); return; }
      toast('Usage stats reset');
      load();
      if (page === 'usage') loadUsage();
    }, ask.yes);
  }

  const label = r => h('div', {}, r.label && h('b', {}, r.label), r.text != null && h('small', {}, r.text), r.hint && h('small', {}, r.hint));
  function row(r) {
    switch (r.type) {
      case 'head': return h('p', { class: 'secthead' }, r.text);
      case 'note': return h('p', { class: 'hintline' }, r.text);
      case 'info': return h('div', { class: 'rowline' }, label(r));
      case 'nav': return h('button', { class: 'navrow flat', 'data-open': r.open }, h('span', { class: 'item-text' }, h('b', {}, r.label), h('small', {}, r.text)),
        icon('chevron', 'ic sm chev'));
      case 'switch': return h('div', { class: 'rowline' }, label(r), h('button', {
        class: 'switch' + (r.value ? ' on' : ''), 'aria-label': r.label, 'aria-pressed': String(r.value),
        onclick: e => { if (!confirmFor(r.key, !r.value)) e.currentTarget.classList.toggle('on'); change(r.key, !r.value); } }));
      case 'seg': { // main.js lights the tapped button
        const bar = h('div', { class: 'seg ' + (r.wide ? 'wide' : 'small') },
          r.options.map(([v, text]) => h('button', { class: v === r.value ? 'on' : null, onclick: () => change(r.key, v) }, text)));
        return r.wide ? [r.label && h('div', { class: 'rowline' }, label(r)), bar] : h('div', { class: 'rowline' }, label(r), bar);
      }
      case 'radio': return h('div', { class: 'list', 'data-radio-group': true }, r.options.map(([v, text, hint]) =>
        h('button', { class: 'radiorow' + (v === r.value ? ' on' : ''), 'data-radio': true, onclick: () => { if (v !== r.value) change(r.key, v); } },
          h('span', { class: 'item-text' }, h('b', {}, text), hint && h('small', {}, hint)), h('span', { class: 'tick' }))));
      case 'slider': {
        const pill = h('span', { class: 'pill' }, tabsText(r.value));
        const input = h('input', { class: 'slider', type: 'range', min: r.min, max: r.max, step: 1, value: r.value, 'aria-label': r.label,
          oninput: e => { pill.textContent = tabsText(+e.target.value); }, onchange: e => change(r.key, +e.target.value) });
        return [h('div', { class: 'rowline' }, label(r), pill), input];
      }
      case 'accents': return h('div', { class: 'accents' }, r.options.map(o => {
        const b = h('button', { class: 'acc' + (o.key === r.value ? ' on' : ''), 'aria-label': o.name, onclick: () => change(r.key, o.key) });
        b.style.setProperty('--a', o.color);
        return b;
      }));
    }
  }

  function draw() {
    if (!state) return;
    const { version, pc } = state.about;
    $('#setabout').textContent = [version && (/^version /i.test(version) ? version : 'Version ' + version), pc].filter(Boolean).join(' · ') || 'Version · this PC';
    $('#aboutver').textContent = version ? (/^version /i.test(version) ? version : 'Version ' + version) : '';
    if (!PAGES.includes(page)) return;
    const view = $('#view-' + page);
    (view.querySelector('[data-rows]') || view.querySelector('.vbody')).replaceChildren(...rows(page, state).map(row).flat().filter(Boolean));
  }

  // The Usage page (GET /api/usage): lifetime (all PCs, this one's beneath), the last 7 days, the longest task, per PC, Reset.
  const fmt = n => (n || 0).toLocaleString('en-US');
  function drawUsage(u) {
    const bars = usageBars(u.days);
    $('#view-usage .vbody').replaceChildren(
      h('div', { class: 'card stat' }, h('p', { class: 'secthead inline' }, 'LIFETIME'), h('p', { class: 'bignum' }, fmt(u.combined ?? u.lifetime)),
        h('p', { class: 'muted' }, `tokens on all your PCs · ${fmt(u.lifetime)} on this one`)),
      h('p', { class: 'secthead' }, 'LAST 7 DAYS'),
      h('div', { class: 'bars' }, bars.map(b => { const el = h('span', { title: `${fmt(b.tokens)} tokens` }); el.style.setProperty('--h', b.h + '%'); return el; })),
      h('div', { class: 'barlabels' }, bars.map(b => h('span', {}, b.letter))),
      h('div', { class: 'list' }, h('div', { class: 'rowline' }, h('div', {}, h('b', {}, 'Longest task')), h('span', { class: 'pill' }, duration(u.longestTask)))),
      (u.pcs || []).length > 0 && h('p', { class: 'secthead' }, 'BY PC'),
      h('div', { class: 'list' }, (u.pcs || []).map(pc => h('div', { class: 'rowline' }, h('div', {}, h('b', {}, pc.name)), h('span', { class: 'pill' }, fmt(pc.lifetime))))),
      h('div', { class: 'pad' }, h('button', { class: 'btn ghost', onclick: resetStats }, 'Reset usage stats')));
  }
  const loadUsage = () => api.get('/api/usage').then(drawUsage, fail);
  registerScreen('usage', { show() { page = 'usage'; loadUsage(); } });

  for (const name of ['settings', ...PAGES])
    registerScreen(name, { show() {
      page = name; draw(); load();
      if (name === 'agents') document.dispatchEvent(new Event('ao:agents')); // agents.js: the workers under the global rows
    } });

  // Connected (main.js): the PC's accent from the start, before Settings is opened; a PC without the route keeps the default.
  document.addEventListener('ao:hello', () => api.get('/api/settings').then(s => { state = normalize(s); applyAccent(); draw(); }, () => {}));
}
