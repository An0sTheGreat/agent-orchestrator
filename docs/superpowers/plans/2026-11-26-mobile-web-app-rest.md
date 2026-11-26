# Mobile web app: the rest (chat management, Skills, Usage, Models and Agents, Code)

**Goal:** finish the spec's remaining steps on the phone: conversation management, the Skills screen, Usage & tokens,
per-worker Models and Agents (Local, primary PC, Remote API; presets, favorites, the GPU estimate), and Code.

**Spec:** docs/superpowers/specs/2026-11-25-mobile-web-app-design.md (steps 2, 4, 5). The user asked (2026-11-26) to
"autonomously continue building all of the rest of the features needed for the mobile web-app".

**Architecture:** as the earlier parts: JSON routes in new `MainWindow.Web*.cs` partials (route lines only in the
WebApp.cs switch), UI-state work on the dispatcher, behind the owner gate; phone screens as plain ES modules in web/js.
Desktop maps (recon, kept with the briefs): rest-map.md, models-map.md, code-map.md.

## Global constraints
- The look is the concept's; missing pieces drawn in its style. No build step, no framework, no CDN; DOM via `h()` /
  textContent, never innerHTML. Bump web/sw.js's cache version and SHELL list for new js files.
- One source of truth: phone writes go through the desktop's own store/fields/refresh calls; the desktop shows the change.
  A phone action never shows, restores or activates the desktop window, and never opens a desktop dialog (the phone
  confirms on its side; a flow that needs a dialog is PC-only or re-done without it).
- Secrets never go to the phone: API keys are reported only as set / not set (and their labels).
- Owner gate on every route (existing). Commits to master; never commit the repo-root App.xaml.cs or requests.txt.

## Decisions (controller's calls, with cost if wrong)
- Long-press on a chat stays "delete" (the user asked for it); the other chat actions live in the chat's ⋯ menu.
  Long-press on a project opens its menu (rename, delete). Cost: one more place to look for rename/move.
- Skills: list, switch, view, create, edit SKILL.md of the user's own skills; built-ins are read-only; no delete (the
  desktop has none). Cost: delete stays a file-system job.
- Usage shows what is stored (lifetime own/combined, last 7 days, longest task, per PC); the concept's "by worker" and
  turn counts have no data and are left out. Cost: a thinner page than the concept.
- Remote API on the phone: pick service and model (lists loaded on the PC with its stored key), providers order, price
  controls, apply to a worker. Entering, renaming or removing keys stays on the PC. Cost: a new key needs the PC.
- Model saves take effect from the next load, as on the desktop; the phone says so instead of the desktop's dialog.

## Part A — chat management, Skills, Usage (server and phone in parallel against these routes)
Conversations (each a `StoreChange(() => _store.X(...))` on the UI thread; 404 `{error}` unknown id):
- `POST /api/conversation/{id}/rename {title}` (blank resets to the derived title) → `{ok:true}`.
- `POST /api/conversation/{id}/archive {archived:bool}` → `{ok:true}`.
- `POST /api/conversation/{id}/move {project:"<id>"|null}` (null = out of its project) → `{ok:true}`; 409 for an archived
  chat ("Unarchive it first"); 404 unknown project.
- `POST /api/projects {name, kind:"chat"|"code"}` → `{id}`; 400 blank name. `POST /api/project/{id}/rename {name}`,
  `POST /api/project/{id}/delete` (its chats move to Recents) → `{ok:true}`; 404 unknown.
- `GET /api/conversations` projects gain `kind`.
Skills:
- `GET /api/skills` → `{skills:[{key, name, description, builtIn, on}]}` (key = folder name; user skills first, a user
  folder overriding a built-in of the same name hides the built-in).
- `GET /api/skill?key=` → `{key, name, description, builtIn, on, text}` (SKILL.md, at most 256 KB); 404 unknown.
- `POST /api/skills/set {key, on}` → SetSkill; `POST /api/skills/new {name, description}` → validate as NewSkill
  (bad name / exists → 400 with its message), write the same SKILL.md template, switch it on, refresh the desktop's
  Skills page if built — no dialog, no editor process → `{key}`; `POST /api/skills/save {key, text}` → user skills only
  (403 built-in), text ≤ 256 KB, written to SkillsRoot\<key>\SKILL.md only → `{ok:true}`.
Usage:
- `GET /api/usage` → `{lifetime (this PC), combined, longestTask (seconds, combined), days:[{day:"yyyy-MM-dd", tokens}]
  (last 7, oldest first, combined), pcs:[{name, lifetime}] (this PC first, then Others)}` plus any streak/peak fields
  UsageStats keeps.

Phone: the chat ⋯ menu's Rename (sheet with a field), Move to a project (sheet: the projects of that kind, New project…,
Remove from project), Archive/Unarchive, Copy reply (the last reply's text) all work; long-press a project → Rename /
Delete project; a New project control in Chats. The Skills tab: Your skills / Built in, a switch each, tap → view
(editable textarea + Save for the user's own), New skill sheet. Settings' Usage row opens the Usage page (lifetime
number, 7 bars, longest task, per PC, Reset usage stats with the existing confirm). Dead concept code removed: the
`[data-accent]` handler, the unreachable demo `#view-agent` stays only if Part B reuses it.

## Part B — Models and Agents (server in two makers, phone in two, all against these routes)
Workers (built-in roles planner=Lead, implementer, reviewer, compactor; custom = Guid ids):
- `GET /api/workers` → `{busy, workers:[{role, title, custom, kind, shown (in Chat), source:"local"|"primary"|"api",
  model (display name), quant, problem (setup problem or "")}]}` in the desktop's order.
- `POST /api/workers/add` → `{role}` (the desktop's AddCustomWorker without selecting a tab); 409 while busy.
  `POST /api/worker/{role}/remove` (custom only: 403 built-in; the phone confirms), `/rename {title}` (TitleProblem → 400),
  `/shown {shown}` (SetChatCard) → `{ok:true}`. 404 unknown role everywhere.
- `GET /api/worker/{role}` → `{role, title, custom, followsMain, source, model:{name, quant, label}, remote:{service,
  baseUrl, model, keyEnv, hasKey, provider, sortPrice, maxInput, maxOutput}|null, options:[{key, label, group, kind,
  value, default, min, max, choices, help, advanced, apiOnly}], custom:{kind, description, systemPrompt}|null,
  presets:{active, groups:[{name, items:[{name, model, quant}]}]}}` — options from ModelOptions.All with the values
  ModelOptions.Read/Effective/Prefill give the desktop editors; groups/advanced as SettingsGroups/AdvancedGroups.
- `POST /api/worker/{role}/save {fields:{key:"value"}, custom?:{title, kind, description, systemPrompt}}` → the
  desktop Save path factored out of ModelSettingsBody (built-in: config.json roles via WriteConfigs; custom: WorkerSetting
  + SyncCustomWorkers) → `{ok:true, note:"Takes effect from the next load"|"" , worker:<GET shape>}`; 400
  `{errors:{key:message}}` from ModelOptions.Apply.
- `POST /api/worker/{role}/vram {fields}` → `{gpu:{weights, kv, compute, vision, total}, whole, gpuTotal}` (bytes;
  null fields when unknown); GGUF reads off the UI thread, cached.
Model choice:
- `GET /api/models/local` → `{models:[{label, display, group:"text"|"vision"|"image", size, quant, favorite, pickable}]}`
  (FindLocalGgufs + DescribeChoices; off the UI thread, cached until _localModelsVersion changes).
- `POST /api/worker/{role}/model {label}` → pick a local model (AssignModel / SetRoleModel), source becomes local.
- `POST /api/worker/{role}/local` → switch an API/primary worker back to local (the desktop's toLocal save).
- `GET /api/primary/models` → `{host, models:[{id}]}` (404 when no primary PC); `POST /api/worker/{role}/primary {model}`
  → UsePrimaryModel (409 CodeBusy).
- `GET /api/remote/services` → `{services:[{id, name, baseUrl, keyEnv, hasKey, hasProviders, modelsNeedKey}],
  favorites:["<serviceId>|<modelId>"]}`; `GET /api/remote/models?service=<id>&baseUrl=<custom only>` →
  `{models:[{id, name, vision, priceIn, priceOut, context}]}` (RemoteCatalog with the stored key, off the UI thread);
  `GET /api/remote/providers?model=<id>` (OpenRouter) → `{providers:[{name, priceIn, priceOut, latency, tps, uptime,
  quant, vision}]}`; `POST /api/worker/{role}/remote {service, baseUrl, model, provider, sortPrice, maxInput, maxOutput}`
  → ApplyRemoteRole (problem → 400 {error}).
- `POST /api/favorites {kind:"local"|"remote", id, on}` → LocalFavorites / RemoteFavorites.
- Presets: `POST /api/worker/{role}/preset/apply {name}` → `{fields}` (the preset's values for the form, not saved, and
  the active-preset record as the desktop's MakeActive), `/preset/save {name, fields}`, `/preset/delete {name}` →
  `{ok:true, presets}`; through the primary PC (ImageShare preset calls, key on the PC) when the worker runs there.

Phone: Settings > Agents lists the workers (model, source, problem; switch "show in Chat"; Add worker; tap → worker page)
under the existing global agent settings. The worker page (#view-agent): title (rename for custom), MODEL selectrow →
the local picker sheet (#sheet-models, groups, favorites star, #sheet-favs), MODEL SOURCE seg Local / Primary PC (when a
primary is set) / Remote API, the options form drawn from `options` (groups, Advanced switch for advanced groups, inputs
by kind, Choice as select, Bool as Default/On/Off), the GPU estimate under Context (re-asked on change, debounced),
Presets (#sheet-mdlpresets: groups, apply fills the form, Save as…, delete with confirm), Revert, Save (shows the note),
custom: kind, description, system prompt, Remove (confirm). The Remote API page (#view-remoteapi): service chips with key
status (no key entry: "add keys on the PC"), Load models list with search and favorites, providers (OpenRouter) with
order ticks, price controls, Apply to this worker. Settings > Models navrow keeps the global settings.
## Part C — Code (server in two makers, phone in two, against these routes)
Decisions for Code (controller's calls): files are viewed, not edited by hand (the spec), except PROJECT.md, which the
desktop also lets the user edit; the folder picker becomes a phone folder browser (directories only); the terminal is the
desktop's one PowerShell, its output kept in a ring buffer so the phone can read it without the PC panel open, input as
typed (owner-only, same power as the desktop's terminal); the browser pane: tabs, URL, back/forward/reload, a screenshot
on request, taps/typing/keys sent as the user's own input; the TODO is the model's last `todo` tool call, kept per
conversation in memory and parsed back from saved tool rows when available. A phone action may switch the PC window's
view (as sends already do) but never surfaces it.
Routes (UI-thread work on the dispatcher; 404 unknown id; 409 {error} with the desktop's toast text where it refuses;
409 DialogOpen when a PC dialog is open):
- `GET /api/code/{id}` → `{id, title, folder, folders:[{path, access:"read"|"write"}], hasProjectMd, startLabel, canStart,
  running (this conversation's project run), busy (any reply/run on it), taskId|null, status, taskTime:{seconds, running},
  turnTime:{seconds, running}, once, todo:[{text, state:"done"|"doing"|""}]|null, activity (last 40 KB of the run
  transcript, live or from <id>.run.txt)}`.
- `POST /api/code/new {folder, project?}` → a Code conversation for that folder (validated as the desktop: full path,
  FolderUsable, not the Agent Orchestrator root) → `{id}`. `POST /api/code/{id}/folder {path}` (UseCodeFolder).
- `POST /api/code/{id}/start {restart?:bool}` → StartCodeProject; a finished project answers 409 `{error, restart:true}`
  unless restart is true (the phone asked). `POST /api/code/{id}/once {on}`; `POST /api/code/{id}/projectmd/template`.
- Extra folders: `POST /api/code/{id}/folders/add {path, access}`, `/folders/remove {path}` (the desktop's refusals).
- `GET /api/fs/dirs?path=` → `{path, parent, dirs:[{name, path}]}` (drives when path is empty); directories only.
- Files (sandboxed to the conversation's folder + extra folders, no `..`, no reparse escape, 2,000,000-byte cap, NUL →
  415 "Not a text file"): `GET /api/code/{id}/files?dir=` → `{dir, entries:[{name, path, dir, size}]}` (dirs first);
  `GET /api/code/{id}/file?path=` → `{path, text, language, size}`; `POST /api/code/{id}/projectmd {text}` (PROJECT.md
  only). Changes: `GET /api/code/{id}/changes` → `{files:[{path, added, removed, msg}]}` from the saved replies' file
  changes; `GET /api/code/{id}/diff?path=&msg=` → `{path, lines:[{kind:" "|"+"|"-", num, text}]}` (DiffView).
- `GET /api/code/{id}/runlog` → `{text}` (ReadRunLogTail).
- Terminal: `GET /api/terminal?since=<seq>` → `{seq, text (since seq, ANSI kept), running, cwd}`; `POST /api/terminal/input
  {data}`; `POST /api/terminal/new` (starts one when none; works without the PC panel).
- Browser: `GET /api/browser` → `{open, tabs:[{index, title, url, shown}], follow, input}`; `GET /api/browser/shot` →
  image/jpeg of the shown tab (no-store; 404 none); `POST /api/browser/{navigate {url}|tab {index}|newtab|close
  {index}|back|forward|reload|tap {x, y}|type {text}|key {key}|options {follow?, input?}}` (x,y in the last shot's pixels).

Phone: a Chat/Code conversation of kind code opens the Code screen (#view-code, the concept's markup restored from git
d722282^ index.html lines 252-455, immersive): header (title, folder, context ring, Start, ⋯ → #sheet-project), the run
strip (task, task time, turn time ticking locally), panes Chat (the conversation as the Chat screen draws it, with the
TODO card from `todo`/tool events, and the composer) / Run (Start/Stop, the live run stream, the activity) / Files
(folder card + extra folders, the file browser, workflow records, a viewer with markdown preview, the changes list with
diffs, PROJECT.md editor, the terminal card, run log) / Browser (tabs, URL bar, screenshot that refreshes, tap to click,
type/keys, follow and input switches). New Code conversation: pick a folder with the phone folder browser (or a recent
folder). #sheet-project: PROJECT.md from template / Edit PROJECT.md, Folders, Stop after the first finished item.
