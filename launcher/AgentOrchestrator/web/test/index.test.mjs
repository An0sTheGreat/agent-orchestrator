import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

const html = readFileSync(new URL('../index.html', import.meta.url), 'utf8');
const section = id => { const s = html.indexOf(`id="${id}"`), e = html.indexOf('<section', s); return html.slice(s, e < 0 ? undefined : e); };

// chat.js looks these up inside the open view (drawState, drawContext): both screens need them.
for (const id of ['view-chat', 'view-code'])
  test(`#${id} has the stop button, the context ring and the queue chip chat.js looks up`, () => {
    const v = section(id);
    assert.match(v, /<button[^>]*\bdata-stop\b/);
    assert.match(v, /class="[^"]*\bctxring\b/);
    assert.match(v, /class="[^"]*\bqueue\b/);
  });

test('#view-chat has the suggestion chip chat.js draws, hidden until a suggestion comes', () => {
  assert.match(section('view-chat'), /<button class="suggest hidden" data-suggest>/);
});
