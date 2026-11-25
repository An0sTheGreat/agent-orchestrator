// The Chats screen: projects, recents and archived from /api/conversations, the All/Chat/Code filter and the search sheet.
import { api } from './api.js';
import { registerScreen, go, fail } from './main.js';
import { h, icon } from './h.js';
import { ago } from './util.js';

const $ = s => document.querySelector(s);
let data = { projects: [], recents: [], archived: [] }, kind = 'all', loads = 0;

const clone = id => document.getElementById(id).content.firstElementChild.cloneNode(true);

function chatRow(tpl, c) {
  const el = clone(tpl), main = el.querySelector('.item-main');
  el.dataset.kind = c.kind;
  if (c.kind === 'code') el.querySelector('use').setAttribute('href', '#i-code');
  el.querySelector('b').textContent = c.title;
  el.querySelector('small').textContent = ago(c.updated);
  if (c.busy) main.append(h('span', { class: 'dot live', 'aria-label': 'replying' }));
  main.addEventListener('click', () => go('chat', { id: c.id }));
  return el;
}

function projectRows(p) {
  const el = clone('tpl-project'), n = p.items.length, folder = p.items.find(c => c.folder)?.folder;
  el.dataset.kinds = p.items.map(c => c.kind).join(' '); // the filter hides a project with no chat of the chosen kind
  el.querySelector('b').textContent = p.name;
  el.querySelector('small').textContent = [`${n} chat${n === 1 ? '' : 's'}`, folder].filter(Boolean).join(' · ');
  return [el, ...p.items.map(c => chatRow('tpl-project-chat', c))];
}

function filter() {
  const off = k => kind !== 'all' && k !== kind;
  document.querySelectorAll('#view-chats .item[data-kind]').forEach(el => { el.hidden = off(el.dataset.kind); });
  document.querySelectorAll('#view-chats .item[data-kinds]').forEach(el => { el.hidden = !el.dataset.kinds.split(' ').some(k => !off(k)); });
}

function draw() {
  const { projects, recents, archived } = data;
  $('#projects').replaceChildren(...projects.flatMap(projectRows));
  $('#recents').replaceChildren(...recents.map(c => chatRow('tpl-chat', c)));
  $('#archived').replaceChildren(...archived.map(c => chatRow('tpl-chat', c)));
  $('#projects').previousElementSibling.hidden = !projects.length;
  $('#recents').previousElementSibling.hidden = !recents.length;
  $('#view-chats .archive-row .count').textContent = archived.length;
  $('#view-chats .archive-row').hidden = !archived.length;
  filter();
  search();
}

// Titles containing the query; with no query, the three latest chats as SUGGESTED (the concept's sheet).
function search() {
  const q = $('#sheet-search input').value.trim().toLowerCase();
  const all = [...data.projects.flatMap(p => p.items), ...data.recents, ...(q ? data.archived : [])];
  const hits = q ? all.filter(c => c.title.toLowerCase().includes(q))
    : all.sort((a, b) => new Date(b.updated) - new Date(a.updated)).slice(0, 3);
  $('#searchResults').replaceChildren(
    h('p', { class: 'secthead' }, q ? (hits.length ? 'RESULTS' : 'NO MATCHES') : 'SUGGESTED'),
    ...hits.map(c => h('button', { class: 'drawer-item', onclick: () => go('chat', { id: c.id }) },
      icon(c.kind === 'code' ? 'code' : 'chat', 'ic sm'), ' ' + c.title)));
}

async function load() {
  const mine = ++loads;
  let d;
  try { d = await api.get('/api/conversations'); } catch (err) { fail(err); return; }
  if (mine !== loads) return; // a later load is on its way
  data = d;
  draw();
}

registerScreen('chats', {
  mount() {
    $('.seg[data-seg=kind]').addEventListener('change', e => { kind = e.currentTarget.dataset.value; filter(); });
    $('#sheet-search input').addEventListener('input', search);
    document.addEventListener('visibilitychange', () => { if (!document.hidden && $('#view-chats').classList.contains('on')) load(); });
  },
  show: load,
});
