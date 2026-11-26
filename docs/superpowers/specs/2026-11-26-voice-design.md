# Voice — design and roadmap

Status: drafted 2026-11-26 for the user's review. Phase 1 (dictation) is next; phases 2 and 3 are the plan for later and
get their own detailed plans when their turn comes.

## Intent

The user wants to talk to Agent Orchestrator instead of typing. Most of all, at the computer: speak a message into a chat
rather than type it. Next, hear replies read aloud. Eventually, a full spoken conversation with a voice model that answers,
searches the web, and hands real work to the app's workers on the PC — "everything I can do on the PC now, also by voice".

## Goals

- **Dictation** (phase 1): a microphone button beside Send in the desktop's Chat and Code composers, then on the phone. Speech
  becomes text in the message box, to edit and send.
- **Speech out** (phase 2): replies read aloud on request, and optionally every reply.
- **Voice tab** (phase 3): a Voice entry in the left bar (desktop) and a tab on the phone for a spoken back-and-forth with a
  small, fast voice model that can search, answer, and start, follow and stop the app's Chat and Code work.
- Everything runs on the user's own PC: no cloud speech service, no account.
- Small models that stay loaded beside a working LLM without taking its GPU memory.

## Non-goals (for now)

- True speech-to-speech models (one model hears and speaks, e.g. Qwen-Omni, Moshi). Revisit when they handle tools well.
- Wake words ("Hey …"), always-listening, or use with the phone locked (see Constraints).
- Voice cloning or custom voices beyond the TTS model's built-in ones.

## Anti-goals (a failure even if it works)

- Speech or transcripts leave the PC (other than the user's own phone over Tailscale).
- Voice slows down or squeezes out the working LLM (GPU memory, CPU starved during a run).
- Dictation sends a message by itself: the user always sees the text and presses Send.
- A spoken instruction runs work the user didn't intend without the same approvals the typed path asks for.

## Approaches

1. **A local pipeline: speech-to-text → LLM → text-to-speech** (picked). Separate small models for hearing (STT) and speaking
   (TTS) around the app's existing LLMs. Works with every tool and worker the app already has; each piece can be swapped; the
   speech models run on the CPU. Cost: about a second of delay before a spoken answer starts, less natural than a single
   speech model.
2. **A speech-to-speech model** (Qwen2.5/3-Omni, Moshi): the most natural conversation and lowest delay, but several GB of GPU
   memory and weak tool use — the opposite of "do my work on the PC". Not now.
3. **The browser's or Windows' built-in speech** (Web Speech API, Windows Speech): no download, but it sends audio to
   Apple/Microsoft or is poor in quality, and does not exist the same way on both sides. Not picked; iPhone keyboard dictation
   stays available on the phone anyway.

## Building blocks (check the newest versions when each phase starts; this field moves fast)

| Job | Pick | Why | Alternatives |
|---|---|---|---|
| Runtime | **sherpa-onnx** (C# NuGet package, ONNX on the CPU) | one library for STT, TTS and VAD, offline, faster than real time on a desktop CPU, no GPU memory | whisper.cpp (STT only) |
| Speech to text | **NVIDIA Parakeet TDT 0.6B** (English) | very accurate, very fast, punctuation and capitals | Parakeet multilingual v3; Whisper large-v3-turbo (99 languages, slower); Moonshine (tiny) |
| Voice activity | **Silero VAD** | knows when speech starts and stops: ends dictation on a pause, lets the user interrupt a spoken reply | — |
| Text to speech | **Kokoro-82M** | natural voices, tiny, faster than real time on the CPU | Piper (smaller, flatter); Chatterbox / Orpheus / Kyutai TTS (more expressive, GPU) |
| Voice model (phase 3) | **a ~4B instruct model**, e.g. Qwen3.5-4B (~3 GB GPU) | fast first words, good at tool calls; stays loaded beside a 9B worker on the 16 GB RTX 5080 (multi-model) | the Lead's own model (no extra memory, slower, busy during runs) |

Models download into the app's data folder from Settings, the way llama-server does (a Download button, the version shown).

## Phase 1 — Dictation (shipped 2026-11-26)

- **Desktop first.** A mic button beside Send in the Chat and Code composers. Click: it listens (the button pulses, a level
  meter shows it hears you). Click again, press Esc, or pause ~2 s: it stops. The text is inserted at the cursor; nothing is
  sent. (My call: click-to-toggle plus auto-stop on silence; a hold-to-talk hotkey such as Ctrl+Space as well.)
- Audio from the default Windows microphone (NAudio or WinMM capture, 16 kHz mono), transcribed on the PC by Parakeet via
  sherpa-onnx in streaming or short-chunk mode, so words appear while talking.
- Settings > General (or a new Voice section): the microphone to use, Download speech models (size shown), the language/model.
- **Phone.** The same mic button in the phone composer: the phone records (getUserMedia over the Tailscale HTTPS address),
  sends the audio to the PC (`POST /api/dictate`), and the PC's text comes back into the box. The keyboard's own dictation
  keeps working too.
- Done when: dictating a paragraph on the desktop puts accurate, punctuated text in the box within a second of stopping, with
  the working LLM's speed unchanged.

## Phase 2 — Speech out, with full voice settings

Approved 2026-11-26: "add an in-depth settings system directly in the app to fully customize the voices".

**2a — Kokoro (next).** Kokoro-82M v1.0 (multi-language, ~54 voices) through sherpa-onnx on the CPU, downloaded from
Settings > Voice like the speech-to-text model.
- **Speak a reply:** a speaker button on each assistant reply (desktop and phone) reads it aloud — markdown read as prose,
  code blocks, tool rows and links' URLs skipped — and turns into a stop button while speaking. Speech starts after the first
  sentence is made (sentence by sentence), not after the whole reply.
- **Read replies aloud** (off by default): each finished reply is read automatically; a new reply or Stop ends the old one.
- **Settings > Voice > Speech output** (the in-depth part):
  - The voice library: every built-in voice, grouped by language and accent, with gender, searchable, each with ▶ preview.
  - **Custom voices by blending:** pick 2–4 voices, set each one's weight with a slider, hear it live, name and save it; saved
    custom voices are listed with play, rename, edit and delete, and can be picked like any voice.
  - Per voice: speed (0.5–2.0), volume, and the pause between sentences; a test box ("Type something to hear it") with Play.
  - Output device (default or a chosen speaker/headset).
  - What gets read: skip code blocks (on), read headings (on), read tables as "a table with N rows" (on), read the thinking
    (off), maximum length before it asks (e.g. 4,000 characters).
  - A different voice per worker (optional): Lead, Implementer, Reviewer and custom workers can each have their own voice.
- On the phone the PC makes the audio and the phone plays it; the phone's Settings get the same voice picker, speed, preview
  and Read replies aloud switch (blending is edited on the PC, its saved voices are pickable on the phone).

**2b — Custom voice engine: Chatterbox (after 2a).** For a real cloned voice: Resemble AI's Chatterbox (~0.5B, MIT licence),
optional, on the GPU (~2–3 GB), downloaded only when chosen. Settings: record a 10–30 s sample with the dictation microphone
or pick a WAV, name it; emotion/exaggeration and pacing sliders; preview. Chosen per voice like Kokoro's; when the GPU is busy
with a working LLM the app falls back to the Kokoro voice picked as that voice's stand-in. The exact runtime (ONNX export vs a
Python sidecar) is decided when 2b starts.

## Phase 3 — Voice tab (voice to voice)

- A **Voice** entry in the left bar (desktop) and a Voice tab on the phone: a big mic, the live transcript, the spoken reply's
  text, and the work it has started.
- The loop: listen (VAD) → Parakeet → the voice model → Kokoro, streamed sentence by sentence so speech starts fast; speaking
  over it interrupts it (barge-in).
- The voice model's tools: web search and fetch (as Chat's Search), and the app itself — start a Chat or Code task with a
  brief in a folder or project, ask how a running task is going, stop it, read back a reply, answer the app's own approval
  questions by voice. Long work runs in the normal Chat/Code conversations (visible in the app); the voice model says when it
  starts and tells you when it finishes.
- The same approvals as typing: sandbox prompts, Full/Super access confirms, deletes — asked out loud and answered by voice or
  a tap.
- Voice conversations are saved like chats (a new kind) so they can be read later.

## Constraints

- iPhone home-screen web apps lose the microphone when the screen locks or another app comes forward: phone voice works only
  with the app open in front. A native app would be needed for pocket or car use.
- The desktop needs a microphone; the PC does all speech work, so the phone only records and plays.
- CPU use during a long dictation or conversation should stay low enough not to slow a run (sherpa-onnx thread count capped).

## Left to the builder

- Exact sherpa-onnx models and packaging (download URLs, checksums), audio capture library, streaming chunk size.
- The hotkey, the level-meter look, where the speech settings live.
- Phone audio format (WebM/Opus or WAV) and conversion on the PC.

## Open questions (for later phases)

- Phase 2: (answered) Read replies aloud is off by default; the voice is the user's pick in Settings > Voice.
- Phase 3: a dedicated voice model loaded all the time, or loaded when the Voice tab opens? How chatty should progress updates
  be during a long run?
