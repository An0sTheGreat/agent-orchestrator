import test from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { route, cacheName, stale, SHELL } from '../sw.js';

const here = 'https://pc.tail0000.ts.net';
const req = (path, mode = 'cors', method = 'GET') => ({ url: new URL(path, here).href, mode, method });

test('a page is network-first, wherever it points on this PC', () => {
  assert.equal(route(req('/', 'navigate'), here), 'page');
  assert.equal(route(req('/?x=1', 'navigate'), here), 'page');
});

test("the shell's files are cache-first", () => {
  for (const path of ['/styles.css', '/js/main.js', '/assets/fonts/Inter-Regular.ttf', '/icons/icon-192.png', '/manifest.webmanifest'])
    assert.equal(route(req(path, 'no-cors'), here), 'shell', path);
});

test('the API, other origins and anything but GET pass through', () => {
  assert.equal(route(req('/api/hello'), here), 'pass');
  assert.equal(route(req('/api/conversation/a/events'), here), 'pass');
  assert.equal(route(req('/api/file?path=x.png', 'no-cors'), here), 'pass');
  assert.equal(route(req('/api/', 'navigate'), here), 'pass');
  assert.equal(route({ url: 'https://laptop.tail0000.ts.net/styles.css', mode: 'cors', method: 'GET' }, here), 'pass');
  assert.equal(route({ url: 'https://laptop.tail0000.ts.net/', mode: 'navigate', method: 'GET' }, here), 'pass');
  assert.equal(route(req('/upload', 'cors', 'POST'), here), 'pass');
});

test("the cache is named by the worker's version; activation drops the shell caches of other versions only", () => {
  assert.equal(cacheName('1.4.2'), 'ao-shell-1.4.2');
  assert.notEqual(cacheName('1.4.2'), cacheName('1.4.3'));
  assert.deepEqual(stale(['ao-shell-1.4.1', 'ao-shell-1.4.2', 'someone-else'], '1.4.2'), ['ao-shell-1.4.1']);
});

test('the shell holds the page, its styles, manifest and every script, font and icon in web/', () => {
  const dir = sub => readdirSync(new URL('../' + sub, import.meta.url)).map(f => `/${sub}/${f}`);
  const want = ['/', '/index.html', '/styles.css', '/manifest.webmanifest', ...dir('js'), ...dir('assets/fonts'), ...dir('icons')];
  assert.deepEqual([...SHELL].sort(), want.sort());
});
