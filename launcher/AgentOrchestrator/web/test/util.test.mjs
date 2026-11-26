import test from 'node:test';
import assert from 'node:assert/strict';
import { ago, makeRouter, longPress } from '../js/util.js';

test('longPress: a 500 ms hold fires once and swallows its click; moving, lifting early or another button does not', t => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const el = new EventTarget();
  let n = 0;
  longPress(el, () => n++);
  const send = (type, props = {}) => {
    const e = Object.assign(new Event(type, { cancelable: true }), { button: 0, clientX: 0, clientY: 0, ...props });
    el.dispatchEvent(e);
    return e;
  };
  send('pointerdown'); t.mock.timers.tick(499); assert.equal(n, 0);
  t.mock.timers.tick(1); assert.equal(n, 1);
  send('contextmenu'); assert.equal(n, 1, 'the context menu after a long press is not a second one');
  assert.equal(send('click').defaultPrevented, true, 'the click ending a long press is swallowed');
  assert.equal(send('click').defaultPrevented, false, 'the next click is not');
  send('pointerdown'); send('pointermove', { clientX: 8, clientY: 7 }); t.mock.timers.tick(600); assert.equal(n, 1, 'moved over 10 px');
  send('pointerdown'); send('pointermove', { clientX: 6, clientY: 6 }); t.mock.timers.tick(600); assert.equal(n, 2, 'a small wobble still counts');
  send('pointerdown'); t.mock.timers.tick(300); send('pointerup'); t.mock.timers.tick(600); assert.equal(n, 2, 'lifted early');
  send('pointerdown', { button: 2 }); t.mock.timers.tick(600); assert.equal(n, 2, 'not the primary button');
  assert.equal(send('contextmenu').defaultPrevented, true); assert.equal(n, 3, 'a right-click');
});

// A tab's history as the browser keeps it (states cloned); back() from its first entry leaves the app.
function tab() {
  const entries = [{ state: null }];
  let i = 0;
  const h = {
    left: false, onpop: null,
    get state() { return entries[i].state; },
    pushState(s) { entries.splice(i + 1, Infinity, { state: structuredClone(s) }); i++; },
    replaceState(s) { entries[i] = { state: structuredClone(s) }; },
    back() { if (i === 0) h.left = true; else { i--; h.onpop(entries[i].state); } },
  };
  return h;
}
const scr = (name, params = {}) => ({ name, params });
const names = r => r.stack.map(s => s.name).join('>');
function app(history = tab()) {
  const r = makeRouter(history);
  history.onpop = s => r.pop(s);
  r.start();
  return [r, history];
}

test('fresh load: back at Chats stays in the app, twice', () => {
  const [r, h] = app();
  assert.equal(names(r), 'chats');
  h.back();
  assert.equal(names(r), 'chats');
  h.back();
  assert.equal(names(r), 'chats');
  assert.equal(h.left, false);
});

test('a go chain goes back one screen per back, then stays at Chats', () => {
  const [r, h] = app();
  r.go(scr('chat', { id: 'a' }));
  r.go(scr('settings'));
  h.back();
  assert.equal(names(r), 'chats>chat');
  assert.deepEqual(r.stack[1].params, { id: 'a' });
  h.back();
  assert.equal(names(r), 'chats');
  h.back();
  assert.equal([names(r), h.left].join(), 'chats,false');
});

test('a tab switch, then back: Chats (no dead press), and back again stays', () => {
  const [r, h] = app();
  r.go(scr('chat', { id: 'a' }));
  r.reset([scr('skills')]);
  assert.equal(names(r), 'skills');
  h.back();
  assert.equal(names(r), 'chats');
  h.back();
  assert.equal([names(r), h.left].join(), 'chats,false');
});

test('new chat from Chats, then back: Chats, still in the app', () => {
  const [r, h] = app();
  r.reset([scr('chats'), scr('chat', { id: null })]);
  h.back();
  assert.equal(names(r), 'chats');
  h.back();
  assert.equal([names(r), h.left].join(), 'chats,false');
});

test('a reload starts clean at Chats, and back stays there', () => {
  const [r, h] = app();
  r.go(scr('chat', { id: 'a' }));
  r.go(scr('settings'));
  const [r2] = app(h); // the reloaded page, same tab history
  assert.equal(names(r2), 'chats');
  h.back();
  assert.equal([names(r2), h.left].join(), 'chats,false');
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
