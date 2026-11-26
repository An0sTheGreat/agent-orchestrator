// The Skills tab: the PC's skills from GET /api/skills in two groups (Your skills, Built in), a switch each (POST /api/skills/set),
// a tap opens the skill's SKILL.md (editable and saved for the user's own, read-only for a built-in), and the New skill sheet.
// groupSkills is pure (Node tests it); the DOM part loads only in a page.
import { api } from './api.js';
import { h } from './h.js';

// The headed groups with skills in them, in order: the user's own, then the built-ins; each keeps the server's order.
export function groupSkills(skills = []) {
  return [['YOUR SKILLS', skills.filter(s => !s.builtIn)], ['BUILT IN', skills.filter(s => s.builtIn)]].filter(([, list]) => list.length);
}

if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, openSheet, closeOverlays } = await import('./main.js');
  const $ = s => document.querySelector(s);
  let open = null; // the skill in the viewer

  function row(s) {
    const sw = h('button', { class: 'switch' + (s.on ? ' on' : ''), 'aria-label': s.name, 'aria-pressed': String(s.on), onclick: async e => {
      const el = e.currentTarget, on = !el.classList.contains('on');
      el.classList.toggle('on', on);
      try { await api.post('/api/skills/set', { key: s.key, on }); } catch (err) { el.classList.toggle('on', !on); fail(err); }
    } });
    return h('div', { class: 'rowline skillrow' },
      h('button', { class: 'item-text', onclick: () => view(s.key) }, h('b', {}, s.name), h('small', {}, s.description || (s.builtIn ? 'Built in' : ''))), sw);
  }

  async function load() {
    let r;
    try { r = await api.get('/api/skills'); } catch (err) { fail(err); return; }
    const groups = groupSkills(r.skills);
    $('#skillgroups').replaceChildren(...(groups.length ? groups.flatMap(([title, list]) => [h('p', { class: 'secthead' }, title), h('div', { class: 'list' }, list.map(row))])
      : [h('div', { class: 'card empty' }, h('p', { class: 'muted' }, 'No skills on this PC yet. + makes one.'))]));
  }

  async function view(key) {
    let s;
    try { s = await api.get('/api/skill?key=' + encodeURIComponent(key)); } catch (err) { fail(err); return; }
    open = s;
    $('#sheet-skill .secthead').textContent = s.name.toUpperCase();
    $('#skillnote').textContent = s.builtIn ? 'Built in: read-only. Make your own skill to change it.' : s.description || '';
    $('#skilltext').value = s.text || '';
    $('#skilltext').readOnly = !!s.builtIn;
    $('#skillsave').hidden = !!s.builtIn;
    openSheet('sheet-skill');
  }

  async function save() {
    if (!open || open.builtIn) return;
    try { await api.post('/api/skills/save', { key: open.key, text: $('#skilltext').value }); } catch (err) { fail(err); return; }
    closeOverlays();
    toast('Skill saved');
    load();
  }

  async function create(e) {
    e.preventDefault();
    const form = e.currentTarget, name = form.elements.name.value.trim(), description = form.elements.description.value.trim();
    if (!name) { toast('Give the skill a name'); return; }
    let r;
    try { r = await api.post('/api/skills/new', { name, description }); } catch (err) { fail(err); return; }
    form.reset();
    load();
    view(r.key);
  }

  registerScreen('skills', {
    mount() {
      $('#skillsave').addEventListener('click', save);
      $('#sheet-newskill').addEventListener('submit', create);
    },
    show: load,
  });
}
