# Phone app

Use Agent Orchestrator from your phone, over Tailscale. It is a web app that opens in the phone's browser and can sit on the home screen like an app. Every desktop feature is planned for it; the first step covers **Chats** and the **Chat** view. Your chats keep running on the PC.

## What you need

- Tailscale on the phone and on each PC, signed in to the same Tailscale account.
- HTTPS certificates and Serve turned on for your tailnet. If either is off, the line under the switch in Settings shows Tailscale's own message, with a link to enable it when Tailscale gives one.
- The desktop app running on each PC. **Settings > General > Startup > Start with Windows** keeps it running in the tray.

## Turn it on

1. On the PC, open **Settings > General** and switch on **Phone app over Tailscale**.
2. The address appears (`https://<this PC>.<your tailnet>.ts.net/`) with a **Copy** button and a QR code. Open it on the phone.

## Add it to the home screen

Open the address in the phone's browser, then use the browser's menu: **Add to Home screen** (Android) or **Share > Add to Home Screen** (iPhone). It then opens full screen with its own icon.

## Switch PCs

Open the drawer (the button at the top left of a page). It lists your other PCs that have the app on. Pick one to switch to it in place.

## Offline

The app itself is cached on the phone, so it still opens when the PC cannot be reached. A banner says so and lists your other PCs, so you can switch to one that answers.

## What the phone does to the desktop

The desktop follows the phone: opening a conversation or sending a message on the phone opens that conversation in the PC's window too. The phone never shows the window or brings it to the front. A window hidden in the tray stays hidden, a minimized one stays minimized, and one behind other windows stays behind.

## Security

- Only your own Tailscale login can open it. The PC checks the login Tailscale adds to each request and refuses anyone else; a phone signed in to another account sees a page saying so.
- There is no password.
- The API accepts calls only from the app's own page and from the pages of your own PCs (this is how switching PCs works). A page on any other site is refused.
- It is not exposed to the internet: the app listens on `127.0.0.1` only, and Tailscale Serve publishes it on your tailnet.

## Limits in step 1

- **Code**, **Models**, **Settings** and **Skills** show "Coming in a later update". Images arrives in a later update; step 1 has no Images view or tab.
- The first back gesture on a freshly opened page can close the app. This is a browser rule.

## For developers

The server is `launcher/AgentOrchestrator/WebServer.cs`, the routes and Settings switch are in `MainWindow.WebApp.cs`, and the front end is the embedded `launcher/AgentOrchestrator/web/` folder (plain JavaScript modules, no build step). The routes and live events are listed in [the reference](reference.md#phone-app-api).

- `--webapp-smoke`: runs only the phone app's checks in the app's window (exit code 0 on success).
- `--webapp-dev <port>` (dev only): serves the app at `http://127.0.0.1:<port>/` for a browser on the same PC, taking every local request as the owner's, with fake Tailscale data, seeded sample chats and a temporary data folder. It never runs `tailscale serve` and saves no setting.
- Front-end tests: `node --test "launcher/AgentOrchestrator/web/test/*.test.mjs"`
