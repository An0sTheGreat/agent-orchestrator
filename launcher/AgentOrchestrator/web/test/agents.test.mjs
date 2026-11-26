import test from 'node:test';
import assert from 'node:assert/strict';
import { formModel, initialValues, changedFields, filledFields, presetFields, estimateFields, boolValue, BOOL, vramView, presetGroups, presetExists } from '../js/agents.js';

// GET /api/worker/{role}'s options, as the plan's contract.
const OPTIONS = [
  { key: 'max_seq_length', label: 'Context length', group: 'Load', kind: 'Int', value: '', default: '32768', min: 0, max: 1048576 },
  { key: 'cache_type_kv', label: 'KV cache', group: 'Memory', kind: 'Choice', value: 'q8_0', default: 'f16', choices: ['f16', 'q8_0'], advanced: true },
  { key: 'gpu_layers', label: 'GPU layers', group: 'Memory', kind: 'Int', value: '', default: '-1', advanced: true },
  { key: 'temperature', label: 'Temperature', group: 'Sampling', kind: 'Number', value: '0.6', default: '0.8' },
  { key: 'enable_thinking', label: 'Thinking', group: 'Sampling', kind: 'Bool', value: 'on', default: '' },
  { key: 'reasoning_max_tokens', label: 'Reasoning max tokens', group: 'Sampling', kind: 'Int', value: '', apiOnly: true },
  { key: 'timeout_seconds', label: 'Timeout', group: 'Request', kind: 'Int', value: 600, default: '600' },
  { key: 'send_extra_body', label: 'Extra body', group: 'Request', kind: 'Bool', value: '', apiOnly: true, advanced: true },
];

test('formModel: groups in the order they come, max_seq_length apart, advanced when every option is', () => {
  const { context, groups } = formModel(OPTIONS);
  assert.equal(context.key, 'max_seq_length');
  assert.deepEqual(groups.map(g => [g.name, g.advanced, g.options.map(o => o.key)]), [
    ['Memory', true, ['cache_type_kv', 'gpu_layers']],
    ['Sampling', false, ['temperature', 'enable_thinking']],
    ['Request', false, ['timeout_seconds']],
  ]);
});

test('formModel: apiOnly options only for an API worker', () => {
  const { groups } = formModel(OPTIONS, true);
  assert.deepEqual(groups.find(g => g.name === 'Sampling').options.map(o => o.key), ['temperature', 'enable_thinking', 'reasoning_max_tokens']);
  assert.deepEqual(groups.find(g => g.name === 'Request').options.map(o => o.key), ['timeout_seconds', 'send_extra_body']);
  assert.equal(groups.find(g => g.name === 'Request').advanced, false);
});

test('formModel: gguf_variant is never a field; an api worker gets no load options, so no Context card', () => {
  const opts = [...OPTIONS, { key: 'gguf_variant', label: 'Quant', group: 'Load', kind: 'Choice', value: 'Q4_K_M', load: true },
    { key: 'gpu_layers2', label: 'Load only', group: 'Memory', kind: 'Int', value: '', load: true }];
  const local = formModel(opts, false);
  assert.equal(local.context.key, 'max_seq_length');
  assert.equal(local.groups.flatMap(g => g.options).some(o => o.key === 'gguf_variant'), false);
  assert.equal(local.groups.flatMap(g => g.options).some(o => o.key === 'gpu_layers2'), true);
  const apiW = formModel(opts.map(o => o.key === 'max_seq_length' ? { ...o, load: true } : o), true);
  assert.equal(apiW.context, null);
  assert.equal(apiW.groups.flatMap(g => g.options).some(o => o.key === 'gpu_layers2' || o.key === 'gguf_variant'), false);
  assert.equal(apiW.groups.flatMap(g => g.options).some(o => o.key === 'temperature'), true);
});

test('presetFields / estimateFields: a cleared field is "" in a preset and its default in the estimate', () => {
  const v = { ...initialValues(OPTIONS), cache_type_kv: '', temperature: '0.7' };
  assert.deepEqual(presetFields(OPTIONS, v), { cache_type_kv: '', temperature: '0.7', enable_thinking: 'on', timeout_seconds: '600' });
  assert.deepEqual(estimateFields(OPTIONS, v), { cache_type_kv: 'f16', temperature: '0.7', enable_thinking: 'on', timeout_seconds: '600' });
});

test('Bool: the seg is Default / On / Off for "" / "on" / "off"; any stored form reads as one of them', () => {
  assert.deepEqual(BOOL.map(([v]) => v), ['', 'on', 'off']);
  assert.deepEqual(BOOL.map(([, label]) => label), ['Default', 'On', 'Off']);
  for (const v of ['on', true, 'true']) assert.equal(boolValue(v), 'on');
  for (const v of ['off', false, 'false']) assert.equal(boolValue(v), 'off');
  for (const v of ['', null, undefined, 'maybe']) assert.equal(boolValue(v), '');
});

test('initialValues: strings, a Bool through boolValue', () => {
  const v = initialValues(OPTIONS);
  assert.equal(v.timeout_seconds, '600');
  assert.equal(v.enable_thinking, 'on');
  assert.equal(v.max_seq_length, '');
});

test('changedFields: only what differs from the server, trimmed; a cleared field is "" (back to the default)', () => {
  const v = { ...initialValues(OPTIONS), temperature: ' 0.7 ', cache_type_kv: '', enable_thinking: 'on', gpu_layers: '' };
  assert.deepEqual(changedFields(OPTIONS, v), { temperature: '0.7', cache_type_kv: '' });
  assert.deepEqual(changedFields(OPTIONS, initialValues(OPTIONS)), {});
  assert.deepEqual(changedFields(OPTIONS, { ...initialValues(OPTIONS), timeout_seconds: '600 ' }), {});
});

test('filledFields: the non-empty fields, trimmed (the GPU estimate and a saved preset)', () => {
  assert.deepEqual(filledFields({ temperature: '0.6', top_k: '', seed: '  ', max_seq_length: ' 8192' }), { temperature: '0.6', max_seq_length: '8192' });
});

const G = 2 ** 30;
test('vramView: parts in percent of the GPU, known ones only; fits', () => {
  const v = vramView({ gpu: { weights: 4 * G, kv: 2 * G, compute: 1 * G, vision: null, total: 7 * G }, whole: 7 * G, gpuTotal: 8 * G });
  assert.deepEqual(v.parts.map(p => [p.cls, p.pct]), [['vw', 50], ['vk', 25], ['vc', 12.5]]);
  assert.equal(v.fits, true);
  assert.equal(v.text, 'fits');
});

test('vramView: over the GPU the bar is in percent of the total and spills to RAM; so does a whole model bigger than the GPU', () => {
  const v = vramView({ gpu: { weights: 8 * G, kv: 2 * G, compute: 0, vision: 2 * G, total: 12 * G }, whole: 12 * G, gpuTotal: 8 * G });
  assert.deepEqual(v.parts.map(p => [p.cls, p.pct]), [['vw', 66.7], ['vk', 16.7], ['vv', 16.7]]);
  assert.equal(v.text, 'spills to RAM');
  assert.equal(vramView({ gpu: { weights: 6 * G, total: 6 * G }, whole: 10 * G, gpuTotal: 8 * G }).text, 'spills to RAM');
});

test('vramView: an unknown GPU or estimate has no fit', () => {
  const v = vramView({ gpu: { weights: 4 * G, total: 4 * G }, whole: null, gpuTotal: null });
  assert.equal(v.fits, null);
  assert.equal(v.text, '');
  assert.equal(v.parts[0].pct, 100);
  assert.deepEqual(vramView({ gpu: { weights: null, kv: null, compute: null, vision: null, total: null }, gpuTotal: 8 * G }).parts, []);
});

test('vramView: no GPU estimate (gpu null) says so when the GPU size is known, else the size is unknown', () => {
  const v = vramView({ gpu: null, whole: null, gpuTotal: 8 * G });
  assert.deepEqual(v.parts, []);
  assert.equal(v.total, 0);
  assert.equal(v.fits, null);
  assert.equal(v.sub, 'No estimate for this model');
  assert.equal(vramView({ gpu: null, whole: null, gpuTotal: null }).sub, 'GPU size unknown');
  assert.equal(vramView({ gpu: { weights: 4 * G, total: 4 * G }, gpuTotal: 8 * G, whole: 4 * G }).sub, 'of 8.0 GB · fits');
});

test('presetGroups: empty groups dropped, the active preset marked; presetExists finds a name in any group', () => {
  const presets = { active: 'fast', groups: [
    { name: 'THIS MODEL', items: [{ name: 'fast', model: 'Qwen', quant: 'Q4_K_M' }, { name: 'long' }] },
    { name: 'OTHER MODELS', items: [] },
    { name: 'ANY MODEL', items: [{ name: 'cold' }] },
  ] };
  assert.deepEqual(presetGroups(presets).map(g => [g.name, g.items.map(i => [i.name, i.active])]),
    [['THIS MODEL', [['fast', true], ['long', false]]], ['ANY MODEL', [['cold', false]]]]);
  assert.deepEqual(presetGroups({}), []);
  assert.equal(presetExists(presets, 'cold'), true);
  assert.equal(presetExists(presets, 'warm'), false);
});
