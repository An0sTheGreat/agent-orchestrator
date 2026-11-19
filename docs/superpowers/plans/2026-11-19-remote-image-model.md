# Remote Image Generation Model Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the Images page generate with a remote API image model (service + model id) instead of only a local stable-diffusion.cpp recipe.

**Architecture:** A new pure-logic file (`RemoteImageGen.cs`) builds the OpenAI-shaped `/images/generations` request and parses its reply; `ImageSettings` gains a nullable `Remote` field that, when set, makes `GenerateImagesAsync` skip the local runtime/recipe/sd-cli path entirely and POST through the app's existing swappable `_remoteHttp` client instead, converging back onto the existing file-naming/gallery/sidecar code.

**Tech Stack:** C#/.NET 8, WPF (existing launcher). No new dependencies.

**Spec:** `docs/superpowers/specs/2026-11-19-remote-image-model-design.md`

## Global Constraints

- No Python/orchestrator.py involvement — stays launcher-only (spec, Constraints).
- Reuse `_remoteHttp` (`MainWindow.RemoteApi.cs:22`) for the HTTP call; don't create a new `HttpClient`.
- Don't hardcode which of `RemoteCatalog.Services` support image generation — offer all of them (plus Custom), let an unsupported one fail with a clear message (spec, Non-goals).
- `ImageSettings` round-trips through `System.Text.Json` to `AppSettings.ImageGen` and each picture's sidecar `.json` — a new property needs no extra (de)serialization code.

## Review Focus

- A chosen remote service has no API key set in the environment: must give a clear, specific error (which env var is missing), not a null-reference exception. (Task 3)
- A service's `/images/generations` doesn't exist or rejects the request (4xx/5xx, or an OpenAI-style `{"error":{"message":...}}` body): the message reaches the user, generation doesn't silently do nothing. (Task 3)
- The service returns fewer images than the requested batch size (e.g. it only supports `n=1`): no crash; only the images actually returned are shown/saved. (Task 3)
- An empty prompt with "Use a Remote API…" selected: same "Write a prompt first" toast as the local path, not a different code path that skips the check. (Task 3)
- Switching "Switch to Local" after using Remote restores the previously selected local recipe/model, not a reset to the first recipe. (Task 2)

---

## Task 1: Remote image request/response logic

**Files:**
- Create: `launcher/AgentOrchestrator/RemoteImageGen.cs`
- Modify: `launcher/AgentOrchestrator/App.xaml.cs` (call the new self-test alongside the existing `ImageGen.ImageGenSelfTest()`, in the same `--self-test` block, `App.xaml.cs:29` per the design doc)

**Interfaces:**
- Produces: `internal sealed record RemoteModel(string BaseUrl, string Model, string KeyEnv)`; `internal sealed record RemoteImage(byte[]? Bytes, string? Url)`; `internal static class RemoteImageGen { public static string BuildRequest(RemoteModel model, ImageSettings s); public static List<RemoteImage> ParseResponse(string json); }`
- Consumes: `ImageSettings` (`ImageGen.cs`) — existing `Prompt`, `Batch`, `Width`, `Height` properties, read-only here.

This repo's own convention (see `ImageGen.ImageGenSelfTest()` in `ImageGen.cs:204-279`) is a single in-process self-test method with `Check(bool, string)` assertions, wired into `--self-test`, not a separate test framework. Follow that pattern exactly: a `RemoteImageGenSelfTest()` method in the new file.

- [ ] **Step 1: Write `RemoteImageGenSelfTest()` in `RemoteImageGen.cs`, called nowhere yet**

```csharp
internal static void RemoteImageGenSelfTest()
{
    static void Check(bool ok, string what)
    {
        if (!ok)
            throw new InvalidOperationException($"Remote image generation self-test: {what}");
    }
    var model = new RemoteModel("https://api.example.com/v1", "gen-1", "X_API_KEY");
    var s = new ImageSettings { Prompt = "a cat", Batch = 2, Width = 512, Height = 768 };
    var req = System.Text.Json.Nodes.JsonNode.Parse(RemoteImageGen.BuildRequest(model, s))!;
    Check((string)req["model"]! == "gen-1" && (string)req["prompt"]! == "a cat" && (int)req["n"]! == 2 &&
        (string)req["size"]! == "512x768" && (string)req["response_format"]! == "b64_json", "request body");

    var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("hello"));
    var one = RemoteImageGen.ParseResponse($$"""{"data":[{"b64_json":"{{b64}}"}]}""");
    Check(one is [{ Url: null, Bytes: { } bytes }] && System.Text.Encoding.UTF8.GetString(bytes) == "hello", "a b64_json image");

    var byUrl = RemoteImageGen.ParseResponse("""{"data":[{"url":"https://x/y.png"}]}""");
    Check(byUrl is [{ Bytes: null, Url: "https://x/y.png" }], "a url image");

    var two = RemoteImageGen.ParseResponse($$"""{"data":[{"b64_json":"{{b64}}"},{"url":"https://x/y.png"}]}""");
    Check(two.Count == 2 && two[0].Bytes is not null && two[1].Url == "https://x/y.png", "two images, order kept");

    try { RemoteImageGen.ParseResponse("""{"error":{"message":"bad request"}}"""); Check(false, "an error reply did not throw"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("bad request"), "error message: " + e.Message); }

    try { RemoteImageGen.ParseResponse("{}"); Check(false, "an empty reply did not throw"); }
    catch (InvalidOperationException e) { Check(e.Message.Contains("unexpected reply"), "empty-reply message: " + e.Message); }
}
```

- [ ] **Step 2: Run and confirm it fails to compile** (the types/methods don't exist yet)

Run: `dotnet build launcher/AgentOrchestrator`
Expected: errors naming `RemoteModel`, `RemoteImage`, `RemoteImageGen`, `BuildRequest`, `ParseResponse` as not found.

- [ ] **Step 3: Implement `RemoteModel`, `RemoteImage`, and `RemoteImageGen.BuildRequest`/`ParseResponse` in `RemoteImageGen.cs`**

`BuildRequest`: serialize an anonymous object `{ model = model.Model, prompt = s.Prompt, n = s.Batch, size = $"{s.Width}x{s.Height}", response_format = "b64_json" }` with `System.Text.Json.JsonSerializer.Serialize`.

`ParseResponse`: parse with `JsonNode.Parse`. If the root has an `"error"` object, throw `InvalidOperationException((string)root["error"]!["message"]! ?? "the image API returned an error")`. If it has no `"data"` array, throw `InvalidOperationException("unexpected reply from the image API")`. Otherwise map each element of `data` to a `RemoteImage`: `b64_json` present → `new(Convert.FromBase64String((string)item["b64_json"]!), null)`; else `url` present → `new(null, (string)item["url"]!)`; neither → skip the item (don't throw per-item; an empty resulting list is caught by the caller in Task 3's "fewer images than requested" handling).

- [ ] **Step 4: Wire the self-test into `--self-test` and run it**

Add `RemoteImageGen.RemoteImageGenSelfTest();` next to the existing `ImageGen.ImageGenSelfTest();` call in `App.xaml.cs` (`:29`).

Run: `dotnet build launcher/AgentOrchestrator && "launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --self-test`
Expected: exit 0.

- [ ] **Step 5: Commit**

```bash
git add launcher/AgentOrchestrator/RemoteImageGen.cs launcher/AgentOrchestrator/App.xaml.cs
git commit -F <message file: "Remote image generation: request body and reply parsing">
```

---

## Task 2: Model-source toggle on the Images page

**Files:**
- Modify: `launcher/AgentOrchestrator/ImageGen.cs` (`ImageSettings`: add `public RemoteImageGen.RemoteModel? Remote { get; set; }`)
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`BuildImagesPage`, `ApplyImageSettings`, `ReadImageSettings`)
- Test: the existing Images `--ui-smoke` block (`MainWindow.xaml.cs`, around `:2872-2930` per today's recon — find its enclosing method first; it already checks `ImagesPage`/`ImagesRecipe`/etc. tags)

**Interfaces:**
- Consumes: Task 1's `RemoteImageGen.RemoteModel`; `RemoteCatalog.Services` (`RemoteCatalog.cs:76-90`, the existing `IReadOnlyList<Service>`, each with `.Id`, `.Name`, `.BaseUrl`, `.KeyEnv`).
- Produces: new tags `ImagesUseRemoteApi` (button, switches to remote), `ImagesSwitchToLocal` (button, switches back), `ImagesRemoteService` (ComboBox of service names), `ImagesRemoteModel` (TextBox, the model id). `ImageSettings.Remote` (consumed by Task 3).

Mirror `MainWindow.ModelSettings.cs`'s existing "Model source" block exactly (`SourceButton` helper `:573`, `localPanel`/`remotePanel` pair and their visibility swap `:891-893`, `switchLocal` handler `:1057`) — same two-panel-with-a-toggle shape, adapted for images instead of chat models. Place it in `BuildImagesPage` right after the existing folder row (`MainWindow.ImageGen.cs:149-151`).

- [ ] **Step 1: Extend the existing Images ui-smoke block with the new checks (written to fail first)**

Add, after whatever it already checks for the folder scan: open Images, confirm `ImagesUseRemoteApi` is visible and `ImagesRemoteService`/`ImagesRemoteModel` are not; click `ImagesUseRemoteApi`; confirm `ImagesRemoteService` is now visible and `_imgRecipe`'s row (`ImagesRecipe`) is hidden; set the service combo to its first item and type a model id into `ImagesRemoteModel`; call `ReadImageSettings()` and assert `.Remote is { } r && r.Model == <the typed id> && r.BaseUrl == RemoteCatalog.Services[0].BaseUrl && r.KeyEnv == RemoteCatalog.Services[0].KeyEnv`; click `ImagesSwitchToLocal`; assert `ReadImageSettings().Remote is null` and the local recipe selection is back to what it was before switching to remote (not reset to index 0 — capture `_imgRecipe.SelectedIndex` before switching and compare after switching back).

- [ ] **Step 2: Run and confirm it fails**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: fails at the new assertions (the tags/behavior don't exist yet) — confirm the failure message names the new check, not an unrelated earlier one.

- [ ] **Step 3: Add `Remote` to `ImageSettings`**

`public RemoteImageGen.RemoteModel? Remote { get; set; }` in `ImageGen.cs`, next to `LocalModel`.

- [ ] **Step 4: Build the toggle UI in `BuildImagesPage`**

Wrap the existing "Model" label + `_imgRecipe` + folder row in a `localPanel` `StackPanel`. Add a `remotePanel` `StackPanel` with: a `ComboBox` (Tag `ImagesRemoteService`, `ItemsSource = RemoteCatalog.Services.Select(s => s.Name)`) and a `TextBox` (Tag `ImagesRemoteModel`). Add the two toggle buttons (`ImagesUseRemoteApi`, `ImagesSwitchToLocal`) following `SourceButton`'s style. A local bool/field (e.g. `_imgRemoteMode`) tracks which panel is active; both buttons flip it and set `localPanel`/`remotePanel`'s `Visibility` accordingly (mirroring `MainWindow.ModelSettings.cs:891-893`'s pattern).

- [ ] **Step 5: Wire `ApplyImageSettings` and `ReadImageSettings`**

`ApplyImageSettings`: if `s.Remote is { } remote`, set `_imgRemoteMode = true`, select the matching service in `ImagesRemoteService` (by `BaseUrl`+`KeyEnv`, falling back to the first item if none matches — e.g. a custom/removed service), set `ImagesRemoteModel.Text = remote.Model`, show `remotePanel`/hide `localPanel`; else do today's local-recipe apply and show `localPanel`/hide `remotePanel`. `ReadImageSettings`: if `_imgRemoteMode`, build `Remote` from the selected service's `BaseUrl`/`KeyEnv` and the typed model id, leaving the local fields (`RecipeId`, `LocalModel`) as whatever they last were (harmless — Task 3 ignores them when `Remote` is set); else `Remote = null` as today.

- [ ] **Step 6: Run the ui-smoke check again**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: exit 0, including the new assertions from Step 1.

- [ ] **Step 7: Commit**

```bash
git add launcher/AgentOrchestrator/ImageGen.cs launcher/AgentOrchestrator/MainWindow.ImageGen.cs launcher/AgentOrchestrator/MainWindow.xaml.cs
git commit -F <message file: "Images page: a Model source toggle for a remote API model">
```

---

## Task 3: Generate with the remote model

**Files:**
- Modify: `launcher/AgentOrchestrator/MainWindow.ImageGen.cs` (`GenerateImagesAsync`, `:649-758`)
- Test: same Images ui-smoke block as Task 2

**Interfaces:**
- Consumes: `RemoteImageGen.BuildRequest`/`ParseResponse`/`RemoteModel` (Task 1); `ImageSettings.Remote` (Task 2); `_remoteHttp` (`MainWindow.RemoteApi.cs:22`); `ImageGen.OutputNames` (existing, `ImageGen.cs:194-202`).
- Produces: nothing new for later tasks — this is the last task.

- [ ] **Step 1: Extend the ui-smoke block with the generation checks (written to fail first)**

Swap `_remoteHttp` for a fake handler the way the existing Remote API self-test does (`MainWindow.RemoteApi.cs:1305-1309` shows the pattern: save `_remoteHttp`, replace with `new HttpClient(fake)`, restore in a `finally`). Cases to add:
1. Fake handler returns `200` with `{"data":[{"b64_json":"<1x1 PNG bytes, base64>"}]}` for a POST to `.../images/generations`. Select Remote mode, set a service+model, type a prompt, click `ImagesGenerate` (await its handler). Assert: the request the fake handler received had an `Authorization: Bearer <value of the service's KeyEnv>` header (set the env var to a known test value first), the posted body's `prompt` matches what was typed, a new file exists under `ImageGen.ImagesFolder`, and `RefreshGallery`'s result shows one more picture than before.
2. No value set for the selected service's `KeyEnv`: clicking Generate throws/toasts an error naming that env var, and nothing is posted.
3. Fake handler returns `400` with `{"error":{"message":"model not found"}}`: clicking Generate surfaces "model not found" (in a toast or thrown message), no file is written.
4. Fake handler returns `200` with only one image while `_imgBatch` is set to 2: clicking Generate succeeds with exactly one new gallery picture, no crash.
5. Remote mode selected, prompt left blank: clicking Generate shows the same "Write a prompt first" toast as the local path, and the fake handler receives no request.

- [ ] **Step 2: Run and confirm it fails**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: fails at the new generation assertions.

- [ ] **Step 3: Branch `GenerateImagesAsync` on `s.Remote`**

After `ImageGen.OutputNames(...)` (`:671`), replace the local-only block (`:662-744`, the runtime install, model download, `ImageGen.Args`/`ImageGen.Paths`, `Process.Start`, stdout/stderr pumps, exit-code handling) with:

```csharp
if (s.Remote is { } remote)
    await GenerateRemoteImageAsync(remote, run, files, ct);
else
{
    // today's existing block, unchanged
}
```

Keep the shared tail (`:745-757`: rename, sidecar `.json`, `ImageStatus("Done…")`, `RefreshGallery`, `ShowOnStage`) and the prompt-empty check (`:652-655`) exactly as they are — both already run before/after the branch regardless of `Remote`.

- [ ] **Step 4: Implement `GenerateRemoteImageAsync`**

```csharp
private async Task GenerateRemoteImageAsync(RemoteImageGen.RemoteModel remote, ImageSettings s,
    List<(string Written, string Final, long Seed)> files, CancellationToken ct)
```

- Read the key: `Environment.GetEnvironmentVariable(remote.KeyEnv)`; if empty, `throw new InvalidOperationException($"Missing API key for {remote.BaseUrl}: set {remote.KeyEnv}")` (mirrors `orchestrator.py`'s `ApiClient.__init__` wording for the same situation on the chat side).
- `ImageStatus("Generating…", indeterminate: true)`.
- Build the request via `RemoteImageGen.BuildRequest(remote, s)`; POST it to `$"{remote.BaseUrl.TrimEnd('/')}/images/generations"` through `_remoteHttp`, with `Authorization: Bearer {key}` and `Content-Type: application/json`, passing `ct`.
- If the response isn't successful, read the body as text and try `RemoteImageGen.ParseResponse` on it purely to surface its `error.message` if present (it'll throw `InvalidOperationException` with that message — let it propagate); if that itself throws for an unrelated reason (not JSON at all), throw `InvalidOperationException($"the image API returned {(int)response.StatusCode}")` instead.
- On success, `var images = RemoteImageGen.ParseResponse(await response.Content.ReadAsStringAsync(ct));`
- For `(image, i)` in `images.Select((img, i) => (img, i))` where `i < files.Count`: bytes = `image.Bytes ?? await _remoteHttp.GetByteArrayAsync(image.Url!, ct)`; `File.WriteAllBytes(files[i].Written, bytes)`.

- [ ] **Step 5: Run the ui-smoke check again**

Run: `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --ui-smoke`
Expected: exit 0, including every case from Step 1.

- [ ] **Step 6: Full regression**

Run: `dotnet build launcher/AgentOrchestrator` (0 errors) then `"launcher/AgentOrchestrator/bin/Debug/net8.0-windows/Agent Orchestrator.exe" --self-test` (exit 0) then `--ui-smoke` once more in full (exit 0, no new failures beyond this plan's own changes).

- [ ] **Step 7: Commit**

```bash
git add launcher/AgentOrchestrator/MainWindow.ImageGen.cs
git commit -F <message file: "Images page: generate with the remote model when one is chosen">
```
