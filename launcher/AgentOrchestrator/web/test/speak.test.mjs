import test from 'node:test';
import assert from 'node:assert/strict';
import { speechOf, keyOf, playState, IDLE, voiceGroups, voiceLabel } from '../js/speak.js';

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
