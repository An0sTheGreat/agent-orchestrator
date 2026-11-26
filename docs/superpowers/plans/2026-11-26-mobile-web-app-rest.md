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

## Part B — Models and Agents (contract written with its briefs)
## Part C — Code (contract written with its briefs, from code-map.md)
