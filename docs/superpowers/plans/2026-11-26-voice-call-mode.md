# Voice: call mode (conversation only) — design and plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task.

Replaces the roadmap's phase 3 "Voice tab" (docs/superpowers/specs/2026-11-26-voice-design.md) with a call mode built from
what exists: dictation (Parakeet + Silero VAD) → the Lead → Kokoro or Chatterbox. Desktop first.

## What the user asked (2026-11-26)
- A separate "mode": a phone call button right above the profile and settings row in the left bar, matching the theme and
  its corner radii. Pressing it starts a call: an animated circular audio visualizer smoothly appears above the button,
  about 25% smaller than the user's sketch (the sketch's circle fills the sidebar width, ~250 px → ~185 px), driven by the
  speaking model's voice. Pressing the button again ends the call and stores the whole conversation's transcript in a
  fresh chat.
- "Conversation only first": no web search, tools or starting work during a call yet (that is a later phase).

## Design (controller's calls, the user may correct)
- **The loop:** listen (the dictation capture + VAD; a phrase ends after ~0.8 s of silence) → the phrase is sent as a
  message in the call's chat (a fresh Chat conversation made when the call starts, titled "Call · <date, time>") through the
  normal chat send path to the Lead → the reply streams; each finished sentence is spoken with the main voice (TtsSettings
  .Voice, Kokoro or Chatterbox incl. the fallback rules) → when speech ends, listening resumes. The chat holds the
  transcript as it goes (crash-safe); ending the call just stops and leaves it, with a toast "Call saved to <title>" that
  opens it.
- **Spoken style:** the chat request carries `"voice": true`; orchestrator.py then adds a short instruction to the system
  prompt ("You are in a spoken call: answer in a few natural spoken sentences, no markdown, lists, code or links; ask a short
  question back when it helps") and turns off search/tools/skills/delegation for that turn.
- **Interrupting:** the mic is not listened to while the model speaks (speakers would feed it back); the user interrupts by
  pressing Space or Esc in the call, or clicking the visualizer — speech stops and listening starts. A setting "Interrupt by
  talking (use headphones)", off by default, keeps the VAD on during speech and stops it when the user speaks.
- **States** shown under the visualizer in small muted text: Listening… · Thinking… · Speaking · Paused (mic muted).
  A small mute toggle beside the call button while in a call.
- **The visualizer:** a WPF element (no images): a soft circular blob in the accent colour made of 64 radial points whose
  radius follows the audio's band energies (FFT of the current samples) with easing, a gentle idle breathing, a faint outer
  glow; while listening it follows the microphone (dimmer, smaller swing), while speaking it follows the voice's output
  samples; thinking = a slow rotating shimmer. 60 fps via CompositionTarget.Rendering only while visible. Diameter ~185 px,
  centred in the sidebar above the call button; appears with a 250 ms scale+fade ease-out, leaves the same way.
- **The call button:** a round icon button (the theme's button radius, phone glyph Segoe MDL2 `E717`) right above the
  profile row; in a call it turns the accent red/"end call" colour with the hang-up glyph. Disabled with a tooltip when the
  speech-to-text or voice model isn't downloaded ("Download them in Settings > Voice").
- **Stops** the call: the button, closing/hiding the window (HideToTray), Unload all models; a send from the keyboard into the
  call's chat works as a normal message (spoken reply).

## Tasks
### Task 1: the call loop (C#) + the engine flag (Python)
`MainWindow.Call.cs`: `CallSession` (state machine idle → listening → thinking → speaking → listening; Start/End; mute;
interrupt), using `DictationSession`/the capture code, the chat send path, and `SpeechPlayer` fed sentence by sentence as the
reply streams; events `StateChanged(CallState)` and `Levels(float[] bands, bool speaking)` (16–32 bands, ~60 Hz) for the
visualizer; a pure `CallState Next(CallState, CallEvent)` with self-test checks. `orchestrator.py`: the `voice` request flag
(prompt addition; tools/search/skills/delegation off) with a unit test. ui-smoke with stub capture/LLM/TTS: start → a phrase
→ a message in the new chat → spoken sentences → listening again; interrupt; end → chat kept, toast.
### Task 2: the button, the visualizer and the animation
`CallVisualizer.cs` (the element), XAML in the left bar above ProfileButton, the show/hide animation, the state text, mute,
the "Interrupt by talking" setting in Settings > Voice. ui-smoke: the button exists and is disabled without models; in a call
the visualizer is visible, sized ~185 px, and animates (its render count grows); end hides it.
