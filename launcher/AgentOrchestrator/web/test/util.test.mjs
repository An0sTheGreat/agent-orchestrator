import test from 'node:test';
import assert from 'node:assert/strict';
import { ago, screensToPop } from '../js/util.js';

test('a back gesture pops one screen, never the root, and forward pops none', () => {
  assert.equal(screensToPop(3, 5, 4), 1); // back one entry
  assert.equal(screensToPop(3, 5, 2), 2); // back several entries, the root stays
  assert.equal(screensToPop(1, 5, 4), 0); // at the root: nothing to pop
  assert.equal(screensToPop(2, 4, 5), 0); // forward
});

test('times read like the concept: min ago, yesterday, days ago, last week', () => {
  const now = new Date(2026, 10, 25, 12, 0);
  assert.equal(ago(new Date(2026, 10, 25, 11, 59, 40), now), 'just now');
  assert.equal(ago(new Date(2026, 10, 25, 11, 57), now), '3 min ago');
  assert.equal(ago(new Date(2026, 10, 25, 9, 0), now), '3 h ago');
  assert.equal(ago(new Date(2026, 10, 24, 23, 0), now), 'yesterday');
  assert.equal(ago(new Date(2026, 10, 23, 8, 0), now), '2 days ago');
  assert.equal(ago(new Date(2026, 10, 16, 8, 0), now), 'last week');
  assert.equal(ago('2026-10-01T08:00:00', now), 'Oct 1');
});
