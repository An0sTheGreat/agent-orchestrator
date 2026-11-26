import test from 'node:test';
import assert from 'node:assert/strict';
import { codeView, tick, fmtClock, todoFromTool, runLine, crumbs, diffClass, workflowRecords } from '../js/code.js';

test('codeView: the header and run strip from the snapshot', () => {
  const v = codeView({ title: '', folder: 'C:\\dev\\weather\\', taskId: null, startLabel: 'Resume Project — 2', canStart: true, busy: true, once: true, hasProjectMd: true });
  assert.equal(v.title, 'weather');
  assert.equal(v.folderName, 'weather');
  assert.equal(v.task, 'No task yet');
  assert.equal(v.startLabel, 'Resume Project — 2');
  assert.deepEqual([v.canStart, v.busy, v.once, v.hasProjectMd, v.running], [true, true, true, true, false]);
  assert.equal(codeView({ title: 'Fix it', folder: '', taskId: '3' }).task, '3');
  assert.equal(codeView({ folder: '' }).folderName, 'No folder');
});

test('clocks tick locally only while running, formatted HH:MM:SS', () => {
  assert.equal(tick({ seconds: 10, running: true }, 1000, 6000), 15);
  assert.equal(tick({ seconds: 10, running: false }, 1000, 6000), 10);
  assert.equal(tick(null, 0, 5000), 0);
  assert.equal(fmtClock(0), '00:00:00');
  assert.equal(fmtClock(3723.9), '01:02:03');
});

test('todoFromTool: the todo tool call\'s items, string or object args; null for other events', () => {
  const items = [{ text: 'a', state: 'done' }, { content: 'b', status: 'in_progress' }, 'c'];
  assert.deepEqual(todoFromTool({ e: 'tool', name: 'todo', args: JSON.stringify({ items }) }),
    [{ text: 'a', state: 'done' }, { text: 'b', state: 'doing' }, { text: 'c', state: '' }]);
  assert.deepEqual(todoFromTool({ e: 'tool', name: 'todo', args: { items: [{ text: 'x', status: 'completed' }] } }), [{ text: 'x', state: 'done' }]);
  assert.equal(todoFromTool({ e: 'tool', name: 'write_file', args: '{}' }), null);
  assert.equal(todoFromTool({ e: 'tool', name: 'todo', args: 'not json' }), null);
  assert.equal(todoFromTool({ e: 'delta', name: 'todo' }), null);
});

test('runLine: events become heads, notes, tool lines, verdict badges and records', () => {
  assert.deepEqual(runLine({ e: 'model', role: 'Lead', name: 'Qwen' }), { kind: 'head', text: 'Lead · Qwen' });
  assert.deepEqual(runLine({ e: 'tool', name: 'list_files', args: {} }), { kind: 'line', tag: 'TOOL', text: 'list_files {}' });
  assert.deepEqual(runLine({ e: 'verdict', verdict: 'PASS skeleton loads' }), { kind: 'verdict', pass: true, tag: 'PASS', text: 'skeleton loads' });
  assert.equal(runLine({ e: 'verdict', verdict: 'FAIL broken' }).pass, false);
  assert.deepEqual(runLine({ e: 'task', id: '1', outcome: 'passed' }), { kind: 'record', text: 'Item 1 passed' });
  assert.deepEqual(runLine({ e: 'complete', summary: 'All done\nmore' }), { kind: 'complete', text: 'All done' });
  assert.deepEqual(runLine({ e: 'task_start', id: '2' }), { kind: 'note', text: 'TASK 2' });
  assert.equal(runLine({ e: 'message', text: '' }), null);
  assert.equal(runLine({ e: 'delta', text: 'x' }), null);
});

test('crumbs: root first, then each folder down to dir', () => {
  assert.deepEqual(crumbs('C:\\dev\\app', 'C:\\dev\\app\\src\\ui'),
    [{ name: 'app', path: 'C:\\dev\\app' }, { name: 'src', path: 'C:\\dev\\app\\src' }, { name: 'ui', path: 'C:\\dev\\app\\src\\ui' }]);
  assert.deepEqual(crumbs('C:\\dev\\app\\', 'C:\\dev\\app'), [{ name: 'app', path: 'C:\\dev\\app' }]);
  assert.deepEqual(crumbs('C:\\dev\\app', 'D:\\other'), [{ name: 'app', path: 'C:\\dev\\app' }]);
});

test('diffClass and workflowRecords', () => {
  assert.deepEqual(['+', '-', ' '].map(diffClass), ['add', 'del', '']);
  const e = [{ name: 'src', dir: true }, { name: 'state.md', dir: false }, { name: 'PROJECT.md', dir: false }, { name: 'index.html', dir: false }, { name: 'REVIEW.md', dir: true }];
  assert.deepEqual(workflowRecords(e).map(x => x.name), ['PROJECT.md', 'state.md']);
});
