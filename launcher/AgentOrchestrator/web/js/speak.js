// Replies read aloud: a "read" button beside copy on each saved assistant reply (POST /api/speak: the PC makes a WAV with Kokoro,
// the phone plays it), "stop" while it plays; one at a time. iOS lets audio play only from a tap, so the first tap of any read
// or ▶ button plays a silent sound on the one audio element, which unlocks it; after that Read replies aloud plays each finished
// reply. Stops on leaving the screen, a new send, another conversation, another PC, or the app going to the background.
// Settings > Voice (GET/POST /api/voice): the PC's voice with ▶ preview, speed, Read replies aloud.
// speechOf, playState and voiceGroups are pure (Node tests them); the DOM part loads only in a page.

/* ---------- pure ---------- */
// What a message reads: an assistant reply's text and worker (the PC picks the worker's voice); null for the user's own messages
// and replies with no words.
export function speechOf(m) {
  if (!m || m.role === 'user') return null;
  const text = (m.text || '').trim();
  return text ? { text, worker: m.worker || '' } : null;
}

// A reply's key, the same each time its node is drawn again.
export const keyOf = m => `${m.time || ''}:${(m.text || '').length}`;

// The player: { key, phase: 'idle' | 'loading' | 'playing' }. An answer for a key no longer current (stopped, or another one
// started) changes nothing, so a late WAV never plays and a failure never leaves a button stuck on stop.
export const IDLE = { key: null, phase: 'idle' };
export function playState(s, a) {
  switch (a.t) {
    case 'start': return { key: a.key, phase: 'loading' };
    case 'playing': return s.key === a.key && s.phase === 'loading' ? { ...s, phase: 'playing' } : s;
    case 'end': return s.key === a.key ? IDLE : s; // ended, failed or refused
    case 'stop': return IDLE;
    default: return s;
  }
}

// The picker's groups: built-in voices by language and accent in the PC's order, then the user's blends.
export function voiceGroups(voices = []) {
  const groups = new Map();
  for (const v of voices) {
    const label = v.custom ? 'Your voices' : [v.language, v.accent && `(${v.accent})`].filter(Boolean).join(' ');
    if (!groups.has(label)) groups.set(label, []);
    groups.get(label).push(v);
  }
  const mine = groups.get('Your voices');
  groups.delete('Your voices');
  return [...groups, ...(mine ? [['Your voices', mine]] : [])];
}

// The picker's label for a voice: "heart · Female" for af_heart, a blend by its name.
export const voiceLabel = v => v.custom ? v.name : `${v.name.replace(/^[a-z]{2}_/, '')} · ${v.gender}`;

/* ---------- the player ---------- */
export let speakButton = () => null, stopSpeech = () => {}, autoSpeak = () => {};

if (typeof document !== 'undefined') {
  const { api } = await import('./api.js');
  const { h, icon } = await import('./h.js');
  const { registerScreen, fail, toast } = await import('./main.js');
  // 0.1 s of silence at 8 kHz: what the unlock plays.
  const SILENCE = 'data:audio/wav;base64,UklGRmQGAABXQVZFZm10IBAAAAABAAEAQB8AAIA+AAACABAAZGF0YUAG' + 'A'.repeat(2136);
  const audio = new Audio();
  let state = IDLE, url = null, abort = null, owner = null, watch = null, unlocked = false, voice = null; // voice: GET /api/voice
  const lastAuto = { key: null };

  // Every read/▶ button on the page shows the player's state for its key.
  function sync() {
    for (const b of document.querySelectorAll('[data-speak]')) {
      const on = b.dataset.speak === state.key && state.phase !== 'idle';
      b.classList.toggle('on', on);
      if (b.dataset.play) b.replaceChildren(icon(on ? 'stop' : 'play', 'ic sm'));
      else b.textContent = on ? 'stop' : 'read';
      b.setAttribute('aria-label', on ? 'Stop reading' : 'Read aloud');
    }
  }
  const set = a => { state = playState(state, a); sync(); };

  // In the tap itself: a play on the element, so iOS lets it play later without one.
  function unlock() {
    if (unlocked) return;
    unlocked = true;
    audio.src = SILENCE;
    audio.play().catch(() => {});
  }

  function release() {
    abort?.abort();
    abort = null;
    audio.pause();
    audio.removeAttribute('src');
    audio.load();
    if (url) URL.revokeObjectURL(url);
    url = null;
    watch?.disconnect();
    watch = owner = null;
  }
  stopSpeech = () => { release(); set({ t: 'stop' }); };
  const ended = () => { if (url && audio.src === url) { const key = state.key; release(); set({ t: 'end', key }); } };
  audio.addEventListener('ended', ended);
  audio.addEventListener('error', ended);

  // Reads `body` ({text, worker?, voice?}) as `key`; `from`: the button's view, whose hiding stops it. `auto`: no toast when refused.
  async function play(key, body, from, auto = false) {
    stopSpeech();
    set({ t: 'start', key });
    const mine = abort = new AbortController();
    owner = from?.closest('.view');
    if (owner) {
      watch = new MutationObserver(() => { if (!owner.offsetParent) stopSpeech(); });
      watch.observe(owner, { attributes: true, attributeFilter: ['class'] });
    }
    try {
      let res;
      try {
        res = await fetch(api.base + '/api/speak', { method: 'POST', credentials: 'omit', signal: mine.signal,
          headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
      } catch (err) { if (err?.name === 'AbortError') return; throw Object.assign(new Error('The PC could not be reached'), { status: 0 }); }
      if (!res.ok) {
        const answer = await res.json().catch(() => null);
        throw Object.assign(new Error(answer?.error || `HTTP ${res.status}`), { status: res.status, body: answer });
      }
      const blob = await res.blob();
      if (abort !== mine) return; // stopped, or another started, while the PC was making it
      url = URL.createObjectURL(blob);
      audio.src = url;
      await audio.play();
      set({ t: 'playing', key });
    } catch (err) {
      if (abort !== mine && err?.name === 'AbortError') return;
      if (state.key === key) { release(); set({ t: 'end', key }); }
      if (err?.name === 'NotAllowedError') { if (!auto) toast('Tap read again to play'); unlocked = false; }
      else if (err?.name !== 'AbortError' && !auto) fail(err);
    }
  }

  function toggle(btn, key, body) {
    unlock();
    if (state.key === key && state.phase !== 'idle') stopSpeech();
    else play(key, body, btn);
  }

  // A saved reply's read button (chat.js), or null for a message with nothing to read.
  speakButton = m => {
    const said = speechOf(m);
    if (!said) return null;
    const key = keyOf(m), btn = h('button', { class: 'ghosttiny', 'data-speak': key, onclick: () => toggle(btn, key, said) }, 'read');
    queueMicrotask(sync); // once it is on the page
    return btn;
  };

  // A finished reply (chat.js, after a `saved` load): read it when Read replies aloud is on and a tap has unlocked audio.
  autoSpeak = (m, from) => {
    const said = speechOf(m), key = m && keyOf(m);
    if (!said || !voice?.settings?.autoRead || !unlocked || key === lastAuto.key) return;
    lastAuto.key = key;
    play(key, said, from, true);
  };

  document.addEventListener('ao:pc', () => { stopSpeech(); voice = null; });
  document.addEventListener('visibilitychange', () => { if (document.hidden) stopSpeech(); });
  const fetchVoice = () => api.get('/api/voice').then(v => { voice = v; drawVoice(); return v; });
  document.addEventListener('ao:hello', () => fetchVoice().catch(() => {})); // a PC without the route: no auto-read

  /* ---------- Settings > Voice ---------- */
  const SAMPLE = 'Hello! This is how I sound when I read your replies aloud.';
  const body = () => document.querySelector('#view-voice .vbody');
  async function write(change) {
    try { voice = await api.post('/api/voice', change); } catch (err) { fail(err); }
    drawVoice();
  }
  function drawVoice() {
    const view = document.getElementById('view-voice');
    if (!voice || !view) return;
    const s = voice.settings, speed = Number(s.speed) || 1;
    const select = h('select', { 'aria-label': 'Voice', onchange: e => write({ voice: e.target.value }) },
      voiceGroups(voice.voices).map(([label, list]) => h('optgroup', { label },
        list.map(v => h('option', { value: v.name, selected: v.name === s.voice }, voiceLabel(v))))));
    const preview = h('button', { class: 'iconbtn', 'data-speak': 'preview', 'data-play': '1', 'aria-label': 'Read aloud',
      onclick: () => toggle(preview, 'preview', { text: SAMPLE, voice: select.value }) }, icon('play', 'ic sm'));
    const pill = h('span', { class: 'pill' }, speed.toFixed(2) + '×');
    const slider = h('input', { class: 'slider', type: 'range', min: 0.5, max: 2, step: 0.05, value: speed, 'aria-label': 'Speed',
      oninput: e => { pill.textContent = (+e.target.value).toFixed(2) + '×'; }, onchange: e => write({ speed: +e.target.value }) });
    body().replaceChildren(
      !voice.ready && h('p', { class: 'hintline' }, 'Download the voice model on the PC (Settings > Voice) to hear replies.'),
      h('p', { class: 'secthead' }, 'Voice'),
      h('div', { class: 'rowline fields' }, select, preview),
      h('div', { class: 'rowline' }, h('div', {}, h('b', {}, 'Speed')), pill), slider,
      h('p', { class: 'secthead' }, 'Replies'),
      h('div', { class: 'rowline' }, h('div', {}, h('b', {}, 'Read replies aloud'), h('small', {}, 'Each finished reply, after you have tapped read or ▶ once.')),
        h('button', { class: 'switch' + (s.autoRead ? ' on' : ''), 'aria-label': 'Read replies aloud', 'aria-pressed': String(!!s.autoRead),
          onclick: () => write({ autoRead: !s.autoRead }) })),
      h('p', { class: 'hintline' }, 'The voice model download and custom voices (blends) are made on the PC.'));
    sync();
  }
  registerScreen('voice', { show() { if (voice) drawVoice(); fetchVoice().catch(fail); } });
}
