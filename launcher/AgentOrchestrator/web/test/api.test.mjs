import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSse, nextWait, tailnetOf } from '../js/api.js';

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
