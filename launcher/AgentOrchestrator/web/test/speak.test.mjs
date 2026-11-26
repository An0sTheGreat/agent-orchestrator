import test from 'node:test';
import assert from 'node:assert/strict';
import { speechOf, keyOf, playState, IDLE, voiceGroups, voiceLabel, fallbackNote, readSentences, autoReadKey, readAutoRead, writeAutoRead } from '../js/speak.js';

test("speechOf: an assistant reply's text and worker; nothing for the user's messages or a reply with no words", () => {
  assert.deepEqual(speechOf({ role: 'assistant', text: '  Hi there. ', worker: 'Reviewer' }), { text: 'Hi there.', worker: 'Reviewer' });
  assert.deepEqual(speechOf({ role: 'assistant', text: 'Hi' }), { text: 'Hi', worker: '' });
  assert.equal(speechOf({ role: 'user', text: 'Hi' }), null);
  assert.equal(speechOf({ role: 'assistant', text: ' \n ' }), null);
  assert.equal(speechOf({ role: 'assistant' }), null);
  assert.equal(speechOf(undefined), null);
});

test('keyOf: the same reply drawn again keeps its key; another reply has another', () => {
  const m = { time: '2026-11-26T10:00:00Z', text: 'Hi' };
  assert.equal(keyOf({ ...m }), keyOf(m));
  assert.notEqual(keyOf({ ...m, text: 'Hello' }), keyOf(m));
});

test('playState: start loads, the answer plays, the end goes idle', () => {
  let s = playState(IDLE, { t: 'start', key: 'a' });
  assert.deepEqual(s, { key: 'a', phase: 'loading' });
  s = playState(s, { t: 'playing', key: 'a' });
  assert.deepEqual(s, { key: 'a', phase: 'playing' });
  assert.deepEqual(playState(s, { t: 'end', key: 'a' }), IDLE);
  assert.deepEqual(playState(s, { t: 'stop' }), IDLE);
});

test('playState: B while A plays replaces A; a late answer or end for A changes nothing; a stop before the answer stays stopped', () => {
  const b = playState({ key: 'a', phase: 'playing' }, { t: 'start', key: 'b' });
  assert.deepEqual(b, { key: 'b', phase: 'loading' });
  assert.deepEqual(playState(b, { t: 'playing', key: 'a' }), b);
  assert.deepEqual(playState(b, { t: 'end', key: 'a' }), b);
  const stopped = playState(playState(IDLE, { t: 'start', key: 'a' }), { t: 'stop' });
  assert.deepEqual(playState(stopped, { t: 'playing', key: 'a' }), IDLE, 'no stuck "speaking" state');
  assert.deepEqual(playState(playState(IDLE, { t: 'start', key: 'a' }), { t: 'end', key: 'a' }), IDLE, 'a failure goes idle');
});

test('voiceGroups: built-ins by language and accent in order, then the blends; labels', () => {
  const voices = [
    { name: 'af_heart', language: 'English', accent: 'American', gender: 'Female' },
    { name: 'mine', custom: true },
    { name: 'bm_george', language: 'English', accent: 'British', gender: 'Male' },
    { name: 'am_adam', language: 'English', accent: 'American', gender: 'Male' },
    { name: 'ef_dora', language: 'Spanish', accent: '', gender: 'Female' },
  ];
  assert.deepEqual(voiceGroups(voices).map(([label, list]) => [label, list.map(v => v.name)]), [
    ['English (American)', ['af_heart', 'am_adam']], ['English (British)', ['bm_george']], ['Spanish', ['ef_dora']], ['Your voices', ['mine']]]);
  assert.deepEqual(voiceGroups([]), []);
  assert.equal(voiceLabel(voices[0]), 'heart · Female');
  assert.equal(voiceLabel(voices[1]), 'mine');
});

test('voiceGroups: Chatterbox voices in their own group before the blends; their label; the fallback note', () => {
  const voices = [
    { name: 'af_heart', language: 'English', accent: 'American', gender: 'Female', engine: 'kokoro' },
    { name: 'mine', custom: true, engine: 'kokoro' },
    { name: 'cb:Default', label: 'Default', engine: 'chatterbox' },
    { name: 'cb:Narrator', label: 'Narrator', engine: 'chatterbox' },
  ];
  assert.deepEqual(voiceGroups(voices).map(([label, list]) => [label, list.map(v => v.name)]), [
    ['English (American)', ['af_heart']], ['Chatterbox', ['cb:Default', 'cb:Narrator']], ['Your voices', ['mine']]]);
  assert.equal(voiceLabel(voices[3]), 'Narrator');
  assert.equal(voiceLabel({ name: 'cb:Old', engine: 'chatterbox' }), 'Old');
  assert.equal(fallbackNote(null), null);
  assert.equal(fallbackNote('af_heart'), 'Chatterbox is unavailable on the PC, reading with af_heart');
  assert.equal(fallbackNote('My%20blend'), 'Chatterbox is unavailable on the PC, reading with My blend');
});

const tick = () => new Promise(r => setTimeout(r, 5));
const aborted = () => Object.assign(new Error('aborted'), { name: 'AbortError' });

test('readSentences: each sentence in order, the next one fetched while this one plays', async () => {
  const log = [];
  await readSentences(['a', 'b', 'c'], async s => { log.push('fetch ' + s); return s; },
    async c => { log.push('play ' + c); await tick(); log.push('end ' + c); }, new AbortController().signal);
  assert.deepEqual(log, ['fetch a', 'fetch b', 'play a', 'end a', 'fetch c', 'play b', 'end b', 'play c', 'end c']);
  const none = [];
  await readSentences([], s => none.push(s), c => none.push(c), new AbortController().signal);
  assert.deepEqual(none, []);
});

test('readSentences: after an abort nothing more is fetched or played, even when a fetch still answers', async () => {
  let ac = new AbortController();
  const fetched = [], played = [];
  // stopped while the first sentence is made; the PC's answer arrives anyway
  await readSentences(['a', 'b', 'c'], async s => { fetched.push(s); if (s === 'a') ac.abort(); return s; }, async c => { played.push(c); }, ac.signal);
  assert.deepEqual([fetched, played], [['a'], []]);
  // stopped while the first plays: the prefetch is aborted, nothing after it
  ac = new AbortController(); fetched.length = played.length = 0;
  await readSentences(['a', 'b', 'c'], (s, signal) => { fetched.push(s); return s === 'a' ? Promise.resolve(s)
    : new Promise((_, reject) => signal.addEventListener('abort', () => setTimeout(() => reject(aborted())))); },
  async c => { played.push(c); ac.abort(); }, ac.signal);
  assert.deepEqual([fetched, played], [['a', 'b'], ['a']]);
});

test("readSentences: the PC's 503 for a sentence dropped after an abort is not an error; a failure before one is", async () => {
  const stopped = () => Promise.reject(Object.assign(new Error('Stopped'), { status: 503 }));
  const ac = new AbortController();
  await readSentences(['a', 'b'], async s => { if (s === 'b') { await tick(); return stopped(); } return s; }, async () => { ac.abort(); }, ac.signal);
  await assert.rejects(readSentences(['a', 'b'], s => s === 'b' ? stopped() : Promise.resolve(s), async () => {}, new AbortController().signal),
    { status: 503 });
});

test('readSentences: a prefetch that fails while a clip fails to play is no unhandled rejection', async () => {
  const unhandled = [], onUnhandled = e => unhandled.push(e);
  process.on('unhandledRejection', onUnhandled);
  try {
    await assert.rejects(readSentences(['a', 'b'], s => s === 'b' ? tick().then(() => { throw new Error('PC gone'); }) : Promise.resolve(s),
      async () => { throw new Error('cannot play'); }, new AbortController().signal), /cannot play/);
    await tick(); await tick();
    assert.deepEqual(unhandled, []);
  } finally { process.off('unhandledRejection', onUnhandled); }
});

test("Read replies aloud: the phone's own, per PC (its API base, or this page's host); off until set; storage errors read as off", () => {
  const store = new Map(), storage = { getItem: k => store.get(k) ?? null, setItem: (k, v) => store.set(k, String(v)) };
  assert.equal(autoReadKey('https://home.tail0000.ts.net', 'here:8443'), 'autoRead:https://home.tail0000.ts.net');
  assert.equal(autoReadKey('', 'here:8443'), 'autoRead:here:8443');
  assert.equal(readAutoRead(storage, '', 'here:8443'), false);
  writeAutoRead(storage, '', 'here:8443', true);
  assert.equal(readAutoRead(storage, '', 'here:8443'), true);
  assert.equal(readAutoRead(storage, 'https://other.tail0000.ts.net', 'here:8443'), false, 'another PC keeps its own');
  writeAutoRead(storage, '', 'here:8443', false);
  assert.equal(readAutoRead(storage, '', 'here:8443'), false);
  const broken = { getItem() { throw new Error('denied'); }, setItem() { throw new Error('denied'); } };
  assert.equal(readAutoRead(broken, '', 'here'), false);
  assert.doesNotThrow(() => writeAutoRead(broken, '', 'here', true));
});
