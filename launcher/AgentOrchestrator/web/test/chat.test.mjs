import test from 'node:test';
import assert from 'node:assert/strict';
import { applyEvent, ringColor, emptyLive, cutOf, landSaved, freshLoad, approvalDone, landLoad, deletablePicture, chatMenu, lastReply, workSummary, isWork } from '../js/chat.js';

test('deletablePicture: a generated picture in images, not a photo in pasted, a screenshot or another URL', () => {
  const url = p => '/api/file?path=' + encodeURIComponent(p);
  assert.equal(deletablePicture(url('C:\\AO\\images\\fox 1.png')), 'C:\\AO\\images\\fox 1.png');
  assert.equal(deletablePicture(url('C:\\AO\\pasted\\a.png')), null);
  assert.equal(deletablePicture(url('C:\\AO\\screenshots\\a.png')), null);
  assert.equal(deletablePicture('/api/conversation/x/picture?frame=1'), null);
});

const bare = ps => ps.map(({ turn, ...p }) => p);
const run = (...evs) => evs.reduce(applyEvent, emptyLive());

test('text deltas append to the reply in order', () => {
  const s = run({ e: 'delta', kind: 'text', text: 'Hel' }, { e: 'delta', kind: 'text', text: 'lo' });
  assert.deepEqual(bare(s.parts), [{ t: 'text', text: 'Hello' }]);
});

test('thinking deltas go to the thinking section, not the reply', () => {
  const s = run({ e: 'delta', kind: 'thinking', text: 'hm' }, { e: 'delta', kind: 'thinking', text: 'm' }, { e: 'delta', kind: 'text', text: 'A' });
  assert.deepEqual(bare(s.parts), [{ t: 'thinking', text: 'hmm' }, { t: 'text', text: 'A' }]);
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
  assert.deepEqual(bare(s.parts)[1], { t: 'image', src: '/api/file?path=' + encodeURIComponent('C:\\d\\images\\a b.png') });
});

const pic = (state, more) => ({ e: 'picture', state, ...more });
const PATH = 'C:\\d\\images\\p q.png', SRC = '/api/file?path=' + encodeURIComponent(PATH);

test('a picture card follows its steps and becomes the saved picture', () => {
  const s = run({ e: 'delta', kind: 'text', text: 'Sure' }, pic('start', { width: 512, height: 768 }));
  assert.deepEqual(bare(s.parts)[1], { t: 'picture', width: 512, height: 768, text: '', progress: null, frame: 0, open: true });
  const p = [pic('progress', { text: 'Step 5/8', progress: 0.62 }), pic('preview', { frame: 3 })].reduce(applyEvent, s);
  assert.deepEqual(bare(p.parts)[1], { t: 'picture', width: 512, height: 768, text: 'Step 5/8', progress: 0.62, frame: 3, open: true });
  const d = [pic('done', { path: PATH }), pic('end')].reduce(applyEvent, p);
  assert.deepEqual(bare(d.parts), [{ t: 'text', text: 'Sure' }, { t: 'image', src: SRC }]);
  assert.equal(d.parts[1].turn, s.parts[1].turn); // still its turn's part: a landed `saved` drops it
});

test('a picture that ends without done leaves no card', () => {
  const open = run(pic('start', { width: 1, height: 1 }), pic('progress', { text: 'Loading model', progress: null }));
  assert.deepEqual(open.parts.map(p => p.t), ['picture']);
  assert.deepEqual(applyEvent(open, pic('end')).parts, []);
});

test('hello mid-picture shows the card at once, after the reply so far', () => {
  const s = run({ e: 'hello', busy: true, reply: { text: 'so far' }, picture: { width: 512, height: 512, text: 'Step 2/8', progress: 0.25, frame: 2 } });
  assert.deepEqual(bare(s.parts), [{ t: 'text', text: 'so far' },
    { t: 'picture', width: 512, height: 512, text: 'Step 2/8', progress: 0.25, frame: 2, open: true }]);
  assert.deepEqual(bare(run({ e: 'hello', busy: true, reply: { text: 'x' }, picture: null }).parts), [{ t: 'text', text: 'x' }]);
});

test("the tool result's Saved: line adds no second copy of a picture done already showed", () => {
  const s = run(pic('start', { width: 1, height: 1 }), pic('done', { path: PATH }), pic('end'),
    { e: 'tool', name: 'generate_image', args: '{}', result: `Image generated. Saved: ${PATH}. Prompt: x` });
  assert.deepEqual(bare(s.parts).map(p => p.t), ['image', 'tool']);
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
  assert.deepEqual([h.busy, h.queued, h.prompt, bare(h.parts)], [true, 0, null, [{ t: 'text', text: 'so far' }]]);
});

test('a delegation shows a card for the worker', () => {
  const s = run({ e: 'chat_stage', role: 'coder', stage: 'delegate', title: 'Coder' }, { e: 'delta', kind: 'text', text: 'x' });
  assert.deepEqual(bare(s.parts), [{ t: 'stage', title: 'Coder' }, { t: 'text', text: 'x' }]);
});

test('a context event updates the ring', () => {
  const s = run({ e: 'context', n_ctx: 1000, prompt_tokens: 400, completion_tokens: 100, parts: { system: 100, messages: 300 } });
  assert.deepEqual(s.context, { nCtx: 1000, used: 500, fraction: 0.5, parts: { system: 100, messages: 300 } });
});

test('the ring is blue under 50%, amber from 50%, red from 90%', () => {
  assert.deepEqual([0, 0.49, 0.5, 0.89, 0.9, 1].map(ringColor), ['blue', 'blue', 'amber', 'amber', 'red', 'red']);
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

test('an outrun load writes nothing: no messages, no title', () => {
  const conv = { title: 'Old', worker: 'W', kind: 'chat', messages: [1] };
  assert.equal(landLoad(2, 1, conv, null), null);
  assert.deepEqual(landLoad(1, 2, conv, null), { messages: [1], context: null, title: 'Old', sub: 'Chat \u00b7 to W' });
});

// The wiring's own path: feed events like onEvent does, collect the cut it makes, land it later with landSaved.
const wire = () => {
  const w = { live: emptyLive(), cuts: [] };
  w.ev = (...evs) => evs.forEach(e => { w.live = applyEvent(w.live, e); const c = cutOf(w.live, e); if (c) w.cuts.push(c); });
  w.land = i => { w.live = landSaved(w.live, w.cuts[i]); };
  w.texts = () => w.live.parts.map(p => p.text ?? p.name ?? p.t);
  return w;
};
const text = t => ({ e: 'delta', kind: 'text', text: t }), tool = { e: 'tool', name: 'tool', args: '{}', result: '' };

test('turn after turn with tools: each landed saved leaves no stale parts', () => {
  const w = wire();
  w.ev(text('A'), { e: 'saved' }); w.land(0);
  assert.deepEqual(w.texts(), []);
  w.ev(text('B'), tool, { e: 'saved' }); w.land(1);
  assert.deepEqual(w.texts(), []);
  w.ev(tool, text('C'), { e: 'saved' }); w.land(2);
  assert.deepEqual(w.texts(), []);
});

test('text, saved, text: the new text survives the load whole', () => {
  const w = wire();
  w.ev(text('old'), { e: 'saved' }, text('Sure,'), text(' ok')); w.land(0);
  assert.deepEqual(w.texts(), ['Sure, ok']);
});

test('two saveds before either load lands: only the third turn remains, in either landing order', () => {
  for (const order of [[0, 1], [1, 0]]) {
    const w = wire();
    w.ev(text('A'), { e: 'saved' }, text('B'), tool, { e: 'saved' }, text('C'));
    order.forEach(w.land);
    assert.deepEqual(w.texts(), ['C']);
  }
});

test('a saved load issued before a reconnect drops nothing from what hello restored', () => {
  const w = wire();
  w.ev(text('A'), { e: 'saved' }, { e: 'hello', busy: true, reply: { text: 'restored' } }); w.land(0);
  assert.deepEqual(w.texts(), ['restored']);
});

test('a server resync reopens the stream, and the new hello replaces a reply with a hole', () => {
  const s = run(text('Hel'), text('o'), { e: 'resync' }); // the server dropped the 'l' between the two
  assert.equal(s.reopen, true);
  const h = applyEvent({ ...s, reopen: false }, { e: 'hello', busy: true, reply: { text: 'Hello' } });
  assert.deepEqual(bare(h.parts), [{ t: 'text', text: 'Hello' }]);
});

test('hello refetches: a turn saved between the first GET and the subscription is loaded', () => {
  const s = run({ e: 'hello', busy: false, queued: 0, prompt: null, reply: { text: '' } });
  assert.equal(s.refetch, true);
  assert.equal(cutOf(s, { e: 'hello' }), null); // a plain load: it drops no live parts
});

test('chat menu: a chat in a project; moves to the other projects of its kind (no kind = either, as the desktop); Archive', () => {
  const c = (id, kind = 'chat') => ({ id, kind, title: id });
  const list = { projects: [{ id: 'p1', name: 'One', kind: 'chat', items: [c('a'), c('k', 'code')] }, { id: 'p2', name: 'Two', kind: 'chat', items: [] },
    { id: 'p3', name: 'Old', items: [] }, { id: 'p4', name: 'Coding', kind: 'code', items: [] }], recents: [c('r'), c('rc', 'code')], archived: [c('x')] };
  assert.deepEqual(chatMenu(list, 'a'), { kind: 'chat', project: { id: 'p1', name: 'One' }, archived: false, archiveLabel: 'Archive',
    targets: [{ id: 'p2', name: 'Two' }, { id: 'p3', name: 'Old' }] });
  assert.deepEqual(chatMenu(list, 'r').targets.map(p => p.id), ['p1', 'p2', 'p3']);
  assert.equal(chatMenu(list, 'r').project, null);
  assert.deepEqual(chatMenu(list, 'rc').targets.map(p => p.id), ['p3', 'p4']);
  assert.deepEqual(chatMenu(list, 'k').targets.map(p => p.id), ['p3', 'p4']);
  const x = chatMenu(list, 'x');
  assert.deepEqual([x.archived, x.archiveLabel], [true, 'Unarchive']);
  assert.equal(chatMenu(list, 'nope'), null);
  assert.equal(chatMenu(undefined, 'a'), null);
});

test("copy reply: the last reply's text, skipping the user's messages and empty replies", () => {
  assert.equal(lastReply([{ role: 'user', text: 'q1' }, { role: 'assistant', text: 'a1' }, { role: 'user', text: 'q2' }]), 'a1');
  assert.equal(lastReply([{ role: 'assistant', text: 'a1' }, { role: 'assistant', text: '' }]), 'a1');
  assert.equal(lastReply([{ role: 'user', text: 'q' }]), '');
  assert.equal(lastReply(undefined), '');
});

test('workSummary: a run of steps by kind, as the desktop says it; isWork picks the steps', () => {
  const tool = name => ({ t: 'tool', name, args: '', result: '' });
  assert.equal(workSummary([{ t: 'thinking', text: 'a' }, tool('read_file'), tool('Read'), { t: 'stage', title: 'X' }, tool('bash'), tool('mystery')]),
    '2 thoughts · read 2 files · ran 1 command · 1 other step');
  assert.equal(workSummary([]), 'working');
  assert.deepEqual(['text', 'thinking', 'tool', 'stage', 'image', 'picture'].map(t => isWork({ t })), [false, true, true, true, false, false]);
});
