import test from 'node:test';
import assert from 'node:assert/strict';
import { termEmpty, termFeed, termText, termMerge, TERM_LINES, shotPoint, barUrl, typedUrl } from '../js/tools.js';

const feed = (...chunks) => termText(chunks.reduce(termFeed, termEmpty()));

test('termFeed: escape codes dropped, the text kept', () => {
  assert.equal(feed('\x1b[?25l\x1b[2J\x1b[m\x1b[HWindows \x1b[32mPowerShell\x1b[0m\r\n\x1b]0;Title\x07PS C:\\>'), 'Windows PowerShell\nPS C:\\>');
  assert.equal(feed('a\x1b]0;t\x1b\\b\x07c\x1b(Bd'), 'abcd');
});

test('termFeed: an escape cut between chunks is finished by the next one', () => {
  assert.equal(feed('red \x1b[3', '1mtext\x1b', '[0m!'), 'red text!');
  assert.equal(feed('x\x1b]0;ti', 'tle\x07y'), 'xy');
});

test('termFeed: \\r overwrites, backspace steps left, CSI K erases, CSI C moves on', () => {
  assert.equal(feed('Progress 10%\rProgress 99%'), 'Progress 99%');
  assert.equal(feed('abc\b\bX'), 'aXc');
  assert.equal(feed('long line\rshort\x1b[K'), 'short');
  assert.equal(feed('a\x1b[3Cb'), 'a   b');
  assert.equal(feed('one\r\ntwo\n'), 'one\ntwo\n');
});

test('termFeed: the last TERM_LINES lines are kept', () => {
  const t = termFeed(termEmpty(), Array.from({ length: TERM_LINES + 50 }, (_, i) => 'line ' + i).join('\n'));
  assert.equal(t.lines.length + 1, TERM_LINES);
  assert.equal(t.lines[0], 'line 50');
  assert.equal(t.line, 'line ' + (TERM_LINES + 49));
});

test('termMerge: appends since the last seq; a seq behind it starts over', () => {
  let { t, since } = termMerge(termEmpty(), 0, { seq: 5, text: 'hello' });
  assert.equal(since, 5);
  ({ t, since } = termMerge(t, since, { seq: 11, text: ' world' }));
  assert.equal(termText(t), 'hello world');
  ({ t, since } = termMerge(t, since, { seq: 11, text: '' }));
  assert.equal(termText(t), 'hello world');
  ({ t, since } = termMerge(t, since, { seq: 3, text: 'new' }));
  assert.deepEqual([termText(t), since], ['new', 3]);
});

test('shotPoint: a tap scaled from the drawn size to the shot\'s pixels, kept inside', () => {
  const rect = { left: 10, top: 100, width: 360, height: 225 };
  assert.deepEqual(shotPoint(190, 212.5, rect, 1280, 800), { x: 640, y: 400 });
  assert.deepEqual(shotPoint(10, 100, rect, 1280, 800), { x: 0, y: 0 });
  assert.deepEqual(shotPoint(500, 400, rect, 1280, 800), { x: 1279, y: 799 });
});

test('barUrl shows the address without https:// or a bare host\'s /; typedUrl sends what was typed', () => {
  assert.equal(barUrl('https://example.com/'), 'example.com');
  assert.equal(barUrl('https://example.com/a/b?q=1'), 'example.com/a/b?q=1');
  assert.equal(barUrl('http://localhost:5173/'), 'http://localhost:5173');
  assert.equal(barUrl(null), '');
  assert.equal(typedUrl('  weather in london '), 'weather in london');
});
