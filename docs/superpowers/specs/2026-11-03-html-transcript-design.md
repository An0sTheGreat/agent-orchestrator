# HTML transcript: the conversation drawn by WebView2

## Goal

The conversation (the scrolling messages in the Chat and Code views) should look like Claude's, which is drawn by a browser: its text, emoji, spacing, Markdown, code and tables. WPF cannot match that closely: its text rendering, colour emoji and selection differ. The rest of the app (window, sidebar, composer, worker cards, settings, browser pane) stays WPF.

Success: a reply in the app and the same reply in Claude look alike side by side (fonts, sizes, colours, emoji, code blocks, tables); every transcript feature that exists today still works; the UI smoke and screenshots cover it.

## Architecture

- **One page per view.** The Chat view and the Code view each host one `WebView2CompositionControl` showing a bundled page (`transcript.html`, `transcript.css`, `transcript.js`, embedded in the exe). The composition control draws inside WPF's visual tree, so toasts, the Context pins, the jump pill and the image viewer still draw over it.
- **One container per conversation.** The page holds a container per conversation id and shows one. Parking a running chat (switching away while it streams) hides its container instead of swapping WPF panels; it keeps streaming.
- **C# stays the source of truth.** Event handling, history, saving, parking decisions and the engine protocol stay where they are. Where the code adds or changes a WPF transcript element today, it posts one JSON message to the page instead.
- **Offline and local.** Everything is served from the exe through `WebResourceRequested` on a private host (`https://ao.local/`): the page, the fonts already in `Fonts\`, a Markdown parser, and image files. A strict Content-Security-Policy allows nothing else. Images are served only for paths C# registered for the open conversation (attachments, pasted pictures, screenshots), so the page can't read arbitrary files.

## The bridge

C# → page (`PostWebMessageAsJson`), each with the conversation id:

| Message | What it does |
|---|---|
| `theme` | CSS variables from the current theme brushes, accent, reading size and fonts (sent on load and on every theme or reading-size change) |
| `show`, `clear` | show a conversation's container; empty it |
| `user` | a user message: text, photo URLs, time, and an id for Copy |
| `reply` | a new reply: its worker header |
| `delta` | streamed text for the open block: `answer`, `thinking` or a stage section (with its title) |
| `section`, `end-section` | open or close a collapsible section (thinking, delegate, stage); the fold rules of today |
| `tool` | a tool step: name, target, result (collapsible), and a screenshot thumbnail if any |
| `stats`, `live` | a call's stats line; the live tokens-so-far line |
| `status` | the reply's italic status line (loading, reading, writing a tool call, waiting for approval) |
| `final` | the authoritative reply text, replacing what streamed |
| `footer` | time, stats, and the reply's Copy |
| `note`, `compacted` | a muted or error note; the compaction line and rule |
| `run-block` | the Code view's project-run blocks (thinking, tool, response, note), as plain text |

Page → C# (`postMessage`): `copy` (text, so Copy uses the app's clipboard and toast), `link` (opens in the app's browser, as today), `image` (opens the existing full-size viewer), `follow` (whether the user is at the bottom, for the jump pill), `ready`.

## What the page does itself

- **Markdown** with a bundled parser (marked, MIT, ~40 KB). Raw HTML in model output is shown as text, never rendered. Headings are serif (Newsreader); body Inter; code JetBrains Mono. Sizes, line height (1.5), SemiBold bold and the spacing follow the current Comfortable/Compact/Large rules, now in CSS.
- **Streaming**: the typewriter reveal (the same pacing as today) and re-rendering only the unfinished tail, done in JS.
- **Code blocks**: the current syntax colours (the tokenizer is ported to JS), a copy button that stays visible while the block scrolls (CSS `position: sticky`).
- **Emoji**: Chromium draws Segoe UI Emoji in colour. The Emoji.Wpf package and the WPF emoji code go.
- **Selection**: text is selectable across messages, including messages with emoji.
- **Follow and jump**: the page keeps the view at the bottom while the user is there; scrolling up stops it, and `follow` tells C# to show the jump pill; the pill's click scrolls the page down.
- **Timestamps**: relative times ("3 minutes ago") refresh in the page; the exact time shows on hover.

## What stays WPF

The composer and its queue list, the Context ring/card/pins, the jump pill, toasts, the full-size image viewer, the approval dialog (already a dialog, not a transcript element), the sidebar, the worker cards and settings, the browser pane, and the remote-access page (separate, unchanged).

## Stages (each lands working, with its smoke checks, before the next)

1. **Foundation and Chat replies**: the control, page, host, CSP, theme variables, fonts; user bubbles with photos; finished replies (Markdown, code, tables, emoji, footer, Copy); Replay of saved conversations; image click → viewer.
2. **Streaming**: deltas, typewriter, sections and their folding, tool steps and screenshot thumbs, stats and live lines, status line, notes, compaction, steering, parking, follow/jump.
3. **Code view**: Code conversations through the same page, and the project run's blocks.
4. **Clean-up**: delete the WPF transcript code (`Markdown.cs`'s document builder, the RichTextBox and expander paths, Emoji.Wpf); rewrite the remaining smoke checks and the screenshot tool.

## Testing

- The UI smoke reads the page's state through `ExecuteScriptAsync` (a test hook returning JSON: messages, sections, footers, copy texts, computed styles) instead of WPF objects. About 20 transcript checks are rewritten; the rest are unaffected.
- A JS test of the Markdown rendering (escaped HTML, code, tables, lists, emoji) runs in the page from `--self-test`.
- Screenshots: the screenshot tool captures the page with `CapturePreviewAsync` (as it does the browser pane) if a WPF picture leaves it blank.

## Risks

- **WebView2 runtime**: already required by the browser pane; Windows 11 ships it.
- **Memory and start-up**: two more WebView2 pages (~60–100 MB, a fraction of a second at start); the pages load once and stay.
- **Composition control speed**: slower than the windowed control for very fast redraws; the typewriter is throttled to animation frames.
- **Size of the change**: stages 1–2 touch most of `MainWindow.Chat.cs`; each stage keeps the old path working until its own switch-over commit.
