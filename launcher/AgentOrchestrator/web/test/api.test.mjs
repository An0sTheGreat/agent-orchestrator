import test from 'node:test';
import assert from 'node:assert/strict';
import { parseSse, nextWait, tailnetOf, failAction, baseOf, startBase, gone, latestOnly } from '../js/api.js';

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

test('the remembered PC is forgotten when it answers with a refusal, not when it is off or slow', async () => {
  let forgot = 0;
  const forget = () => { forgot++; };
  const refusing = async () => { throw { status: 404 }; };
  const off = async () => { throw new TypeError('Failed to fetch'); };
  const slow = async () => { throw Object.assign(new Error('timed out'), { name: 'TimeoutError' }); };
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', off, forget), '');
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', slow, forget), '');
  assert.equal(forgot, 0);
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', refusing, forget), '');
  assert.equal(forgot, 1);
});

test('the remembered PC is gone when a fresh PCs list (with this PC on the tailnet) no longer has it', () => {
  const pcs = [{ pc: 'PC', dnsName: 'pc.tail0000.ts.net.', self: true }, { pc: 'LAPTOP', dnsName: 'Laptop.tail0000.ts.net.', self: false }];
  assert.equal(gone('laptop.tail0000.ts.net', pcs), false);
  assert.equal(gone('pc.tail0000.ts.net', pcs), false);
  assert.equal(gone('old.tail0000.ts.net', pcs), true);
  assert.equal(gone(null, pcs), false);
  // this PC's tailnet unknown (Tailscale down): the list proves nothing
  assert.equal(gone('laptop.tail0000.ts.net', [{ pc: 'PC', dnsName: '', self: true }]), false);
});

test('only the latest call settles: a late answer or failure from an earlier call is dropped', async () => {
  const pending = [];
  const call = latestOnly(() => new Promise((resolve, reject) => pending.push({ resolve, reject })));
  const settled = [];
  call().then(v => settled.push('first ' + v), e => settled.push('first failed ' + e));
  call().then(v => settled.push('second ' + v));
  pending[1].resolve('new');
  pending[0].resolve('old');
  await new Promise(r => setTimeout(r, 10));
  assert.deepEqual(settled, ['second new']);
  call().then(() => settled.push('third'), e => settled.push('third failed ' + e));
  call().then(() => {}, e => settled.push('fourth failed ' + e));
  pending[2].reject('down');
  pending[3].reject('lost');
  await new Promise(r => setTimeout(r, 10));
  assert.deepEqual(settled, ['second new', 'fourth failed lost']);
});

test('the remembered PC is kept when its probe answers 5xx, forgotten on 403', async () => {
  let forgot = 0;
  const forget = () => { forgot++; };
  const broken = async () => { throw { status: 500 }; };
  const refusing = async () => { throw { status: 403 }; };
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', broken, forget), '');
  assert.equal(forgot, 0);
  assert.equal(await startBase('laptop.tail0000.ts.net', 'pc.tail0000.ts.net', refusing, forget), '');
  assert.equal(forgot, 1);
});
