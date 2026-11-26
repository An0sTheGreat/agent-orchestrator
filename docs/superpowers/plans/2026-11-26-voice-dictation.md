# Voice phase 1: dictation — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** speak into the desktop's Chat and Code message boxes (and then the phone's) and get accurate, punctuated text in
the box, transcribed on the PC, with a level meter drawn inside the box while listening.

**Architecture:** a C# speech engine on sherpa-onnx (Parakeet TDT 0.6B for speech to text, Silero VAD for speech/silence),
models downloaded into the data folder from Settings; the desktop captures the default microphone (NAudio) and feeds the
engine; the phone records in the browser and posts 16 kHz PCM to a new route.

**Tech stack:** .NET 8 WPF; NuGet `org.k2fsa.sherpa.onnx` (with its win-x64 native runtime) and `NAudio` (capture) — the
two new dependencies the approved design names; plain ES modules on the phone (AudioWorklet).

**Spec:** docs/superpowers/specs/2026-11-26-voice-design.md (Phase 1). The user approved it on 2026-11-26 and added: "the
level meter displays aesthetically inside of the message box where text would be typed".

## Global constraints
- All speech work on the PC's CPU (no GPU memory); audio and text never leave the PC except to the user's own phone.
- Dictation never sends: text goes into the box at the caret; the user presses Send.
- Models download only when the user presses Download (size shown), into `<DataRoot>\speech\`; nothing downloads on its own.
- Speech threads capped (2) so a run's LLM speed is unchanged.
- Desktop look: the app's existing styles (SendCircle, GhostButton, RailGlyph, DynamicResource colours, the accent); phone:
  the concept's tokens. Commits go to master via the usual worktree + cherry-pick; never commit repo-root App.xaml.cs or
  requests.txt.

## Decisions (controller's calls)
- Start/stop: click the mic (or press Esc to stop); auto-stop after 2 s of silence once speech was heard, or 10 s with no
  speech at all; Ctrl+Space held = hold-to-talk on the desktop.
- Text arrives per spoken phrase (each VAD segment is transcribed as it ends), so long dictation shows up as you go.
- The meter: while listening the box's text area shows a centred row of ~28 thin rounded bars in the accent colour, their
  heights following the live microphone level (newest on the right, scrolling left, eased), a soft red dot and the elapsed
  time at the left, "Listening… Esc to stop" muted when there is room; typed text is hidden underneath (not lost) and shown
  again, with the dictated text inserted at the caret, when listening stops. Same look on the phone inside the input pill.
- The mic button sits just left of Send/Stop and is always visible (Send appears only with text).
- English Parakeet TDT 0.6B v2 int8 first (language choice is a later setting).

## Review focus
1. No microphone / microphone blocked by Windows privacy settings → a clear toast, button back to idle, no crash.
2. Models not downloaded → the mic explains and offers Settings > Voice; a half-finished download never counts as ready.
3. Dictating while a reply streams or a run works → allowed (only the box changes); CPU stays capped.
4. Switching chats, sending, or closing the window while listening → listening stops, audio device released.
5. Phone: the microphone permission denied or not HTTPS → message, no stuck state.

---

### Task 1: the speech engine, model download, Settings > Voice (C#)

**Files:** create `launcher/AgentOrchestrator/Speech.cs` (engine + pure helpers + `SpeechSelfTest()`), create
`MainWindow.Voice.cs` (Settings > Voice section handlers, download); modify `AgentOrchestrator.csproj` (the two packages),
`MainWindow.xaml` (Settings > Voice section), `App.xaml.cs` (call `Speech.SelfTest()` in `--self-test`), `publish.ps1` only if
the native runtime is not copied.

**Interfaces (produces):**
- `static class SpeechModels { string Folder; bool Ready; long DownloadBytes; Task DownloadAsync(IProgress<(long done,long total)>, CancellationToken) }`
  — files: Parakeet TDT 0.6B v2 int8 (encoder/decoder/joiner `.int8.onnx`, `tokens.txt`) and `silero_vad.onnx`; downloaded to
  `*.part` then renamed; `Ready` only when every file is present with its expected size.
- `sealed class DictationSession : IDisposable` — `DictationSession(Func<float[], string> transcribe)`; `void Feed(float[] samples16k)`;
  events `Level(float level01)`, `Text(string phrase)`, `Ended(string reason)` ("silence", "nothing heard");
  `void Stop()` (flushes the last segment). Uses sherpa-onnx VAD; transcribe = Parakeet offline recognizer.
- `static Func<float[], string> Speech.Recognizer()` — the shared recognizer (created once, 2 threads), or throws when not Ready.
- Pure: `float[] Speech.Resample(ReadOnlySpan<float> input, int fromRate, int toRate)`; `float Speech.LevelOf(ReadOnlySpan<float>)`
  (RMS → 0..1 on a dB scale, −60 dB = 0); `(string text, int caret) Speech.Insert(string box, int caret, string phrase)`
  (adds a space when needed, capital after sentence end or at start, no double spaces).
- Settings > Voice: status ("Speech model not downloaded · 640 MB" / "Parakeet TDT 0.6B ready"), Download / progress / Cancel,
  Microphone picker (NAudio device names, saved as `AppSettings.Microphone`, "" = Windows default).

- [ ] Self-test checks: Resample 48k→16k length and a sine's frequency kept; LevelOf silence ≈ 0, full-scale ≈ 1; Insert cases
  ("" + "hello." → "Hello."; "Hi" caret 2 + "there" → "Hi there"; after "end. " + "next" → "Next"; mid-text insert keeps the
  rest); a DictationSession fed silence ends with "nothing heard" after 10 s of audio (fake transcribe, VAD real when models are
  present, else a stub VAD path) — say which.
- [ ] Gate: build 0 errors; `--self-test` 0; negative: break Insert's capitalisation → self-test fails; restore.
- [ ] Verify the package names/versions and download URLs on the web (pin exact versions; the HF repo for the int8 Parakeet
  model and the sherpa-onnx release for silero_vad), and that `publish.ps1`'s output runs the recognizer (native DLLs present).
- [ ] Commit: "Voice: the speech engine (sherpa-onnx, Parakeet, Silero VAD), model download, Settings > Voice".

### Task 2: desktop dictation — mic button and the meter inside the box

**Files:** create `MainWindow.Dictation.cs` (capture, session, meter, keys); modify `MainWindow.xaml` (Chat and Code composer:
a mic column left of Stop/Send; the meter overlay in the TextBox's grid cell), the composer key handlers (Esc, Ctrl+Space).

**Interfaces:** consumes Task 1's `SpeechModels`, `Speech.Recognizer()`, `DictationSession`, `Speech.Insert`, `Speech.LevelOf`,
`Speech.Resample`, `AppSettings.Microphone`. Produces `StartDictation(TextBox box)`, `StopDictation()`, `bool Dictating`.

- [ ] Capture: NAudio `WaveInEvent` (device from settings) 16 kHz mono 16-bit if available, else device rate + Resample;
  buffers to float, fed to the session off the UI thread; Level/Text marshalled to the UI.
- [ ] The meter as in Decisions; mic button states: idle (mic glyph), listening (accent fill, pulsing), working (spinner while
  the last phrase transcribes). Not ready → toast "Download the speech model in Settings > Voice" and open it.
- [ ] Stops on: mic click, Esc in the box, Send, switching conversation, window closing, auto-stop.
- [ ] ui-smoke: the mic button exists in both composers; with a fake capture (feed a WAV or synthetic samples) and a fake
  transcribe the meter shows inside the box while listening, a phrase lands at the caret with the Insert rules, Esc stops and
  the device is released; models not ready → the toast. Run hidden, never the real microphone.
- [ ] Gate: build; self-test 0; `--ui-smoke` (orchestrator runs it); manual for the reviewer: the meter lines.
- [ ] Commit: "Voice: dictation in the Chat and Code boxes, with a level meter inside the box".

### Task 3: phone dictation

**Files:** modify `MainWindow.WebApp.cs` (route lines) + a new `MainWindow.WebVoice.cs`; `web/index.html` (a mic button in
both composers' send rows), new `web/js/dictate.js` (+ `web/test/dictate.test.mjs`), `web/sw.js` SHELL.

**Interfaces:** `GET /api/dictate` → `{ready}`; `POST /api/dictate` body = raw PCM 16-bit little-endian mono 16 kHz
(`Content-Type: application/octet-stream`, ≤ 10 min) → `{text}` (Task 1's recognizer through a DictationSession, phrases
joined); 409 "Download the speech model on the PC (Settings > Voice)" when not ready; 413 too long.
- [ ] Phone: tap the mic → getUserMedia (HTTPS only) → AudioWorklet → downsample to 16 kHz Int16 (pure `downsample()` and
  `toInt16()` tested), the meter inside the input pill (canvas, same look), auto-stop after 2 s of silence (client RMS gate)
  or tap again; then post, show a small spinner in the pill, insert the text at the caret. Permission denied → toast.
- [ ] webapp-smoke: POST a short synthetic PCM (or a tiny WAV turned PCM) → 200 or 409 when models absent (both checked by
  shape); a non-PCM / odd-length body → 400; too long → 413.
- [ ] Gate: build, self-test, webapp-smoke 0; node tests fail 0 with dictate.test.mjs; negative on downsample.
- [ ] Commit: "Voice: dictation on the phone, transcribed on the PC".
