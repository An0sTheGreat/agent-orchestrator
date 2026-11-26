import test from 'node:test';
import assert from 'node:assert/strict';
import { groupSkills } from '../js/skills.js';

const sk = (key, builtIn) => ({ key, name: key, description: '', builtIn, on: true });

test("skills: the user's own under YOUR SKILLS first, then BUILT IN, each in the server's order", () => {
  const list = [sk('b-mine', false), sk('a-builtin', true), sk('a-mine', false), sk('z-builtin', true)];
  assert.deepEqual(groupSkills(list).map(([t, l]) => [t, l.map(s => s.key)]),
    [['YOUR SKILLS', ['b-mine', 'a-mine']], ['BUILT IN', ['a-builtin', 'z-builtin']]]);
});

test('skills: an empty group is left out; no skills, no groups', () => {
  assert.deepEqual(groupSkills([sk('x', true)]).map(([t]) => t), ['BUILT IN']);
  assert.deepEqual(groupSkills([sk('x', false)]).map(([t]) => t), ['YOUR SKILLS']);
  assert.deepEqual(groupSkills([]), []);
  assert.deepEqual(groupSkills(), []);
});
