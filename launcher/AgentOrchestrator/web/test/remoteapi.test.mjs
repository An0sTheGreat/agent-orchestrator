import test from 'node:test';
import assert from 'node:assert/strict';
import { modelList, toggleOrder, providerList, parseOrder, withFloor, hasFloor, baseId, floorable, applyBody, serviceFor } from '../js/remoteapi.js';

const MODELS = [
  { id: 'qwen/qwen3.5-4b', name: 'Qwen 3.5 4B' },
  { id: 'openai/gpt-5-mini', name: 'GPT-5 mini', vision: true },
  { id: 'deepseek/deepseek-v4', name: 'DeepSeek V4' },
];

test('modelList: favorites (of this service) first, the server order otherwise', () => {
  const list = modelList(MODELS, ['openrouter|deepseek/deepseek-v4', 'openai|qwen/qwen3.5-4b'], 'openrouter');
  assert.deepEqual(list.map(m => m.id), ['deepseek/deepseek-v4', 'qwen/qwen3.5-4b', 'openai/gpt-5-mini']);
  assert.deepEqual(list.map(m => m.favorite), [true, false, false]);
});

test('modelList: search matches the id or the name, any case, trimmed', () => {
  assert.deepEqual(modelList(MODELS, [], 'openrouter', ' GPT ').map(m => m.id), ['openai/gpt-5-mini']);
  assert.deepEqual(modelList(MODELS, [], 'openrouter', 'deepseek v4').map(m => m.id), ['deepseek/deepseek-v4']);
  assert.deepEqual(modelList(MODELS, ['openrouter|qwen/qwen3.5-4b'], 'openrouter', 'e').map(m => m.id),
    ['qwen/qwen3.5-4b', 'openai/gpt-5-mini', 'deepseek/deepseek-v4']);
  assert.deepEqual(modelList(MODELS, [], 'openrouter', 'nothing'), []);
});

test('provider order: a tap adds at the end or takes out (the rest move up); the comma list as the desktop', () => {
  let order = [];
  for (const p of ['DeepInfra', 'Together', 'Groq']) order = toggleOrder(order, p);
  assert.deepEqual(order, ['DeepInfra', 'Together', 'Groq']);
  order = toggleOrder(order, 'DeepInfra');
  assert.deepEqual(order, ['Together', 'Groq']);
  assert.deepEqual(toggleOrder(order, 'DeepInfra'), ['Together', 'Groq', 'DeepInfra']);
  assert.equal(providerList(['Together', 'Groq']), 'Together, Groq');
  assert.equal(providerList([]), '');
  assert.deepEqual(parseOrder(' Together, Groq ,,'), ['Together', 'Groq']);
  assert.deepEqual(parseOrder(''), []);
});

test(':floor: on adds it to a base id, off takes it off; an id with its own variant gets none', () => {
  assert.equal(withFloor('qwen/qwen3.5-4b', true), 'qwen/qwen3.5-4b:floor');
  assert.equal(withFloor('qwen/qwen3.5-4b:floor', true), 'qwen/qwen3.5-4b:floor');
  assert.equal(withFloor('qwen/qwen3.5-4b:floor', false), 'qwen/qwen3.5-4b');
  assert.equal(withFloor(' qwen/qwen3.5-4b ', false), 'qwen/qwen3.5-4b');
  assert.equal(withFloor('qwen/qwen3.5-4b:free', true), 'qwen/qwen3.5-4b:free');
  assert.equal(withFloor('', true), '');
  assert.equal(hasFloor('a/b:floor'), true);
  assert.equal(hasFloor('a/b:free:floor'), false);
  assert.equal(baseId('a/b:floor'), 'a/b');
  assert.equal(floorable('a/b'), true);
  assert.equal(floorable('a/b:floor'), true);
  assert.equal(floorable('a/b:free'), false);
});

const OPENROUTER = { id: 'openrouter', name: 'OpenRouter', baseUrl: 'https://openrouter.ai/api/v1', hasProviders: true };
const GROQ = { id: 'groq', name: 'Groq', baseUrl: 'https://api.groq.com/openai/v1', hasProviders: false };
const CUSTOM = { id: 'custom', name: 'Custom URL…', baseUrl: '', hasProviders: false };

test('applyBody: OpenRouter sends the floor id, the order and the price settings', () => {
  assert.deepEqual(applyBody(OPENROUTER, { model: ' qwen/qwen3.5-4b ', floor: true, order: ['Together', 'Groq'], sortPrice: true, maxInput: ' 0.3 ', maxOutput: '' }), {
    service: 'openrouter', baseUrl: 'https://openrouter.ai/api/v1', model: 'qwen/qwen3.5-4b:floor', provider: 'Together, Groq',
    sortPrice: true, maxInput: '0.3', maxOutput: '' });
});

test("applyBody: other services send the bare id and none of OpenRouter's settings; Custom URL its own address", () => {
  const form = { model: 'llama-4', floor: true, order: ['X'], sortPrice: true, maxInput: '1', maxOutput: '2', baseUrl: ' http://box:8080/v1/ ' };
  assert.deepEqual(applyBody(GROQ, form), { service: 'groq', baseUrl: 'https://api.groq.com/openai/v1', model: 'llama-4', provider: '', sortPrice: false, maxInput: '', maxOutput: '' });
  assert.deepEqual(applyBody(CUSTOM, form), { service: 'custom', baseUrl: 'http://box:8080/v1', model: 'llama-4', provider: '', sortPrice: false, maxInput: '', maxOutput: '' });
});

test("serviceFor: a worker's remote by id or name, else its address, else Custom URL", () => {
  const all = [OPENROUTER, GROQ, CUSTOM];
  assert.equal(serviceFor(all, { service: 'groq' }), GROQ);
  assert.equal(serviceFor(all, { service: 'OpenRouter' }), OPENROUTER);
  assert.equal(serviceFor(all, { baseUrl: 'https://api.groq.com/openai/v1/' }), GROQ);
  assert.equal(serviceFor(all, { baseUrl: 'http://box:8080/v1' }), CUSTOM);
});
