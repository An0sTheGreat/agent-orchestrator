import test from 'node:test';
import assert from 'node:assert/strict';
import { applyEvent, ringColor, emptyLive, afterCut, freshLoad, approvalDone } from '../js/chat.js';

const run = (...evs) => evs.reduce(applyEvent, emptyLive());

test('text deltas append to the reply in order', () => {
  const s = run({ e: 'delta', kind: 'text', text: 'Hel' }, { e: 'delta', kind: 'text', text: 'lo' });
  assert.deepEqual(s.parts, [{ t: 'text', text: 'Hello' }]);
});

test('thinking deltas go to the thinking section, not the reply', () => {
  const s = run({ e: 'delta', kind: 'thinking', text: 'hm' }, { e: 'delta', kind: 'thinking', text: 'm' }, { e: 'delta', kind: 'text', text: 'A' });
  assert.deepEqual(s.parts, [{ t: 'thinking', text: 'hmm' }, { t: 'text', text: 'A' }]);
});

test('a tool adds a row and the text after it starts a new section', () => {
  const s = run({ e: 'delta', kind: 'text', text: 'before' }, { e: 'tool', name: 'web_search', args: '{"q":1}', result: 'ok' },
    { e: 'delta', kind: 'text', text: 'after' });
  assert.deepEqual(s.parts.map(p => p.t), ['text', 'tool', 'text']);
  assert.equal(s.parts[1].name, 'web_search');
  assert.equal(s.parts[2].text, 'after');
});

test("a generate_image tool's Saved: line shows the picture", () => {
  const s = run({ e: 'tool', name: 'generate_image', args: '{}', result: 'Image generated and shown to the user. Model: m. Size: 1x1. Saved: C:\\d\\images\\a b.png. Prompt: x' });
  assert.deepEqual(s.parts[1], { t: 'image', src: '/api/file?path=' + encodeURIComponent('C:\\d\\images\\a b.png') });
});

test('saved and resync set refetch', () => {
  assert.equal(run({ e: 'saved' }).refetch, true);
  assert.equal(run({ e: 'resync' }).refetch, true);
  assert.equal(emptyLive().refetch, false);
});

test('state updates busy, queued and the prompt; hello also sets the reply so far', () => {
  const prompt = { title: 'T', message: 'M', nonce: 'n', choices: [{ label: 'Yes', value: 'y' }] };
  const s = run({ e: 'state', busy: true, queued: 2, prompt });
  assert.deepEqual([s.busy, s.queued, s.prompt], [true, 2, prompt]);
  const h = run({ e: 'delta', kind: 'text', text: 'stale' }, { e: 'hello', busy: true, queued: 0, prompt: null, reply: { text: 'so far' } });
  assert.deepEqual([h.busy, h.queued, h.prompt, h.parts], [true, 0, null, [{ t: 'text', text: 'so far' }]]);
});

test('a delegation shows a card for the worker', () => {
  const s = run({ e: 'chat_stage', role: 'coder', stage: 'delegate', title: 'Coder' }, { e: 'delta', kind: 'text', text: 'x' });
  assert.deepEqual(s.parts, [{ t: 'stage', title: 'Coder' }, { t: 'text', text: 'x' }]);
});

test('a context event updates the ring', () => {
  const s = run({ e: 'context', n_ctx: 1000, prompt_tokens: 400, completion_tokens: 100, parts: { system: 100, messages: 300 } });
  assert.deepEqual(s.context, { nCtx: 1000, used: 500, fraction: 0.5, parts: { system: 100, messages: 300 } });
});

test('the ring is blue under 50%, amber from 50%, red from 90%', () => {
  assert.deepEqual([0, 0.49, 0.5, 0.89, 0.9, 1].map(ringColor), ['blue', 'blue', 'amber', 'amber', 'red', 'red']);
});

test('after saved, the next turn parts that arrived during the load survive; the ones before it go', () => {
  const s = run({ e: 'delta', kind: 'text', text: 'old reply' }, { e: 'saved' });
  const cut = s.parts.length;
  const next = applyEvent(applyEvent(s, { e: 'delta', kind: 'thinking', text: 'new' }), { e: 'chat_stage', stage: 'delegate', title: 'Coder' });
  assert.deepEqual(afterCut(next, cut).parts, [{ t: 'thinking', text: 'new' }, { t: 'stage', title: 'Coder' }]);
  assert.deepEqual(afterCut(s, cut).parts, []);
});

test('an older load response is ignored', () => {
  assert.equal(freshLoad(0, 1), true);
  assert.equal(freshLoad(2, 1), false);
  assert.equal(freshLoad(2, 2), false);
  assert.equal(freshLoad(2, 3), true);
});

test('an answered approval draws done; a new nonce draws clickable again', () => {
  assert.equal(approvalDone('n1', { nonce: 'n1' }), true);
  assert.equal(approvalDone('n1', { nonce: 'n2' }), false);
  assert.equal(approvalDone(null, { nonce: 'n1' }), false);
});
