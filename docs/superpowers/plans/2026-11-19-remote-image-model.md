# Remote Image Generation Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Images page generate with an OpenRouter image-generation model (model + optional pinned providers) instead of only a local recipe.

**Architecture:** `RemoteCatalog.cs` gains OpenRouter-specific fetch/parse functions for the dedicated image-models and image-provider endpoints, mirroring its existing text-model functions. `ImageSettings` gains two plain string fields (model id, pinned providers). `GenerateImagesAsync` branches: when a remote model is chosen, it skips the local runtime/recipe/sd-cli path and POSTs to OpenRouter's image endpoint through the app's existing `_remoteHttp`, converging back onto the existing file-naming/gallery/sidecar code.

**Tech Stack:** C#/.NET 8, WPF (existing launcher). No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-19-remote-image-model-design.md`

## Global Constraints

- OpenRouter only — no other remote service, no generic service picker (spec, Non-goals).
- `OPENROUTER_API_KEY` is the fixed key env var (spec, Constraints).
- Reuse `_remoteHttp` (`MainWindow.RemoteApi.cs:22`) for every HTTP call in this plan.
- `MainWindow.ModelPicker.cs` is **not** reused for the image-model list — it's built around
  local GGUF files on disk (`DescribeChoices`, `ModelChoice`), not an HTTP list of ~70 remote
  models. Build a plain picker instead (see Task 2).
- `ImageSettings` round-trips whole through `System.Text.Json` — new string fields need no extra
  (de)serialization code.

## Review Focus

- `OPENROUTER_API_KEY` not set in the environment: a clear error naming it, not a null-reference
  exception, both when listing models/providers and when generating. (Tasks 1, 3)
- OpenRouter returns an error body (e.g. the model/provider combination doesn't support a
  requested parameter): the message reaches the user, not a silent no-op. (Task 3)
- Fewer images returned than the requested batch size (a provider that only supports `n=1`): no
  crash; only the images actually returned are shown/saved. (Task 3)
- An empty prompt with the remote model selected: same "Write a prompt first" toast as the local
  path. (Task 3)
- Switching back to local after using remote restores the previously selected local recipe, not a
  reset to the first one. (Task 2)

---

## Task 1: Fetch and parse OpenRouter's image models and their providers

**Files:**
- Modify: `launcher/AgentOrchestrator/RemoteCatalog.cs`

**Interfaces:**
- Produces: `public sealed record ImageModelEntry(string Id, string Name)`; `public sealed record ImageProviderRow(string ProviderName, string ProviderTag, decimal? CostPerImage)`; `public static List<ImageModelEntry> ParseImageModels(string json)`; `public static async Task<List<ImageModelEntry>> FetchImageModelsAsync(HttpClient http, string? apiKey, CancellationToken ct)`; `public static List<ImageProviderRow> ParseImageProviders(string json)`; `public static async Task<List<ImageProviderRow>> FetchImageProvidersAsync(HttpClient http, string modelId, CancellationToken ct, string? apiKey)`.
- Consumes: this file's existing private `GetAsync(http, url, apiKey, ct)` helper (used by `FetchModelsAsync`/`FetchProvidersAsync` already — reuse it, don't duplicate it) and `Reply(json, errorMessage, read)` helper (used by `ParseModels`/`ParseProviders`).

This file's own `RemoteCatalog.SelfTest()` (search for it; the existing `ParseModels`/`ParseProviders` checks around `:249-350` are the pattern to extend) is where the new checks go — follow its existing `Check(bool, string)` style, not a new test file.

- [ ] **Step 1: Add the new Check(...) assertions to `RemoteCatalog.SelfTest()`, calling functions that don't exist yet**

```csharp
var images = ParseImageModels("""{"data":[{"id":"bytedance-seed/seedream-4.5","name":"Seedream 4.5","architecture":{"input_modalities":["text"],"output_modalities":["image"]}},{"id":"no-name/x"}]}""");
Check(images is [{ Id: "bytedance-seed/seedream-4.5", Name: "Seedream 4.5" }, { Id: "no-name/x", Name: "" }], "image models: id and name, name defaults to \"\"");
try { ParseImageModels("{\"data\":5}"); Check(false, "junk accepted as image models"); }
catch (InvalidOperationException e) when (e.Message.Contains("not a")) { }

var providers = ParseImageProviders("""{"id":"x","endpoints":[{"provider_name":"Seed","provider_tag":"seed","pricing":[{"billable":"output_image","unit":"image","cost_usd":0.04},{"billable":"input_image","unit":"image","cost_usd":0}]},{"provider_name":"NoPricing","provider_tag":"np"}]}""");
Check(providers is [{ ProviderName: "Seed", ProviderTag: "seed", CostPerImage: 0.04m }, { ProviderName: "NoPricing", CostPerImage: null }], "image providers: price from the output_image entry, null when absent");
```

Also add a `FetchImageModelsAsync`/`FetchImageProvidersAsync` case to the existing `FakeHandler`-based async test (find where `FetchModelsAsync`/`FetchProvidersAsync` are already exercised against a fake `HttpClient`, e.g. around `:330-345`) asserting: a 401 throws with a message naming the missing key, and a successful fake reply round-trips through `ParseImageModels`/`ParseImageProviders` as above, called with the exact URLs `https://openrouter.ai/api/v1/images/models` and `https://openrouter.ai/api/v1/images/models/{modelId}/endpoints`.

- [ ] **Step 2: Run and confirm it fails to compile**

Run: `dotnet build launcher/AgentOrchestrator`
Expected: errors naming `ParseImageModels`, `ParseImageProviders`, `FetchImageModelsAsync`, `FetchImageProvidersAsync`, `ImageModelEntry`, `ImageProviderRow` as not found.

- [ ] **Step 3: Implement the four functions and two records**

`ImageModelEntry`/`ImageProviderRow`: plain records as above, `Name` and `ProviderName`/`ProviderTag` default to `""` when the JSON field is absent (mirror `ParseModels`' existing null-coalescing style for `Name`).

`ParseImageModels(json)`: parse with the same `Reply(json, "...is not an image model list.", root => ...)` helper `ParseModels` uses; read `(root as JsonObject)?["data"] as JsonArray`, map each object to `new ImageModelEntry(Str(e["id"]) ?? "", Str(e["name"]) ?? "")`. The server already returns only image-generation models (verified live) — no client-side modality filtering needed.

`FetchImageModelsAsync`: `ParseImageModels(await GetAsync(http, "https://openrouter.ai/api/v1/images/models", apiKey, ct))`.

`ParseImageProviders(json)`: `(root as JsonObject)?["endpoints"] as JsonArray`, map each to `new ImageProviderRow(Str(e["provider_name"]) ?? "", Str(e["provider_tag"]) ?? "", (e["pricing"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => Str(p["billable"]) == "output_image") is { } priced ? PerUnit(priced["cost_usd"]) : null)` — add a small `PerUnit` (or reuse `PerMillion`'s numeric-parsing half without the ÷1,000,000; check `PerMillion`'s body before deciding whether to factor out a shared numeric-parse helper or write a 1-line duplicate — this file already has small single-purpose numeric helpers, follow that style).

`FetchImageProvidersAsync`: `ParseImageProviders(await GetAsync(http, $"https://openrouter.ai/api/v1/images/models/{modelId}/endpoints", apiKey, ct))`.

- [ ] **Step 4: Run and confirm it passes**

Run: `dotnet build launcher/AgentOrchestrator && "launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --self-test`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add launcher/AgentOrchestrator/RemoteCatalog.cs
git commit -F <message file: "RemoteCatalog: fetch and parse OpenRouter's image models and their providers">
```

---

## Task 2: Model-source toggle, model picker, and provider picker on the Images page

**Files:**
- Modify: `launcher/AgentOrchestrator/ImageGen.cs` (`ImageSettings`: add `RemoteModel`, `RemoteProvider`)
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`BuildImagesPage`, `ApplyImageSettings`, `ReadImageSettings`)
- Test: the existing Images `--ui-smoke` block (`MainWindow.xaml.cs`, around `:2872-2930`)

**Interfaces:**
- Consumes: Task 1's `RemoteCatalog.ImageModelEntry`/`ImageProviderRow`/`FetchImageModelsAsync`/`FetchImageProvidersAsync`; `RemoteCatalog.Services[0]` (OpenRouter's `Service`, for its `KeyEnv`) — or just the literal `"OPENROUTER_API_KEY"` string; check which this file already does elsewhere for consistency before picking.
- Produces: `ImageSettings.RemoteModel`/`RemoteProvider` (consumed by Task 3); tags `ImagesUseRemoteApi` (switch to remote), `ImagesSwitchToLocal` (switch back), `ImagesRemoteModelButton` (opens the model-pick popup, its `Content` shows the chosen model's name or "Choose a model…"), `ImagesRemoteProvidersButton` (opens the provider-pick popup).

Mirror `MainWindow.ModelSettings.cs`'s "Model source" shape (`SourceButton` `:573`, `localPanel`/`remotePanel` pair `:891-893`, `switchLocal` `:1057`) for the toggle. For the model list itself (~70 items with no local file metadata), a plain searchable popup is enough — not `ModelPicker.cs` (see Global Constraints) and not the full `MainWindow.ApiModel.cs` `BuildProviderDialog` dialog (that one's columns — price per token, latency, uptime — don't apply here); a smaller version listing `ImageProviderRow.ProviderName`/`ProviderTag`/`CostPerImage` with a tick per row, following that dialog's tick/order pattern at a glance but without copying its token-pricing columns.

- [ ] **Step 1: Extend the Images ui-smoke block with the new checks (written to fail first)**

Using a fake `_remoteHttp` (mirror `MainWindow.RemoteApi.cs:1305-1309`'s save/replace/restore pattern) that answers `images/models` and `images/models/*/endpoints` with small fixed lists: open Images, confirm `ImagesUseRemoteApi` is visible and the remote controls are not; click it; confirm the local `ImagesRecipe` row is hidden and `ImagesRemoteModelButton`/`ImagesRemoteProvidersButton` are visible; open the model popup, pick the second fixture model, confirm `ReadImageSettings().RemoteModel` equals its id and `ImagesRemoteModelButton.Content` shows its name; open the provider popup, tick two of the fixture's providers in order, confirm `ReadImageSettings().RemoteProvider` equals their tags joined with `", "` in tick order (mirror the existing text-model provider dialog's own order-tracking test, `MainWindow.ModelSettings.cs` around `:1416-1422`, and the exact separator `MainWindow.ApiModel.cs:18` already uses); note the current `_imgRecipe.SelectedIndex`, click `ImagesSwitchToLocal`, confirm `ReadImageSettings().RemoteModel == ""` and `_imgRecipe.SelectedIndex` is back to the noted value.

- [ ] **Step 2: Run and confirm it fails**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: fails at the new assertions.

- [ ] **Step 3: Add the two fields to `ImageSettings`**

```csharp
public string RemoteModel { get; set; } = ""; // "" = local; an OpenRouter image model id otherwise
public string RemoteProvider { get; set; } = ""; // comma-separated provider tags to pin, "" = OpenRouter chooses
```

- [ ] **Step 4: Build the toggle, model popup, and provider popup in `BuildImagesPage`**

Wrap the existing "Model" label + `_imgRecipe` + folder row in a `localPanel`. Add a `remotePanel` with the two buttons above. `ImagesRemoteModelButton`'s click fetches `RemoteCatalog.FetchImageModelsAsync(_remoteHttp, key, ct)` (key via `Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")`) and shows them in a simple filterable popup (a `TextBox` filter + a `ListBox`, following this file's existing `Muted`/button-style helpers); picking one stores the id+name and closes it. `ImagesRemoteProvidersButton`'s click fetches `RemoteCatalog.FetchImageProvidersAsync(_remoteHttp, <chosen model id>, ct, key)` and shows a tick-list (name, tag, `CostPerImage` formatted like `RemoteApi.cs`'s existing `RemotePrice`-style helper — reuse `RemotePrice` if its signature fits a plain `decimal?`, it already does) with order numbers, mirroring `MainWindow.ApiModel.cs`'s `BuildProviderDialog` tick/order mechanics (`orderMarks`, `picked` list) at a smaller scale. Store the ticked tags into `RemoteProvider` joined with `", "` — the exact separator the existing text-model provider list already uses (`MainWindow.ApiModel.cs:18`, `string.Join(", ", providers)`), not a bare `","`. A local bool field (e.g. `_imgRemoteMode`) tracks which panel is showing, toggled by the two buttons, driving `localPanel`/`remotePanel` visibility. Switching to remote must not touch `_imgRecipe.SelectedIndex` at all (no code path should reset or reapply it while entering remote mode) — that preserves the previous local pick for when the user switches back, which Step 1's test checks explicitly.

- [ ] **Step 5: Wire `ApplyImageSettings` and `ReadImageSettings`**

`ApplyImageSettings`: if `s.RemoteModel.Length > 0`, set `_imgRemoteMode = true`, show `remotePanel`/hide `localPanel`, set the model button's content/stored id from `s.RemoteModel` (name unknown until the list is fetched — show the id until then), store `s.RemoteProvider` for the provider popup to pre-tick next time it opens; else do today's local apply and show `localPanel`/hide `remotePanel`. `ReadImageSettings`: if `_imgRemoteMode`, `RemoteModel`/`RemoteProvider` from the stored picks; else both `""` as today.

- [ ] **Step 6: Run the ui-smoke check again**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: exit 0, including the new assertions.

- [ ] **Step 7: Commit**

```bash
git add launcher/AgentOrchestrator/ImageGen.cs launcher/AgentOrchestrator/MainWindow.ImageGen.cs launcher/AgentOrchestrator/MainWindow.xaml.cs
git commit -F <message file: "Images page: pick an OpenRouter image model and its providers">
```

---

## Task 3: Generate via OpenRouter's image endpoint

**Files:**
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`GenerateImagesAsync`, `:649-758`)
- Test: same Images ui-smoke block

**Interfaces:**
- Consumes: `ImageSettings.RemoteModel`/`RemoteProvider` (Task 2); `_remoteHttp`; `ImageGen.OutputNames` (existing).
- Produces: nothing for later tasks — last task.

- [ ] **Step 1: Extend the ui-smoke block with the generation checks (written to fail first)**

With the fake `_remoteHttp` answering `POST https://openrouter.ai/api/v1/images`:
1. `200` with `{"data":[{"b64_json":"<base64 1x1 PNG>"}]}`: remote model+no provider pinned, a prompt typed, click `ImagesGenerate`. Assert the posted body has `model`/`prompt` matching the UI, no `provider` key (none pinned), an `Authorization: Bearer <OPENROUTER_API_KEY value>` header, a new gallery picture, and its sidecar `.json`'s `RemoteModel` matches.
2. With two providers pinned in order: the posted body's `provider` equals `{"order":["<tag1>","<tag2>"],"allow_fallbacks":false}` (same shape the existing text-model provider pinning already produces — compare against that exact JSON structure).
3. `OPENROUTER_API_KEY` unset: clicking Generate throws/toasts an error naming it, nothing posted.
4. `400` with `{"error":{"message":"bad aspect ratio"}}`: the message surfaces, no file written.
5. `200` with one image while `_imgBatch` requests 2: succeeds with exactly one new picture, no crash.
6. Remote mode, blank prompt: the existing "Write a prompt first" toast, nothing posted.
7. Remote mode switched on but no model chosen yet (`ReadImageSettings().RemoteModel == ""` while `_imgRemoteMode` is true — `_imgRemoteMode` is a private field of this same `MainWindow` partial class, so `GenerateImagesAsync` can read it directly): clicking Generate toasts "Choose an image model first" (same `Toast(..., "#F7C873")` style as the blank-prompt toast), posts nothing, and does NOT fall through to the local sd-cli path. (Task 2 deliberately leaves `RemoteModel` empty in this state; this task owns refusing it.)

- [ ] **Step 2: Run and confirm it fails**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`

- [ ] **Step 3: Branch `GenerateImagesAsync` on `s.RemoteModel`**

Right after the existing blank-prompt check (`:652-655`), add: `if (_imgRemoteMode && s.RemoteModel.Length == 0) { Toast("Choose an image model first", "#F7C873"); return; }`. Then, after `ImageGen.OutputNames(...)` (`:671`): `if (s.RemoteModel.Length > 0) await GenerateRemoteImageAsync(run, files, ct); else { /* today's local block, unchanged */ }`. Shared tail (`:745-757`) and the prompt-empty check (`:652-655`) stay as they are.

- [ ] **Step 4: Implement `GenerateRemoteImageAsync(ImageSettings s, List<(string Written, string Final, long Seed)> files, CancellationToken ct)`**

- Key: `Environment.GetEnvironmentVariable("OPENROUTER_API_KEY")`; empty → `throw new InvalidOperationException("Missing API key for OpenRouter: set OPENROUTER_API_KEY")`.
- `ImageStatus("Generating…", indeterminate: true)`.
- Build the JSON body: `model = s.RemoteModel`, `prompt = s.Prompt`, `n = s.Batch`, `aspect_ratio = s.Aspect`, `response_format = "b64_json"`, and when `s.RemoteProvider.Length > 0` a `provider = { order = s.RemoteProvider.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), allow_fallbacks = false }` object (the same split call the existing text-model provider reading already uses, `MainWindow.ApiModel.cs:95` — not a bare `.Split(',')`, since `RemoteProvider` is stored comma-**space**-joined) — same shape as the existing chat-side provider pinning (`MainWindow.RemoteApi.cs` builds the identical `{"order":[...],"allow_fallbacks":false}` object already; find that exact code and mirror its construction rather than inventing new JSON-building code).
- POST to `https://openrouter.ai/api/v1/images` via `_remoteHttp`, `Authorization: Bearer {key}`, passing `ct`.
- Non-success: read the body as text; try parsing it as `{"error":{"message":...}}` and throw with that message; if it isn't that shape, throw `InvalidOperationException($"OpenRouter returned {(int)response.StatusCode}")`.
- Success: parse `data[]` (same per-item `b64_json`/`url` shape as the original plan's `RemoteImageGen.ParseResponse` — write that parsing inline here or as a small private helper in this file; it's a few lines, doesn't need its own file now that there's only one caller and one endpoint). For each image with index `i < files.Count`: bytes from `b64_json` (base64-decode) or download `url` via `_remoteHttp.GetByteArrayAsync`; `File.WriteAllBytes(files[i].Written, bytes)`.

- [ ] **Step 5: Run the ui-smoke check again**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: exit 0, including every case from Step 1.

- [ ] **Step 6: Full regression**

Run: `dotnet build launcher/AgentOrchestrator` (0 errors), `--self-test` (exit 0), `--ui-smoke` once more in full (exit 0).

- [ ] **Step 7: Commit**

```bash
git add launcher/AgentOrchestrator/MainWindow.ImageGen.cs
git commit -F <message file: "Images page: generate with the chosen OpenRouter model">
```
