# Voice phase 2b: Chatterbox Turbo voices — implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

**Goal:** a second speech engine, Resemble AI's Chatterbox Turbo (350M, MIT, English), for lively, natural replies in
voices the user makes from a short recording: a file (e.g. a clip saved from a video), the dictation microphone, or a
Kokoro voice. Kokoro stays as the CPU fallback.

**Spec:** docs/superpowers/specs/2026-11-26-voice-design.md "Phase 2" (2b). The user, 2026-11-26: Kokoro "doesn't sound lively
and natural enough and isn't saying all of the words"; "let's go ahead and add chatterbox turbo now"; asked whether a
YouTube clip can make a voice — yes, from a file the user saves (the app does not download from websites).

## Global constraints
- Nothing installs or downloads until the user presses Install in Settings > Voice (size shown); everything under
  `<DataRoot>\chatterbox\`; a half-finished install never counts as ready; Uninstall frees it.
- GPU first (the RTX 5080), CPU allowed but slow; the engine unloads after 10 idle minutes and before a model load that would
  not fit (VRAM check), so a working LLM always wins; when Chatterbox cannot run (not installed, no room, an error) the
  reply is read by the voice's chosen Kokoro stand-in with a one-line note.
- Text goes to the engine as written (no lexicon: no dropped words); the same SpeechText rules for code/links/tables.
- The speak paths, Stop, auto-read and the phone stay as phase 2a built them; a Chatterbox voice is just another voice choice.
- A short caution in the voice-adding UI: "Use voices you have the right to copy (your own, with permission, public domain)".
- Commits via worktree + cherry-pick; never commit repo-root App.xaml.cs or requests.txt.

## Decisions (controller's calls)
- Runtime: prefer an official/maintained ONNX export of Chatterbox Turbo run from C# (ONNX Runtime with the CUDA or DirectML
  provider) if one exists and works; otherwise an app-managed Python environment (`python -m venv` with the user's Python,
  `pip install` of pinned torch (CUDA wheel matching the driver) + chatterbox-tts) running a small local HTTP sidecar
  (`127.0.0.1`, random port, a token) started on demand and stopped with the app. Task 1 decides with evidence and records why.
- Voices: `AppSettings.ChatterboxVoices: [{Name, Clip (path under chatterbox\voices\), Exaggeration=0.5, Cfg=0.5, Temperature=0.8,
  Fallback (a Kokoro voice name)}]`; the clip stored as 24 kHz mono WAV, trimmed to the chosen 5–20 s and loudness-normalised.
- Sentence-by-sentence synthesis as Kokoro (first sentence fast); a per-voice conditioning cache so repeated sentences don't
  re-encode the reference clip.

## Review focus
1. GPU busy / out of memory → falls back to Kokoro, never crashes the LLM run or the app.
2. Install interrupted / pip fails / wrong CUDA → a clear status and Retry; the app stays usable.
3. A clip that is too short, silent, music-heavy or the wrong format → refused with a reason before saving.
4. Closing the app or Stop mid-sentence → the sidecar/engine stops promptly; no orphan process.
5. Paralinguistic tags in replies ([laugh] etc.) → passed through only for Chatterbox, stripped for Kokoro.

---

### Task 1: runtime, install and engine (C#)
**Files:** `Chatterbox.cs` (engine + install + sidecar or ONNX), a sidecar script if Python (`speech/chatterbox_server.py`
embedded or shipped beside the app), `MainWindow.Voice.cs` (an Install/Uninstall row with status and size).
**Produces:** `ChatterboxRuntime.Ready / InstallAsync(progress, ct) / Uninstall()`; `Task<float[]> Chatterbox.Synthesize(string text,
ChatterboxVoice voice, CancellationToken ct)` (24 kHz mono); `bool Chatterbox.CanRunNow(out string reason)` (VRAM check);
idle unload; `Chatterbox.Shutdown()` in OnClosing.
- Gates: build; self-test (pure parts: WAV/clip helpers, VRAM decision); webapp-smoke; real synthesis on this PC in a temp data
  folder (never the user's data) of two sentences with the default voice and with a clip made from Kokoro af_heart — report
  time to first sentence, real-time factor, VRAM used, and that the audio is not silent. Delete the temp install afterwards.
- Commit: "Voice: Chatterbox Turbo engine — install, GPU synthesis, idle unload, Kokoro fallback".

### Task 2: the Chatterbox voice library (desktop Settings > Voice)
- Add a voice: from an audio file (WAV/MP3/M4A/FLAC via NAudio MediaFoundationReader) with a simple trim (start and length
  sliders over a waveform, ▶ the selection), from the microphone (reuse the dictation capture, record up to 20 s), or from a
  Kokoro voice/blend (synthesise a reference paragraph); name; ▶ preview; per-voice Exaggeration, Cfg/pace, Temperature sliders;
  the Kokoro stand-in; rename/delete. Chatterbox voices appear in every voice picker (main, per worker) marked "Chatterbox".
- ui-smoke with a stub engine; never real audio or GPU in tests.
- Commit: "Voice: the Chatterbox voice library".

### Task 3: speak paths and the phone
- SpeechPlayer and /api/speak use Chatterbox for a Chatterbox voice (fallback rules above); /api/voice lists Chatterbox voices
  (the phone picks them; adding voices stays on the PC). Smoke + node tests.
- Commit: "Voice: Chatterbox voices read replies on the desktop and the phone".
