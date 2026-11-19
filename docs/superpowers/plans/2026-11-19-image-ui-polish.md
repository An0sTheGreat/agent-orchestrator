# Images Page UI Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** (1) The OpenRouter image-model picker becomes a dropdown attached to its button, shows each model's price in the list, and closes on any outside click; the provider popup's prices get correct units. (2) The positive/negative prompt boxes (local and OpenRouter modes) get a maximum height with a fading bottom edge and an expand/collapse button when the text is taller.

**Architecture:** Task 1 adds a one-request price lookup to `RemoteCatalog.cs` (OpenRouter's `GET /api/v1/models?output_modalities=image`, joined to the image-model list by id) plus a unit-aware price formatter, and replaces the model `DialogShell` in `MainWindow.ImageGen.cs` with a `Popup` anchored to the model button, closing technique copied from `MainWindow.ModelPicker.cs`. Task 2 wraps the two prompt `TextBox`es in a max-height container with a bottom fade and a toggle button.

**Tech Stack:** C#/.NET 8, WPF (existing launcher). No new dependencies.

**Spec:** the user's request (this conversation, 2026-11-19) and the facts verified live against OpenRouter below; no separate design doc (the repo's lightweight pattern, as agreed for the previous Images change).

## Verified facts the plan rests on (live OpenRouter checks, 2026-11-19)

- `GET /api/v1/images/models` has NO price fields (keys: architecture, created, description, endpoints, id, name, supported_parameters, supports_streaming).
- `GET /api/v1/models?output_modalities=image` (public, one request, 74 entries incl. `:free` variants) carries `pricing` per model, in mixed units: `"image": "0.008"` (USD per image) on some (e.g. `bytedance-seed/seedream-5-5`); otherwise only per-image-TOKEN prices `"image_output"` / `"image_token"` (USD per token, e.g. `microsoft/mai-image-2.7-flash`: `0.00001`). Models with token pricing cannot be converted to a per-image price without a token count.
- Per-provider `GET /images/models/{id}/endpoints` `pricing` is a list of `{billable, unit, cost_usd[, variant]}` where `unit` is `"image"` or `"token"` (e.g. `microsoft/mai-image-2.7-flash` azure: `output_image` unit `token` 1e-05; `recraft/recraft-v4.1-flash-v2`: `output_image` unit `image` 0.007; `bytedance-seed/seedream-5-5`: two `output_image` entries, the second with `variant: "high_resolution"`).
- **Latent defect in the shipped feature (found while researching this request):** `ParseImageProviders` takes the first `output_image` entry's `cost_usd` and the provider popup renders it with `RemotePrice` (a per-million formatter) under the heading "USD per image" — wrong for `unit: "token"` entries (a $0.00001/token price shows as "<$0.0001" "per image"). Task 1 fixes this.

## Global Constraints

- OpenRouter only; key env var via the existing `ImageKeyEnv` lookup (`MainWindow.ImageGen.cs`).
- Reuse `_remoteHttp` for every HTTP call.
- Do not change the local sd-cli path, `GenerateImagesAsync`, or the provider-popup layout beyond price formatting.
- A failed price lookup must never block choosing a model (prices show "—").
- Everything must work in all themes (use `SetResourceReference`/`DynamicResource` brushes, never hard-coded colours, for new chrome).
- The full `--ui-smoke` fails at baseline at `MainWindow.xaml.cs:1407` (unrelated, pre-existing); the Images smoke block is verified via a throwaway scratch run (copy of the Images block placed right after the first `OpenModelSettings(); await Settle(); ShowView();` in `RunUiSmokeAsync`, never committed, restored with `git checkout --` afterwards).

## Review Focus

- A price-lookup failure or a model with no price entry: list still works, price shows "—". (Task 1)
- A token-billed model/provider never displays as a per-image price. (Task 1)
- Clicking anywhere outside the dropdown closes it, including while the filter box has focus; picking a model closes it; reopening works (no "just closed" race that makes the button seem dead). (Task 1)
- Prompt text that is exactly at the limit, text that shrinks back under it (button disappears, fade disappears), very long single-line text without newlines, and the box becoming visible/hidden (OpenRouter mode hides the negative row) without leaving a stale toggle. (Task 2)
- Expanded state survives switching Local ↔ OpenRouter and applying settings without surprising resets. (Task 2)

---

## Task 1: Model dropdown with prices; correct provider price units

**Files:**
- Modify: `launcher/AgentOrchestrator/RemoteCatalog.cs`
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`ChooseImageModel`, `ChooseImageProviders`' price cell, the model button in `BuildImagesPage`/`ShowImageSource`)
- Modify: `launcher/AgentOrchestrator/MainWindow.xaml.cs` (the Images smoke block: the model-popup lookups move from the `DialogShell` hook to the new popup hook)
- Modify: `launcher/AgentOrchestrator/MainWindow.RemoteApi.cs` (`RemoteApiFakeHandler`: answer `GET .../models?output_modalities=image` with a small fixture including one `image`-priced model, one token-priced model, one with no price)

**Interfaces:**
- Produces (RemoteCatalog): `public sealed record ImagePrice(decimal? PerImage, decimal? PerMillionTokens)`; `public static Dictionary<string, ImagePrice> ParseImagePrices(string json)`; `public static async Task<Dictionary<string, ImagePrice>> FetchImagePricesAsync(HttpClient http, string? apiKey, CancellationToken ct)` (URL `https://openrouter.ai/api/v1/models?output_modalities=image`); `public static string ImagePriceText(ImagePrice? price)`; and `ImageProviderRow` gains a trailing `string Unit = "image"` (new trailing parameter with a default, so existing positional callers stay valid) filled from the chosen `output_image` pricing entry's `unit`; `public static string ImageCostText(decimal? cost, string unit)`.
- Consumes: existing `RemoteCatalog.GetAsync`, `Reply`, `Str`, `PerUnit`; `ImageKeyEnv`, `RemotePrice`, `RemoteMuted`, `FindResource("GhostButton")`; the click-outside technique in `MainWindow.ModelPicker.cs` (`ModelPicker` class, ~:73-195: a `Popup` with `PlacementTarget = anchor`, `Placement = Bottom`, `StaysOpen = false`, plus a window-level `PreviewMouseDown` handler and a `JustClosed` guard, because `StaysOpen=false` alone misses presses while the search box holds the mouse). Read it before writing the popup; mirror the technique, do not reuse the class (it is built for local GGUF files).

- [ ] **Step 1: Write the failing self-test checks in `RemoteCatalog.SelfTest()` (follow its `Check(bool, string)` style)**

`ParseImagePrices` on `{"data":[{"id":"a/img","pricing":{"prompt":"0","completion":"0","image":"0.008","image_output":"0.00001"}},{"id":"b/tok","pricing":{"prompt":"0","completion":"0","image_output":"0.00001"}},{"id":"c/none","pricing":{"prompt":"0","completion":"0"}},{"id":"d/free:free"}]}` → `a/img` = `(0.008m, null)`; `b/tok` = `(null, 10m)` (0.00001 × 1,000,000); `c/none` and `d/free:free` absent or `(null, null)`. `ImagePriceText(new(0.008m, null)) == "$0.008 / image"`, `ImagePriceText(new(null, 10m)) == "$10 / M tokens"`, `ImagePriceText(null) == "—"`. `ParseImageProviders` on the two shapes (`unit: "image"` 0.04 and `unit: "token"` 1e-05) → rows with `Unit == "image"`/`"token"` and `CostPerImage` unchanged; `ImageCostText(0.04m, "image") == "$0.04 / image"`, `ImageCostText(0.00001m, "token") == "$10 / M tokens"`, `ImageCostText(null, "image") == "—"`. Add a fake-`HttpClient` case for `FetchImagePricesAsync` asserting the exact URL and a successful round trip (extend the existing `FakeHandler` the way Task 1 of the previous change did for the image GET endpoints).

- [ ] **Step 2: Run and confirm it fails to compile**

Run: `dotnet build launcher/AgentOrchestrator`
Expected: errors naming the new types/functions.

- [ ] **Step 3: Implement the RemoteCatalog additions**

`ParseImagePrices`: for each `data[]` object with a non-empty `id`, read `pricing.image` (USD per image, `PerUnit`) and, when absent or zero, `pricing.image_output` falling back to `pricing.image_token` (USD per token → `PerMillionTokens = PerUnit(..) * 1_000_000m`); a zero or missing value is "no price". Reuse `Reply`'s error wrapping like the sibling parsers. `ImagePriceText`/`ImageCostText` format via the existing `RemotePrice` conventions (`$` + `0.00##`, `<$0.0001` floor) followed by ` / image` or ` / M tokens`; null/no price → `"—"`. In `ParseImageProviders` capture the picked entry's `unit` (default `"image"` when absent).

- [ ] **Step 4: Extend the Images ui-smoke block (written to fail first), then implement the dropdown**

Smoke changes (Images block in `MainWindow.xaml.cs`, "OpenRouter image models" section): the model popup is now a `Popup`, not a `DialogShell` — add a test hook for it (e.g. `_imgModelPopupTest`, mirroring how `_imgDialogTest` works for the provider dialog) and update the existing lookups. Assert: opening it leaves the model button's `Content` unchanged and shows a child tagged `imgmodel:<id>` per fixture model; each row shows its price text (fixture: `"$0.008 / image"` for the image-priced model, `"$10 / M tokens"` for the token-priced one, `"—"` for the unpriced); the empty-id entry is still skipped; picking a row closes the popup and sets `ReadImageSettings().RemoteModel`; a simulated press elsewhere in the window (`RaiseEvent` of a `PreviewMouseDown` on the window or a control outside the popup) closes it; reopening immediately after a pick still opens (JustClosed race). Also assert the provider popup shows `"$0.04 / image"` and `"$10 / M tokens"` for the two unit fixtures (extend the fake provider fixture with one token-billed endpoint).

Implementation: `ChooseImageModel` builds a `Popup` anchored under `_imgRemoteModelButton` (`PlacementTarget`, `Placement = Bottom`, small `VerticalOffset`, width at least the button's `ActualWidth`, `StaysOpen = false`, `AllowsTransparency = true`, card styling via `SetResourceReference` brushes like `ModelPicker`'s card). Contents: the filter `TextBox` (Tag `ImagesModelFilter`, focused on open), a status line, a scrolling list; each row (Tag `imgmodel:<id>`) shows name + muted id on the left and the price text right-aligned (muted). Fetch `FetchImageModelsAsync` and `FetchImagePricesAsync` together (`Task.WhenAll`); a prices failure is swallowed into "—" prices, a models failure shows the existing "Could not load the models" status. Escape closes; Up/Down/Enter keyboard nav is optional (do it only if `ModelPicker` makes it cheap). Closing: the window-level `PreviewMouseDown` + `JustClosed` guard technique from `ModelPicker`; unsubscribe the window handler when the popup closes. Give the model button a trailing `▾` so it reads as a dropdown (`ShowImageSource` sets its `Content`; keep the "Choose a model…" text the smoke already asserts or update that assertion consistently). Remove the model `DialogShell` and its `ShowImagePopup` use (the provider popup keeps `ShowImagePopup`). In `ChooseImageProviders` replace the `RemotePrice(c)` cost cell with `RemoteCatalog.ImageCostText(r.CostPerImage, r.Unit)` and fix the status line wording (not "USD per image" blanket).

- [ ] **Step 5: Run the checks**

Run: `dotnet build launcher/AgentOrchestrator` (0 errors); `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --self-test` (exit 0); the scratch Images-only `--ui-smoke` (exit 0; first RED against the unmodified code at the new assertions, then GREEN; plus one deliberate sabotage, e.g. make the outside-click handler a no-op, confirming the block then fails at the close assertion; undo it).

- [ ] **Step 6: Commit**

Message in a plain file, then `git commit -F <file>`; trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

---

## Task 2: Prompt boxes: maximum height, fading bottom edge, expand/collapse

**Files:**
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`BuildImagesPage`: the `_imgPrompt` and `_imgNegative` construction and their containers; `_imgNegativeRow` stays the negative row's container)
- Modify: `launcher/AgentOrchestrator/MainWindow.xaml.cs` (the Images smoke block)

**Interfaces:**
- Consumes: `_imgPrompt` (Tag `ImagesPrompt`, today `MinHeight = 80`, `AcceptsReturn`, `TextWrapping.Wrap`, `VerticalScrollBarVisibility.Auto`, no max), `_imgNegative` (`MinHeight = 44`, inside `_imgNegativeRow` tagged `ImagesNegative`). Existing readers/writers of `.Text` (`ReadImageSettings`, `ApplyImageSettings`, the recipe-change handler, "Use these settings") must keep working unchanged — the controls stay the same `TextBox` objects.
- Produces: tags `ImagesPromptToggle` and `ImagesNegativeToggle` (the expand/collapse `Button`s), and an internal helper (e.g. `AttachPromptFade(TextBox box, double collapsedMaxHeight, string toggleTag) -> FrameworkElement`) that returns the container to put in the page in place of the bare `TextBox`.

Applies to both the Local and OpenRouter modes: the prompt boxes are shared controls (the negative row is simply hidden in OpenRouter mode by `ShowImageSource`), so one implementation covers both.

- [ ] **Step 1: Extend the Images ui-smoke block (written to fail first)**

With `Settle()` between steps so layout runs: short prompt → `ImagesPromptToggle` not visible and the box's height equals its natural height; set `_imgPrompt.Text` to ~40 lines → the toggle is visible with "Show more"-style content, the box's `ActualHeight` ≤ the collapsed maximum (and the fade overlay/mask is present), `ExtentHeight > ViewportHeight`; click the toggle → `ActualHeight` greater than the collapsed maximum (up to the text's height, within a sane larger cap with its own scroll beyond that), toggle content flips to the collapse wording; click again → collapsed again; set the text back to one line → the toggle disappears and the box is not stuck at a fixed height; repeat for the negative box in local mode on a recipe that takes a negative prompt (`sdxl`): set `_imgNegative.Text` long → `ImagesNegativeToggle` visible; switch to OpenRouter mode → the negative row (and its toggle) hidden, and back to local → the state is intact; text of exactly the limit's line count shows no toggle (boundary).

- [ ] **Step 2: Run the scratch Images-only smoke and confirm RED at the new assertions**

- [ ] **Step 3: Implement**

`AttachPromptFade(box, collapsedMaxHeight, toggleTag)`: a container (e.g. a `Grid`) holding the `TextBox` with `MaxHeight = collapsedMaxHeight` while collapsed (suggested: ~150 px for the prompt, ~90 px for the negative; the implementer may tune by eye but keep the prompt's collapsed height at least its current `MinHeight`); overflow = `box.ExtentHeight > box.ViewportHeight + 1` evaluated after layout (hook `TextChanged`, `SizeChanged`, and the container's `Loaded`/`IsVisibleChanged`; avoid a layout feedback loop — set `MaxHeight`/visibility only when the computed state actually changes). When collapsed and overflowing: a bottom fade (a theme-safe `OpacityMask` `LinearGradientBrush` on the box, or an overlay gradient built from the box's own `Background` brush; no hard-coded colours; it must read well in every theme the app ships, including the light "Parchment" theme) that fades the last ~20 px so the user can see there is more, and a small ghost-style toggle `Button` (Tag from the parameter) anchored at the bottom edge, content "Show more ▾" / "Show less ▴" (`GhostButton` style, `RailGlyph`-style chevron if that's what neighbours use). Clicking expands: remove the collapsed `MaxHeight` (cap it at a larger value, e.g. 420 px, beyond which the box's own vertical scrollbar takes over), hide the fade, flip the toggle. Not overflowing: no toggle, no fade, no max-height clamp (natural size). The toggle shows in the expanded state whenever the text is still taller than the collapsed limit, so the user can collapse again. Pressing the toggle must not steal text focus oddly or reset the caret/selection. Replace the two bare `TextBox` insertions in `BuildImagesPage` with the returned containers; keep `_imgPrompt`/`_imgNegative` fields and tags unchanged.

- [ ] **Step 4: Run the checks**

Run: `dotnet build launcher/AgentOrchestrator` (0 errors); `--self-test` (exit 0); the scratch Images-only `--ui-smoke` GREEN, plus one deliberate sabotage (e.g. never clamp `MaxHeight`) failing at the expected assertion; undone.

- [ ] **Step 5: Commit**

Message in a plain file, then `git commit -F <file>`; trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.

---

## Task 3 (execute FIRST): List the user's local FLUX.2 Klein 4B model

User report: `C:\.llm-models\flux-2-klein-4b-Q5_K_M.gguf` is not listed; the page says "No image models". Cause: the only flux recipe (`flux3-klein`) has `Family: @"flux[-_.]?3"`, which never matches `flux-2-klein-4b-...`. sd.cpp runs FLUX.2 Klein 4B with (docs/models/flux2.md): `--diffusion-model <klein gguf> --vae <flux2 vae> --llm <Qwen3-4B, not the Instruct-2507 that Z-Image uses> --cfg-scale 1.0 --steps 4` (Klein "base" variants: cfg 4.0, steps 20).

**Files:**
- Modify: `launcher/AgentOrchestrator/ImageGen.cs` (the `Recipes` array and `ImageGenSelfTest`)

**Interfaces:**
- Produces: a new `ImageRecipe` with Id `"flux2-klein-4b"`, inserted right AFTER the `flux3-klein` entry in `ImageGen.Recipes` (folder listings sort by recipe order; `Recipes[0]` stays Z-Image Turbo). Name `"FLUX.2 Klein 4B"`, Note `""`. Files (verified against Hugging Face, 2026-11-19): `new("--diffusion-model", "leejet/FLUX.2-klein-4B-GGUF", "flux-2-klein-4b-Q4_0.gguf", 2460378560)`, `new("--vae", "Comfy-Org/Flux-2-Klein-4B", "split_files/vae/flux2-vae.safetensors", 336211292)`, `new("--llm", "unsloth/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", 2497281312, Local: @"^qwen3-4b-(?!instruct).*\.gguf$")`. `Steps: 4, Cfg: 1.0, Sampler: "euler", Negative: false, Multiple: 16`, `Family: @"flux[-_.]?2[-_.]?klein[-_.]?(?:base[-_.]?)?4b"` (matches the 4B Klein and Klein-base 4B file names case-insensitively; NOT 9B, which needs a different text encoder; NOT `flux-3-image-klein`).

- [ ] **Step 1: Extend `ImageGenSelfTest`'s scan check first and confirm it fails**

In the folder fixture add `(@"f\flux-2-klein-4b-Q5_K_M.gguf", 1)`, `(@"f\flux-2-klein-9b-Q4_0.gguf", 1)` (9B must NOT be listed) and `(@"g\Qwen3-4B-Instruct-2507-Q4_K_M.gguf", 1)`. Expected listing becomes `"z-image-turbo=Z-Image-Turbo-FP8.safetensors,flux2-klein-4b=flux-2-klein-4b-Q5_K_M.gguf,qwen-image-2.1=qwen-image-2.1-Q4_K_M.gguf,moss-image-sft=MOSS-Image-SFT-Q8_0.gguf"` (the existing `moss = found[^1]` still holds). Add a check that for the Klein entry `Paths(klein.Recipe, klein.Path, all, root)` has `[0] == klein.Path`, `[1]` and `[2]` the downloads (`LocalPath(root, klein.Recipe.Files[1])`, `LocalPath(root, klein.Recipe.Files[2])`; the Instruct-2507 file in folder `g` must NOT be picked as Klein's text encoder).
  Run `dotnet build launcher/AgentOrchestrator`, then `--self-test`: the new checks must FAIL before Step 2.

- [ ] **Step 2: Add the recipe** per Interfaces; build; `--self-test` exits 0.

- [ ] **Step 3: Confirm on the user's real folder (read-only)** with a throwaway, uncommitted check that `ImageGen.ScanFolder(@"C:\.llm-models")` lists `flux-2-klein-4b-Q5_K_M.gguf` under `flux2-klein-4b`. Never touch the user's running app (`dist\Agent Orchestrator.exe`).

- [ ] **Step 4: Commit** (`git commit -F <file>`; trailer `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`).
