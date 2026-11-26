// The Code screen: a Code conversation's header (Start, the project menu), the run strip, and its panes: Chat (chat.js draws the
// conversation and the composer sends it; the TODO card above), Run (Start/Stop, the live run stream), Files (folders, the file
// browser, workflow records, the viewer, changes with diffs, the run log, the terminal card) and Browser (tools.js draws those two).
// The pure helpers below are Node-tested; the DOM part loads only in a page.
import { api } from './api.js';
import { h, icon } from './h.js';

/* ---------- pure ---------- */
const leaf = p => String(p || '').replace(/[\\/]+$/, '').split(/[\\/]/).pop() || String(p || '');

// GET /api/code/{id} → what the header and the run strip show.
export function codeView(s) {
  return { title: s.title || leaf(s.folder) || 'Code', folder: s.folder || '', folderName: leaf(s.folder) || 'No folder',
    startLabel: s.startLabel || 'Start project', canStart: !!s.canStart, running: !!s.running, busy: !!s.busy,
    task: s.taskId || 'No task yet', status: s.status || '', once: !!s.once, hasProjectMd: !!s.hasProjectMd };
}

// A clock {seconds, running} read at `at` (ms), `now` ms later than that: a running one has gone on ticking.
export const tick = (c, at, now) => (c?.seconds || 0) + (c?.running ? Math.max(0, now - at) / 1000 : 0);
export function fmtClock(sec) {
  const s = Math.floor(sec || 0), p = n => String(n).padStart(2, '0');
  return `${p(Math.floor(s / 3600))}:${p(Math.floor(s / 60) % 60)}:${p(s % 60)}`;
}

// The TODO items of a `tool` event for the model's todo tool (its whole list, each call); null for any other event.
const STATES = { done: 'done', completed: 'done', complete: 'done', doing: 'doing', in_progress: 'doing', active: 'doing', current: 'doing' };
export function todoFromTool(ev) {
  if (ev?.e !== 'tool' || ev.name !== 'todo') return null;
  let a = ev.args;
  if (typeof a === 'string') { try { a = JSON.parse(a); } catch { return null; } }
  const items = Array.isArray(a) ? a : a?.items || a?.todos;
  if (!Array.isArray(items)) return null;
  return items.map(i => typeof i === 'string' ? { text: i, state: '' }
    : { text: String(i.text ?? i.content ?? i.title ?? ''), state: STATES[String(i.state ?? i.status ?? '').toLowerCase()] || (i.done ? 'done' : '') });
}

// A run-stream line for an engine event: {kind: 'head'|'note'|'line'|'verdict'|'record'|'complete', tag?, text, pass?}; null = not shown.
const first = t => String(t || '').trim().split('\n')[0].slice(0, 200);
export function runLine(ev) {
  switch (ev?.e) {
    case 'model': return { kind: 'head', text: [ev.role, ev.name || ev.model].filter(Boolean).join(' · ') };
    case 'task_start': return { kind: 'note', text: 'TASK ' + (ev.id ?? '') };
    case 'resume': return { kind: 'note', text: `RESUME ${ev.id ?? ''}${ev.round ? ' · round ' + ev.round : ''}` };
    case 'tool': return { kind: 'line', tag: 'TOOL', text: `${ev.name || ''} ${typeof ev.args === 'string' ? ev.args : JSON.stringify(ev.args ?? {})}`.slice(0, 200) };
    case 'message': return first(ev.text) ? { kind: 'line', tag: 'RESPONSE', text: first(ev.text) } : null;
    case 'verdict': return { kind: 'verdict', pass: /^pass/i.test(String(ev.verdict || '')), tag: first(ev.verdict).split(/\s/)[0] || 'VERDICT', text: first(ev.verdict).replace(/^\S+\s*/, '') };
    case 'task': return { kind: 'record', text: `Item ${ev.id ?? ''} ${ev.outcome || 'recorded'}` };
    case 'complete': return { kind: 'complete', text: first(ev.summary) || 'Project complete' };
    default: return null;
  }
}

// The breadcrumbs from the folder `root` down to `dir` (a path inside it): [{name, path}], the root first.
export function crumbs(root, dir) {
  const r = String(root || '').replace(/[\\/]+$/, ''), d = String(dir || r).replace(/[\\/]+$/, '');
  const out = [{ name: leaf(r) || r, path: r }];
  if (!d.toLowerCase().startsWith(r.toLowerCase()) || d.length <= r.length) return out;
  let at = r;
  for (const name of d.slice(r.length).split(/[\\/]/).filter(Boolean)) { at += '\\' + name; out.push({ name, path: at }); }
  return out;
}

export const diffClass = kind => kind === '+' ? 'add' : kind === '-' ? 'del' : '';

// The workflow records among a folder's entries, in workflow order.
export const RECORDS = ['PROJECT.md', 'STATE.md', 'TASK.md', 'RESULT.md', 'REVIEW.md', 'ISSUES.md'];
export const workflowRecords = entries => RECORDS.map(n => (entries || []).find(e => !e.dir && e.name.toLowerCase() === n.toLowerCase())).filter(Boolean);

const size = n => n == null ? '' : n < 1024 ? n + ' B' : n < 1048576 ? (n / 1024).toFixed(1) + ' KB' : (n / 1048576).toFixed(1) + ' MB';

/* ---------- the screen ---------- */
if (typeof document !== 'undefined') {
  const { registerScreen, fail, toast, confirmSheet, openSheet, closeOverlays, replace } = await import('./main.js');
  const chat = await import('./chat.js');
  const { renderMarkdown } = await import('./markdown.js');
  await import('./tools.js'); // the terminal card and the Browser pane: each polls while it is on screen
  const $ = s => document.querySelector(s);
  const view = $('#view-code');
  let id = null, snap = null, at = 0, gen = 0, poll = 0, timer = 0, dir = '', browse = null;
  const url = (verb, q = '') => '/api/code/' + encodeURIComponent(id) + (verb ? '/' + verb : '') + q;
  const shown = () => view.classList.contains('on');

  function drawClocks() {
    if (!snap) return;
    const now = Date.now();
    $('#taskclock').textContent = fmtClock(tick(snap.taskTime, at, now));
    $('#turnline').textContent = fmtClock(tick(snap.turnTime, at, now));
  }

  function drawTodo(items) {
    const box = $('#codetodo');
    box.hidden = !items?.length;
    box.querySelector('ol').replaceChildren(...(items || []).map(i => h('li', { class: i.state || null }, i.text)));
  }

  function drawHead() {
    const v = codeView(snap);
    view.querySelector('.vtitle').textContent = v.title;
    view.querySelector('.vsub').replaceChildren(icon('folder', 'ic xs'), ' ' + v.folderName + (v.status ? ' · ' + v.status : ''));
    $('#taskline').textContent = v.task;
    $('#starthead').disabled = !v.canStart;
    $('#runbtn').disabled = !v.canStart;
    $('#runbtn span').textContent = v.startLabel;
    $('#runstop').disabled = !v.busy;
    $('#onceswitch').classList.toggle('on', v.once);
    $('#sheet-project .secthead').textContent = v.folderName.toUpperCase();
    $('#pmdtemplate').hidden = v.hasProjectMd;
    drawClocks();
  }

  async function refresh(mine = gen) {
    let s;
    try { s = await api.get(url('')); } catch (err) { if (mine === gen && err.status !== 404) fail(err); return; }
    if (mine !== gen) return;
    snap = s; at = Date.now();
    drawHead();
    if (s.todo) drawTodo(s.todo);
  }

  function addRunLine(ev) {
    const l = runLine(ev);
    if (!l) return;
    const box = $('#runstream'), stick = box.scrollHeight - box.scrollTop - box.clientHeight < 80;
    const tag = t => h('span', { class: 'fold-tag' }, t);
    box.append(l.kind === 'head' ? h('p', { class: 'runlog-head' }, l.text)
      : l.kind === 'note' ? h('p', { class: 'note' }, l.text)
      : l.kind === 'verdict' ? h('p', { class: 'line passline' }, h('span', { class: l.pass ? 'badge pass' : 'badge ok' }, l.tag), ' ' + l.text)
      : l.kind === 'record' || l.kind === 'complete' ? h('p', { class: l.kind === 'complete' ? 'record complete' : 'record' }, icon('check', 'ic sm'), ' ' + l.text)
      : h('p', { class: 'line' }, tag(l.tag), ' ' + l.text));
    if (stick) box.scrollTop = box.scrollHeight;
  }

  // Events of the open conversation, from chat.js's stream.
  chat.tapEvents(ev => {
    if (!shown() || !id) return;
    const todo = todoFromTool(ev);
    if (todo) drawTodo(todo);
    addRunLine(ev);
    if (['task_start', 'task_end', 'state', 'saved'].includes(ev.e)) refresh();
  });

  /* the sheet viewer: a file, a diff, the run log */
  function viewer(title, ...body) {
    $('#sheet-codeview .secthead').textContent = title;
    $('#codeviewbody').replaceChildren(...body);
    openSheet('sheet-codeview');
  }
  const codeBlock = text => h('div', { class: 'codeblock' }, h('pre', {}, h('code', {}, text)));
  async function openFile(path) {
    let f;
    try { f = await api.get(url('file', '?path=' + encodeURIComponent(path))); }
    catch (err) { if (err.status === 415) toast('Not a text file'); else fail(err); return; }
    viewer(leaf(path), /\.md$/i.test(path) ? h('div', { class: 'md' }, renderMarkdown(f.text)) : codeBlock(f.text));
  }
  async function openDiff(c) {
    let d;
    try { d = await api.get(url('diff', '?path=' + encodeURIComponent(c.path) + '&msg=' + encodeURIComponent(c.msg ?? ''))); } catch (err) { fail(err); return; }
    viewer(leaf(c.path), h('div', { class: 'codeblock diff' }, h('pre', {}, ...d.lines.map(l =>
      h('code', { class: diffClass(l.kind) || null }, `${l.kind} ${l.num ?? ''}\t${l.text}\n`)))));
  }

  /* Files */
  const row = (ic, name, note, onclick) => h('button', { class: 'filerow', onclick }, icon(ic, 'ic sm'), ' ' + name, h('small', {}, note));
  async function drawFiles(d = dir) {
    const mine = gen;
    let r;
    try { r = await api.get(url('files', '?dir=' + encodeURIComponent(d || ''))); } catch (err) { if (mine === gen) fail(err); return; }
    if (mine !== gen) return;
    dir = r.dir;
    const root = [snap.folder, ...(snap.folders || []).map(f => f.path)].find(p => dir.toLowerCase().startsWith(String(p).toLowerCase())) || snap.folder;
    $('#codecrumbs').replaceChildren(...crumbs(root, dir).flatMap((c, i) => [i ? ' / ' : '', h('button', { class: 'ghosttiny', onclick: () => drawFiles(c.path) }, c.name)]));
    $('#codefiles').replaceChildren(...r.entries.map(e => e.dir ? row('folder', e.name, '', () => drawFiles(e.path)) : row('file', e.name, size(e.size), () => openFile(e.path))));
    if (dir.toLowerCase() === String(snap.folder).toLowerCase()) {
      const recs = workflowRecords(r.entries);
      $('#coderecords').replaceChildren(...recs.map(e => row('lock', e.name, size(e.size), () => openFile(e.path))));
      $('#coderecords').previousElementSibling.hidden = !recs.length;
    }
  }
  function drawFolders() {
    const card = $('#codefoldercard');
    card.querySelector('b').textContent = leaf(snap.folder) || 'No folder';
    card.querySelector('small').textContent = snap.folder ? snap.folder + ' · full edit' : '';
    $('#codeextra').replaceChildren(...(snap.folders || []).map(f => row('folder', leaf(f.path), f.access === 'write' ? 'full edit' : 'read only', () => drawFiles(f.path))));
  }
  async function drawChanges() {
    let r;
    try { r = await api.get(url('changes')); } catch (err) { fail(err); return; }
    $('#codechanges').replaceChildren(...(r.files.length ? r.files.map(c => row('file', leaf(c.path), `+${c.added} −${c.removed}`, () => openDiff(c)))
      : [h('p', { class: 'muted' }, 'No changes from the model yet.')]));
  }
  async function showFiles() { if (!snap) return; drawFolders(); drawFiles(snap.folder); drawChanges(); }

  /* the folder browser (GET /api/fs/dirs): picks a directory, then calls browse(path) */
  async function drawDirs(path) {
    let r;
    try { r = await api.get('/api/fs/dirs?path=' + encodeURIComponent(path || '')); } catch (err) { fail(err); return; }
    $('#dirpath').textContent = r.path || 'This PC';
    $('#dirpick').hidden = !r.path;
    $('#dirpick').onclick = () => { const go = browse; closeOverlays(); go?.(r.path); };
    $('#dirlist').replaceChildren(
      ...(r.path ? [row('back', r.parent ? '..' : 'Drives', '', () => drawDirs(r.parent || ''))] : []),
      ...r.dirs.map(d => row('folder', d.name, '', () => drawDirs(d.path))));
  }
  async function pickFolder(title, onPick, recent = []) {
    browse = onPick;
    $('#sheet-dirs .secthead').textContent = title;
    $('#dirrecent').replaceChildren(...recent.map(p => row('folder', leaf(p), p, () => { closeOverlays(); onPick(p); })));
    $('#dirrecent').previousElementSibling.hidden = !recent.length;
    openSheet('sheet-dirs');
    drawDirs('');
  }
  async function newCodeChat() {
    let list = {};
    try { list = await api.get('/api/conversations'); } catch { /* recents are a convenience */ }
    const all = [...(list.recents || []), ...(list.projects || []).flatMap(p => p.items)].sort((a, b) => new Date(b.updated) - new Date(a.updated));
    const recent = [...new Set(all.filter(c => c.folder).map(c => c.folder))].slice(0, 5);
    pickFolder('NEW CODE CHAT · PICK A FOLDER', async folder => {
      let r;
      try { r = await api.post('/api/code/new', { folder }); } catch (err) { fail(err); return; }
      replace('code', { id: r.id });
    }, recent);
  }

  /* the project sheet */
  async function start(restart = false) {
    try { await api.post(url('start'), restart ? { restart: true } : {}); }
    catch (err) {
      if (err.status === 409 && err.body?.restart) confirmSheet('Restart project', 'Start the project again from the beginning?', () => start(true), 'Restart');
      else fail(err);
      return;
    }
    $('#runstream').replaceChildren();
    refresh();
  }
  async function editProjectMd() {
    closeOverlays();
    let text = '';
    try { text = (await api.get(url('file', '?path=' + encodeURIComponent(snap.folder.replace(/[\\/]+$/, '') + '\\PROJECT.md')))).text; }
    catch (err) { if (err.status !== 404) { fail(err); return; } }
    $('#pmdtext').value = text;
    openSheet('sheet-projectmd');
  }
  function drawFolderSheet() {
    $('#extralist').replaceChildren(...(snap.folders || []).map(f => h('div', { class: 'rowline' },
      h('div', {}, h('b', {}, leaf(f.path)), h('small', {}, `${f.path} · ${f.access === 'write' ? 'full edit' : 'read only'}`)),
      h('button', { class: 'btn ghost tight', onclick: async () => {
        try { await api.post(url('folders/remove'), { path: f.path }); } catch (err) { fail(err); return; }
        await refresh(); drawFolderSheet(); drawFolders();
      } }, 'Remove'))));
    if (!snap.folders?.length) $('#extralist').replaceChildren(h('p', { class: 'muted' }, 'Only the project folder.'));
  }
  const addFolder = () => pickFolder('ADD A FOLDER', path => {
    $('#sheet-access .hintline').textContent = path;
    const add = access => async () => {
      closeOverlays();
      try { await api.post(url('folders/add'), { path, access }); } catch (err) { fail(err); return; }
      await refresh(); drawFolders(); toast('Folder added');
    };
    $('#accessread').onclick = add('read');
    $('#accesswrite').onclick = add('write');
    openSheet('sheet-access');
  });

  function showPane(name) {
    view.querySelectorAll('.pane').forEach(p => p.classList.toggle('on', p.id === name));
    if (name === 'pane-files') showFiles();
  }

  async function show(params) {
    const mine = ++gen;
    clearInterval(poll); clearInterval(timer);
    id = params?.id ?? null;
    snap = null; dir = '';
    drawTodo(null);
    $('#runstream').replaceChildren();
    view.querySelector('.panes').hidden = !id;
    $('#codeempty').hidden = !!id;
    if (!id) {
      view.querySelector('.vtitle').textContent = 'New code chat';
      view.querySelector('.vsub').textContent = '';
      newCodeChat();
      return;
    }
    chat.showIn(view, { id });
    await refresh(mine);
    if (mine !== gen || !snap) return;
    if (snap.activity) $('#runstream').append(h('p', { class: 'note' }, 'RUN OUTPUT'), codeBlock(snap.activity));
    if (view.querySelector('#pane-files.on')) showFiles();
    poll = setInterval(() => { if (shown() && !document.hidden) refresh(mine); else if (!shown()) clearInterval(poll); }, 5000);
    timer = setInterval(() => { if (shown()) drawClocks(); else clearInterval(timer); }, 1000);
  }

  registerScreen('code', {
    mount() {
      view.querySelector('.seg[data-pane-group]').addEventListener('change', e => showPane(e.currentTarget.querySelector('button.on').dataset.paneOn));
      $('#starthead').addEventListener('click', () => start());
      $('#runbtn').addEventListener('click', () => start());
      $('#runstop').addEventListener('click', () => api.post('/api/stop', { id }).then(() => refresh(), fail));
      $('#codepick').addEventListener('click', newCodeChat);
      $('#pmdtemplate').addEventListener('click', async () => {
        closeOverlays();
        try { await api.post(url('projectmd/template')); } catch (err) { fail(err); return; }
        toast('PROJECT.md made from the template'); refresh();
      });
      $('#pmdedit').addEventListener('click', editProjectMd);
      $('#pmdsave').addEventListener('click', async () => {
        try { await api.post(url('projectmd'), { text: $('#pmdtext').value }); } catch (err) { fail(err); return; }
        closeOverlays(); toast('PROJECT.md saved'); refresh();
      });
      $('#pfolders').addEventListener('click', () => { if (!snap) return; drawFolderSheet(); openSheet('sheet-codefolders'); });
      $('#extraadd').addEventListener('click', addFolder);
      $('#codefoldercard button').addEventListener('click', addFolder);
      $('#onceswitch').addEventListener('click', async e => {
        const on = e.currentTarget.classList.contains('on'); // main.js flipped it already
        try { await api.post(url('once'), { on }); } catch (err) { e.currentTarget.classList.toggle('on', !on); fail(err); }
      });
      $('#coderunlog').addEventListener('click', async () => {
        let r;
        try { r = await api.get(url('runlog')); } catch (err) { fail(err); return; }
        viewer('RUN LOG', codeBlock(r.text || 'No run log yet.'));
      });
    },
    show,
  });
}
