# Share this PC's models with another PC (design)

The user's goal: use the app on a second PC, have the models run on the primary PC (the one with the GPU), and get the
output (replies, files, pictures) on the second PC. Option 3 from the 2026-11-19 discussion.

## Shape

- The app runs on BOTH PCs. The second PC's app does the work (projects, files and pictures are written there); only
  model inference happens on the primary PC.
- Network (revised 2026-11-19: Tailscale needs an admin prompt): **one Cloudflare quick tunnel**, no install, no admin.
  Both servers bind 127.0.0.1 only: llama-server on 8781, the app's ImageShare server on 8782. cloudflared (downloaded after
  asking, Authenticode-checked, as Remote access does; its own pid file `cloudflared-share.pid`) tunnels a random
  `https://<name>.trycloudflare.com` to 8782, the single front door: `/v1/*` is reverse-proxied to llama-server (the key is
  checked first, the request goes on unchanged but for `Connection: close`, the reply is piped back as it arrives so SSE
  streams), `/images/*` is served as below. **The address changes each time sharing starts**, so the other PC needs the new code.
- **Network choice** (added 2026-11-19, `AppSettings.ShareNetwork` = `tailscale` | `cloudflare`, "" until the user picks):
  "Network: Tailscale (private) / Cloudflare tunnel (any network)" under the switch. Unpicked, `ModelShare.Network` chooses
  Tailscale when the PC has a Tailscale IPv4 (100.64.0.0/10) at the moment sharing starts, else Cloudflare. On **Tailscale**
  no cloudflared runs; ImageShare binds the Tailscale IP on 8782 (llama-server stays on 127.0.0.1:8781 behind it, key still
  required) and the code is `ao-share://<tailscale-ip>?key=<key>`, a fixed address that survives restarts. No Tailscale IP:
  "Tailscale is not connected: sign in to Tailscale, or choose Cloudflare" and sharing goes off. Windows may ask once to let
  the app through its firewall. Switching the network while sharing restarts it. The other PC calls a Tailscale host at
  `http://<ip>:8782` (Tailscale encrypts the link), a tunnel host at `https://<host>`.
- One random **share key** (32 bytes, base64url) protects both servers (`Authorization: Bearer <key>`).
- A **connection code** carries the address and key in one string to paste on the second PC:
  `ao-share://<name>.trycloudflare.com?key=<key>`; the other PC accepts only a `*.trycloudflare.com` host or a canonical IPv4 in 100.64.0.0/10 (no other IP or host, no port).

## Primary PC: Settings > Models > "Share models with my other PCs"

- A switch, a state line, the connection code with Copy, and "New key" (signs other PCs out). Off by default.
- On: download cloudflared if missing (asks), start llama-server, ImageShare and the tunnel; the state line shows the tunnel
  (starting, online, failed with the reason).
- **Chat**: starts llama-server in router mode:
  `llama-server --host 127.0.0.1 --port 8781 --api-key <key> --models-preset <data>\share\models.ini --models-max 1`
  `models.ini` is written on each start from the local chat GGUFs (`LauncherLogic.FindLocalGgufs(ModelsFolder, HubCache())`,
  no mmproj, no image models): one section per model, `[<display id>]` with `model = <full path>` (plus `mmproj = <path>`
  when the model has a matching mmproj in its folder). The router loads a model on its first request and keeps one loaded.
- **Images**: an app-owned HTTP server (raw `TcpListener` + minimal HTTP/1.1 parsing; `HttpListener` on a non-loopback
  address needs a URL ACL, which needs admin) on `127.0.0.1:8782`, bearer key required (401 otherwise, constant-time compare):
  - `GET /images/models` -> `{"models":[{"id","name","note","negative","multiple","steps","cfg","sampler"}]}`: the
    primary PC's recipes and the image models in its Images folder (same list as its own Images page).
  - `POST /images/generate` body = `ImageSettings` JSON (RecipeId/LocalModel identify the model by the id from
    /images/models) -> runs sd-cli on the primary PC exactly as its Images page does (downloads missing parts, Low VRAM
    retry), returns `{"images":[{"png":"<base64>","seed":n}]}`; errors `{"error":"..."}` with 4xx/5xx. One generation at a
    time (409 while busy, also while the primary PC's own Images page generates).
- Both stop when the switch goes off or the app closes (by pid, never by name). Each process is restarted if it exits
  while sharing is on (backoff 5 s doubling to 1 min), like RemoteTunnel.

## Second PC: "Connect to another PC"

- Same Settings section: paste a connection code, Connect checks `GET <ModelShare.ChatUrl(host)>/models` with the key and
  stores the address and key (key in the existing API key store as `AO_SHARE_KEY`; never in settings.json or logs).
- **Chat**: the Remote API page gets a service "Primary PC" (`BaseUrl` = `ModelShare.ChatUrl(host)`, `KeyEnv AO_SHARE_KEY`,
  models listed from /v1/models), so any worker can run on a primary-PC model like any other remote API model.
- **Images**: the Images page source switch gets "Primary PC" beside Local and OpenRouter: its model picker lists
  `/images/models`; Generate posts to `/images/generate` and saves the returned PNGs (with their `.json` settings) in
  this PC's own gallery.

## Not in this version

Streaming image progress (the second PC shows an indeterminate bar), more than one key, a fixed Cloudflare address (a named tunnel needs a Cloudflare account; Tailscale gives one), files or projects on the primary PC.
