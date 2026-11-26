import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSse, nextWait, tailnetOf, failAction, baseOf, startBase } from '../js/api.js';

test('two events in one chunk', () => {
  assert.deepEqual(parseSse('data: {"e":"a"}\n\ndata: {"e":"b"}\n\n'), [{ e: 'a' }, { e: 'b' }]);
});

test('a frame split across two chunks is joined', () => {
  const state = {};
  assert.deepEqual(parseSse('data: {"e":', state), []);
  assert.deepEqual(parseSse('"a"}\n\n: keepalive\n\n', state), [{ e: 'a' }]);
});

test('the retry wait doubles from 1 s and stops at 15 s', () => {
  assert.deepEqual([1000, 2000, 8000, 15000].map(nextWait), [2000, 4000, 15000, 15000]);
});

test('the tailnet is the DNS name without the machine label', () => {
  assert.equal(tailnetOf('home.tail0000.ts.net.'), 'tail0000.ts.net');
  assert.equal(tailnetOf('home.tail0000.ts.net'), 'tail0000.ts.net');
  assert.equal(tailnetOf(''), '');
});

test('only the signed-out 403 reloads, once; unreachable is lost; the rest is a toast', () => {
  const err = (status, message) => Object.assign(new Error(message), { status });
  assert.equal(failAction(err(403, 'SignedOut'), false), 'reload');
  assert.equal(failAction(err(403, 'SignedOut'), true), 'toast'); // already reloaded for it: no loop
  assert.equal(failAction(err(403, 'Cross-site request refused'), false), 'toast');
  assert.equal(failAction(err(0, 'The PC could not be reached'), false), 'lost');
  assert.equal(failAction(err(500, 'boom'), false), 'toast');
});

test('a PC is reached at its https origin; this page\'s own PC (or none) is the page\'s origin', () => {
  assert.equal(baseOf('laptop.tail0000.ts.net', 'pc.tail0000.ts.net'), 'https://laptop.tail0000.ts.net');
  assert.equal(baseOf('laptop.tail0000.ts.net.', 'pc.tail0000.ts.net'), 'https://laptop.tail0000.ts.net');
  assert.equal(baseOf('PC.tail0000.ts.net', 'pc.tail0000.ts.net'), '');
  assert.equal(baseOf(null, 'pc.tail0000.ts.net'), '');
});

test('on start: the remembered PC if it answers, else this one', async () => {
  const asked = [];
  const answers = async base => { asked.push(base); };
  const silent = async base => { asked.push(base); throw new Error('no'); };
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', answers), 'https://laptop.tail0000.ts.net');
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', silent), '');
  assert.deepEqual(asked, ['https://laptop.tail0000.ts.net', 'https://laptop.tail0000.ts.net']);
  assert.equal(await startBase('pc.tail0000.ts.net', 'pc.tail0000.ts.net', silent), ''); // this PC: nothing to ask first
  assert.equal(await startBase(null, 'pc.tail0000.ts.net', silent), '');
  assert.equal(asked.length, 2);
});
