import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSse } from '../js/api.js';

test('two events in one chunk', () => {
  assert.deepEqual(parseSse('data: {"e":"a"}\n\ndata: {"e":"b"}\n\n'), [{ e: 'a' }, { e: 'b' }]);
});

test('a frame split across two chunks is joined', () => {
  const state = {};
  assert.deepEqual(parseSse('data: {"e":', state), []);
  assert.deepEqual(parseSse('"a"}\n\n: keepalive\n\n', state), [{ e: 'a' }]);
});
