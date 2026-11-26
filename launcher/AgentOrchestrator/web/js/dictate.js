// Dictation in the Chat and Code composers: the mic left of Send records in the browser (HTTPS only), draws a level meter inside
// the input pill over the text (hidden, not lost), stops after 2 s of silence once speech was heard, 10 s of nothing, or a tap,
// then posts 16 kHz PCM to the PC (POST /api/dictate) and puts the text at the caret. The PC transcribes; nothing is sent.
// downsample, toInt16, pcmBlock, joinPcm, levelOf, silenceGate and insertAt are pure (Node tests them); the DOM part loads only in
// a page.

/* ---------- pure ---------- */
export const RATE = 16000;

// Speech.Resample's rules: down, each output sample is the mean of the input it covers; up, linear interpolation.
export function downsample(input, fromRate, toRate = RATE) {
  if (fromRate === toRate) return Float32Array.from(input);
  const out = new Float32Array(Math.floor(input.length * toRate / fromRate)), step = fromRate / toRate;
  for (let i = 0; i < out.length; i++) {
    if (step > 1) {
      const start = Math.floor(i * step), end = Math.min(input.length, Math.max(start + 1, Math.floor((i + 1) * step)));
      let sum = 0;
      for (let j = start; j < end; j++) sum += input[j];
      out[i] = sum / (end - start);
    } else {
      const at = i * step, j = Math.floor(at), next = Math.min(j + 1, input.length - 1);
      out[i] = input[j] + (input[next] - input[j]) * (at - j);
    }
  }
  return out;
}

// Floats (-1..1, clipped) to 16-bit PCM.
export function toInt16(input) {
  const out = new Int16Array(input.length);
  for (let i = 0; i < input.length; i++) {
    const s = Math.max(-1, Math.min(1, input[i]));
    out[i] = s < 0 ? s * 0x8000 : s * 0x7FFF;
  }
  return out;
}

// One recorded block at the device rate to 16 kHz PCM, as it arrives: only 16-bit samples are kept while listening.
// ponytail: each block is downsampled alone, so a fraction of a sample per block is dropped (0.04 at 44.1 kHz); inaudible.
export const pcmBlock = (block, rate) => toInt16(downsample(block, rate));

// The blocks end to end, for the post.
export function joinPcm(chunks) {
  const all = new Int16Array(chunks.reduce((n, c) => n + c.length, 0));
  chunks.reduce((at, c) => { all.set(c, at); return at + c.length; }, 0);
  return all;
}

// Speech.LevelOf: RMS on a dB scale, -60 dB (or silence) = 0, full scale = 1.
export function levelOf(samples) {
  if (!samples.length) return 0;
  let sum = 0;
  for (const s of samples) sum += s * s;
  const rms = Math.sqrt(sum / samples.length);
  return rms <= 0 ? 0 : Math.max(0, Math.min(1, (20 * Math.log10(rms) + 60) / 60));
}

// The auto-stop: `level` for the next `ms` of audio. Speech is the PC's energy gate (about -36 dB); stop after 2 s of silence once
// speech was heard, or 10 s with none. Start from { heard: false, quiet: 0, total: 0 }.
export const SPEECH_LEVEL = 0.4;
export function silenceGate(g, level, ms) {
  const speech = level >= SPEECH_LEVEL;
  const next = { heard: g.heard || speech, quiet: speech ? 0 : g.quiet + ms, total: g.total + ms };
  next.stop = next.heard ? next.quiet >= 2000 : next.total >= 10000;
  return next;
}

// Speech.Insert: the phrase at the caret, a space before and after when needed (never two), a capital at the start or after a
// sentence end; the caret lands just after the phrase.
export function insertAt(box, caret, phrase) {
  phrase = phrase.trim();
  caret = Math.max(0, Math.min(caret, box.length));
  if (!phrase) return { text: box, caret };
  const before = box.slice(0, caret), after = box.slice(caret), lead = before.trimEnd();
  const lower = phrase[0] !== phrase[0].toUpperCase();
  if ((!lead || /[.!?]$/.test(lead) || before.endsWith('\n')) && lower) phrase = phrase[0].toUpperCase() + phrase.slice(1);
  const punct = c => '.,!?;:)'.includes(c), space = c => /\s/.test(c);
  if (before && !space(before.at(-1)) && !punct(phrase[0])) phrase = ' ' + phrase;
  const tail = after && !space(after[0]) && !punct(after[0]) ? ' ' : '';
  return { text: before + phrase + tail + after, caret: before.length + phrase.length };
}

/* ---------- the mic ---------- */
if (typeof document !== 'undefined') {
  const { api } = await import('./api.js');
  const { h } = await import('./h.js');
  const { fail, toast } = await import('./main.js');
  const BARS = 28, MAX_MS = 10 * 60 * 1000; // the PC takes 10 minutes at most
  // The recorder's tap: the microphone's samples in blocks of 2048 to the page.
  const WORKLET = `registerProcessor('ao-tap', class extends AudioWorkletProcessor {
    constructor() { super(); this.buf = new Float32Array(2048); this.n = 0; }
    process(inputs) {
      const ch = inputs[0][0];
      if (ch) for (let i = 0; i < ch.length; i++) {
        this.buf[this.n++] = ch[i];
        if (this.n === this.buf.length) { this.port.postMessage(this.buf); this.buf = new Float32Array(2048); this.n = 0; }
      }
      return true;
    }
  });`;
  let cur = null; // the one dictation: { box, btn, overlay, canvas, ctx, stream, chunks (16 kHz Int16), levels, shown, gate, started, frame, watch }

  const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
  const clock = ms => { const s = Math.floor(ms / 1000); return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`; };

  // The meter: a soft red dot and the elapsed time at the left, a centred row of thin rounded bars in the accent following the
  // level (newest on the right, scrolling left, eased), "Listening…" muted at the right when there is room.
  function draw() {
    const m = cur;
    if (!m?.canvas) return;
    const c = m.canvas, dpr = devicePixelRatio || 1, w = c.clientWidth, ht = c.clientHeight, mid = ht / 2;
    if (c.width !== Math.round(w * dpr) || c.height !== Math.round(ht * dpr)) { c.width = Math.round(w * dpr); c.height = Math.round(ht * dpr); }
    const g = c.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, ht);
    const ms = performance.now() - m.started;
    g.globalAlpha = 0.65 + 0.35 * Math.sin(ms / 320);
    g.fillStyle = '#E5534B';
    g.beginPath(); g.arc(6, mid, 3.5, 0, 2 * Math.PI); g.fill();
    g.globalAlpha = 1;
    g.font = '500 12px Inter, system-ui, sans-serif';
    g.textBaseline = 'middle';
    g.fillStyle = m.muted;
    g.fillText(clock(ms), 15, mid);
    const bw = 3, gap = 3, span = BARS * bw + (BARS - 1) * gap, x0 = Math.max(54, (w - span) / 2);
    g.fillStyle = m.accent;
    for (let i = 0; i < BARS; i++) {
      m.shown[i] += (m.levels[i] - m.shown[i]) * 0.3;
      const bh = Math.max(3, m.shown[i] * (ht - 4)), x = x0 + i * (bw + gap), y = mid - bh / 2;
      g.beginPath();
      if (g.roundRect) g.roundRect(x, y, bw, bh, 1.5); else g.rect(x, y, bw, bh);
      g.fill();
    }
    if (w - (x0 + span) > 84) { g.fillStyle = m.muted; g.textAlign = 'right'; g.fillText('Listening…', w - 2, mid); g.textAlign = 'left'; }
    m.frame = requestAnimationFrame(draw);
  }

  // Over the textarea's own box (the pill's text area), so the pill keeps its size; the text under it is hidden, not lost.
  function cover(box) {
    const wrap = box.closest('.inputwrap'), overlay = h('div', { class: 'dictmeter' });
    overlay.style.cssText = `top:${box.offsetTop}px;left:${box.offsetLeft}px;width:${box.offsetWidth}px;height:${box.offsetHeight}px`;
    wrap.append(overlay);
    box.classList.add('dictating');
    return overlay;
  }

  // The microphone off: tracks stopped, the audio graph closed, the drawing ended. The overlay stays for the spinner.
  function release(m) {
    cancelAnimationFrame(m.frame);
    m.watch?.disconnect();
    m.stream?.getTracks().forEach(t => t.stop());
    m.ctx.close().catch(() => {});
    m.btn.classList.remove('on');
  }

  function finish(m) {
    m.overlay.remove();
    m.box.classList.remove('dictating');
    m.btn.disabled = false;
    if (cur === m) cur = null;
  }

  // Leaving the screen, a PC switch, Send, the app going to the background: no post.
  function cancel() {
    const m = cur;
    if (!m || m.posting) return;
    release(m);
    finish(m);
  }

  async function stop() {
    const m = cur;
    if (!m || m.posting) return;
    const pcm = joinPcm(m.chunks);
    if (!pcm.length) { cancel(); return; } // tapped again before anything was recorded (the permission prompt still up): nothing to post
    m.posting = true;
    release(m);
    m.btn.disabled = true;
    m.canvas.remove();
    m.overlay.append(h('span', { class: 'spin' }), h('span', { class: 'muted' }, 'Transcribing…'));
    try {
      const { text } = await api.dictate(pcm);
      if (!text) { toast('Nothing heard'); return; }
      const r = insertAt(m.box.value, m.caret, text);
      m.box.value = r.text;
      m.box.setSelectionRange(r.caret, r.caret);
      m.box.closest('.composer')?.classList.remove('collapsed');
      m.box.dispatchEvent(new Event('input'));
    } catch (err) { fail(err); } finally { finish(m); }
  }

  async function start(box, btn) {
    if (!window.isSecureContext || !navigator.mediaDevices?.getUserMedia || !window.AudioWorkletNode) {
      toast('Dictation needs the app on its HTTPS address');
      return;
    }
    const ctx = new AudioContext(); // in the tap itself, so iOS lets it run
    const m = cur = { box, btn, ctx, caret: box.selectionEnd ?? box.value.length, chunks: [], levels: new Array(BARS).fill(0), shown: new Array(BARS).fill(0),
      gate: { heard: false, quiet: 0, total: 0 }, accent: css('--accent') || '#C0C0C0', muted: css('--muted') || '#9A9A9A' };
    btn.classList.add('on');
    m.overlay = cover(box);
    m.canvas = h('canvas');
    m.overlay.append(m.canvas);
    m.started = performance.now();
    draw();
    try {
      const url = URL.createObjectURL(new Blob([WORKLET], { type: 'text/javascript' }));
      try { await ctx.audioWorklet.addModule(url); } finally { URL.revokeObjectURL(url); }
      m.stream = await navigator.mediaDevices.getUserMedia({ audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true } });
      if (cur !== m) { m.stream.getTracks().forEach(t => t.stop()); return; } // cancelled while asking
      await ctx.resume();
      const tap = new AudioWorkletNode(ctx, 'ao-tap');
      tap.port.onmessage = e => {
        if (cur !== m || m.posting) return;
        const block = e.data, ms = block.length / ctx.sampleRate * 1000, level = levelOf(block);
        m.chunks.push(pcmBlock(block, ctx.sampleRate));
        m.levels.push(level);
        m.levels.shift();
        m.gate = silenceGate(m.gate, level, ms);
        if (m.gate.stop || m.gate.total >= MAX_MS) stop();
      };
      ctx.createMediaStreamSource(m.stream).connect(tap);
      tap.connect(ctx.destination); // silent: the tap writes no output, but a connected node is always processed
      // Leaving the screen (the view or the Code pane hidden) stops it.
      m.watch = new MutationObserver(() => { if (!box.offsetParent) cancel(); });
      for (const el of [box.closest('.view'), box.closest('.pane')]) if (el) m.watch.observe(el, { attributes: true, attributeFilter: ['class'] });
    } catch (err) {
      if (cur === m) cancel();
      toast(err?.name === 'NotAllowedError' ? 'The microphone is blocked: allow it for this site in the browser'
        : err?.name === 'NotFoundError' ? 'No microphone found' : err?.message || String(err));
    }
  }

  for (const [boxId, sendId] of [['chatbox', 'sendbtn'], ['codebox', 'codesend']]) {
    const box = document.getElementById(boxId), send = document.getElementById(sendId);
    const btn = send.parentElement.querySelector('[data-mic]');
    btn.addEventListener('click', e => {
      e.stopPropagation(); // the resting pill would open (and the keyboard with it)
      if (cur?.box === box) stop();
      else { cancel(); start(box, btn); }
    });
    send.addEventListener('click', () => { if (cur?.box === box) cancel(); });
  }
  document.addEventListener('ao:pc', cancel);
  document.addEventListener('visibilitychange', () => { if (document.hidden) cancel(); });
}
