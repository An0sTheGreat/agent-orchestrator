import test from 'node:test';
import assert from 'node:assert/strict';
import { normalize, rows, accentTokens, confirmFor, tabsText, appName, currentAccent, usageBars, duration } from '../js/settings.js';

// GET /api/settings, as the plan's contract.
const GET = {
  general: { startWithWindows: false, notifyWhenDone: true, linkOpen: 'app', followTabs: 0, computerUse: false,
    computerApps: ['C:\\Program Files\\Paint\\mspaint.exe'], stats: '1,284,930 lifetime tokens', phoneApp: { on: true, address: 'https://pc.tail0000.ts.net' } },
  appearance: { theme: 'Graphite', themes: [{ key: 'Flat', name: 'Flat' }, { key: 'Graphite', name: 'Graphite' }], accent: 'clay',
    accents: [{ key: 'gray', name: 'Light gray', color: '#C0C0C0' }, { key: 'clay', name: 'Clay', color: '#C15F3C' }],
    centerChat: true, readingSize: 'comfortable', emoji: 'less', contextPercent: true },
  models: { keepLoaded: true, multiModel: 'fit', share: { on: false } },
  agents: { workflow: 'dual', codeEngine: '', fullAccess: true, sandboxNetwork: false },
  plugins: { ponytail: 'full' },
  about: { version: '1.9.4', pc: 'DESKTOP' },
};
const s = normalize(GET);
const writable = page => rows(page, s).filter(r => r.key);
const byKey = (page, key) => rows(page, s).find(r => r.key === key);

test('normalize: followTabs 0 or 11+ is Unlimited (11), others kept to 3..10; missing sections and lists are filled', () => {
  assert.equal(s.general.followTabs, 11);
  assert.equal(normalize({ general: { followTabs: 7 } }).general.followTabs, 7);
  assert.equal(normalize({ general: { followTabs: 2 } }).general.followTabs, 3);
  assert.equal(normalize({ general: { followTabs: 14 } }).general.followTabs, 11);
  const empty = normalize({});
  assert.deepEqual([empty.general.computerApps, empty.appearance.themes, empty.appearance.accents], [[], [], []]);
  assert.deepEqual([empty.general.linkOpen, empty.agents.codeEngine, empty.plugins.ponytail], ['', '', '']);
  assert.deepEqual(empty.models.share, {});
  assert.deepEqual(empty.general.phoneApp, {});
});

test('every writable key of the contract has exactly one row, on the page of its section, holding the GET value', () => {
  const keys = ['general', 'appearance', 'models', 'agents', 'plugins'].flatMap(p => writable(p).map(r => r.key));
  assert.deepEqual(keys.sort(), ['accent', 'centerChat', 'codeEngine', 'computerUse', 'contextPercent', 'emoji', 'followTabs', 'fullAccess',
    'keepLoaded', 'linkOpen', 'multiModel', 'notifyWhenDone', 'ponytail', 'readingSize', 'sandboxNetwork', 'share.on',
    'startWithWindows', 'theme', 'workflow'].sort());
  assert.equal(byKey('general', 'linkOpen').value, 'app');
  assert.equal(byKey('general', 'followTabs').value, 11);
  assert.equal(byKey('agents', 'fullAccess').value, true);
  assert.equal(byKey('plugins', 'ponytail').value, 'full');
  assert.equal(writable('about').length, 0);
});

test("choices offer the desktop's stored values", () => {
  const values = (page, key) => byKey(page, key).options.map(o => o[0]);
  assert.deepEqual(values('general', 'linkOpen'), ['', 'app', 'default']);
  assert.deepEqual(values('appearance', 'readingSize'), ['compact', 'comfortable', 'large']);
  assert.deepEqual(values('appearance', 'emoji'), ['none', 'less', 'standard', 'more']);
  assert.deepEqual(values('appearance', 'theme'), ['Flat', 'Graphite']);
  assert.deepEqual(values('models', 'multiModel'), ['never', 'fit', 'always']);
  assert.deepEqual(values('agents', 'workflow'), ['dual', 'tri', 'solo']);
  assert.deepEqual(values('agents', 'codeEngine'), ['', 'pi']);
  assert.deepEqual(values('plugins', 'ponytail'), ['', 'lite', 'full', 'ultra']);
  assert.deepEqual(byKey('appearance', 'accent').options, GET.appearance.accents);
});

test('read-only rows: approved applications by name, the phone app (only off at the PC), version and PC; the accent heading names it', () => {
  const text = page => rows(page, s).filter(r => r.type === 'info').map(r => [r.label, r.text]);
  assert.deepEqual(text('general'), [['Approved applications', 'mspaint.exe'], ['Phone app over Tailscale', 'On · https://pc.tail0000.ts.net']]);
  assert.match(rows('general', s).find(r => r.label === 'Phone app over Tailscale').hint, /only be turned off at the PC/);
  assert.equal(rows('general', normalize({})).find(r => r.label === 'Approved applications').text, 'None approved.');
  assert.equal(rows('general', normalize({})).find(r => r.label === 'Phone app over Tailscale').text, 'Off');
  assert.deepEqual(text('about'), [['Version', '1.9.4'], ['PC', 'DESKTOP']]);
  assert.ok(rows('appearance', s).some(r => r.type === 'head' && r.text === 'Accent · Clay'));
});

test('usage: a row on General opens the Usage page, showing the stats line', () => {
  const nav = rows('general', s).filter(r => r.type === 'nav');
  assert.deepEqual(nav, [{ type: 'nav', open: 'view-usage', label: 'Usage & tokens', text: '1,284,930 lifetime tokens' }]);
  assert.equal(rows('general', normalize({})).find(r => r.type === 'nav').text, '—');
});

test('usage bars: height relative to the busiest day, weekday letters in order; all zero is all flat', () => {
  const days = ['2026-11-20', '2026-11-21', '2026-11-22', '2026-11-23', '2026-11-24', '2026-11-25', '2026-11-26'] // Fri..Thu
    .map((day, i) => ({ day, tokens: [0, 500, 1000, 250, 0, 750, 1000][i] }));
  const bars = usageBars(days);
  assert.deepEqual(bars.map(b => b.h), [0, 50, 100, 25, 0, 75, 100]);
  assert.equal(bars.map(b => b.letter).join(''), 'FSSMTWT');
  assert.equal(bars[1].tokens, 500);
  assert.deepEqual(usageBars(days.map(d => ({ ...d, tokens: 0 }))).map(b => b.h), [0, 0, 0, 0, 0, 0, 0]);
  assert.deepEqual(usageBars(), []);
});

test('duration: hours and minutes, minutes and seconds, or seconds', () => {
  assert.equal(duration(3900), '1 h 05 min');
  assert.equal(duration(750), '12 min 30 s');
  assert.equal(duration(45), '45 s');
  assert.equal(duration(undefined), '0 s');
});

test('helpers: tabs text, an app name from its path, the current accent', () => {
  assert.equal(tabsText(11), 'Unlimited');
  assert.equal(tabsText(5), '5 tabs');
  assert.equal(appName('D:/Tools/blender.exe'), 'blender.exe');
  assert.equal(currentAccent(s).color, '#C15F3C');
  assert.equal(currentAccent(normalize({})), undefined);
});

test("accent tokens: the colour, 40% toward white, dark text on a light accent; the concept's twelve swatches get their text colour", () => {
  assert.deepEqual(accentTokens('#c0c0c0'), { '--accent': '#C0C0C0', '--accent-light': '#D9D9D9', '--on-accent': '#141414' });
  assert.deepEqual(accentTokens('#FFC15F3C'), accentTokens('#C15F3C')); // WPF's #AARRGGBB
  const dark = ['#3DDC84', '#1FC8C8', '#6EA8FF', '#D4A017', '#FF6FB5', '#FF8A3D', '#C0C0C0', '#CF8E4E'];
  const light = ['#9B6BFF', '#2B4FB0', '#4C8DFF', '#C15F3C'];
  for (const c of dark) assert.equal(accentTokens(c)['--on-accent'], '#141414', c);
  for (const c of light) assert.equal(accentTokens(c)['--on-accent'], '#FFFFFF', c);
  for (const bad of [undefined, '', 'red', '#12345', 'url(x)']) assert.equal(accentTokens(bad), null, String(bad));
});

test('the phone confirms Full access going on (naming the sandbox isolation and every folder) and Reset usage stats, nothing else', () => {
  assert.match(confirmFor('fullAccess', true).text, /isolation.*every folder/s);
  assert.equal(confirmFor('fullAccess', false), null);
  assert.equal(confirmFor('superAccess', true), null); // no Super access switch: Full access gives it
  assert.ok(confirmFor('resetStats', true));
  for (const key of ['sandboxNetwork', 'computerUse', 'theme']) assert.equal(confirmFor(key, true), null);
});
