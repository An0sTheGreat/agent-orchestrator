# Local API for other apps on this PC (design)

## Intent

The user is designing another app on the same PC. It should call Agent Orchestrator the way an app calls OpenRouter: an
OpenAI-style HTTP API with a key, except everything stays on this PC. AO does the work with its own model settings (which
model each worker runs, local or remote, its API key, its sampling) and sends the answer back. The other app keeps no model
settings or provider keys of its own; changing a worker's model in AO changes what the app gets, with no change in the app.

## Goals

- Any OpenAI client library works by setting `base_url = http://127.0.0.1:<port>/v1` and `api_key = <AO's local API key>`.
- `model` in a request is an AO **worker name**: a built-in role (`planner`, `implementer`, `reviewer`) or a custom
  worker's id, also accepted by its display title (case-insensitive).
- Chat completions, streaming included, with tool calls, usage and the stream format exactly as the upstream server sends them.
- Image generation with AO's local image models.
- A Settings section to switch the API on, see its address, copy the key, and make a new key (which revokes the old one).

## Non-goals

- No AO system prompt, skills, tools or conversation history are added: the app sends its own messages, as to OpenRouter.
- Calls do not appear in AO's conversation list or the Images gallery.
- No access from other PCs, the phone or the tailnet (Share models already covers other PCs).
- No `/v1/completions`, embeddings, audio, or a model id that is not a worker (decided: worker names only).
- No per-app keys or one-time pairing codes: one key, shown in Settings.

## Anti-goals (a failure even if it works)

- The API is reachable from anything but this PC, or answers without the key.
- A provider API key (OpenRouter's, etc.) leaves AO in any reply, error or log.
- A call opens a dialog on the desktop or changes the user's settings (the Images page's settings, worker settings).
- A call from the app unloads the model a running AO chat or Code turn is using.

## Approaches considered

- **A. C# does everything** (reads the worker's settings, forwards to its URL or llama-server). Rejected: loading local
  models lives in the Python engine (`llama_ensure`, multi-model policy, stale-copy handling); a C# copy would drift.
- **B. Engine per call** (spawn `orchestrator.py` per request like a chat turn and translate its events to OpenAI chunks).
  Rejected: a lossy translation (tool calls, usage, stream shape differ from a real OpenAI API) and Python startup per call.
- **C. Pass-through (picked by the user).** The engine only makes the worker ready and reports where it is served; AO then
  forwards the app's request to that server and pipes the reply back unchanged.

## Design

### Server

- New `LocalApi.cs` (the pure parts plus the listener) and `MainWindow.LocalApi.cs` (routes that need the window), in the
  style of `WebServer.cs`: `HttpListener` on `http://127.0.0.1:<port>/` only.
- Fixed port, default **8830** (`AppSettings.LocalApiPort`, settings.json only, not shown in the UI), not "first free": the
  app's config must not change under it. (8790-8809 is the phone app's `WebAppPort` range, 8781/8782 Share models.)
  Port taken: the state line says so and the API stays off.
- Every request: the remote endpoint must be loopback (as `WebServer`), the `Host` header must be `127.0.0.1:<port>` or
  `localhost:<port>` (DNS rebinding), then `Authorization: Bearer <key>` compared with `CryptographicOperations.FixedTimeEquals`
  over the UTF-8 bytes. Failures: 401 `{"error":{"message":"…","type":"invalid_request_error"}}` (OpenAI error shape) for a
  missing or wrong key, 403 for a bad host or remote. The body is not read before the key passes. Body limit 25 MB (images in messages).
- No CORS headers (the app is a desktop app; a browser page cannot read replies). Easy to add later if the app is a web page.
- Never shows a dialog; never touches `_settings` except its own fields.

### Key and settings

- Key: `ModelShare.NewKey()` (32 random bytes, base64url), stored with `ApiKeys` under `AO_LOCAL_API_KEY` (DPAPI, `keys.json`)
  but **not exported to the process environment** by `ApiKeys.ApplyAll` (child processes, the engine's `run_command`, the
  terminal must not inherit it). Created the first time the API is switched on.
- `AppSettings.LocalApi` (bool, off by default) and `LocalApiPort` (int, 8830).
- Settings > Models, a section after "Share models with my other PCs": title **"Local API for my apps"**, a switch
  ("Let apps on this PC use AO's workers and image models"), a state line (`Listening on http://127.0.0.1:8830/v1` /
  `Port 8830 is in use by another program` / off), a read-only box with the key, **Copy**, **New key** ("New key signs
  your apps out"), and one muted line: base URL, key, and `model` = a worker name.

### Routes

- `GET /v1/models`: `{"object":"list","data":[{"id","object":"model","owned_by":"agent-orchestrator","name","kind"}]}`:
  each worker AO's phone app lists (`/api/workers`, the same list, hidden ones included) (id = role/custom id, `name` = its title, `kind:"chat"`), then each local image model AO's
  Images page offers (`kind:"image"`, id as `ImageShare.Models` gives it).
- `POST /v1/chat/completions`: pass-through, below.
- `POST /v1/images/generations`: below.
- Anything else: 404 in the OpenAI error shape.

### Chat pass-through

1. Parse the body as a JSON object; `model` must name a worker (400 `model_not_found`-style error listing the worker ids otherwise).
2. Ask the engine for the worker's **target**: a new `orchestrator.py --api-target <role> [--only-loaded]` (same config
   folder and runtime environment as `--load-role`: `PrepareChatFolder` + `ApplyRuntime`). It runs `role_settings` and
   `ensure_model` (loading a local model on AO's own llama-server exactly as a chat would), then prints one JSON line:
   `{"base_url","model","key_env","defaults"}`. `key_env` is the variable **name** (empty for local); never the key.
   `defaults` is the worker's sampling turned into request fields the way `ApiClient.complete` does (factor that part
   out into one helper both use): `extra_body` flattened, `reasoning_budget`, local-only fields dropped for a remote API
   unless `send_extra_body`, `enable_thinking`/`reasoning_effort` moved into `chat_template_kwargs` for llama-server,
   OpenRouter's `reasoning: {effort: none}` for thinking off. Errors: exit 1 with `ERROR: …` as today.
3. **Busy rule.** "Busy" = `ModelsWorking` (a Chat or Code turn, desktop or phone, or a project run) or `_loadingRole`
   set. While busy, C# passes `--only-loaded`: the engine returns a running, healthy llama-server whose args can serve
   the worker (exactly the reuse branch of `llama_ensure`, `llama_compatible`), which may be the very server the turn is
   using (llama-server queues or runs the requests in parallel slots), and otherwise fails without loading or stopping anything; C# answers **503** `"AO is busy with another model; retry when its turn ends"`
   with `Retry-After: 10`. Remote workers never need it. Target calls are serialized (one `SemaphoreSlim`) so two app
   calls never load at once. When not busy, the load goes as Load models does: `FreeGpuForChat()` first, then the engine.
   A model the API loads is an ordinary AO-loaded server (in the engine's server state): AO's multi-model policy may
   replace it on the next chat turn (an app stream then fails), and it is unloaded on exit as today.
   While an app's chat call to a local worker streams, `ChatModelsInUse` is true, so a picture does not send that model off the GPU.
4. Forward with `HttpClient` (`HttpCompletionOption.ResponseHeadersRead`; `timeout_seconds` caps the wait for the
   response headers, a streaming body has no overall cap; `--api-target` is capped at 10 minutes, so a cold load can
   outlast a client's own timeout, which is the client's to set): `POST {base_url}/chat/completions`, body = `defaults` overlaid by the app's fields (the app's win),
   `model` replaced by the target's model id; `Authorization: Bearer <Environment value of key_env>` for remote,
   `no-key` for local. The status, `Content-Type` and body stream back as they arrive (SSE unchanged, flushed per read).
   A non-2xx body is relayed with the key text replaced by `***`. The app hanging up cancels the upstream request.
5. Reply bodies are not rewritten (the `model` field shows the real model id).

### Images

- Body (OpenAI shape): `prompt` (required), `model` (optional: an image model id from `/v1/models`; default the Images
  page's current local model), `n` (1 to `ImageGen.MaxBatch`, default 1), `size` (`"WxH"`, default the Images page's),
  `response_format` (`b64_json` only; `url` → 400), plus AO extras `negative_prompt`, `steps`, `cfg_scale`, `seed`.
- Runs through `GenerateForShareAsync`'s path (temp folder, deleted after; the page's settings untouched; the job shows
  on the Images page's status line as "Generating for an app…"). It frees the GPU as the Images page does
  (`FreeGpuForImageAsync`, which already leaves chat models alone while they are in use). While a picture is being made, the call **waits** its turn
  (up to the client's patience; the app hanging up gives up its place) instead of 409.
- Reply: `{"created": <unix>, "data": [{"b64_json": "<png>", "revised_prompt": null, "seed": <n>}]}`.
- Local image models only; the Images page's OpenRouter model and a primary PC's shared models are not offered.

### Tests

- `--self-test` (`LocalApi.SelfTest`): key check (right, wrong, missing, wrong length, constant-time helper), host check,
  OpenAI error shape, worker-name resolution (id, title, case), body merge (app's fields win, `model` replaced), size parsing,
  key scrubbing in a relayed error.
- `--webapp-smoke`-style smoke: start the listener on a free port with a fake upstream (a local `HttpListener` acting as
  llama-server, streaming SSE) and a fake target resolver; check 401 without a key, 200 `/v1/models`, a streamed chat
  completion arrives chunk by chunk unchanged with the key header sent upstream, 503 under the busy rule, images happy
  path with a fake generator, and a non-loopback `Host` refused.
- Python: a unit check for `--api-target`'s JSON (no key value in it) and `--only-loaded` refusing to load.

### Docs

- `docs/reference.md`: a "Local API" section: switching it on, base URL, key, `model` = worker name, the three routes,
  the busy rule, image request fields, and that keys never leave AO.

## Left to the builder

- New key: later requests need the new key; requests already past the check finish.
- A worker with no model, or whose engine target fails: 400 with the engine's `ERROR:` text (keys never appear in it).
- Exact factoring of the sampling-to-request helper in `orchestrator.py`, and of the image path shared with `GenerateForShareAsync`.
- Whether `--only-loaded` is a flag on `llama_ensure` or a separate check; it must never stop or start a server.
- Start/stop wiring (on app start when `LocalApi` is on, on the switch, on exit), following `MainWindow.Share.cs`.

## Calls made by Claude (not decided by the user)

- One key, no one-time pairing code; port 8830 fixed; the busy rule above; models the API loads follow AO's normal unload policy; no CORS; images not saved to the gallery; no AO system prompt;
  local image models only; an image call waits for a running picture instead of failing; the key is kept out of the
  process environment.
