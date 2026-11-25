# Mobile web app — design

Status: approved by the user (2026-11-25).

## Intent

The user wants to work from their phone on any of their PCs that run Agent Orchestrator, reached over Tailscale: first
general chat (questions, research, web search, pictures made in the chat), then Code (files on the PC, working in its
folders, building apps and projects), then everything else the desktop app does. It must look exactly like the user's
mobile concept (`web-app-concept`, below). The desktop app stays as it is; the web app is a second interface beside it,
built so it could one day replace the desktop's.

## Goals

- Every desktop feature on the phone except the Models Hub (later, maybe).
- The look of the concept, exactly: its HTML structure, `styles.css`, fonts, icons, portrait and its own landscape layout.
- Sign-in is the phone's Tailscale login: no app password on the tailnet.
- Reach every PC on the tailnet that runs the app, and switch between them.
- Live: replies, thinking, tool steps and pictures stream in as they are made, as on the desktop.
- What the phone does, the desktop shows too (one conversation store, one engine per PC).

## Non-goals

- The Models Hub (downloads, catalogue, VRAM estimates for downloads, model sharing setup).
- A native app or app-store install. A home-screen web app (PWA manifest) is enough.
- Replacing the desktop interface now.
- Reaching PCs outside the user's tailnet. The Cloudflare tunnel + password page (`remote.html`) stays as it is for now.

## Anti-goals (a failure even if it works)

- Anyone but the user (their Tailscale login) can reach a PC's app.
- The phone and the desktop disagree about a conversation (two sources of truth).
- The phone version drifts from the concept's look.
- A phone action blocks or hangs the desktop window.

## The concept (source of the look)

The user's folder `web-app-concept` (README, `index.html` 83 KB with every screen, sheet and dialog and an SVG icon
sprite, `styles.css` 47 KB with the Graphite tokens and all components, `app.js` 22 KB with a router, drawer, sheets,
switches, accent picker and simulated turns, `assets/fonts` with Inter, Newsreader and JetBrains Mono). It is copied into
the repo unchanged as the starting point (`web/`), minus its demo frame (the phone drawn on a PC page and the jump list).

Screens: Chats (projects, recents, archived, Chat/Code filter, search, drawer with profile and lifetime tokens); Chat
(context ring and popover, markdown replies, thinking, tool rows, delegation card, sandbox permission card, compaction
note, queue chip, stop, timestamps, copy; pill composer that expands; `+` sheet with attachments, web search, tools,
vision, image gen, effort, worker, sandbox permissions, skills; `/` skill completion); Code (folder and project menu,
Start/Resume, task and turn time, TODO list, the conversation, the live run, Files with editor preview, terminal and run
logs, and the model's browser); Skills; Models (per-worker settings for Local, Remote PC and Remote API, GPU estimate,
presets, model picker, favorites, sampling and the rest); Settings (General, Appearance, Models, Agents, Plugins, Remote
API, Usage & tokens, About). Landscape is its own layout (left icon rail, two-column Chats, split Code, two-column
settings).

Not in the concept, to be drawn in its style:
- **Tailscale and PCs**: the drawer's top shows the Tailscale login (name, tailnet) and the PC list (this one ticked,
  each with online/offline and its models); the title bar's subtitle names the PC. Tapping a PC switches to it.
- **Signed-out / not on Tailscale** page: what to do (install Tailscale, sign in, open the PC's address).
- **Offline PC** and **connection lost** states (a banner over the screen, retrying).
- *(The user's decision)* The desktop's **Images** page, drawn in the concept's style: prompt and negative prompt,
  model (this PC's, the primary PC's, OpenRouter), size and aspect, steps, guidance, seed, the number of pictures, start
  image, the live preview, the queue with its per-picture cancel, and the gallery. In portrait a fifth tab; in landscape
  a rail entry.

## Approaches (decided)

1. **Separate web app beside the desktop one** (picked by the user) vs one web interface for both (the desktop window
   hosting the web app): one interface avoids building features twice but means rebuilding the desktop UI now.
2. **The concept's own files become the app** (picked) vs rebuilding the concept in a framework (Preact/Svelte with a
   build step): using the files guarantees the look and keeps the repo free of Node tooling; the cost is structuring a
   large plain-JavaScript app, met with ES modules and a small rendering helper.

## How it works

### Hosting and sign-in

- Each PC's desktop app serves the web app and its API on a **loopback** port (as `RemoteServer` does today: no URL ACL,
  no admin rights), and publishes it on the tailnet with **Tailscale Serve** (`tailscale serve --bg --https=443
  http://127.0.0.1:<port>`), run by the app when the web app is switched on. Tailscale gives the PC an HTTPS address
  (`https://<pc>.<tailnet>.ts.net`), which a phone browser needs for a home-screen app, the clipboard and the camera.
- **Identity**: Tailscale Serve adds `Tailscale-User-Login` to each request it proxies. The app lets in requests whose
  login is the PC's own Tailscale user (`tailscale status --json` → `Self.UserID` / the user's login), and refuses the
  rest (403). Requests from loopback without the header (another local program) are refused too. No password, no cookie
  for the tailnet path.
- Requirements the app checks and explains in Settings: Tailscale installed and signed in on the PC, MagicDNS and HTTPS
  certificates enabled in the tailnet (a one-time switch in the Tailscale admin console; the app links to it).
- A new switch in Settings > General: **Phone app over Tailscale**, with the PC's address and a QR code.

### PCs

- The serving PC lists peers from `tailscale status --json` (online, same user), and asks each `GET /api/hello` over its
  `ts.net` address (a short timeout); those that answer are the user's PCs with the app on. The list carries name,
  address, online, and the PC's models (loaded/available).
- *(Builder check, my call)* The app is installed to the home screen from one PC's address, and a **service worker**
  caches the app shell there, so the icon opens even when that PC is off. Every PC's API allows **cross-origin** calls
  from the user's other PCs' `ts.net` origins (CORS with credentials, each request still checked by its own PC's Tailscale
  identity), so switching PCs changes which API the page talks to, without leaving the page. The PC list and the last PC
  are remembered in that origin's storage.

### Data

- **One source of truth**: every phone action runs on the desktop's UI thread through the desktop's own code paths (as
  `RemoteAct` does today: open the conversation, put the text in its composer, `SendOrQueue`; Stop is `StopWork`;
  approvals resolve the open popup), so the desktop window shows the same state.
- *(The user's decision)* **The desktop follows the phone, without surfacing**: opening or sending to a conversation from
  the phone opens it in the PC's window too (the window has one open Chat and one open Code conversation), but a phone
  action never shows, restores or activates the window: hidden in the tray stays hidden, minimized stays minimized, and
  a window in the background stays behind. (Why not independent views: a PC's conversations are run by its window today,
  unlike the remote-PC setup, where the other PC runs its own app and borrows only the models; separate views would mean
  moving conversation running out of the window first, a large refactor before any phone work.)
- **API**: JSON over HTTPS, one area at a time (conversations, a conversation, send/stop/steer, attachments upload,
  approvals, settings read/write per page, workers and model settings, presets, skills, files, runs, images). Exact
  routes are left to the plan.
- **Live events**: one Server-Sent Events stream per open conversation (`GET /api/conversation/{id}/events`), carrying
  what the desktop's reply view consumes from the engine: text and thinking deltas, tool steps and results, usage and
  context, pictures, approvals, queue changes, done. Reconnects resume from the conversation's saved state, then the
  stream. Replaces today's once-a-second polling for the new app.
- **Pictures and files** are fetched from the PC by URL (`/api/file?...` limited to the conversation's pictures, the
  gallery, and the folders the conversation may read).
- *(Builder check, my call)* **Desktop pop-ups**: every `Choose(...)` question the desktop asks while the phone is
  connected is mirrored to the phone through one generic prompt (title, message, the same buttons, a nonce), answered from
  either side, first answer wins. Desktop dialogs that are not `Choose` (folder pickers, file pickers, downloads) get a
  phone-side flow when their feature is built (a folder browser on the phone for Code's folders); until then the phone
  says the PC is waiting on a dialog, as today's 409.
- *(Builder check, my call)* **Files**: the phone views files (browse, read, the editor preview, diffs of the model's
  changes); the model does the editing, as at the desktop. Editing by hand on the phone is not in this design.
- *(Builder check, my call)* **Settings are the PC's**: the settings pages read and write the PC's own settings, so a
  theme or accent picked on the phone changes the desktop too, and the web app follows the PC's theme and accent.
- *(Builder check, my call)* **Scope**: the concept sets the look; every desktop feature it lacks (Send now / steer,
  editing the queue, the items in Open questions) is drawn new in its style.

### Front end

- `web/` in the repo: `index.html`, `styles.css`, fonts, icons from the concept; the JavaScript split into ES modules
  (router, state per screen, the API client and event stream, a small `h()` element helper for lists and messages).
  No build step, no CDN, no framework. Embedded in the exe as resources (as `remote.html` is) and served from them.
- Demo data is replaced with live data screen by screen; a screen not wired yet shows the concept's layout read-only with
  a "coming soon" note rather than demo data.
- Portrait and landscape as the concept defines them, driven by the real orientation and width.
- A PWA manifest and icons so it can be added to the home screen and open full-screen.

## Order (the user's priorities)

1. **Foundation + Chat**: Tailscale Serve and identity, the PC list and switcher, the Chats screen (list, projects,
   recents, archived, search, filter), the Chat screen with live streaming, the composer and its `+` sheet (attachments
   from the phone, web search, tools, vision, image gen with the chat's model/size/steps, effort, worker, skills),
   context ring and popover, approvals, queue and stop, copy, drawer.
2. **Code**: folders and projects, Files (browse, view, edit preview), the terminal, the live run, run logs, the model's
   browser (its screenshots and tabs; input actions as on the desktop's browser pane).
3. **Images** *(placed here, my call: pictures were part of the user's day-one use)*.
4. **Models and Agents**: per-worker model settings for the three sources, presets, the GPU estimate, favorites.
5. **Settings and Skills**: General, Appearance (themes and accents apply to the web app too), Plugins, Remote API,
   Usage, About; Skills; polish.

Each step ships on its own, usable before the next starts.

## Constraints

- The desktop app must be running on a PC for the phone to reach it (Start with Windows, to the tray, exists for this).
- Windows 11 Home PCs; .NET 8 WPF app; the engine is `orchestrator.py`. Tailscale 1.104 on this PC.
- Commits go to master (the user's standing rule); the user approves each publish; the other PC updates by `git pull`.
- Tests: the app's `--self-test` and `--ui-smoke`; the web app gets its own checks (see below).

## Left to the builder

- Exact API routes and payloads; the module split of the JavaScript; the `h()` helper.
- How the SSE stream is fed from the desktop's event handling (one hub per conversation fan-out).
- Whether Tailscale Serve's identity header is double-checked with `tailscale whois` of the forwarded client address.
  The tailnet app runs on its own loopback listener, apart from the Cloudflare one, so a tunnel client can never send a
  `Tailscale-User-Login` header the app would trust.
- How the web app is tested: an API test through `--ui-smoke` against the loopback server (as the remote page's is), and
  a headless check of the front end against a fake API if it earns its keep.
- Caching headers, the PWA icon set, offline behaviour beyond the banner.

## Open questions

None left: the builder check's questions are answered above (the user's decisions and my calls are marked).
