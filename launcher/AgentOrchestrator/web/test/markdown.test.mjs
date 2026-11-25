import test from 'node:test';
import assert from 'node:assert/strict';
import { parseMarkdown } from '../js/markdown.js';

const text = runs => runs.map(r => r.text).join('');

test('heading, paragraph with bold and code, list, fenced code', () => {
  const b = parseMarkdown('# T\n\nA **b** `c`\n\n- x\n- y\n\n```js\nlet a\n```');
  assert.deepEqual(b.map(x => x.type), ['heading', 'paragraph', 'list', 'code']);
  assert.equal(b[0].level, 1);
  assert.equal(text(b[0].runs), 'T');
  assert.deepEqual(b[1].runs, [{ t: 'text', text: 'A ' }, { t: 'bold', text: 'b' }, { t: 'text', text: ' ' }, { t: 'code', text: 'c' }]);
  assert.equal(b[2].ordered, false);
  assert.deepEqual(b[2].items.map(text), ['x', 'y']);
  assert.deepEqual([b[3].lang, b[3].text], ['js', 'let a']);
});

test('table with a header row and two rows', () => {
  const [t] = parseMarkdown('| a | b |\n|---|---|\n| 1 | 2 |\n| 3 | 4 |');
  assert.equal(t.type, 'table');
  assert.deepEqual(t.head.map(text), ['a', 'b']);
  assert.deepEqual(t.rows.map(r => r.map(text)), [['1', '2'], ['3', '4']]);
});

test('numbered list, quote, italic, http link', () => {
  const b = parseMarkdown('1. one\n2. two\n\n> said\n\n*i* [x](https://e.com)');
  assert.deepEqual([b[0].type, b[0].ordered, b[0].items.length], ['list', true, 2]);
  assert.deepEqual([b[1].type, text(b[1].runs)], ['quote', 'said']);
  assert.deepEqual(b[2].runs, [{ t: 'italic', text: 'i' }, { t: 'text', text: ' ' }, { t: 'link', text: 'x', href: 'https://e.com' }]);
});

test('a javascript: link stays plain text', () => {
  const [p] = parseMarkdown('[x](javascript:alert(1))');
  assert.ok(p.runs.every(r => r.t === 'text'));
  assert.equal(text(p.runs), '[x](javascript:alert(1))');
});
