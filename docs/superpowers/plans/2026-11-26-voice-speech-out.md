# Voice phase 2a: speech out with Kokoro and full voice settings — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** replies read aloud on the desktop and the phone by Kokoro on the PC's CPU, with an in-depth Settings > Voice >
Speech output: the voice library with previews, custom voices by blending, speed/volume/pauses, a test box, the output
device, what gets read, a voice per worker, and Read replies aloud.

**Architecture:** a C# TTS engine beside phase 1's `Speech.cs` (sherpa-onnx `OfflineTts` with the Kokoro multi-language
model); text prepared for speech by pure functions; sentence-by-sentence synthesis and NAudio playback; settings in
`AppSettings`; phone routes return WAV made on the PC.

**Spec:** docs/superpowers/specs/2026-11-26-voice-design.md — "Phase 2" (2a only; 2b Chatterbox is a later plan).

## Global constraints
- CPU only, 2 threads, nothing downloaded until the user presses Download (size shown) in Settings > Voice; files go to
  `<DataRoot>\speech\`; half-finished downloads never count as ready (phase 1's `SpeechModels` patterns and HubDownloader).
- Read replies aloud is off by default. A new reply, Stop, switching conversation, the window hiding to the tray or closing
  stops speech and releases the audio device.
- The app's existing styles on the desktop; the concept's on the phone; DOM via `h()`/textContent. Commits to master via
  worktree + cherry-pick; never commit repo-root App.xaml.cs or requests.txt.

## Decisions (controller's calls)
- Model: sherpa-onnx's Kokoro multi-language v1.0 package (model, voices, tokens, espeak-ng data, lexicons), int8 if offered
  and good; the voice catalog (name → speaker id, language, accent, gender) kept as data in code.
- Custom voices are blends: 2–4 built-in voices with weights (normalised to 1); synthesised by a voices file the app writes
  with the blends appended after the built-ins (or the API's own mixing if sherpa-onnx has one — check first). Saved as
  `AppSettings.CustomVoices: [{Name, Mix: {voice: weight}}]`.
- Settings stored as `AppSettings.Tts { Voice, Speed=1.0, Volume=1.0, SentencePauseMs=250, Device="", SkipCode=true,
  ReadHeadings=true, TableSummary=true, ReadThinking=false, MaxChars=4000, AutoRead=false, WorkerVoices: {role: voice} }`.
- Speech text rules: markdown → prose; fenced and inline-long code skipped ("code skipped" said once when SkipCode), links read
  by their text, headings read as a sentence, tables → "a table with N rows", lists read item by item, emoji dropped; over
  MaxChars → the desktop asks once ("Read all 9,000 characters?").
- Playback: synthesise sentence n+1 while sentence n plays; one player at a time.

## Review focus
1. Very long replies, replies that are only code, empty replies → sensible speech or a short note, no hang.
2. Clicking speak on reply B while A speaks → A stops, B starts; Stop always stops within ~200 ms.
3. Models not downloaded / the output device missing → a clear toast, button back to idle.
4. A custom blend that names a voice that no longer exists → falls back to the default voice with a note.
5. Phone: auto-play blocked by iOS until a tap → the first speak needs a tap; no stuck "speaking" state.

---

### Task 1: the TTS engine, download, text-for-speech, playback (C#)
**Files:** create `launcher/AgentOrchestrator/Tts.cs` (engine, catalog, blends, `TtsSelfTest`), `SpeechText.cs` (pure text
rules + sentence splitter); modify `Speech.cs`/`MainWindow.Voice.cs` only to add the Kokoro download beside STT.
**Produces:** `TtsModels.Ready/DownloadAsync`; `Tts.Voices` (catalog records `VoiceInfo(Name, Id, Language, Accent, Gender)`);
`float[] Tts.Synthesize(string text, VoiceChoice voice, float speed)` (24 kHz mono) where `VoiceChoice` = a built-in name or a
custom blend; `string SpeechText.ForSpeech(string markdown, TtsSettings s)`; `List<string> SpeechText.Sentences(string)`;
`sealed class SpeechPlayer : IDisposable { Task Speak(IEnumerable<string> sentences, VoiceChoice, TtsSettings, CancellationToken); void Stop(); bool Speaking; event Action<bool> SpeakingChanged }`
(NAudio WaveOutEvent on the chosen device, volume, pause between sentences, pipelined synthesis).
- Self-test: ForSpeech rules (code skipped, link text, heading, table summary, list items, emoji), Sentences (abbreviations
  like "e.g." and "3.5" not split), blend weights normalised, a stub engine path when models are absent.
- Gates: build; `--self-test` 0; negative on ForSpeech's code rule; with the models downloaded into a temp data folder (test
  only), synthesise "Hello from Kokoro." and report its length and that it is not silent.
- Commit: "Voice: Kokoro text to speech — engine, voices, blends, text for speech, playback".

### Task 2: desktop — Settings > Voice > Speech output, and the speak button
**Files:** `MainWindow.Voice.cs` + XAML (the Speech output section as in the spec's 2a list), a new `MainWindow.SpeakOut.cs`
(speak button on Chat/Code reply rows, auto-read on a finished reply, stop rules), `Models.cs` (`TtsSettings`, `CustomVoices`).
- The library: grouped list, search, ▶ preview per voice; the blend editor (2–4 voices, weight sliders, live ▶, Name, Save);
  saved voices (play/rename/edit/delete); speed, volume, pause sliders; test box + Play; output device; the reading rules;
  per-worker voices; Read replies aloud.
- ui-smoke (`SpeakOutSmoke`, a fake engine via a seam, never real audio): the speak button exists on a reply and toggles to
  stop; a second reply's speak stops the first; Stop stops; auto-read speaks a finished reply only when on; a blend saves,
  renames, deletes and shows in the voice pickers; settings persist.
- Gates: build; self-test; webapp-smoke; ui-smoke (owed to the orchestrator). Commit: "Voice: Speech output settings and
  the speak button".

### Task 3: phone — the speak button and voice settings
**Files:** `MainWindow.WebVoice.cs` (routes), `web/js/speak.js` (+ test), chat.js hooks, settings.js (a Voice page), sw.js.
- Routes: `GET /api/voice` → `{ready, voices:[{name, language, accent, gender, custom}], settings:{voice, speed, autoRead}}`;
  `POST /api/voice {voice?, speed?, autoRead?}` (the PC's settings); `POST /api/speak {text, voice?}` → `audio/wav` (24 kHz
  16-bit, the reply text prepared by ForSpeech; ≤ MaxChars × 2 → else 413); 409 when not ready.
- Phone: a speaker button on each assistant reply (stop while playing), auto-read of a finished reply when on (after the first
  tap unlocks audio on iOS); Settings > Voice: voice picker with ▶ preview, speed, Read replies aloud; Download stays on the PC.
- webapp-smoke + node tests (pure: the reply text picked for speech, the play-state reducer). Commit: "Voice: replies read
  aloud on the phone".
