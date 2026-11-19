# Remote image generation model — design

## Intent and why

The Images page (added in commits `8493e04`, `be1c170`, `4553ab1`, `42a5774`) only generates
pictures with a local model run through stable-diffusion.cpp's `sd-cli.exe`. The user wants the
option to pick a remote API image model instead, selectable from the Images tab, the same way a
chat worker can already pick "Runs on this computer" or "Use a Remote API…" in Model Settings.

## Goals

- From the Images page, choose a remote image-generation API (service + model id) instead of a
  local recipe, and generate with it.
- Reuse this app's existing remote-service list and API-key plumbing (`RemoteCatalog.Services`,
  `Environment.GetEnvironmentVariable(KeyEnv)`) rather than inventing new credential storage.
- The choice persists the same way local settings already do (`AppSettings.ImageGen`).
- A remote request that a given service doesn't actually support fails with a clear message,
  rather than silently producing nothing.

## Non-goals / anti-goals

- Not verifying in advance which of `RemoteCatalog.Services` actually serve image generation —
  that isn't knowable from this codebase, and hardcoding a guess would be worse than a clear
  error from the service itself.
- Not building a "pick providers" sub-dialog like OpenRouter's chat routing — that concept
  (order, fallbacks, price sort) is chat-specific and nothing in this codebase shows an
  equivalent for images.
- Not changing the local generation path, the gallery, or the stage at all.

## Constraints

- No Python/orchestrator.py involvement — image generation is launcher-only today and stays that
  way.
- Reuse the existing swappable `_remoteHttp` field (`MainWindow.RemoteApi.cs:22`) for the new
  HTTP call, so it can be faked in tests the same way `RemoteCatalog.FetchModelsAsync` already is.
- `ImageSettings` (`ImageGen.cs`) is serialized whole to `AppSettings.ImageGen`
  (`Models.cs:320`) and to each picture's sidecar `.json` — a new field on it round-trips for
  free through `System.Text.Json`; no separate settings schema work needed.

## Approaches considered

1. **Reuse `LauncherLogic.RemoteRole`** (the chat-worker remote-model record: `BaseUrl, Model,
   KeyEnv, Timeout, Provider, SortPrice, MaxInput, MaxOutput`). Rejected: most of those fields
   are chat-specific (OpenRouter provider routing, per-token price caps) and don't mean anything
   for an image request; forcing them in would leave dead fields or awkward reinterpretation.
2. **A new minimal record, `RemoteImageGen.RemoteModel(BaseUrl, Model, KeyEnv)`** — only the three
   fields an image request actually needs. **Chosen.** Smallest type that fits; no unused fields.
3. **Fold remote image generation into the existing `ImageRecipe`/`ImageChoice` picker** (treat a
   remote model as another dropdown entry like a local file). Rejected: every downstream local
   function (`ImageGen.Args`, `Paths`, `MissingImageFiles`) takes an `ImageRecipe` and builds
   sd-cli arguments; a remote model has no recipe, no files, no sd-cli args at all. Forcing it
   through that shape would mean stubbing fake recipe fields. Instead, remote is a **separate
   source**, chosen via a toggle (mirroring Model Settings' "Model source" toggle), which skips
   the recipe/runtime/download logic entirely when active.

## Design

### Model source toggle (Images page)

Mirrors `MainWindow.ModelSettings.cs`'s existing "Model source" block (`SourceButton` helper at
`:573`, `localPanel`/`remotePanel` pair toggled by `remoteMode()` at `:891-893`, `switchLocal`
click handler at `:1057`). In `BuildImagesPage` (`MainWindow.ImageGen.cs:88+`), right after the
existing folder row (`:149-151`):

```
Model source
  [Runs on this computer]   [Use a Remote API…]
```//one is the active state, shown as plain text; the other as a button, exactly like Model
Settings already does it (`Muted("Runs on this computer.")` vs `SourceButton("Use a Remote API…", ...)`)

- `localPanel`: wraps the existing "Model" label, `_imgRecipe`, and the folder row — unchanged
  internals, just now hideable as a group.
- `remotePanel`: a service `ComboBox` (`RemoteCatalog.Services`, Tag `ImagesRemoteService`) and a
  model-id `TextBox` (Tag `ImagesRemoteModel`), plus a `switchLocal`-style button back to local.
- Visibility of the two panels is driven by whether `ImageSettings.Remote` is set, the same way
  `remoteMode()` drives `localPanel`/`remotePanel` today.

### `ImageSettings.Remote` (`ImageGen.cs`)

```csharp
public RemoteImageGen.RemoteModel? Remote { get; set; }
```

`null` = local (today's behavior, unchanged). Non-null = this remote model.

### `RemoteImageGen.cs` (new file — pure logic, no WPF types, mirrors `ImageGen.cs`'s style)

```csharp
internal sealed record RemoteModel(string BaseUrl, string Model, string KeyEnv);
internal sealed record RemoteImage(byte[]? Bytes, string? Url); // exactly one is set

internal static class RemoteImageGen
{
    public static string BuildRequest(RemoteModel model, ImageSettings s);
    public static List<RemoteImage> ParseResponse(string json);
}
```

- `BuildRequest`: JSON body `{"model": model.Model, "prompt": s.Prompt, "n": s.Batch, "size":
  $"{s.Width}x{s.Height}", "response_format": "b64_json"}`.
- `ParseResponse`: reads a `{"data": [...]}` reply; each item's `b64_json` (base64-decoded to
  `Bytes`) or `url` becomes one `RemoteImage`. A top-level `{"error": {"message": "..."}}` throws
  `InvalidOperationException(message)`. A reply with neither `data` nor `error` throws
  `InvalidOperationException("unexpected reply from the image API")`.

### Generation (`MainWindow.ImageGen.cs`, `GenerateImagesAsync`)

Today (`:649-758`) the function reads settings, saves them, ensures the local runtime and model
files are present, builds sd-cli args, runs the process, then (shared tail, `:745-757`) renames
each produced file, writes its sidecar JSON, refreshes the gallery, and shows the first picture.

Change: after `ImageGen.OutputNames(...)` (`:671`), branch:

- `s.Remote is { } remote`: skip the runtime/download/sd-cli block entirely (`:662-744`). POST
  `RemoteImageGen.BuildRequest(remote, run)` to `{remote.BaseUrl.TrimEnd('/')}/images/generations`
  via `_remoteHttp`, bearer token from `Environment.GetEnvironmentVariable(remote.KeyEnv)`
  (missing key: throw clearly, mirroring `ApiClient.__init__`'s "Missing API key for {base_url}:
  set {source}" wording). A non-2xx response throws with the body's error message if present,
  else the status code. On success, `RemoteImageGen.ParseResponse` the body; for each image with
  `Url` set, download it (`_remoteHttp.GetByteArrayAsync`); write each image's bytes to the
  matching `files[i].Written`. Fewer images than `files.Count` is fine — the existing shared tail
  already only processes files that exist (`files.Where(f => File.Exists(f.Written))`,
  unchanged).
- otherwise: today's local block, unchanged.

The random-seed assignment (`:668-669`) stays unconditional — it's only used for file naming and
the sidecar, which remote pictures still want.

## What's left to the builder

- Exact WPF layout/styling of the new toggle row (follow `BuildImagesPage`'s existing helpers:
  `Muted`, `Label`, `SourceButton`-equivalent) — the plan fixes the Tags and behavior, not pixel
  layout.
- Exact wording of error messages beyond what's specified above.
- Whether `ApplyImageSettings`/`ReadImageSettings` need a small helper to read the two new
  controls — mechanical, follows the existing pattern for every other field in those two
  functions.
