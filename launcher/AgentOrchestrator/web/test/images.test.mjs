import test from 'node:test';
import assert from 'node:assert/strict';
import { groupModels, fieldsFor, lockedHeight, settingsToForm, formToSettings, stageView, pollDelay, pictureUrl } from '../js/images.js';

const local = { id: 'sdxl', source: 'local', negative: true, multiple: 64 };
const shared = { id: 'flux', source: 'shared', negative: false };
const remote = { id: 'or/gpt-image', source: 'remote', negative: false };

test('the model picker groups by source in the order This PC, Primary PC, OpenRouter; an empty group is left out', () => {
  assert.deepEqual(groupModels([remote, local, shared]), [['This PC', [local]], ['Primary PC', [shared]], ['OpenRouter', [remote]]]);
  assert.deepEqual(groupModels([remote]), [['OpenRouter', [remote]]]);
});

test('a model shows the negative prompt only when it takes one; OpenRouter hides the PC-only fields', () => {
  assert.deepEqual(fieldsFor(local), { negative: true, local: true });
  assert.deepEqual(fieldsFor(shared), { negative: false, local: true });
  assert.deepEqual(fieldsFor(remote), { negative: false, local: false });
  assert.deepEqual(fieldsFor(undefined), { negative: false, local: false });
});

test("a locked size keeps the aspect ratio in the model's multiple; Custom has none", () => {
  assert.equal(lockedHeight('3:2', 1024, 64), 704); // 682.7 -> 11 x 64
  assert.equal(lockedHeight('1:1', 1024, 64), 1024);
  assert.equal(lockedHeight('9:16', 576, 16), 1024);
  assert.equal(lockedHeight('Custom', 1024, 64), null);
  assert.equal(lockedHeight('1:1', 0, 64), null);
});

test('settings -> form: a random seed is an empty box, numbers become the inputs\' text', () => {
  const s = { model: 'sdxl', prompt: 'a fox', negative: 'blur', steps: 30, cfg: 6.5, aspect: '3:2', width: 1024, height: 704,
    unlocked: false, count: 2, seed: -1, lowVram: true };
  const f = settingsToForm(s);
  assert.deepEqual(f, { model: 'sdxl', prompt: 'a fox', negative: 'blur', steps: '30', cfg: '6.5', aspect: '3:2', width: '1024',
    height: '704', unlocked: false, count: '2', seed: '', lowVram: true });
  assert.equal(settingsToForm({ ...s, seed: 42 }).seed, '42');
  assert.deepEqual(formToSettings(f, local), { ...s, seed: -1 }, 'and back');
});

test('form -> settings: count kept to 1..maxBatch, a bad seed is random, no negative for a model without one', () => {
  const f = { model: 'flux', prompt: '  a fox ', negative: 'blur', steps: '20', cfg: '3.5', aspect: '1:1', width: '1024', height: '1024',
    unlocked: true, count: '50', seed: ' 7 ', lowVram: false };
  const s = formToSettings(f, shared, 20);
  assert.equal(s.prompt, 'a fox');
  assert.equal(s.negative, '');
  assert.equal(s.count, 20);
  assert.equal(s.seed, 7);
  assert.equal(s.cfg, 3.5);
  assert.equal(s.unlocked, true);
  assert.equal(formToSettings({ ...f, count: '0', seed: 'abc' }, shared).count, 1);
  assert.equal(formToSettings({ ...f, seed: '-5' }, shared).seed, -1);
  assert.equal(formToSettings({ ...f, seed: '' }, local).seed, -1);
  assert.equal(formToSettings(f, local).negative, 'blur');
});

test('status -> stage: busy shows the text, the percentage and the frame; a finished job seen here shows the newest picture', () => {
  assert.deepEqual(stageView({ busy: true, text: 'Step 3/30', progress: 0.104, frame: 3 }, false),
    { mode: 'busy', text: 'Step 3/30', pct: '10%', frame: 3 });
  assert.deepEqual(stageView({ busy: true, text: 'Loading model…', progress: null, frame: 0 }, true),
    { mode: 'busy', text: 'Loading model…', pct: '', frame: 0 });
  assert.equal(stageView({ busy: true, progress: 1.4 }, false).pct, '100%');
  assert.deepEqual(stageView({ busy: false, latest: 'C:\\images\\a b.png' }, true), { mode: 'done', src: pictureUrl('C:\\images\\a b.png') });
  assert.equal(pictureUrl('C:\\images\\a b.png'), '/api/file?path=C%3A%5Cimages%5Ca%20b.png');
  assert.deepEqual(stageView({ busy: false, latest: 'C:\\images\\a.png' }, false), { mode: 'none' }, 'not run here: the gallery has it');
  assert.deepEqual(stageView({ busy: false, latest: null }, true), { mode: 'none' });
});

test('the status is polled every second while busy, every 5 s while idle', () => {
  assert.equal(pollDelay(true), 1000);
  assert.equal(pollDelay(false), 5000);
});
