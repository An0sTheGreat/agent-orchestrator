import test from 'node:test';
import assert from 'node:assert/strict';
import { sizeBody, sizeBoxes, slashMatches, sendPayload, swapBody } from '../js/composer.js';

test('a size edit posts only the field that changed', () => {
  assert.deepEqual(sizeBody('height', '720'), { imageGen: { height: 720 } });
  assert.deepEqual(sizeBody('width', '1000'), { imageGen: { width: 1000 } });
});

test('an emptied size box posts 0 (empty), not a size', () => {
  assert.deepEqual(sizeBody('width', ''), { imageGen: { width: 0 } });
  assert.deepEqual(sizeBody('height', 'abc'), { imageGen: { height: 0 } });
});

test('the size boxes show the box values (0 = empty), the effective size only as the hint', () => {
  assert.deepEqual(sizeBoxes({ width: 0, height: 720, size: '1280×720' }), { width: '', height: '720', hintWidth: '1280', hintHeight: '720' });
  assert.deepEqual(sizeBoxes({ width: 0, height: 0, size: '' }), { width: '', height: '', hintWidth: '', hintHeight: '' });
});

const skills = [{ name: 'review', on: true }, { name: 'debug', on: false }, { name: 'Ponytail', on: false }];

test('a lone / lists the skills and /compact', () => {
  assert.deepEqual(slashMatches('/', skills).map(r => r.name), ['review', 'debug', 'Ponytail', 'compact']);
});

test('a skill picked from the / list inserts $name; /compact inserts itself', () => {
  assert.deepEqual(slashMatches('/', skills).map(r => r.insert), ['$review ', '$debug ', '$Ponytail ', '/compact ']);
});

test('a skill row shows its description when it has one', () => {
  assert.deepEqual(slashMatches('/b', [{ name: 'blender', on: true, description: 'Drive Blender' }, { name: 'bare', on: false, description: '' }]).map(r => r.help),
    ['Drive Blender', 'Skill']);
});

test('the / list filters by prefix, ignoring case', () => {
  assert.deepEqual(slashMatches('/p', skills).map(r => r.name), ['Ponytail']);
  assert.deepEqual(slashMatches('/co', skills).map(r => r.name), ['compact']);
  assert.deepEqual(slashMatches('/zz', skills), []);
});

test('the / list is only for a / word at the start of the box', () => {
  assert.equal(slashMatches('hi /re', skills), null);
  assert.equal(slashMatches('/review now', skills), null);
  assert.equal(slashMatches('', skills), null);
});

test('the send payload: a new chat sends id "", the text trimmed, the uploaded paths', () => {
  assert.deepEqual(sendPayload(null, '  hello \n', ['C:\\p\\a.png']), { id: '', text: 'hello', attachments: ['C:\\p\\a.png'] });
  assert.deepEqual(sendPayload('abc', 'hi', []), { id: 'abc', text: 'hi', attachments: [] });
});

test('nothing to send: no payload', () => {
  assert.equal(sendPayload('abc', '   ', []), null);
  assert.deepEqual(sendPayload('abc', '', ['C:\\p\\a.png']), { id: 'abc', text: '', attachments: ['C:\\p\\a.png'] });
});

test('the swap flips the ratio and swaps the typed boxes (empty stays empty)', () => {
  assert.deepEqual(swapBody({ aspect: '3:2', width: 1024, height: 0 }), { imageGen: { aspect: '2:3', width: 0, height: 1024 } });
  assert.deepEqual(swapBody({ aspect: '9:16', width: 576, height: 1024 }), { imageGen: { aspect: '16:9', width: 1024, height: 576 } });
});
