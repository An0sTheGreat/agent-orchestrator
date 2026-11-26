import test from 'node:test';
import assert from 'node:assert/strict';
import { downsample, toInt16, levelOf, silenceGate, insertAt } from '../js/dictate.js';

test('downsample: 48 kHz to 16 kHz keeps a 1 kHz tone (Speech.Resample rules)', () => {
  const sine = Float32Array.from({ length: 48000 }, (_, i) => Math.sin(2 * Math.PI * 1000 * i / 48000));
  const down = downsample(sine, 48000, 16000);
  assert.equal(down.length, 16000);
  let crossings = 0;
  for (let i = 1; i < down.length; i++) if ((down[i - 1] < 0) !== (down[i] < 0)) crossings++;
  assert.ok(Math.abs(crossings - 2000) <= 4, `${crossings} zero crossings in 1 s`);
});

test('downsample: each output is the mean of the input it covers; 44.1 kHz gives the right length; up interpolates', () => {
  assert.deepEqual([...downsample(Float32Array.from([1, 2, 3, 4, 5, 6]), 48000, 16000)], [2, 5]);
  assert.equal(downsample(new Float32Array(44100), 44100, 16000).length, 16000);
  assert.deepEqual([...downsample(Float32Array.from([0, 1]), 8000, 16000)], [0, 0.5, 1, 1]);
  assert.deepEqual([...downsample(Float32Array.from([0.25]), 16000)], [0.25]);
});

test('toInt16: full scale both ways, clipped beyond', () => {
  assert.deepEqual([...toInt16(Float32Array.from([0, 1, -1, 2, -2, 0.5]))], [0, 32767, -32768, 32767, -32768, 16383]);
});

test('levelOf: silence 0, full scale 1, below -60 dB 0', () => {
  assert.equal(levelOf(new Float32Array(1600)), 0);
  assert.equal(levelOf(new Float32Array(1600).fill(1)), 1);
  assert.equal(levelOf(new Float32Array(1600).fill(0.0005)), 0);
});

const feed = (levels, ms = 100) => levels.reduce((g, l) => silenceGate(g, l, ms), { heard: false, quiet: 0, total: 0 });

test('silenceGate: 2 s of silence after speech stops it, not before', () => {
  const speech = new Array(10).fill(0.8);
  assert.equal(feed([...speech, ...new Array(19).fill(0.1)]).stop, false);
  assert.equal(feed([...speech, ...new Array(20).fill(0.1)]).stop, true);
  assert.equal(feed([...speech, ...new Array(15).fill(0.1), 0.8, ...new Array(10).fill(0.1)]).stop, false, 'speech again resets the pause');
});

test('silenceGate: 10 s with no speech stops it', () => {
  assert.equal(feed(new Array(99).fill(0.2)).stop, false);
  assert.equal(feed(new Array(100).fill(0.2)).stop, true);
});

test('insertAt: the Speech.Insert rules', () => {
  assert.deepEqual(insertAt('', 0, 'hello.'), { text: 'Hello.', caret: 6 });
  assert.deepEqual(insertAt('Hi', 2, 'there'), { text: 'Hi there', caret: 8 });
  assert.deepEqual(insertAt('end. ', 5, 'next'), { text: 'end. Next', caret: 9 });
  assert.deepEqual(insertAt('end.', 4, 'next'), { text: 'end. Next', caret: 9 });
  assert.deepEqual(insertAt('Hello world', 5, 'big'), { text: 'Hello big world', caret: 9 });
  assert.deepEqual(insertAt('Hello world', 6, 'big'), { text: 'Hello big world', caret: 9 });
  assert.deepEqual(insertAt('Hi', 2, ', you'), { text: 'Hi, you', caret: 7 });
  assert.deepEqual(insertAt('Hi', 2, '  '), { text: 'Hi', caret: 2 });
  assert.deepEqual(insertAt('Line\n', 5, 'next'), { text: 'Line\nNext', caret: 9 });
  assert.deepEqual(insertAt('Really?', 99, 'yes'), { text: 'Really? Yes', caret: 11 });
});
