# Mobile web app: the Images tab and the Settings tab

**Goal:** the phone gets the desktop's Images page (generate, queue, live preview, gallery, viewer, delete) and the full
Settings tab (General, Appearance, Models, Agents, Plugins, About), in the concept's style, reading and writing the PC's
own state through the desktop's code paths.

**Spec:** docs/superpowers/specs/2026-11-25-mobile-web-app-design.md (step 3 Images; step 5 Settings). The user asked for
both now (2026-11-25), plus long-press delete of chats and pictures (shipped separately first).

**Architecture:** new JSON routes in MainWindow.WebApp.cs (UI-state work on the dispatcher, behind the existing owner
gate), new screens in web/ (plain ES modules, `h()` DOM, concept tokens). Recon of the desktop pages:
the controller's desktop map (kept with this plan's briefs).

## Global constraints
- The look is the concept's; missing pieces drawn in its style. No build step, no framework, no CDN; XSS-safe DOM.
- One source of truth: phone writes go through the same fields and refresh calls the desktop uses; the desktop shows the
  change. A phone action never shows, restores or activates the desktop window.
- Never a desktop dialog from a phone route (the phone confirms on its side); a route that would need one is refused or
  left PC-only.
- Owner gate on every route (existing). Commits go to master; never commit the repo-root App.xaml.cs or requests.txt.

## Decisions (controller's calls, with cost if wrong)
- Images form covers prompt, negative, model (this PC / primary PC / OpenRouter via the composer's existing model list),
  aspect/size/lock, steps, guidance, count, seed, Low VRAM. Sampler, scheduler, strength, start image, OpenRouter
  providers, quant and the models folder stay PC-only. Cost: those need the PC.
- Images progress is polled (`GET /api/images/status`, 1 s while busy, 5 s idle, only while the tab is shown), not a new
  event stream. Cost: up to 1 s lag, a request per second while generating.
- Settings that are PC-only by nature stay off the phone or read-only: Start with Windows is allowed (a registry write);
  folder pickers, downloads/installs (llama-server, Pi, image runtime), sandbox setup (UAC), Remote access (Cloudflare
  tunnel + password), share connection codes and keys are not shown; the Phone app switch is read-only (turning it off
  would cut the phone). Cost: those need the PC.
- Full access and Super access can be switched from the phone, with a phone-side confirm that names what they turn off.
  Cost: a security switch reachable from the phone (owner-only, as every route).
- Appearance: theme and accent are set on the PC; the phone applies the accent colour to its own tokens and stays on its
  dark palette unless styles.css already has a light one. Cost: the phone does not mirror all eight themes.

## Part 1 — Images (server and phone in parallel against the contract below)

Routes:
- `GET /api/images` → `{ settings, models:[{id,name,note,source:"local"|"shared"|"remote",negative,steps,cfg,multiple}],
  aspects:[…], maxBatch:20, pictures:[{path, url, time}] }` — `settings` = `{ model, prompt, negative, steps, cfg, aspect,
  width, height, unlocked, count, seed, lowVram }` from `_settings.ImageGen`; pictures newest first, at most 200, from
  `ImageGen.ImagesFolder` as RefreshGallery lists them; `url` = `/api/file?path=…`.
- `GET /api/images/status` → `{ busy, text, progress (0..1|null), frame (preview count, 0 none), queue:[{index, prompt,
  seed}], latest:"<newest picture path>"|null }`.
- `GET /api/images/preview?frame=N` → `image/png` of the latest step preview of the running job (404 none); no-store.
- `GET /api/images/picture?path=…` → that picture's sidecar settings (same shape as `settings`) for "Use these settings";
  404 when no sidecar. Path checks as `/api/file` (images folder only).
- `POST /api/images/generate` `{settings}` → validate as `PicturesToMake` does (empty prompt, no model → 400 with its
  message), save `_settings.ImageGen` (the desktop page shows it via `ApplyImageSettings`), resolve the seed, expand to
  Batch=1 pictures (OpenRouter: one entry), append to `_imgQueue`, `RefreshQueue()`, start `RunImageJob(RunQueueAsync)`
  if idle (after the lazy `BuildImagesPage`) → `{queued:n}`.
- `POST /api/images/cancel` → `CancelImageGen()`. `POST /api/images/unqueue` `{index}` → remove that waiting picture
  (404 out of range).
- Delete uses the shipped `POST /api/picture/delete`.

Phone: a fifth tab "Images" in portrait (a rail entry in landscape). The screen: the form (prompt, negative when the
model takes one, model picker grouped by source, aspect + width/height + lock, steps, guidance, count 1–20, seed, an
Advanced fold with Low VRAM), Generate (queues while busy; an "N queued" control lists the waiting pictures with ✕ each,
plus Cancel), a stage card in the picture-card style showing the status, percentage and the live preview, then the new
picture; the gallery as a 3-column grid, newest first; tap opens a full-screen viewer (swipe/arrows, Use these settings,
Delete); long-press a thumbnail to delete.

Gates: build 0 errors; `--self-test` 0; `--webapp-smoke` 0 with checks for each route (generate validation 400, queue +
unqueue, status shape, preview 404 when idle, sidecar read, path refusal); node tests for the pure parts (form ↔
settings mapping, status → stage state); negative gates on the route lines.

## Part 2 — Settings (server and phone in parallel against the contract below)

Routes:
- `GET /api/settings` → `{ general:{startWithWindows, notifyWhenDone, linkOpen, followTabs, computerUse,
  computerApps:[…] (read-only), stats:"…" (read-only), phoneApp:{on, address} (read-only)}, appearance:{theme,
  themes:[{key,name}], accent, accents:[{key,name,color}], centerChat, readingSize, emoji, contextPercent},
  models:{keepLoaded, multiModel, share:{on, network}}, agents:{workflow, codeEngine, fullAccess, superAccess,
  sandboxNetwork}, plugins:{ponytail}, about:{version, pc} }`.
- `POST /api/settings` `{key, value}` for exactly those writable keys; applies like the desktop handler (field, Save, the
  same refresh/side-effect calls: ApplyTheme, EndComputer when computer use goes off, the idle-server stop for keep
  loaded, StartShare/StopShare, SetPonytail, SetStartWithWindows…); 409 with the desktop's message where the desktop
  refuses (workflow/code engine while a reply or run is busy); 400 unknown key or bad value → `{ok:true, settings}`.
- `POST /api/settings/reset-stats` → resets usage stats (the desktop's reset without its dialog).

Phone: the Settings screen in the concept's style with sections General, Appearance, Models, Agents, Plugins, About;
switches/selects/radios as the desktop; a phone confirm before Full/Super access go on and before Reset usage stats;
the accent applies to the phone's tokens.

Gates: build, self-test, webapp-smoke with checks per key class (a boolean, a choice, a refused-while-busy key, an unknown
key 400, reset-stats), node tests for the pure parts, negative gates.

## Also
- iOS keyboard: when the composer is focused the page scrolls the header off-screen and leaves a gap above the keyboard
  — size the shell to the visual viewport while the keyboard is up (`visualViewport` resize) so the header stays.
  Ships with Part 1.
