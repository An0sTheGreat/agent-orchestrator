# Remote access (design)

Use the app from a phone or another computer through a browser, from anywhere.

## Decisions (from the user)

- Browser page only: no remote mode in the desktop app.
- Reached through a Cloudflare quick tunnel (`cloudflared`), as Unsloth Studio does: nothing to install on the other
  device, HTTPS from Cloudflare. No Tailscale, no LAN mode, no port forwarding.

## Shape

- **Server** (`RemoteServer.cs`): `HttpListener` on `http://127.0.0.1:<port>/` only (port 8765 by default, a free one if
  taken). Loopback needs no admin rights or URL ACL, and only the tunnel can reach it from outside.
- **Tunnel** (`RemoteTunnel.cs`): runs `cloudflared tunnel --url http://127.0.0.1:<port> --no-autoupdate --http-host-header 127.0.0.1:<port>`, reads the
  `https://*.trycloudflare.com` address from its output, restarts it if it exits while remote access is on, and kills it
  (by pid) when access is turned off or the app closes. `cloudflared.exe` lives in `<DataRoot>\tools`; when missing, the
  app asks before downloading it from Cloudflare's GitHub releases (`cloudflared-windows-amd64.exe`).
- **Settings → General → Remote access**: a switch, the password, the current address with Copy, a QR code of it
  (QRCoder package), and the state (starting, online, failed with the reason). The address changes on every start.
- **Login**: a password the user sets (stored as a PBKDF2 hash, never plain). Remote access cannot be turned on without one.
  A correct login sets a random session cookie (HttpOnly, Secure, SameSite=Strict) kept in memory; changing the password
  or turning access off signs every device out. Wrong passwords: after 5 failures, each further try waits longer
  (doubling, up to 5 minutes), counted globally because every request arrives from 127.0.0.1. Every `/api` call except
  login needs the cookie; the page and its assets are served without it.
- **Page** (`remote.html`, an embedded resource, vanilla JS, no CDN): login; the conversation list with New chat; a
  conversation with its messages (plain markdown: paragraphs, code blocks, inline code, bold, lists), the reply in
  progress with its status, a composer with Send (queues when busy, as the desktop does) and Stop; and any pending
  request popup (a command or folder permission) with the same choices the desktop shows. Follows the app's theme
  colours. Works at phone width. Polls about once a second while a conversation is open.
- **API** (JSON):
  - `POST /api/login {password}`
  - `GET /api/conversations`: id, title, kind (chat or code), updated, busy
  - `GET /api/conversation/{id}`: title, kind, messages (role, text), reply in progress (text, status), busy, queued,
    pending prompt (title, message, choices)
  - `POST /api/send {id?, text}`: no id starts a new chat
  - `POST /api/stop {id}`, `POST /api/answer {id, choice}`
- **Driving the app**: every remote action runs on the UI thread through the desktop's own paths: open the conversation,
  put the text in its composer, `SendOrQueue`; Stop is `StopWork`; an answer resolves the open request popup as if
  clicked. So queueing, Code turns, permissions and history behave exactly as at the PC.

## Not in the first version

Attachments and images, settings and model changes, Code folder grants, renaming or deleting conversations, more than
one password or per-device sign-out, a fixed address (a named tunnel with the user's own domain).

## Testing

- `--self-test`: password hashing, the lockout timing, the trycloudflare address parser, request routing and auth.
- `--ui-smoke`: start the server on a free port, log in, list conversations, send to a chat with a fake engine and read
  the reply back, answer a fake request popup, and check a request without the cookie gets 401. The tunnel itself is
  not started in tests (it needs the network); its process handling is tested with a fake `cloudflared` that prints an
  address.
