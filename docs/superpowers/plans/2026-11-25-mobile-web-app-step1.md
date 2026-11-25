# Mobile web app, step 1 (Foundation + Chat) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The user opens the app on their phone over Tailscale, sees their PCs, and chats on any of them (questions, research, web search, pictures), looking exactly like their concept.

**Architecture:** Each PC's desktop app serves the concept's files and a JSON + Server-Sent Events API on its own loopback listener (`WebServer`, apart from the Cloudflare `RemoteServer`), published on the tailnet with `tailscale serve`. Every request must carry Serve's `Tailscale-User-Login` header naming the PC's own Tailscale user. Actions run on the UI thread through the desktop's own paths (as `RemoteAct` does); engine events are copied out of `DrainLines` to the phone per conversation.

**Tech Stack:** .NET 8 WPF (`HttpListener`, `System.Threading.Channels`), the Tailscale CLI (via `Tailscale.cs`), plain ES-module JavaScript with no build step, `node --test` for the front end's pure modules.

**Spec:** `docs/superpowers/specs/2026-11-25-mobile-web-app-design.md` (approved). The concept: the user's `web-app-concept` folder (index.html, styles.css, app.js, assets/fonts), copied into `launcher/AgentOrchestrator/web/` in Task 1.

## Global Constraints

- The look is the concept's, exactly: its HTML structure, `styles.css`, fonts and icon sprite; portrait and its own landscape layout. Missing pieces are drawn in its style.
- Sign-in is Tailscale only on this path: a request is served only if `Tailscale-User-Login` equals the PC's own Tailscale login; no password, no cookie. Loopback requests without the header get 403.
- The tailnet app has its own loopback listener; the Cloudflare `RemoteServer` and `remote.html` are unchanged.
- One source of truth: phone actions run on the UI thread through the desktop's own code paths; the desktop window shows the same state.
- A phone action never shows, restores or activates the desktop window (hidden in the tray stays hidden, minimized stays minimized).
- No build step, no CDN, no framework; the web files are embedded resources of the exe.
- Screens not wired in step 1 (Code, Skills, Settings, Models, Images) show the concept's empty-state style with "Coming in a later update", never demo data.
- Commits go to master; never commit the repo-root `App.xaml.cs` or `requests.txt`.

## Review Focus

- The phone and the PC lose each other mid-reply (Wi-Fi switch, phone locked): the reply view recovers on reconnect from the saved conversation plus the live stream, with no duplicated or lost text. Test in Task 4 (`events_resume_after_reconnect`).
- A request reaches the loopback port without Serve (another local program, or Serve's header missing or naming another user): 403, nothing leaks. Test in Task 1 (`IsOwner` cases) and Task 3 (smoke: no header, wrong login).
- A large photo from the phone (10+ MB): uploads, attaches, and is refused clearly over the limit (25 MB). Test in Task 5.
- The desktop is hidden in the tray when the phone sends: the window stays hidden and the reply still streams. Test in Task 5.
- A desktop dialog is open at the PC when the phone acts: the phone shows the question (a Choose) or says the PC is waiting (any other dialog), never hangs. Test in Task 4 (mirrored Choose) and Task 5 (409 DialogOpen).

---

### Task 1: Web assets and the WebServer

**Files:**
- Create: `launcher/AgentOrchestrator/web/` — `index.html`, `styles.css`, `assets/fonts/*` copied from the concept; `index.html` without the demo frame (the phone drawn on a PC page, the jump list and Rotate); `js/` empty for now
- Create: `launcher/AgentOrchestrator/WebServer.cs`
- Modify: `launcher/AgentOrchestrator/AgentOrchestrator.csproj` (embed `web\**` with `LogicalName="web/%(RecursiveDir)%(Filename)%(Extension)"`, forward slashes)
- Modify: `launcher/AgentOrchestrator/App.xaml.cs` (`--self-test` calls `WebServer.SelfTest()`)

**Interfaces:**
- Produces:
  - `internal sealed class WebServer`
  - `public delegate Task<(int Status, object Body)> WebApi(string method, string path, IReadOnlyDictionary<string, string> query, byte[] body, string contentType)`
  - `public WebServer(Func<string?> ownerLogin, Func<string?> tailnetSuffix, WebApi api, WebEvents events)`
  - `public bool Start(int port)` (first free of 20 from `port`, `http://127.0.0.1:{p}/`), `public int? Port`, `public void Stop()`
  - `public static bool IsOwner(string? headerLogin, string? owner)` — true only when both are non-empty and equal, ignoring case
  - `public static string? AllowedOrigin(string? origin, string? suffix)` — the origin to echo in `Access-Control-Allow-Origin` when it is `https://<name>.<suffix>`, else null
  - `public static (string Resource, string Type)? Asset(string path)` — `/` → `web/index.html`; refuses `..`, backslashes and anything not embedded
  - `public static byte[] SseFrame(string json)` — `data: <json>\n\n` (UTF-8)
  - Routes (every path needs the identity header, assets included): `GET` assets; `OPTIONS` preflight answered with CORS headers; `/api/conversation/{id}/events` streams (Task 4); other `/api/*` go to `WebApi`, bodies up to 25 MB, JSON out.
  - Response headers as `RemoteServer`'s (CSP `default-src 'self'` plus `connect-src` for the tailnet origins, nosniff, no frames); `Cache-Control: no-store` on `/api/*`.

- [ ] **Step 1: Write the self-test** — `WebServer.SelfTest()` with checks:
  - `IsOwner("me@x.com","ME@x.com")`, not `IsOwner(null,"me@x.com")`, not `IsOwner("a@x.com","b@x.com")`, not `IsOwner("", "")`
  - `AllowedOrigin("https://laptop.tail1234.ts.net","tail1234.ts.net") == "https://laptop.tail1234.ts.net"`; null for `http://laptop.tail1234.ts.net`, `https://evil.com`, `https://x.tail1234.ts.net.evil.com`
  - `Asset("/")` is `web/index.html` text/html; `Asset("/styles.css")` is text/css; `Asset("/assets/fonts/Inter-Regular.ttf")` is font/ttf; null for `/../remote.html`, `/web/index.html`, `/x\y`, `/missing.js`
  - `SseFrame("{\"e\":\"delta\"}")` bytes equal `data: {"e":"delta"}\n\n`
- [ ] **Step 2: Run** `dotnet build -c Debug -nologo -v q` then the Debug exe `--self-test` hidden. Expected: FAIL (WebServer missing).
- [ ] **Step 3: Copy the concept into `web/`, embed it, implement `WebServer`** (HttpListener loop as `RemoteServer.Serve`; streaming responses via `SendChunked` for SSE).
- [ ] **Step 4: Run the self-test.** Expected: exit 0.
- [ ] **Step 5: Commit** `WebServer: the tailnet app's loopback server, its assets and identity gate`.

### Task 2: Tailscale Serve and the Settings switch

**Files:**
- Modify: `launcher/AgentOrchestrator/Tailscale.cs` (State gains `DnsName` — Self.DNSName without the trailing dot, `MagicDnsSuffix` — CurrentTailnet.MagicDNSSuffix, `CertDomains` — CertDomains; `ServeAsync`, `ServeOffAsync`)
- Create: `launcher/AgentOrchestrator/MainWindow.WebApp.cs` (start/stop, state, Settings glue)
- Modify: `launcher/AgentOrchestrator/Models.cs` (AppSettings `WebAppOn`, `WebAppPort = 8790`)
- Modify: `launcher/AgentOrchestrator/MainWindow.xaml` (Settings > General, under Remote access: **Phone app over Tailscale** switch, the address `https://<DnsName>/` with Copy and a QR code, a state line)

**Interfaces:**
- Consumes: `WebServer` (Task 1); `Tailscale.StatusAsync`, `Tailscale.State` (existing).
- Produces:
  - `public static Task<bool> Tailscale.ServeAsync(int port, CancellationToken ct)` — runs `tailscale serve --bg --https=443 http://127.0.0.1:{port}`, true on exit 0
  - `public static Task Tailscale.ServeOffAsync(CancellationToken ct)` — `tailscale serve --https=443 off`
  - `public static string? Tailscale.WebAppProblem(State? s)` — null when ready, else the user-facing reason: "Tailscale is not installed", "Tailscale is not signed in", "Turn on HTTPS certificates for your tailnet in the Tailscale admin console (DNS page)" when `CertDomains` is empty
  - `internal Task<bool> MainWindow.StartWebAppAsync()`, `internal Task MainWindow.StopWebAppAsync()`; `internal string? MainWindow.WebAppAddress` (null while off); the switch persists `WebAppOn`; the app starts it at launch when on; `StopWebAppAsync` on Quit.
  - Owner login and tailnet suffix captured from `StatusAsync` at start and refreshed every 5 minutes.

- [ ] **Step 1: Self-test** in `Tailscale.SelfTest()`: parse a status JSON fixture with `Self.DNSName "home.tail1234.ts.net."`, `CurrentTailnet.MagicDNSSuffix "tail1234.ts.net"`, `CertDomains ["home.tail1234.ts.net"]` → `DnsName "home.tail1234.ts.net"`, suffix, one cert domain, `WebAppProblem(s) == null`; the same without CertDomains → the HTTPS message; `WebAppProblem(null)` → "Tailscale is not installed".
- [ ] **Step 2: Run** the self-test. Expected: FAIL.
- [ ] **Step 3: Implement** the State fields, `ServeAsync`, `ServeOffAsync`, `WebAppProblem`, `MainWindow.WebApp.cs` start/stop and the Settings rows (the existing Remote access rows are the model for the address, Copy and QR).
- [ ] **Step 4: Run** the self-test. Expected: exit 0.
- [ ] **Step 5: Smoke** (`WebAppSmoke`, new, called before `ComputerSmoke`): with an injected owner `smoke@example.com` and suffix `tail0000.ts.net` and no real Serve (a `_webServe` hook replaces `ServeAsync`), turning the switch on starts the server on a free loopback port and sets `WebAppOn`; off stops it. Expected: passes.
- [ ] **Step 6: Commit** `Phone app over Tailscale: Serve lifecycle and the Settings switch`.

### Task 3: Read API — hello, PCs, conversations, files

**Files:**
- Modify: `launcher/AgentOrchestrator/MainWindow.WebApp.cs`

**Interfaces:**
- Consumes: `WebApi` (Task 1), `ConversationStore.View()`, `Get(id)`, `RunningId`, `ContextSnapshot` (existing).
- Produces (`WebAct` routes, all JSON):
  - `GET /api/hello` → `{ pc, dnsName, user, version }` (`pc` = `Environment.MachineName`)
  - `GET /api/pcs` → `[{ pc, dnsName, online, self }]` — `self` first; peers from `tailscale status --json` owned by the same user, each probed with `GET https://<peer DNSName>/api/hello` (3 s timeout); a peer that answers is `online: true`
  - `GET /api/conversations` → `{ projects: [{ id, name, items }], recents: [...], archived: [...] }` with items `{ id, title, kind, updated, busy, folder }` (from `View()`: Groups → projects, Ungrouped → recents, Archived)
  - `GET /api/conversation/{id}` → `{ id, title, kind, busy, queued, prompt, reply, context, messages }`; messages `{ role, text, time, stats, images: [url] }`; `context` = the saved or live `ContextSnapshot` as `{ nCtx, used, fraction, parts }`; `prompt`/`reply`/`queued` as `RemoteConversation` builds them
  - `GET /api/file?path=<full path>` → the bytes, only for files under `<DataRoot>\images`, `\pasted`, `\screenshots`; 404 otherwise
  - Image URLs are `/api/file?path=<escaped>`.

- [ ] **Step 1: Smoke** in `WebAppSmoke`: with a stored chat (two messages, one assistant image in `<DataRoot>\images`): `/api/conversations` lists it under recents; `/api/conversation/{id}` returns both messages and the image URL; that URL returns the picture's bytes; `/api/file?path=C:\Windows\win.ini` → 404; every route without the header → 403 and with `Tailscale-User-Login: other@example.com` → 403; `/api/hello` names this PC.
- [ ] **Step 2: Run** `--ui-smoke` hidden. Expected: FAIL at the new check.
- [ ] **Step 3: Implement** the routes.
- [ ] **Step 4: Run** `--ui-smoke`. Expected: passes past `WebAppSmoke` (a locked screen still fails later at `ComputerSmoke`; that is not this task).
- [ ] **Step 5: Commit** `Phone app: the read API`.

### Task 4: Live events and mirrored questions

**Files:**
- Create: `launcher/AgentOrchestrator/WebEvents.cs`
- Modify: `launcher/AgentOrchestrator/MainWindow.xaml.cs` (`DrainLines`: each line of a running turn or run, deltas included, also goes to `PublishWebEvent(RunningId, json)`)
- Modify: `launcher/AgentOrchestrator/MainWindow.Code.cs` (`Choose(title, message, options)` registers a remote prompt for `RunningId ?? ""` whenever the web app is on, not only the sandbox's)
- Modify: `launcher/AgentOrchestrator/MainWindow.WebApp.cs` (the events route; synthetic events)

**Interfaces:**
- Consumes: `WebServer.SseFrame` (Task 1).
- Produces:
  - `internal sealed class WebEvents` — `public ChannelReader<string> Subscribe(string conversationId, CancellationToken ct)`; `public void Publish(string conversationId, string json)`; each subscriber gets its own bounded channel (1,000 events; when full the oldest are dropped and a `{"e":"resync"}` is sent so the phone refetches)
  - `GET /api/conversation/{id}/events` — SSE; first event `{"e":"hello","busy":…}`, then the engine's events for that conversation as the desktop gets them, plus synthetic `{"e":"state","busy","queued","prompt"}` on each change and `{"e":"saved"}` when the turn's messages are saved; a comment line every 15 s keeps it open
  - `private void PublishWebEvent(string? conversationId, string json)` (no-op when the web app is off or id is null)

- [ ] **Step 1: Self-test** `WebEvents.SelfTest()`: two subscribers to `c1` both get `a` then `b` in order; a subscriber to `c2` gets nothing; a cancelled subscription stops; 1,001 publishes to a slow subscriber end with `resync` and the newest event.
- [ ] **Step 2: Smoke** in `WebAppSmoke` (`events_resume_after_reconnect`): open the events stream for a chat, run a turn with the remote smoke's engine stand-in (`RemoteSmoke`'s fake `orchestrator.py` pattern) that streams deltas and a final reply; the stream carries `hello`, the deltas in order, `saved`; reconnect mid-turn: `/api/conversation/{id}` plus the new stream together give the whole reply once. A `Choose` opened during the turn appears as `prompt` in a `state` event and `POST /api/answer` with its nonce resolves it.
- [ ] **Step 3: Run** both. Expected: FAIL.
- [ ] **Step 4: Implement** `WebEvents`, the hook, the route, the synthetic events and the `Choose` change.
- [ ] **Step 5: Run** both. Expected: pass.
- [ ] **Step 6: Commit** `Phone app: live events and mirrored questions`.

### Task 5: Sending — text, photos, composer options, stop

**Files:**
- Modify: `launcher/AgentOrchestrator/MainWindow.WebApp.cs`
- Modify: `launcher/AgentOrchestrator/MainWindow.Remote.cs` (only if `RemoteSend` needs a parameter for attachments; keep it working for `remote.html`)

**Interfaces:**
- Consumes: `RemoteSend`'s path (`OpenChatConversation`/`OpenCodeConversation`, `Attach(c, path)`, `SendOrQueue(c)`), `CurrentExtras`, the AppSettings toggles.
- Produces:
  - `POST /api/upload` (raw body, `X-File-Name`) → `{ path }` saved under `<DataRoot>\pasted` (25 MB cap → 413 "The file is over 25 MB")
  - `POST /api/send {id, text, attachments: [path]}` → `{ id, queued }` (id "" = a new chat); 409 `{error:"DialogOpen"}` while a desktop dialog the phone cannot answer is open; 400 for an empty message
  - `GET /api/composer` → `{ search, tools, vision, effort, worker, workers: [{role, name}], skills: [{name, on}], imageGen: {model, size, steps} }`; `POST /api/composer {…}` writes the same AppSettings the desktop composer does and calls `RefreshExtras()`
  - `POST /api/stop {id}`, `POST /api/answer {id, nonce, choice}` as `RemoteAct`
  - None of these show, restore or activate the window.

- [ ] **Step 1: Smoke**: hide the window (`HideToTray(quiet: true)` with a tray, or `Hide()`), then upload a small PNG, send `"describe this"` with it to a new chat through the stand-in engine: the chat is created, the user message carries the image, the reply arrives in the stream, and `IsVisible` is still false and `WindowState` unchanged; a 26 MB upload → 413; `POST /api/composer {search:false}` → `_settings.UseSearch` false and the desktop toggle unticked; sending while a `Choose` is not answerable → 409.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: pass.
- [ ] **Step 5: Commit** `Phone app: send, photos, composer options and stop`.

### Task 6: Front end — modules, Chats, drawer and PCs

**Files:**
- Create: `web/js/main.js` (router, screens, drawer, sheets — what the concept's `app.js` does, without its demo data), `web/js/h.js`, `web/js/api.js`, `web/js/markdown.js`, `web/js/chats.js`, `web/js/pcs.js`
- Create: `web/test/markdown.test.mjs`, `web/test/api.test.mjs`
- Modify: `web/index.html` (script `type=module src=js/main.js`; the drawer's top: Tailscale name and the PC list; title subtitles name the PC; the "Coming in a later update" empty states for unwired tabs; the signed-out page; the offline and connection-lost banners)

**Interfaces:**
- Consumes: the API (Tasks 3-5).
- Produces:
  - `h(tag: string, props?: object, ...children): Element` — text children are text nodes (never HTML)
  - `parseMarkdown(text: string): Block[]` (pure, Node-testable) and `renderMarkdown(text: string): DocumentFragment`; blocks: heading 1-3, paragraph, bullet and numbered lists, fenced code with language, table, blockquote; inline: bold, italic, inline code, links (http/https only)
  - `api.base` (the PC's origin), `api.get(path)`, `api.post(path, body)`, `api.upload(file): Promise<string>`, `api.events(id, onEvent): () => void` (EventSource, reconnects with backoff 1→15 s and calls `onEvent({e:"resync"})` after a reconnect); `parseSse(chunk: string): object[]` (pure)
  - `pcs.list(): Promise<Pc[]>`, `pcs.switchTo(pc)` (sets `api.base` to `https://<dnsName>`, remembers it in localStorage)

- [ ] **Step 1: Node tests**: `parseMarkdown("# T\n\nA **b** `c`\n\n- x\n- y\n\n```js\nlet a\n```")` gives heading(1,"T"), paragraph with bold and code runs, list of 2, code(js,"let a"); a table with a header row and 2 rows; `[x](javascript:alert(1))` stays plain text; `parseSse("data: {\"e\":\"a\"}\n\ndata: {\"e\":\"b\"}\n\n")` → two events; a split frame across two chunks is joined.
- [ ] **Step 2: Run** `node --test launcher/AgentOrchestrator/web/test`. Expected: FAIL.
- [ ] **Step 3: Implement** the modules and wire the Chats screen (projects, recents, archived, Chat/Code filter, search) and the drawer (Tailscale name, PCs with online state, tap to switch) to the API.
- [ ] **Step 4: Run** the Node tests. Expected: pass.
- [ ] **Step 5: Look**: serve the embedded app through the smoke's loopback server with the header injected by a dev flag (`--webapp-dev <port>`: loopback only, owner header added for 127.0.0.1 requests, never with Serve) and open it in the built-in browser at 390×844 and 844×390: Chats matches the concept's screenshots side by side.
- [ ] **Step 6: Commit** `Phone app: front end modules, Chats, drawer and PCs`.

### Task 7: Front end — the Chat screen

**Files:**
- Create: `web/js/chat.js`
- Modify: `web/index.html` only where the concept's Chat markup needs live hooks (ids, templates)

**Interfaces:**
- Consumes: `h`, `renderMarkdown`, `api` (Task 6); routes and events (Tasks 3-5).
- Produces: `openChat(id: string | null)`; renders messages (markdown replies, user photos, pictures, timestamps, copy), the live reply from events (deltas into the reply, thinking collapsible, tool rows, pictures as they arrive, delegation card), the context ring and popover from `context` (blue under 50%, amber from 50%, red from 90%), the queue chip, Stop, the approval card from `prompt` with its buttons, the pill composer that expands, the `+` sheet (photos from the phone, web search, tools, vision, image gen, effort, worker, this turn's skills) bound to `/api/composer`, and `/` skill completion from the skills list.

- [ ] **Step 1: Node test** for the pure reducer `applyEvent(state, ev)`: deltas append to the reply text in order; `thinking` deltas go to the thinking section; `tool` adds a row; `saved` sets `refetch: true`; `resync` sets `refetch: true`; `state` updates busy/queued/prompt.
- [ ] **Step 2: Run** `node --test`. Expected: FAIL.
- [ ] **Step 3: Implement** `chat.js` with `applyEvent` exported.
- [ ] **Step 4: Run** `node --test`. Expected: pass.
- [ ] **Step 5: Look** (as Task 6 step 5) with a real model on this PC: a question streams in, a web search runs, a picture is made and shows, Stop stops, the context ring fills; portrait and landscape match the concept.
- [ ] **Step 6: Commit** `Phone app: the Chat screen`.

### Task 8: Home screen app and switching PCs

**Files:**
- Create: `web/manifest.webmanifest`, `web/sw.js`, `web/icons/` (192, 512 px from `Assets/app.ico`'s artwork)
- Modify: `web/index.html` (manifest link, theme colour `#1E1E1E`), `web/js/main.js` (register the service worker; on start, the last PC from localStorage, else this one; offline PC → the offline banner and the PC list)
- Modify: `launcher/AgentOrchestrator/WebServer.cs` (CORS for the user's other PCs, Task 1's `AllowedOrigin`; `Access-Control-Allow-Credentials: false` — identity comes from Serve, not cookies)

**Interfaces:**
- Produces: the service worker caches the app shell (`/`, `index.html`, `styles.css`, `js/*`, fonts, icons) and never caches `/api/*`.

- [ ] **Step 1: Smoke**: `OPTIONS /api/hello` with `Origin: https://laptop.tail0000.ts.net` → 204 with that origin allowed; with `Origin: https://evil.com` → no allow header; `/manifest.webmanifest` served as `application/manifest+json`.
- [ ] **Step 2: Run.** Expected: FAIL.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run.** Expected: pass.
- [ ] **Step 5: Manual gate (the user, on the phone)**: Tailscale app signed in; open `https://<pc>.<tailnet>.ts.net/`, Add to Home Screen; the icon opens full screen; switch to the other PC from the drawer; turn one PC's app off: the banner and the PC list show.
- [ ] **Step 6: Commit** `Phone app: home screen app and switching PCs`.

### Task 9: Docs and publish

**Files:**
- Create: `docs/phone-app.md` (what it is, requirements: Tailscale on the phone and PCs, HTTPS certificates on, the app running — Start with Windows; how to add it to the home screen)
- Modify: `README.md` (one paragraph and the link), `docs/reference.md` (the routes and events)

- [ ] **Step 1: Write the docs.**
- [ ] **Step 2: Full runs**: `--self-test`, `--ui-smoke`, `node --test`, `python -m unittest test_orchestrator` (the engine is untouched; this confirms it). Expected: all pass (`ComputerSmoke` needs an unlocked screen).
- [ ] **Step 3: Commit** `Phone app: docs`, then publish with the user's OK (close with `--quit`).

---

## Later steps (each gets its own plan when its turn comes)

- **Step 2, Code**: folders and projects (a folder browser on the phone for adding folders), Files (browse, read, the editor preview, diffs of the model's changes), the terminal (output, and input as the desktop's), the live run and run logs, the TODO list, task and turn time, Start/Resume, the model's browser (screenshots, tabs, URL; input as the desktop's pane).
- **Step 3, Images**: the Images page drawn in the concept's style — prompt and negative prompt, model (this PC's, the primary PC's, OpenRouter), size and aspect, steps, guidance, seed, the number of pictures, start image, the live preview, the queue with per-picture cancel, the gallery; a fifth tab in portrait, a rail entry in landscape.
- **Step 4, Models and Agents**: per-worker model settings for Local, Remote PC and Remote API, presets (shared with the primary PC as today), the GPU estimate, favorites, the model picker, workers and Add agent.
- **Step 5, Settings and Skills**: General, Appearance (the PC's theme and accent, applied to the web app too), Plugins, Remote API, Usage, About; Skills; conversation management (rename, move, archive, delete); polish.
